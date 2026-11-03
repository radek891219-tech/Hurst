// HTS Trend Start – cBot (strategia) dla cTradera
//  START: wszystkie włączone warunki spełnione na zamkniętej świecy → wejście po rynku:
//   1. wolna wstęga (144) już opada / rośnie,
//   2. szybka wstęga (33) cała pod / nad wolną – świeże przecięcie,
//   3. szybka wstęga mocno opada / rośnie,
//   4. zamknięcie za szybką wstęgą,
//   5. wcześniej odbicie od wolnej wstęgi (niższy szczyt / wyższy dołek),
//   6. odstęp między wstęgami rośnie,
//   7. (opcja) trend HTS na wyższym interwale zgodny.
//  SL za ekstremum ostatnich N świec + zapas, min. ryzyko w ATR, wielkość pozycji z ryzyka w $.
//  Dokładki: cofnięcie do szybkiej wstęgi i odrzucenie, tylko gdy START jest otwarty i ma już ≥ X R zysku.
//  Wyjście wg parametru Management:
//   Base – stały SL, wyjście na końcu wzorca,
//   BreakEven – SL na wejście po zysku BE R,
//   PartialTpBE – zamknięcie części przy TP R, potem SL na wejście,
//   TrailingFastBand – po zysku X R SL idzie za szybką wstęgą,
//   TimeStopBE – po N świecach wyjście, jeśli zysk max < X R; plus BE,
//   AllCombined – wszystko razem.
//  Koniec wzorca (N zamknięć za szybką wstęgą albo zamknięcie za wolną) zamyka wszystkie pozycje.
//  Okno handlu (UTC), zamykanie w piątek, ochrona FTMO. Bez rysowania na wykresie.
using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum TsbDirection { Both, ShortOnly, LongOnly }
    public enum TsbManagement { Base, BreakEven, PartialTpBE, TrailingFastBand, TimeStopBE, AllCombined }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HTS_TrendStart_Bot : Robot
    {
        [Parameter("Bot Label", DefaultValue = "HTS_START", Group = "System")]
        public string BotLabel { get; set; }

        [Parameter("Log signals", DefaultValue = true, Group = "System")]
        public bool LogSignals { get; set; }

        // ───────── WSTĘGI ─────────
        [Parameter("Fast Band", DefaultValue = 33, MinValue = 2, Group = "HTS Bands")]
        public int FastLen { get; set; }

        [Parameter("Slow Band", DefaultValue = 144, MinValue = 2, Group = "HTS Bands")]
        public int SlowLen { get; set; }

        [Parameter("MA Type", DefaultValue = MovingAverageType.WilderSmoothing, Group = "HTS Bands")]
        public MovingAverageType MaType { get; set; }

        [Parameter("ATR Period", DefaultValue = 100, MinValue = 1, Group = "HTS Bands")]
        public int AtrLen { get; set; }

        // ───────── WARUNKI ─────────
        [Parameter("C1 Slow Band Slope On", DefaultValue = true, Group = "Start Conditions")]
        public bool Use1 { get; set; }

        [Parameter("C1 Slope Lookback (bars)", DefaultValue = 30, MinValue = 1, Group = "Start Conditions")]
        public int S1Len { get; set; }

        [Parameter("C1 Min Change (x ATR)", DefaultValue = 0.15, MinValue = 0, Group = "Start Conditions")]
        public double S1Min { get; set; }

        [Parameter("C2 Fresh Cross On", DefaultValue = true, Group = "Start Conditions")]
        public bool Use2 { get; set; }

        [Parameter("C2 Max Bars Since Cross", DefaultValue = 40, MinValue = 1, Group = "Start Conditions")]
        public int S2Len { get; set; }

        [Parameter("C3 Fast Band Slope On", DefaultValue = true, Group = "Start Conditions")]
        public bool Use3 { get; set; }

        [Parameter("C3 Slope Lookback (bars)", DefaultValue = 10, MinValue = 1, Group = "Start Conditions")]
        public int S3Len { get; set; }

        [Parameter("C3 Min Change (x ATR)", DefaultValue = 0.4, MinValue = 0, Group = "Start Conditions")]
        public double S3Min { get; set; }

        [Parameter("C4 Close Beyond Fast Band On", DefaultValue = true, Group = "Start Conditions")]
        public bool Use4 { get; set; }

        [Parameter("C5 Slow Band Rejection On", DefaultValue = true, Group = "Start Conditions")]
        public bool Use5 { get; set; }

        [Parameter("C5 Touch Within (bars)", DefaultValue = 60, MinValue = 1, Group = "Start Conditions")]
        public int S5Len { get; set; }

        [Parameter("C5 No Close Beyond Slow (bars)", DefaultValue = 20, MinValue = 1, Group = "Start Conditions")]
        public int S5Stay { get; set; }

        [Parameter("C6 Gap Growing On", DefaultValue = true, Group = "Start Conditions")]
        public bool Use6 { get; set; }

        [Parameter("C6 Compare Gap (bars back)", DefaultValue = 5, MinValue = 1, Group = "Start Conditions")]
        public int S6Len { get; set; }

        [Parameter("C7 Higher TF Trend On", DefaultValue = false, Group = "Start Conditions")]
        public bool Use7 { get; set; }

        [Parameter("C7 Higher TF", DefaultValue = "Hour", Group = "Start Conditions")]
        public TimeFrame HtfTf { get; set; }

        [Parameter("Direction", DefaultValue = TsbDirection.Both, Group = "Start Conditions")]
        public TsbDirection Direction { get; set; }

        // ───────── WZORZEC I DOKŁADKI ─────────
        [Parameter("End: Closes Beyond Fast Band", DefaultValue = 3, MinValue = 1, Group = "Pattern And Add-ons")]
        public int EndBars { get; set; }

        [Parameter("Trade Add-ons", DefaultValue = true, Group = "Pattern And Add-ons")]
        public bool TradePb { get; set; }

        [Parameter("Add-on Min Bars Since Last Entry", DefaultValue = 10, MinValue = 1, Group = "Pattern And Add-ons")]
        public int PbCooldown { get; set; }

        [Parameter("Add-on Max Per Pattern", DefaultValue = 4, MinValue = 0, Group = "Pattern And Add-ons")]
        public int PbMax { get; set; }

        [Parameter("Add-on Only If START Profit (R, 0 = always)", DefaultValue = 1.0, MinValue = 0, Group = "Pattern And Add-ons")]
        public double PbNeedR { get; set; }

        // ───────── RYZYKO ─────────
        [Parameter("Risk per Entry ($)", DefaultValue = 250.0, MinValue = 1.0, Group = "Risk")]
        public double RiskCash { get; set; }

        [Parameter("START SL Lookback (bars)", DefaultValue = 20, MinValue = 1, Group = "Risk")]
        public int SlLen { get; set; }

        [Parameter("Add-on SL Lookback (bars)", DefaultValue = 5, MinValue = 1, Group = "Risk")]
        public int PbSlLen { get; set; }

        [Parameter("SL Buffer (x ATR)", DefaultValue = 0.2, MinValue = 0, Group = "Risk")]
        public double SlBuf { get; set; }

        [Parameter("Min Risk (x ATR)", DefaultValue = 0.5, MinValue = 0.05, Group = "Risk")]
        public double MinRisk { get; set; }

        [Parameter("Max SL (pips, 0 = off)", DefaultValue = 0.0, MinValue = 0, Group = "Risk")]
        public double MaxSlPips { get; set; }

        // ───────── ZARZĄDZANIE ─────────
        [Parameter("Management", DefaultValue = TsbManagement.AllCombined, Group = "Management")]
        public TsbManagement Management { get; set; }

        [Parameter("BE After Profit (R)", DefaultValue = 1.0, MinValue = 0.1, Group = "Management")]
        public double BeR { get; set; }

        [Parameter("Partial TP At (R)", DefaultValue = 2.0, MinValue = 0.1, Group = "Management")]
        public double TpR { get; set; }

        [Parameter("Partial TP Close (%)", DefaultValue = 50.0, MinValue = 1, MaxValue = 99, Group = "Management")]
        public double TpPct { get; set; }

        [Parameter("Trailing Start (R)", DefaultValue = 1.0, MinValue = 0, Group = "Management")]
        public double TrStartR { get; set; }

        [Parameter("Trailing Buffer Beyond Fast Band (x ATR)", DefaultValue = 0.3, MinValue = 0, Group = "Management")]
        public double TrBuf { get; set; }

        [Parameter("Time Stop After (bars)", DefaultValue = 15, MinValue = 1, Group = "Management")]
        public int TsBars { get; set; }

        [Parameter("Time Stop If Max Profit Below (R)", DefaultValue = 0.5, MinValue = 0, Group = "Management")]
        public double TsR { get; set; }

        // ───────── OKNO ─────────
        [Parameter("Start Hour (UTC)", DefaultValue = 7, MinValue = 0, MaxValue = 23, Group = "Trading Window")]
        public int StartHour { get; set; }

        [Parameter("End Hour (UTC)", DefaultValue = 15, MinValue = 0, MaxValue = 23, Group = "Trading Window")]
        public int EndHour { get; set; }

        [Parameter("Close EOD Everyday", DefaultValue = false, Group = "Trading Window")]
        public bool CloseEodEveryday { get; set; }

        [Parameter("Close EOD on Friday", DefaultValue = true, Group = "Trading Window")]
        public bool CloseEodFriday { get; set; }

        // ───────── FTMO ─────────
        [Parameter("Enable FTMO Protection", DefaultValue = true, Group = "FTMO Protection")]
        public bool FtmoProtection { get; set; }

        [Parameter("Max Daily Loss ($, 0 = off)", DefaultValue = 2000.0, MinValue = 0, Group = "FTMO Protection")]
        public double MaxDailyLossCash { get; set; }

        [Parameter("Max Trades per Day (0 = off)", DefaultValue = 8, MinValue = 0, Group = "FTMO Protection")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Max Consecutive Losses per Day (0 = off)", DefaultValue = 3, MinValue = 0, Group = "FTMO Protection")]
        public int MaxConsecLosses { get; set; }

        [Parameter("Day Reset Time Zone (Windows ID)", DefaultValue = "Central European Standard Time", Group = "FTMO Protection")]
        public string DayResetTimeZone { get; set; }

        // ───────── stan ─────────
        private class Info
        {
            public double E;
            public double Rk;
            public double Mfe;
            public int Bar;
            public bool Start;
            public bool TpDone;
        }

        private MovingAverage _fh, _fl, _sh, _sl, _hfh, _hfl, _hsh, _hsl;
        private AverageTrueRange _atr;
        private Bars _htf;
        private int _warm;

        private int _dir, _pStart, _endCnt, _pbCnt, _lastEnt;
        private double _pEntry, _pRisk, _pExt;
        private bool _patTraded;
        private readonly Dictionary<int, Info> _info = new Dictionary<int, Info>();

        private TimeZoneInfo _dayTz;
        private DateTime _ftmoDay;
        private double _dayStartBalance;
        private int _tradesToday;
        private int _consecLosses;
        private bool _dayBlocked;

        private int nSignals, nOpened;

        private bool HasBe { get { int v = (int)Management; return v == 1 || v == 4 || v == 5; } }
        private bool HasTp { get { int v = (int)Management; return v == 2 || v == 5; } }
        private bool HasTr { get { int v = (int)Management; return v == 3 || v == 5; } }
        private bool HasTs { get { int v = (int)Management; return v == 4 || v == 5; } }

        protected override void OnStart()
        {
            _fh  = Indicators.MovingAverage(Bars.HighPrices, FastLen, MaType);
            _fl  = Indicators.MovingAverage(Bars.LowPrices, FastLen, MaType);
            _sh  = Indicators.MovingAverage(Bars.HighPrices, SlowLen, MaType);
            _sl  = Indicators.MovingAverage(Bars.LowPrices, SlowLen, MaType);
            _atr = Indicators.AverageTrueRange(AtrLen, MovingAverageType.WilderSmoothing);
            if (Use7)
            {
                _htf = MarketData.GetBars(HtfTf);
                _hfh = Indicators.MovingAverage(_htf.HighPrices, FastLen, MaType);
                _hfl = Indicators.MovingAverage(_htf.LowPrices, FastLen, MaType);
                _hsh = Indicators.MovingAverage(_htf.HighPrices, SlowLen, MaType);
                _hsl = Indicators.MovingAverage(_htf.LowPrices, SlowLen, MaType);
            }
            _warm = Math.Max(SlowLen, AtrLen) + Math.Max(Math.Max(S1Len, S5Len), Math.Max(S2Len, SlLen)) + 5;

            try { _dayTz = TimeZoneInfo.FindSystemTimeZoneById(DayResetTimeZone); }
            catch (Exception) { _dayTz = TimeZoneInfo.Utc; }
            StartDay(LocalNow().Date);
            Positions.Closed += OnPositionClosed;

            foreach (var p in MyPositions())
            {
                if (!p.StopLoss.HasValue) continue;
                var t = new Info();
                t.E = p.EntryPrice;
                t.Rk = Math.Abs(p.EntryPrice - p.StopLoss.Value);
                t.Bar = Bars.Count - 2;
                t.Start = p.Comment == "START";
                _info[p.Id] = t;
            }

            Print("HTS Trend Start: start. Świec historii: {0}, potrzebne na rozgrzewkę: {1}. Okno handlu {2}:00–{3}:00 UTC, zarządzanie: {4}",
                Bars.Count, _warm, StartHour, EndHour, Management);
        }

        protected override void OnStop()
        {
            Print("HTS Trend Start: sygnałów START {0}, otwartych pozycji {1}", nSignals, nOpened);
        }

        // ───────── tick: FTMO, EOD, częściowe TP i BE ─────────
        protected override void OnTick()
        {
            CheckFtmo();

            var pos = MyPositions();
            if (pos.Length == 0) return;

            if (!IsTradeTime())
            {
                bool friday = Server.Time.DayOfWeek == DayOfWeek.Friday;
                bool afterEnd = StartHour < EndHour ? Server.Time.Hour >= EndHour : true;
                if (CloseEodEveryday || (CloseEodFriday && friday && afterEnd))
                {
                    foreach (var p in pos) ClosePosition(p);
                    return;
                }
            }

            foreach (var p in pos)
            {
                Info t;
                if (!_info.TryGetValue(p.Id, out t) || t.Rk <= 0) continue;
                int d = p.TradeType == TradeType.Buy ? 1 : -1;
                double px = d == 1 ? Symbol.Bid : Symbol.Ask;
                t.Mfe = Math.Max(t.Mfe, d * (px - t.E) / t.Rk);

                if (HasTp && !t.TpDone && t.Mfe >= TpR)
                {
                    t.TpDone = true;
                    double part = Symbol.NormalizeVolumeInUnits(p.VolumeInUnits * TpPct / 100, RoundingMode.Down);
                    if (part >= Symbol.VolumeInUnitsMin && p.VolumeInUnits - part >= Symbol.VolumeInUnitsMin)
                        ClosePosition(p, part);
                    MoveSl(p, t.E, d);
                }
                if (HasBe && t.Mfe >= BeR)
                    MoveSl(p, t.E, d);
            }
        }

        // ───────── zamknięta świeca: wzorzec, wejścia, trailing, stop czasowy, koniec ─────────
        protected override void OnBar()
        {
            int i = Bars.Count - 2;
            if (i < _warm) return;
            double atr = _atr.Result[i];
            if (double.IsNaN(atr) || atr <= 0) return;

            if (_dir == 0 && MyPositions().Length > 0) ManageOnBar(i, atr);

            if (_dir != 0)
            {
                int d = _dir;
                _pExt = d == -1 ? Math.Min(_pExt, L(i)) : Math.Max(_pExt, H(i));
                bool against = d == -1 ? C(i) > _fh.Result[i] : C(i) < _fl.Result[i];
                bool hard    = d == -1 ? C(i) > _sh.Result[i] : C(i) < _sl.Result[i];
                _endCnt = against ? _endCnt + 1 : 0;

                if (_endCnt >= EndBars || hard)
                {
                    foreach (var p in MyPositions()) ClosePosition(p);
                    if (LogSignals) Print("Koniec wzorca: zasięg {0:F1} ATR, {1} świec", d * (_pExt - _pEntry) / atr, i - _pStart);
                    _dir = 0;
                    return;
                }

                ManageOnBar(i, atr);

                if (TradePb && _patTraded && StartIsOpen() && _pbCnt < PbMax && i - _lastEnt >= PbCooldown
                    && (PbNeedR == 0 || d * (_pExt - _pEntry) / _pRisk >= PbNeedR))
                {
                    bool pb = d == -1
                        ? H(i) >= _fl.Result[i] && C(i) < FM(i) && C(i) < O(i) && FM(i) < FM(i - S3Len)
                        : L(i) <= _fh.Result[i] && C(i) > FM(i) && C(i) > O(i) && FM(i) > FM(i - S3Len);
                    if (pb)
                    {
                        _pbCnt++;
                        _lastEnt = i;
                        if (IsTradeTime() && FtmoAllowsNewTrade())
                            OpenEntry(d, SlLevel(i, d, PbSlLen), false, i);
                    }
                }
                return;
            }

            bool okS = Direction != TsbDirection.LongOnly;
            bool okL = Direction != TsbDirection.ShortOnly;
            bool allS = okS && AllConds(i, -1);
            bool allL = !allS && okL && AllConds(i, 1);
            if (!allS && !allL) return;

            int dir = allS ? -1 : 1;
            double slLevel = SlLevel(i, dir, SlLen);
            _dir = dir;
            _pStart = i;
            _pEntry = C(i);
            _pRisk = Math.Abs(C(i) - slLevel);
            _pExt = dir == -1 ? L(i) : H(i);
            _endCnt = 0;
            _pbCnt = 0;
            _lastEnt = i;
            nSignals++;

            bool timeOk = IsTradeTime();
            bool ftmoOk = FtmoAllowsNewTrade();
            _patTraded = timeOk && ftmoOk && OpenEntry(dir, slLevel, true, i);
            if (LogSignals && !_patTraded)
                Print("START {0} bez wejścia: {1}", dir == -1 ? "SHORT" : "LONG", !timeOk ? "poza oknem handlu" : !ftmoOk ? "blokada FTMO" : "zlecenie odrzucone");
        }

        private bool StartIsOpen()
        {
            foreach (var p in MyPositions())
            {
                Info t;
                if (_info.TryGetValue(p.Id, out t) && t.Start) return true;
            }
            return false;
        }

        private void ManageOnBar(int i, double atr)
        {
            foreach (var p in MyPositions())
            {
                Info t;
                if (!_info.TryGetValue(p.Id, out t) || t.Rk <= 0) continue;
                int d = p.TradeType == TradeType.Buy ? 1 : -1;
                t.Mfe = Math.Max(t.Mfe, d * ((d == -1 ? L(i) : H(i)) - t.E) / t.Rk);

                if (HasTs && i - t.Bar >= TsBars && t.Mfe < TsR)
                {
                    ClosePosition(p);
                    continue;
                }
                if (HasBe && t.Mfe >= BeR) MoveSl(p, t.E, d);
                if (HasTr && t.Mfe >= TrStartR)
                    MoveSl(p, d == -1 ? _fh.Result[i] + TrBuf * atr : _fl.Result[i] - TrBuf * atr, d);
            }
        }

        // SL przesuwany tylko w stronę zysku; gdy poziom jest już za ceną – wyjście po rynku
        private void MoveSl(Position p, double level, int d)
        {
            if (p.StopLoss.HasValue && (d == -1 ? level >= p.StopLoss.Value : level <= p.StopLoss.Value)) return;
            double px = d == 1 ? Symbol.Bid : Symbol.Ask;
            if (d == -1 ? level <= px : level >= px)
            {
                ClosePosition(p);
                return;
            }
            var r = ModifyPosition(p, level, p.TakeProfit, ProtectionType.Absolute);
            if (!r.IsSuccessful) Print("Nie udało się przesunąć SL: {0}", r.Error);
        }

        private bool OpenEntry(int d, double slLevel, bool isStart, int i)
        {
            var type = d == -1 ? TradeType.Sell : TradeType.Buy;
            double atr = _atr.Result[i];
            double entry = d == -1 ? Symbol.Bid : Symbol.Ask;
            double dist = Math.Max(d * (entry - slLevel), MinRisk * atr);
            double slPips = dist / Symbol.PipSize;
            if (MaxSlPips > 0 && slPips > MaxSlPips)
            {
                Print("Pominięte: SL {0:F1} pips > max {1}", slPips, MaxSlPips);
                return false;
            }

            double units = Symbol.NormalizeVolumeInUnits(RiskCash / (slPips * Symbol.PipValue), RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin)
            {
                Print("Pominięte: za mały wolumen dla ryzyka {0} $ (SL {1:F1} pips)", RiskCash, slPips);
                return false;
            }
            units = Math.Min(units, Symbol.VolumeInUnitsMax);

            string comment = isStart ? "START" : "PB";
            var res = ExecuteMarketOrder(type, SymbolName, units, BotLabel, slPips, null, comment);
            if (!res.IsSuccessful)
            {
                Print("Błąd otwarcia: {0}", res.Error);
                return false;
            }
            var pos = res.Position;
            if (!pos.StopLoss.HasValue)
            {
                Print("Pozycja bez SL – zamykam");
                ClosePosition(pos);
                return false;
            }
            var t = new Info();
            t.E = pos.EntryPrice;
            t.Rk = Math.Abs(pos.EntryPrice - pos.StopLoss.Value);
            t.Bar = i;
            t.Start = isStart;
            _info[pos.Id] = t;
            _tradesToday++;
            nOpened++;
            if (LogSignals) Print("{0} {1} @ {2}, SL {3:F1} pips, {4} units", comment, type, pos.EntryPrice, slPips, units);
            return true;
        }

        // ───────── warunki ─────────
        private double H(int i) { return Bars.HighPrices[i]; }
        private double L(int i) { return Bars.LowPrices[i]; }
        private double C(int i) { return Bars.ClosePrices[i]; }
        private double O(int i) { return Bars.OpenPrices[i]; }
        private double FM(int i) { return (_fh.Result[i] + _fl.Result[i]) / 2; }
        private double SM(int i) { return (_sh.Result[i] + _sl.Result[i]) / 2; }
        private bool Trend(int i, int d) { return d == -1 ? _fh.Result[i] < _sl.Result[i] : _fl.Result[i] > _sh.Result[i]; }
        private double Gap(int i, int d) { return d == -1 ? _sl.Result[i] - _fh.Result[i] : _fl.Result[i] - _sh.Result[i]; }

        private bool AllConds(int i, int d)
        {
            double atr = _atr.Result[i];
            int used = 0;
            if (Use1)
            {
                used++;
                if (d * (SM(i) - SM(i - S1Len)) < S1Min * atr) return false;
            }
            if (Use2)
            {
                used++;
                if (!Trend(i, d)) return false;
                bool fresh = false;
                for (int m = 0; m <= S2Len && i - m - 1 >= 0; m++)
                {
                    if (!Trend(i - m - 1, d)) { fresh = true; break; }
                }
                if (!fresh) return false;
            }
            if (Use3)
            {
                used++;
                if (d * (FM(i) - FM(i - S3Len)) < S3Min * atr) return false;
            }
            if (Use4)
            {
                used++;
                if (d == -1 ? C(i) >= _fl.Result[i] : C(i) <= _fh.Result[i]) return false;
            }
            if (Use5)
            {
                used++;
                bool touched = false;
                for (int k = 0; k <= S5Len && i - k >= 0; k++)
                {
                    if (d == -1 ? H(i - k) >= _sl.Result[i - k] : L(i - k) <= _sh.Result[i - k]) { touched = true; break; }
                }
                if (!touched) return false;
                for (int k = 0; k < S5Stay && i - k >= 0; k++)
                {
                    if (d == -1 ? C(i - k) > _sh.Result[i - k] : C(i - k) < _sl.Result[i - k]) return false;
                }
            }
            if (Use6)
            {
                used++;
                if (Gap(i, d) <= Gap(i - S6Len, d)) return false;
            }
            if (Use7)
            {
                used++;
                if (!HtfTrend(i, d)) return false;
            }
            return used > 0;
        }

        private bool HtfTrend(int i, int d)
        {
            if (_htf == null) return false;
            int h = _htf.OpenTimes.GetIndexByTime(Bars.OpenTimes[i]) - 1;   // ostatnia zamknięta świeca wyższego TF
            if (h < SlowLen) return false;
            return d == -1 ? _hfh.Result[h] < _hsl.Result[h] : _hfl.Result[h] > _hsh.Result[h];
        }

        private double SlLevel(int i, int d, int len)
        {
            double atr = _atr.Result[i];
            double hi = double.MinValue, lo = double.MaxValue;
            for (int k = 0; k < len && i - k >= 0; k++)
            {
                hi = Math.Max(hi, H(i - k));
                lo = Math.Min(lo, L(i - k));
            }
            double raw = d == -1 ? hi + SlBuf * atr : lo - SlBuf * atr;
            double r = Math.Max(Math.Abs(raw - C(i)), MinRisk * atr);
            return C(i) - d * r;
        }

        private Position[] MyPositions()
        {
            return Positions.Where(p => p.SymbolName == SymbolName && p.Label == BotLabel).ToArray();
        }

        private bool IsTradeTime()
        {
            int h = Server.Time.Hour;
            if (StartHour == EndHour) return true;
            return StartHour < EndHour ? h >= StartHour && h < EndHour : h >= StartHour || h < EndHour;
        }

        // ───────── FTMO PROTECTION ─────────
        private DateTime LocalNow()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(Server.Time, DateTimeKind.Utc), _dayTz);
        }

        private void StartDay(DateTime day)
        {
            _ftmoDay = day;
            _dayStartBalance = Account.Balance;
            _tradesToday = 0;
            _consecLosses = 0;
            _dayBlocked = false;
        }

        private void CheckFtmo()
        {
            if (!FtmoProtection) return;
            var today = LocalNow().Date;
            if (today != _ftmoDay) StartDay(today);

            double dayPnl = Account.Equity - _dayStartBalance;
            if (!_dayBlocked && MaxDailyLossCash > 0 && dayPnl <= -MaxDailyLossCash)
            {
                Block("daily loss " + dayPnl.ToString("F0") + " $");
                foreach (var p in MyPositions()) ClosePosition(p);
            }
        }

        private bool FtmoAllowsNewTrade()
        {
            if (!FtmoProtection) return true;
            if (_dayBlocked) return false;
            if (MaxTradesPerDay > 0 && _tradesToday >= MaxTradesPerDay)
            {
                Block(_tradesToday + " trades today");
                return false;
            }
            return true;
        }

        private void Block(string reason)
        {
            _dayBlocked = true;
            Print("FTMO PROTECTION – entries blocked until next day: {0}", reason);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p.SymbolName != SymbolName || p.Label != BotLabel) return;
            _info.Remove(p.Id);
            _consecLosses = p.NetProfit < 0 ? _consecLosses + 1 : 0;
            if (FtmoProtection && !_dayBlocked && MaxConsecLosses > 0 && _consecLosses >= MaxConsecLosses)
                Block(_consecLosses + " losses in a row");
        }
    }
}
