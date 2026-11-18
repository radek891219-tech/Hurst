// Gap Flip – odwracanie pozycji wokół poziomu luki (cBot / strategia dla cTradera)
//  Zasady:
//   • poziom luki L = zamknięcie poprzedniej sesji (ostatnia świeca przed Session Close),
//     luka = otwarcie pierwszej świecy sesji − L (filtr: min/max wielkości luki w ATR),
//   • sesja może przechodzić przez północ (Session Open > Session Close), np. CFD GER40: luka na nocnej przerwie,
//   • luka w górę: świeca zamyka się POD L − bufor → SHORT; potem zamyka się NAD L + bufor → odwrócenie na LONG;
//     znowu pod → SHORT … i tak dalej, aż cena pójdzie w jedną stronę,
//   • luka w dół: lustrzanie (pierwsze wejście LONG po zamknięciu nad L + bufor),
//   • stop = odwrócenie; dodatkowo zawsze awaryjny SL na serwerze (za poziomem, na wypadek szpilki),
//   • wyjście: TP (k × ATR od wejścia) – po TP koniec gry w tej sesji – albo zamknięcie na koniec sesji,
//   • limit wejść na sesję: po ostatnim wejściu sygnał przeciwny tylko zamyka pozycję,
//   • ATR = ATR z wykresu dziennego (ostatnia zamknięta świeca D1) – działa od pierwszego dnia backtestu,
//   • sygnały na ZAMKNIĘTEJ świecy, zlecenie po rynku od razu (≈ otwarcie następnej świecy),
//   • bezpiecznik: dzienny limit straty liczony jak FTMO (equity vs saldo z północy CET).
//  Przy starcie bot wypisuje w Dziennikach ostatnie przerwy w notowaniach (godziny w strefie sesji) –
//  według nich ustaw Session Open (pierwsza świeca po przerwie) i Session Close (koniec ostatniej świecy przed przerwą).
//  Wykres: zalecany m1 lub m5.
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
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
        [Parameter("Session Open (HH:mm, first bar after break)", DefaultValue = "23:00", Group = "Session")]
        public string SessOpenStr { get; set; }

        [Parameter("Session Close (HH:mm, end of last bar before break)", DefaultValue = "22:00", Group = "Session")]
        public string SessCloseStr { get; set; }

        [Parameter("Session Time Zone (Windows ID)", DefaultValue = "Central European Standard Time", Group = "Session")]
        public string SessTzId { get; set; }

        // ── Luka
        [Parameter("ATR Period (daily bars)", DefaultValue = 20, MinValue = 2, Group = "Gap")]
        public int AtrDays { get; set; }

        [Parameter("Min Gap (x ATR)", DefaultValue = 0.1, MinValue = 0, Group = "Gap")]
        public double MinGap { get; set; }

        [Parameter("Max Gap (x ATR, 0 = off)", DefaultValue = 1.5, MinValue = 0, Group = "Gap")]
        public double MaxGap { get; set; }

        [Parameter("Buffer Around Level (x ATR)", DefaultValue = 0.05, MinValue = 0, Group = "Gap")]
        public double BufAtr { get; set; }

        [Parameter("First Entry With Gap at Open", DefaultValue = false, Group = "Gap")]
        public bool WithGap { get; set; }

        [Parameter("Max Entries per Session", DefaultValue = 3, MinValue = 1, MaxValue = 50, Group = "Gap")]
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
        private bool _wrap;                 // sesja przechodzi przez północ
        private TimeSpan _step = TimeSpan.FromMinutes(1);   // długość świecy
        private Bars _d1;
        private AverageTrueRange _atr;

        // stan sesji
        private DateTime _sessDay = DateTime.MinValue;   // data otwarcia sesji
        private double _sessLast = double.NaN, _prevClose = double.NaN;
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
            _open = ParseTime(SessOpenStr, new TimeSpan(23, 0, 0));
            _close = ParseTime(SessCloseStr, new TimeSpan(22, 0, 0));
            _wrap = _open > _close;
            _sessTz = FindTz(SessTzId);
            _dayTz = FindTz(DayResetTz);
            _guardDay = ToTz(Server.Time, _dayTz).Date;
            _dayStartBal = Account.Balance;

            _d1 = MarketData.GetBars(TimeFrame.Daily);
            _atr = Indicators.AverageTrueRange(_d1, AtrDays, MovingAverageType.Simple);

            Positions.Closed += OnClosed;

            Print("Gap Flip: start. Sesja {0:hh\\:mm}–{1:hh\\:mm}{2} ({3}), luka {4}–{5} ATR D1, bufor {6} ATR, TP {7}, maks. {8} wejść/sesję",
                _open, _close, _wrap ? " (przez północ)" : "", _sessTz.Id, MinGap,
                MaxGap > 0 ? MaxGap.ToString(CultureInfo.InvariantCulture) : "∞", BufAtr,
                TpAtr > 0 ? TpAtr.ToString(CultureInfo.InvariantCulture) + " ATR" : "koniec sesji", MaxEntries);
            _step = BarStep();
            PrintBreaks();

            // rozgrzewka na historii: zamknięcie poprzedniej sesji, bez handlu
            for (int j = 1; j < Bars.Count; j++) Step(j, false);
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
            bool closedInSess = InSession(tc) && SessKey(tc) == _sessDay;
            if (closedInSess) _sessLast = Bars.ClosePrices[c];

            bool nextInSess = InSession(tn);
            bool newSession = nextInSess && SessKey(tn) != _sessDay;

            // ostatnia świeca sesji (następna byłaby już po Session Close) – zamknij przed przerwą
            bool lastBar = nextInSess && !newSession && ToTz(Bars.OpenTimes[j] + _step, _sessTz) >= SessEnd(_sessDay);

            // koniec sesji (albo nowa sesja bez wcześniejszego zamknięcia) – zamknij pozycje
            if (live && (!nextInSess || newSession || lastBar) && MyPositions().Length > 0)
            {
                sEod++;
                CloseAll("koniec sesji");
            }

            if (newSession)
            {
                if (_sessDay != DateTime.MinValue && !double.IsNaN(_sessLast)) _prevClose = _sessLast;
                _sessDay = SessKey(tn);
                _sessLast = double.NaN;
                SetupDay(j, live);
            }

            if (!live || !_active || _done || _blocked || !closedInSess || !nextInSess || newSession || lastBar) return;

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
            var t = Bars.OpenTimes[j];
            if (double.IsNaN(_prevClose))
            {
                if (LogOn) Print("{0:yyyy-MM-dd HH:mm}: brak zamknięcia poprzedniej sesji – bez gry", ToTz(t, _sessTz));
                return;
            }
            _A = AtrAt(t);
            if (double.IsNaN(_A) || _A <= 0)
            {
                if (LogOn) Print("{0:yyyy-MM-dd HH:mm}: brak ATR z D1 (za mało świec dziennych) – bez gry", ToTz(t, _sessTz));
                return;
            }
            _L = _prevClose;
            double gap = Bars.OpenPrices[j] - _L;
            _gapDir = gap > 0 ? 1 : -1;
            double g = Math.Abs(gap) / _A;
            if (g < MinGap || (MaxGap > 0 && g > MaxGap))
            {
                if (LogOn) Print("{0:yyyy-MM-dd HH:mm}: luka {1:F1} pkt = {2:F2} ATR – poza filtrem {3}–{4}, bez gry",
                    ToTz(t, _sessTz), gap, g, MinGap, MaxGap);
                return;
            }
            _active = true;
            sGapDays++;
            if (LogOn) Print("{0:yyyy-MM-dd HH:mm}: luka {1} {2:F1} pkt ({3:F2} ATR), poziom {4}, ATR D1 {5:F1} pkt",
                ToTz(t, _sessTz), _gapDir > 0 ? "w górę" : "w dół", gap, g, _L, _A);
            if (DrawOn) DrawLevel(j);
            if (WithGap && !_blocked) Enter(_gapDir, "wejście z luką na otwarciu");
        }

        // ATR z ostatniej ZAMKNIĘTEJ świecy dziennej przed czasem t
        private double AtrAt(DateTime t)
        {
            int k = _d1.OpenTimes.GetIndexByTime(t);
            if (k < 0) k = _d1.Count - 1;
            if (_d1.OpenTimes[k] <= t) k--;          // świeca dzienna, w której jest t, jeszcze się nie zamknęła
            if (k < AtrDays) return double.NaN;
            return _atr.Result[k];
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
                if (LogOn) Print("Za mały wolumen ({0}) – wejście pominięte. Zwiększ Risk per Entry albo użyj Fixed Lots", units);
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
            if (LogOn) Print("{0} #{1}: {2} {3} lot @ {4}, SL {5:F1}, TP {6}, strata do SL {7:F0} $",
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
                if (LogOn) Print("TP: {0:F0} $ – ruch poszedł, koniec gry w tej sesji", a.Position.NetProfit);
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
            var t2 = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(SessEnd(_sessDay), DateTimeKind.Unspecified), _sessTz);
            string k = _sessDay.ToString("yyyyMMdd");
            Chart.DrawTrendLine("gf_L_" + k, t1, _L, t2, _L, Color.Orange, 2, LineStyle.Solid);
            if (BufAtr > 0)
            {
                double b = BufAtr * _A;
                Chart.DrawTrendLine("gf_U_" + k, t1, _L + b, t2, _L + b, Color.Orange, 1, LineStyle.Dots);
                Chart.DrawTrendLine("gf_D_" + k, t1, _L - b, t2, _L - b, Color.Orange, 1, LineStyle.Dots);
            }
        }

        // długość świecy = najmniejszy odstęp między świecami
        private TimeSpan BarStep()
        {
            var step = TimeSpan.FromDays(1);
            for (int i = Math.Max(1, Bars.Count - 2000); i < Bars.Count; i++)
            {
                var d = Bars.OpenTimes[i] - Bars.OpenTimes[i - 1];
                if (d > TimeSpan.Zero && d < step) step = d;
            }
            return step < TimeSpan.FromDays(1) ? step : TimeSpan.FromMinutes(1);
        }

        // koniec sesji otwartej w dniu key (czas lokalny strefy sesji)
        private DateTime SessEnd(DateTime key)
        {
            return key + _close + (_wrap ? TimeSpan.FromDays(1) : TimeSpan.Zero);
        }

        // ostatnie przerwy w notowaniach (> 3 świece) – pomoc przy ustawieniu godzin sesji
        private void PrintBreaks()
        {
            if (Bars.Count < 3) return;
            var step = _step;
            var found = new List<string>();
            for (int i = Bars.Count - 1; i > 0 && found.Count < 5; i--)
            {
                var d = Bars.OpenTimes[i] - Bars.OpenTimes[i - 1];
                if (d.TotalMinutes > step.TotalMinutes * 3 && d.TotalHours < 30)
                    found.Add(string.Format("ostatnia świeca przed przerwą kończy się {0:ddd HH:mm}, pierwsza po przerwie {1:ddd HH:mm}",
                        ToTz(Bars.OpenTimes[i - 1] + step, _sessTz), ToTz(Bars.OpenTimes[i], _sessTz)));
            }
            Print("Przerwy w notowaniach (strefa {0}) – ustaw Session Close = koniec przed przerwą, Session Open = pierwsza po przerwie:", _sessTz.Id);
            foreach (var s in found) Print("   " + s);
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
            var t = local.TimeOfDay;
            return _wrap ? (t >= _open || t < _close) : (t >= _open && t < _close);
        }

        // data otwarcia sesji, do której należy dana chwila
        private DateTime SessKey(DateTime local)
        {
            return _wrap && local.TimeOfDay < _close ? local.Date.AddDays(-1) : local.Date;
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
            return TimeSpan.TryParse((s ?? "").Trim(), CultureInfo.InvariantCulture, out t) && t < TimeSpan.FromDays(1) ? t : def;
        }
    }
}
