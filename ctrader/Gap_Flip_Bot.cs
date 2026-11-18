// Gap Flip – odwracanie pozycji wokół poziomu luki (cBot / strategia dla cTradera)
//  Zasady:
//   • poziom luki L = zamknięcie poprzedniej sesji (ostatnia świeca przed Session Close),
//     luka = otwarcie pierwszej świecy sesji − L (filtr: min/max wielkości luki w ATR),
//   • luka w górę: świeca zamyka się POD L − bufor → SHORT; potem zamyka się NAD L + bufor → odwrócenie na LONG;
//     znowu pod → SHORT … i tak dalej, aż cena pójdzie w jedną stronę,
//   • luka w dół: lustrzanie (pierwsze wejście LONG po zamknięciu nad L + bufor),
//   • stop = odwrócenie; dodatkowo zawsze awaryjny SL na serwerze (za poziomem, na wypadek szpilki),
//   • wyjście: TP (k × ATR od wejścia) – po TP koniec gry na ten dzień – albo zamknięcie na koniec sesji,
//   • limit wejść na dzień: po ostatnim wejściu sygnał przeciwny tylko zamyka pozycję,
//   • ATR = średni zakres sesji (high − low) z ostatnich N sesji, bez dnia bieżącego,
//   • sygnały na ZAMKNIĘTEJ świecy, zlecenie po rynku od razu (≈ otwarcie następnej świecy),
//   • bezpiecznik: dzienny limit straty liczony jak FTMO (equity vs saldo z północy CET).
//  Wykres: zalecany m1 lub m5. Czas sesji w strefie z parametru (domyślnie Berlin – DAX/GER40: 09:00–17:30).
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum GfSizing { FixedLots, RiskCash }

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

        // ── Sesja
        [Parameter("Session Open (HH:mm)", DefaultValue = "09:00", Group = "Session")]
        public string SessOpenStr { get; set; }

        [Parameter("Session Close (HH:mm)", DefaultValue = "17:30", Group = "Session")]
        public string SessCloseStr { get; set; }

        [Parameter("Session Time Zone (Windows ID)", DefaultValue = "Central European Standard Time", Group = "Session")]
        public string SessTzId { get; set; }

        // ── Luka
        [Parameter("ATR Sessions (avg session range)", DefaultValue = 20, MinValue = 2, Group = "Gap")]
        public int AtrDays { get; set; }

        [Parameter("Min Gap (x ATR)", DefaultValue = 0.1, MinValue = 0, Group = "Gap")]
        public double MinGap { get; set; }

        [Parameter("Max Gap (x ATR, 0 = off)", DefaultValue = 1.5, MinValue = 0, Group = "Gap")]
        public double MaxGap { get; set; }

        [Parameter("Buffer Around Level (x ATR)", DefaultValue = 0.05, MinValue = 0, Group = "Gap")]
        public double BufAtr { get; set; }

        [Parameter("First Entry With Gap at Open", DefaultValue = false, Group = "Gap")]
        public bool WithGap { get; set; }

        [Parameter("Max Entries per Day", DefaultValue = 3, MinValue = 1, MaxValue = 50, Group = "Gap")]
        public int MaxEntries { get; set; }

        // ── Wyjście / ryzyko
        [Parameter("Take Profit (x ATR from entry, 0 = session end only)", DefaultValue = 0.6, MinValue = 0, Group = "Exit & Risk")]
        public double TpAtr { get; set; }

        [Parameter("Emergency SL Beyond Level (x ATR)", DefaultValue = 0.3, MinValue = 0.01, Group = "Exit & Risk")]
        public double SlAtr { get; set; }

        [Parameter("Sizing", DefaultValue = GfSizing.RiskCash, Group = "Exit & Risk")]
        public GfSizing Sizing { get; set; }

        [Parameter("Fixed Lots", DefaultValue = 1.0, MinValue = 0.01, Group = "Exit & Risk")]
        public double Lots { get; set; }

        [Parameter("Risk per Entry ($, to emergency SL)", DefaultValue = 200.0, MinValue = 1, Group = "Exit & Risk")]
        public double RiskCash { get; set; }

        // ── Bezpiecznik
        [Parameter("Max Daily Loss ($, 0 = off)", DefaultValue = 1000.0, MinValue = 0, Group = "Equity Guard")]
        public double MaxDailyLoss { get; set; }

        [Parameter("Day Reset Time Zone (Windows ID)", DefaultValue = "Central European Standard Time", Group = "Equity Guard")]
        public string DayResetTz { get; set; }

        private TimeZoneInfo _sessTz, _dayTz;
        private TimeSpan _open, _close;

        // stan sesji
        private DateTime _sessDay = DateTime.MinValue;
        private double _sessHi, _sessLo, _sessLast = double.NaN, _prevClose = double.NaN;
        private readonly List<double> _ranges = new List<double>();
        private bool _active, _done;
        private double _L, _A;
        private int _gapDir, _nEnt;

        // bezpiecznik
        private DateTime _guardDay;
        private double _dayStartBal;
        private bool _blocked;

        // statystyka
        private int sDays, sGapDays, sEntries, sFlips, sTp, sSl, sEod, sGuard;

        protected override void OnStart()
        {
            _open = ParseTime(SessOpenStr, new TimeSpan(9, 0, 0));
            _close = ParseTime(SessCloseStr, new TimeSpan(17, 30, 0));
            _sessTz = FindTz(SessTzId);
            _dayTz = FindTz(DayResetTz);
            _guardDay = ToTz(Server.Time, _dayTz).Date;
            _dayStartBal = Account.Balance;

            Positions.Closed += OnClosed;

            // rozgrzewka na historii: ATR sesji i zamknięcie poprzedniej sesji, bez handlu
            for (int j = 1; j < Bars.Count; j++) Step(j, false);

            Print("Gap Flip: start. Sesja {0}–{1} ({2}), luka {3}–{4} ATR, bufor {5} ATR, TP {6} ATR, maks. {7} wejść/dzień. Historia ATR: {8}/{9} sesji",
                _open, _close, _sessTz.Id, MinGap, MaxGap > 0 ? MaxGap.ToString(CultureInfo.InvariantCulture) : "∞", BufAtr,
                TpAtr > 0 ? TpAtr.ToString(CultureInfo.InvariantCulture) : "koniec sesji", MaxEntries, Math.Min(_ranges.Count, AtrDays), AtrDays);
        }

        protected override void OnStop()
        {
            Print("Gap Flip: sesji {0}, z luką w filtrze {1}, wejść {2} (odwróceń {3}), TP {4}, awaryjny SL {5}, koniec sesji {6}, bezpiecznik {7}",
                sDays, sGapDays, sEntries, sFlips, sTp, sSl, sEod, sGuard);
        }

        protected override void OnTick()
        {
            if (MaxDailyLoss <= 0) return;
            var d = ToTz(Server.Time, _dayTz).Date;
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
            Step(Bars.Count - 1, true);
        }

        // j = świeca, która właśnie się otworzyła; j−1 = ostatnia zamknięta
        private void Step(int j, bool live)
        {
            int c = j - 1;
            var tc = ToTz(Bars.OpenTimes[c], _sessTz);
            var tn = ToTz(Bars.OpenTimes[j], _sessTz);
            bool closedInSess = InSession(tc) && tc.Date == _sessDay;

            if (closedInSess)
            {
                _sessHi = Math.Max(_sessHi, Bars.HighPrices[c]);
                _sessLo = Math.Min(_sessLo, Bars.LowPrices[c]);
                _sessLast = Bars.ClosePrices[c];
            }

            bool nextInSess = InSession(tn);
            bool newSession = nextInSess && tn.Date != _sessDay;

            // koniec sesji (albo nowa sesja bez wcześniejszego zamknięcia) – zamknij pozycje
            if (live && (!nextInSess || newSession) && MyPositions().Length > 0)
            {
                sEod++;
                CloseAll("koniec sesji");
            }

            if (newSession)
            {
                if (_sessDay != DateTime.MinValue && _sessHi > _sessLo)
                {
                    _ranges.Add(_sessHi - _sessLo);
                    _prevClose = _sessLast;
                }
                _sessDay = tn.Date;
                _sessHi = double.MinValue;
                _sessLo = double.MaxValue;
                SetupDay(j, live);
            }

            if (!live || !_active || _done || _blocked || !closedInSess || !nextInSess || newSession) return;

            double close = Bars.ClosePrices[c];
            double buf = BufAtr * _A;
            int side = close > _L + buf ? 1 : close < _L - buf ? -1 : 0;
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
            else if (cur != 0 && LogOn)
                Print("Limit {0} wejść – pozycja zamknięta, bez odwrócenia", MaxEntries);
        }

        private void SetupDay(int j, bool live)
        {
            _active = false;
            _done = false;
            _nEnt = 0;
            if (!live) return;
            sDays++;
            if (_ranges.Count < AtrDays || double.IsNaN(_prevClose))
            {
                if (LogOn) Print("{0:yyyy-MM-dd}: za mało historii do ATR ({1}/{2} sesji)", _sessDay, _ranges.Count, AtrDays);
                return;
            }
            _A = _ranges.Skip(_ranges.Count - AtrDays).Average();
            _L = _prevClose;
            double gap = Bars.OpenPrices[j] - _L;
            _gapDir = gap > 0 ? 1 : -1;
            double g = Math.Abs(gap) / _A;
            if (g < MinGap || (MaxGap > 0 && g > MaxGap))
            {
                if (LogOn) Print("{0:yyyy-MM-dd}: luka {1:F1} pkt = {2:F2} ATR – poza filtrem, bez gry", _sessDay, gap, g);
                return;
            }
            _active = true;
            sGapDays++;
            if (LogOn) Print("{0:yyyy-MM-dd}: luka {1} {2:F1} pkt ({3:F2} ATR), poziom {4}, ATR {5:F1} pkt",
                _sessDay, _gapDir > 0 ? "w górę" : "w dół", gap, g, _L, _A);
            if (DrawOn) DrawLevel(j);
            if (WithGap && !_blocked) Enter(_gapDir, "wejście z luką na otwarciu");
        }

        private void Enter(int side, string why)
        {
            var type = side == 1 ? TradeType.Buy : TradeType.Sell;
            double px = side == 1 ? Symbol.Ask : Symbol.Bid;
            double slLevel = _L - side * (BufAtr * _A + SlAtr * _A);
            double slDist = side * (px - slLevel);
            if (slDist <= Symbol.Spread * 2)
            {
                slLevel = px - side * Math.Max(SlAtr * _A, Symbol.Spread * 3);
                slDist = side * (px - slLevel);
            }
            double slPips = slDist / Symbol.PipSize;
            double tpPips = TpAtr > 0 ? TpAtr * _A / Symbol.PipSize : 0;

            double units = Sizing == GfSizing.FixedLots
                ? Symbol.QuantityToVolumeInUnits(Lots)
                : RiskCash / (slPips * Symbol.PipValue);
            units = Math.Min(Symbol.NormalizeVolumeInUnits(units, RoundingMode.Down), Symbol.VolumeInUnitsMax);
            if (units < Symbol.VolumeInUnitsMin)
            {
                if (LogOn) Print("Za mały wolumen ({0}) – wejście pominięte", units);
                return;
            }

            _nEnt++;
            var r = ExecuteMarketOrder(type, SymbolName, units, Label, slPips, tpPips > 0 ? tpPips : (double?)null);
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
            if (LogOn) Print("{0} #{1}: {2} {3} @ {4}, SL {5:F1}, TP {6}, strata do SL {7:F0} $",
                type == TradeType.Buy ? "LONG" : "SHORT", _nEnt, why, Symbol.VolumeInUnitsToQuantity(units), r.Position.EntryPrice,
                r.Position.StopLoss, r.Position.TakeProfit.HasValue ? r.Position.TakeProfit.Value.ToString("F1") : "–",
                slPips * Symbol.PipValue * units);
        }

        private void OnClosed(PositionClosedEventArgs a)
        {
            if (a.Position.Label != Label || a.Position.SymbolName != SymbolName) return;
            if (a.Reason == PositionCloseReason.TakeProfit)
            {
                _done = true;
                sTp++;
                if (LogOn) Print("TP: {0:F0} $ – ruch poszedł, koniec gry na dziś", a.Position.NetProfit);
            }
            else if (a.Reason == PositionCloseReason.StopLoss)
            {
                sSl++;
                if (LogOn) Print("Awaryjny SL: {0:F0} $", a.Position.NetProfit);
            }
        }

        private void DrawLevel(int j)
        {
            var t1 = Bars.OpenTimes[j];
            var t2 = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(_sessDay + _close, DateTimeKind.Unspecified), _sessTz);
            string k = _sessDay.ToString("yyyyMMdd");
            Chart.DrawTrendLine("gf_L_" + k, t1, _L, t2, _L, Color.Orange, 2, LineStyle.Solid);
            if (BufAtr > 0)
            {
                double b = BufAtr * _A;
                Chart.DrawTrendLine("gf_U_" + k, t1, _L + b, t2, _L + b, Color.Orange, 1, LineStyle.Dots);
                Chart.DrawTrendLine("gf_D_" + k, t1, _L - b, t2, _L - b, Color.Orange, 1, LineStyle.Dots);
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

        private bool InSession(DateTime local)
        {
            if (local.DayOfWeek == DayOfWeek.Saturday || local.DayOfWeek == DayOfWeek.Sunday) return false;
            var t = local.TimeOfDay;
            return t >= _open && t < _close;
        }

        private static DateTime ToTz(DateTime utc, TimeZoneInfo tz)
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(utc, DateTimeKind.Utc), tz);
        }

        private TimeZoneInfo FindTz(string id)
        {
            try { return TimeZoneInfo.FindSystemTimeZoneById(id); }
            catch (Exception)
            {
                Print("Nie znaleziono strefy '{0}', używam UTC", id);
                return TimeZoneInfo.Utc;
            }
        }

        private static TimeSpan ParseTime(string s, TimeSpan def)
        {
            TimeSpan t;
            return TimeSpan.TryParseExact((s ?? "").Trim(), @"hh\:mm", CultureInfo.InvariantCulture, out t) ? t : def;
        }
    }
}
