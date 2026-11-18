// Gap Flip – odwracanie pozycji wokół poziomu luki (cBot / strategia dla cTradera)
//  Zasady (wersja podstawowa – bez filtrów, kolejne filtry dojdą później):
//   • luka = świeca, która odskakuje od poprzedniej o co najmniej Min Gap (pkt), o każdej godzinie. Gap Type:
//       FullGap (domyślnie): cała świeca za poprzednią – LOW powyżej HIGH poprzedniej (w górę) / HIGH poniżej LOW (w dół),
//                  świece na siebie nie zachodzą, na wykresie widać pustą dziurę; sprawdzane na zamknięciu świecy luki,
//       OpenBeyondHighLow: samo otwarcie powyżej HIGH / poniżej LOW poprzedniej (świeca może potem wrócić),
//       OpenVsClose: otwarcie różni się od CLOSE poprzedniej (łapie też szybkie skoki na przełomie świecy),
//   • poziom luki L = zamknięcie świecy przed luką,
//   • luka w górę: świeca zamyka się POD L − bufor → SHORT; potem zamyka się NAD L + bufor → odwrócenie na LONG;
//     znowu pod → SHORT … i tak dalej, aż cena pójdzie w jedną stronę (TP),
//   • luka w dół: lustrzanie (pierwsze wejście LONG po zamknięciu nad L + bufor),
//   • stop = odwrócenie; dodatkowo zawsze awaryjny SL na serwerze (za poziomem, na wypadek szpilki),
//   • po TP koniec gry na tej luce; nowa luka zastępuje starą (otwarta pozycja ze starej luki jest zamykana),
//   • limit wejść na lukę: po ostatnim wejściu sygnał przeciwny tylko zamyka pozycję,
//   • sygnały na ZAMKNIĘTEJ świecy, zlecenie po rynku od razu (≈ otwarcie następnej świecy),
//   • bezpiecznik: dzienny limit straty liczony jak FTMO (equity vs saldo z północy CET).
//  Odległości w punktach ceny (np. GER40: 1 pkt = 1.0 ceny).
using System;
using System.Globalization;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum GfSizing { FixedLots, RiskCash }
    public enum GfGapType { FullGap, OpenBeyondHighLow, OpenVsClose }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class Gap_Flip_Bot : Robot
    {
        // ── System
        [Parameter("Bot Label", DefaultValue = "GAP_FLIP", Group = "System")]
        public string Label { get; set; }

        [Parameter("Log", DefaultValue = true, Group = "System")]
        public bool LogOn { get; set; }

        [Parameter("Draw Gap Level", DefaultValue = true, Group = "System")]
        public bool DrawOn { get; set; }

        // ── Luka
        [Parameter("Gap Type", DefaultValue = GfGapType.FullGap, Group = "Gap")]
        public GfGapType GapType { get; set; }

        [Parameter("Min Gap (points)", DefaultValue = 10.0, MinValue = 0.0, Group = "Gap")]
        public double MinGap { get; set; }

        [Parameter("Buffer Around Level (points)", DefaultValue = 2.0, MinValue = 0.0, Group = "Gap")]
        public double Buf { get; set; }

        [Parameter("Max Entries per Gap", DefaultValue = 5, MinValue = 1, MaxValue = 100, Group = "Gap")]
        public int MaxEntries { get; set; }

        // ── Wyjście / ryzyko
        [Parameter("Take Profit (points from entry, 0 = off)", DefaultValue = 40.0, MinValue = 0.0, Group = "Exit & Risk")]
        public double TpPts { get; set; }

        [Parameter("Emergency SL Beyond Level (points)", DefaultValue = 20.0, MinValue = 0.1, Group = "Exit & Risk")]
        public double SlPts { get; set; }

        [Parameter("Sizing", DefaultValue = GfSizing.FixedLots, Group = "Exit & Risk")]
        public GfSizing Sizing { get; set; }

        [Parameter("Fixed Lots", DefaultValue = 1.0, MinValue = 0.01, Group = "Exit & Risk")]
        public double Lots { get; set; }

        [Parameter("Risk per Entry ($, to emergency SL)", DefaultValue = 200.0, MinValue = 1.0, Group = "Exit & Risk")]
        public double RiskCash { get; set; }

        // ── Bezpiecznik
        [Parameter("Max Daily Loss ($, 0 = off)", DefaultValue = 1000.0, MinValue = 0.0, Group = "Equity Guard")]
        public double MaxDailyLoss { get; set; }

        [Parameter("Day Reset Time Zone (Windows ID)", DefaultValue = "Central European Standard Time", Group = "Equity Guard")]
        public string DayResetTz { get; set; }

        // stan luki
        private bool _active, _done;
        private double _L, _gapOpen;
        private int _gapDir, _nEnt;
        private DateTime _gapTime;
        private string _gapId = "";

        // bezpiecznik
        private TimeZoneInfo _dayTz;
        private DateTime _guardDay;
        private double _dayStartBal;
        private bool _blocked;

        // statystyka
        private int sGaps, sEntries, sFlips, sTp, sSl, sReplaced, sGuard;

        protected override void OnStart()
        {
            try { _dayTz = TimeZoneInfo.FindSystemTimeZoneById(DayResetTz); }
            catch (Exception) { _dayTz = TimeZoneInfo.Utc; Print("Nie znaleziono strefy '{0}', używam UTC", DayResetTz); }
            _guardDay = ToTz(Server.Time).Date;
            _dayStartBal = Account.Balance;

            Positions.Closed += OnClosed;

            Print("Gap Flip: start. Typ luki {5}, luka ≥ {0} pkt, bufor {1} pkt, maks. {2} wejść na lukę, TP {3}, awaryjny SL {4} pkt za poziomem",
                MinGap, Buf, MaxEntries, TpPts > 0 ? TpPts.ToString(CultureInfo.InvariantCulture) + " pkt" : "wył.", SlPts,
                GapType == GfGapType.FullGap ? "cała świeca za poprzednią (bez zachodzenia)"
                : GapType == GfGapType.OpenBeyondHighLow ? "otwarcie za high/low poprzedniej świecy" : "otwarcie vs zamknięcie poprzedniej świecy");
        }

        protected override void OnStop()
        {
            Print("Gap Flip: luk {0}, wejść {1} (odwróceń {2}), TP {3}, awaryjny SL {4}, zamknięte nową luką {5}, bezpiecznik {6}",
                sGaps, sEntries, sFlips, sTp, sSl, sReplaced, sGuard);
        }

        protected override void OnTick()
        {
            if (MaxDailyLoss <= 0) return;
            var d = ToTz(Server.Time).Date;
            if (d != _guardDay)
            {
                _guardDay = d;
                _dayStartBal = Account.Balance;
                _blocked = false;
            }
            if (!_blocked && Account.Equity - _dayStartBal <= -MaxDailyLoss)
            {
                _blocked = true;
                sGuard++;
                CloseAll("dzienny limit straty");
                if (LogOn) Print("Bezpiecznik: strata dnia {0:F0} $ ≥ {1} $ – blokada do końca dnia", _dayStartBal - Account.Equity, MaxDailyLoss);
            }
        }

        protected override void OnBar()
        {
            int j = Bars.Count - 1;   // świeca, która właśnie się otworzyła
            int c = j - 1;            // ostatnia zamknięta
            if (c < 1) return;

            // 1) nowa luka – zastępuje poprzednią
            //    FullGap: świeca luki = c (właśnie zamknięta, znamy jej low/high); pozostałe typy: świeca luki = j (otwarcie)
            int g = GapType == GfGapType.FullGap ? c : j;
            if (g >= 1)
            {
                double gap = GapSize(g, g - 1);   // > 0 luka w górę, < 0 w dół, 0 brak
                if (gap != 0 && Math.Abs(gap) >= MinGap)
                {
                    NewGap(g, g - 1, gap);
                    if (DrawOn) DrawLevel(Bars.OpenTimes[j]);
                    return;
                }
            }

            // 2) sygnał na zamkniętej świecy dla aktywnej luki
            if (_active && !_done && !_blocked)
            {
                Signal(Bars.ClosePrices[c]);
                if (DrawOn) DrawLevel(Bars.OpenTimes[j]);
            }
        }

        // wielkość luki wg wybranego typu (FullGap: pusta przestrzeń między świecami; OpenBeyondHighLow: otwarcie za high/low)
        private double GapSize(int j, int c)
        {
            double o = Bars.OpenPrices[j];
            if (GapType == GfGapType.FullGap)
            {
                if (Bars.LowPrices[j] > Bars.HighPrices[c]) return Bars.LowPrices[j] - Bars.HighPrices[c];
                if (Bars.HighPrices[j] < Bars.LowPrices[c]) return Bars.HighPrices[j] - Bars.LowPrices[c];
                return 0;
            }
            if (GapType == GfGapType.OpenVsClose) return o - Bars.ClosePrices[c];
            if (o > Bars.HighPrices[c]) return o - Bars.HighPrices[c];
            if (o < Bars.LowPrices[c]) return o - Bars.LowPrices[c];
            return 0;
        }

        private void NewGap(int j, int c, double gap)
        {
            if (MyPositions().Length > 0)
            {
                sReplaced++;
                CloseAll("nowa luka");
            }
            sGaps++;
            _active = true;
            _done = false;
            _nEnt = 0;
            _L = Bars.ClosePrices[c];
            _gapOpen = Bars.OpenPrices[j];
            _gapDir = gap > 0 ? 1 : -1;
            _gapTime = Bars.OpenTimes[j];
            _gapId = _gapTime.ToString("yyyyMMddHHmm");
            if (LogOn) Print("Luka {0}: {1:F1} pkt {2}, zamknięcie {3} → otwarcie {4} (poziom {3}), przerwa {5:F0} min",
                _gapDir > 0 ? "w górę" : "w dół", Math.Abs(gap),
                GapType == GfGapType.FullGap ? "pustej przestrzeni między świecami"
                : GapType == GfGapType.OpenVsClose ? "za close poprzedniej świecy" : (_gapDir > 0 ? "za high poprzedniej świecy" : "za low poprzedniej świecy"),
                _L, _gapOpen, (Bars.OpenTimes[j] - Bars.OpenTimes[c]).TotalMinutes);
            if (DrawOn)
            {
                Chart.DrawRectangle("gf_Z_" + _gapId, Bars.OpenTimes[c], _L, _gapTime, _gapOpen, Color.FromArgb(70, 255, 165, 0)).IsFilled = true;
                DrawLevel(_gapTime);
            }
        }

        private void Signal(double close)
        {
            int side = close > _L + Buf ? 1 : close < _L - Buf ? -1 : 0;
            if (side == 0) return;

            int cur = CurSide();
            if (cur == side) return;

            if (_nEnt == 0)
            {
                if (side == -_gapDir) Enter(side, "cena przebiła lukę");
                return;
            }

            if (cur != 0)
            {
                CloseAll("odwrócenie");
                if (_nEnt < MaxEntries) sFlips++;
            }
            if (_nEnt < MaxEntries)
                Enter(side, side == 1 ? "powrót nad poziom luki" : "powrót pod poziom luki");
            else if (cur != 0)
            {
                _done = true;
                if (LogOn) Print("Limit {0} wejść – pozycja zamknięta, koniec gry na tej luce", MaxEntries);
            }
        }

        private void Enter(int side, string why)
        {
            var type = side == 1 ? TradeType.Buy : TradeType.Sell;
            double px = side == 1 ? Symbol.Ask : Symbol.Bid;
            double slLevel = _L - side * (Buf + SlPts);
            double slDist = side * (px - slLevel);
            if (slDist <= Symbol.Spread * 2)
            {
                slLevel = px - side * Math.Max(SlPts, Symbol.Spread * 3);
                slDist = side * (px - slLevel);
            }
            double slPips = slDist / Symbol.PipSize;
            double? tpPips = TpPts > 0 ? TpPts / Symbol.PipSize : (double?)null;

            double units = Sizing == GfSizing.FixedLots
                ? Symbol.QuantityToVolumeInUnits(Lots)
                : RiskCash / (slPips * Symbol.PipValue);
            units = Math.Min(Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down), Symbol.VolumeInUnitsMax);
            if (units < Symbol.VolumeInUnitsMin)
            {
                if (LogOn) Print("Za mały wolumen ({0}) – wejście pominięte. Zwiększ Risk per Entry albo użyj Fixed Lots", units);
                return;
            }

            _nEnt++;
            var r = ExecuteMarketOrder(type, SymbolName, units, Label, slPips, tpPips);
            if (!r.IsSuccessful)
            {
                if (LogOn) Print("Zlecenie odrzucone: {0}", r.Error);
                return;
            }
            sEntries++;
            if (r.Position.StopLoss == null)
            {
                ClosePosition(r.Position);
                if (LogOn) Print("Brak SL na pozycji – zamknięta");
                return;
            }
            if (LogOn) Print("{0} #{1}: {2}, {3} lot @ {4}, SL {5}, TP {6}, strata do SL {7:F0} $",
                type == TradeType.Buy ? "LONG" : "SHORT", _nEnt, why, Symbol.VolumeInUnitsToQuantity(units), r.Position.EntryPrice,
                r.Position.StopLoss, r.Position.TakeProfit.HasValue ? r.Position.TakeProfit.Value.ToString(CultureInfo.InvariantCulture) : "–",
                slPips * Symbol.PipValue * units);
        }

        private void OnClosed(PositionClosedEventArgs a)
        {
            if (a.Position.Label != Label || a.Position.SymbolName != SymbolName) return;
            if (a.Reason == PositionCloseReason.TakeProfit)
            {
                _done = true;
                sTp++;
                if (LogOn) Print("TP: {0:F0} $ – ruch poszedł, koniec gry na tej luce", a.Position.NetProfit);
            }
            else if (a.Reason == PositionCloseReason.StopLoss)
            {
                sSl++;
                if (LogOn) Print("Awaryjny SL: {0:F0} $", a.Position.NetProfit);
            }
        }

        // linia poziomu luki od luki do bieżącej świecy (+ kropkowany bufor)
        private void DrawLevel(DateTime t2)
        {
            var color = _done ? Color.Gray : Color.Orange;
            Chart.DrawTrendLine("gf_L_" + _gapId, _gapTime, _L, t2, _L, color, 2, LineStyle.Solid);
            if (Buf > 0)
            {
                Chart.DrawTrendLine("gf_U_" + _gapId, _gapTime, _L + Buf, t2, _L + Buf, color, 1, LineStyle.Dots);
                Chart.DrawTrendLine("gf_D_" + _gapId, _gapTime, _L - Buf, t2, _L - Buf, color, 1, LineStyle.Dots);
            }
        }

        // ── pomocnicze
        private Position[] MyPositions()
        {
            return Positions.FindAll(Label, SymbolName);
        }

        private int CurSide()
        {
            var p = MyPositions();
            if (p.Length == 0) return 0;
            return p[0].TradeType == TradeType.Buy ? 1 : -1;
        }

        private void CloseAll(string why)
        {
            foreach (var p in MyPositions())
            {
                var r = ClosePosition(p);
                if (LogOn) Print("Zamknięcie ({0}): {1:F0} $", why, r.IsSuccessful ? r.Position.NetProfit : 0);
            }
        }

        private DateTime ToTz(DateTime utc)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), _dayTz);
        }
    }
}
