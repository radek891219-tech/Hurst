// HTS – wczesne wykrywanie trendu – cBot (te same zasady co HTS_TrendStart_Indicator / HTS_TrendStart.pine)
//  • START: wszystkie włączone warunki spełnione na zamkniętej świecy → wejście po rynku na otwarciu następnej,
//    SL za ekstremum ostatnich N świec + zapas (min. ryzyko w ATR), wielkość pozycji z ryzyka w $
//  • dokładki: cofnięcie do szybkiej wstęgi i odrzucenie, tylko gdy START jest otwarty i ma już ≥ X R zysku
//  • wyjście wg wybranego wariantu zarządzania:
//      Base – stały SL, wyjście na końcu wzorca,
//      BreakEven – SL na wejście po zysku BE R,
//      PartialTpBE – zamknięcie części przy TP R, potem SL na wejście,
//      TrailingFastBand – po zysku X R SL idzie za szybką wstęgą,
//      TimeStopBE – po N świecach wyjście, jeśli zysk max < X R; plus BE,
//      AllCombined – wszystko razem
//  • koniec wzorca (N zamknięć za szybką wstęgą albo zamknięcie za wolną) zamyka wszystkie pozycje
//  • okno handlu (UTC), zamykanie w piątek, ochrona FTMO (dzienny limit, transakcje, serie strat)
//  Backtest w cTraderze liczy prawdziwy spread i prowizję – to jest właściwy test, czy wzorzec jest na plusie.
using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum TsDirection { Both, ShortOnly, LongOnly }
    public enum TsManagement { Base, BreakEven, PartialTpBE, TrailingFastBand, TimeStopBE, AllCombined }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HTS_TrendStart_Bot : Robot
    {
        [Parameter("Bot Label", DefaultValue = "HTS_START", Group = "=== SYSTEM ===")]
        public string BotLabel { get; set; }

        // ───────── WSTĘGI ─────────
        [Parameter("Fast Band", DefaultValue = 33, MinValue = 2, Group = "=== HTS BANDS ===")]
        public int FastLen { get; set; }

        [Parameter("Slow Band", DefaultValue = 144, MinValue = 2, Group = "=== HTS BANDS ===")]
        public int SlowLen { get; set; }

        [Parameter("MA Type", DefaultValue = MovingAverageType.WilderSmoothing, Group = "=== HTS BANDS ===")]
        public MovingAverageType MaType { get; set; }

        [Parameter("ATR Period (distance unit)", DefaultValue = 100, MinValue = 1, Group = "=== HTS BANDS ===")]
        public int AtrLen { get; set; }

        // ───────── WARUNKI ─────────
        [Parameter("1. Slow band sloping", DefaultValue = true, Group = "=== START CONDITIONS ===")]
        public bool Use1 { get; set; }

        [Parameter("   slow slope lookback (bars)", DefaultValue = 30, MinValue = 1, Group = "=== START CONDITIONS ===")]
        public int S1Len { get; set; }

        [Parameter("   min slow change (x ATR)", DefaultValue = 0.15, MinValue = 0, Group = "=== START CONDITIONS ===")]
        public double S1Min { get; set; }

        [Parameter("2. Fresh cross: fast fully beyond slow", DefaultValue = true, Group = "=== START CONDITIONS ===")]
        public bool Use2 { get; set; }

        [Parameter("   max bars since cross", DefaultValue = 40, MinValue = 1, Group = "=== START CONDITIONS ===")]
        public int S2Len { get; set; }

        [Parameter("3. Fast band sloping strongly", DefaultValue = true, Group = "=== START CONDITIONS ===")]
        public bool Use3 { get; set; }

        [Parameter("   fast slope lookback (bars)", DefaultValue = 10, MinValue = 1, Group = "=== START CONDITIONS ===")]
        public int S3Len { get; set; }

        [Parameter("   min fast change (x ATR)", DefaultValue = 0.4, MinValue = 0, Group = "=== START CONDITIONS ===")]
        public double S3Min { get; set; }

        [Parameter("4. Close beyond fast band", DefaultValue = true, Group = "=== START CONDITIONS ===")]
        public bool Use4 { get; set; }

        [Parameter("5. Earlier rejection at slow band", DefaultValue = true, Group = "=== START CONDITIONS ===")]
        public bool Use5 { get; set; }

        [Parameter("   slow band touched within (bars)", DefaultValue = 60, MinValue = 1, Group = "=== START CONDITIONS ===")]
        public int S5Len { get; set; }

        [Parameter("   no close beyond slow for (bars)", DefaultValue = 20, MinValue = 1, Group = "=== START CONDITIONS ===")]
        public int S5Stay { get; set; }

        [Parameter("6. Band gap growing", DefaultValue = true, Group = "=== START CONDITIONS ===")]
        public bool Use6 { get; set; }

        [Parameter("   compare gap with (bars back)", DefaultValue = 5, MinValue = 1, Group = "=== START CONDITIONS ===")]
        public int S6Len { get; set; }

        [Parameter("7. Higher TF HTS trend agrees", DefaultValue = false, Group = "=== START CONDITIONS ===")]
        public bool Use7 { get; set; }

        [Parameter("   higher timeframe", DefaultValue = "Hour", Group = "=== START CONDITIONS ===")]
        public TimeFrame HtfTf { get; set; }

        [Parameter("Direction", DefaultValue = TsDirection.Both, Group = "=== START CONDITIONS ===")]
        public TsDirection Direction { get; set; }

        // ───────── WZORZEC I DOKŁADKI ─────────
        [Parameter("End: closes beyond fast band in a row", DefaultValue = 3, MinValue = 1, Group = "=== PATTERN & ADD-ONS ===")]
        public int EndBars { get; set; }

        [Parameter("Trade add-ons (pullback to fast band)", DefaultValue = true, Group = "=== PATTERN & ADD-ONS ===")]
        public bool TradePb { get; set; }

        [Parameter("   min bars since last entry", DefaultValue = 10, MinValue = 1, Group = "=== PATTERN & ADD-ONS ===")]
        public int PbCooldown { get; set; }

        [Parameter("   max add-ons per pattern", DefaultValue = 4, MinValue = 0, Group = "=== PATTERN & ADD-ONS ===")]
        public int PbMax { get; set; }

        [Parameter("   only when START profit >= (R, 0 = always)", DefaultValue = 1.0, MinValue = 0, Group = "=== PATTERN & ADD-ONS ===")]
        public double PbNeedR { get; set; }

        // ───────── RYZYKO ─────────
        [Parameter("Risk per Entry ($)", DefaultValue = 250.0, MinValue = 1.0, Group = "=== RISK ===")]
        public double RiskCash { get; set; }

        [Parameter("START SL: beyond extreme of last (bars)", DefaultValue = 20, MinValue = 1, Group = "=== RISK ===")]
        public int SlLen { get; set; }

        [Parameter("Add-on SL: beyond extreme of last (bars)", DefaultValue = 5, MinValue = 1, Group = "=== RISK ===")]
        public int PbSlLen { get; set; }

        [Parameter("SL buffer (x ATR)", DefaultValue = 0.2, MinValue = 0, Group = "=== RISK ===")]
        public double SlBuf { get; set; }

        [Parameter("Min risk (x ATR)", DefaultValue = 0.5, MinValue = 0.05, Group = "=== RISK ===")]
        public double MinRisk { get; set; }

        [Parameter("Max SL (pips, 0 = off)", DefaultValue = 0.0, MinValue = 0, Group = "=== RISK ===")]
        public double MaxSlPips { get; set; }

        // ───────── ZARZĄDZANIE ─────────
        [Parameter("Management", DefaultValue = TsManagement.AllCombined, Group = "=== MANAGEMENT ===")]
        public TsManagement Management { get; set; }

        [Parameter("BE: SL to entry after profit (R)", DefaultValue = 1.0, MinValue = 0.1, Group = "=== MANAGEMENT ===")]
        public double BeR { get; set; }

        [Parameter("Partial TP at (R)", DefaultValue = 2.0, MinValue = 0.1, Group = "=== MANAGEMENT ===")]
        public double TpR { get; set; }

        [Parameter("Partial TP: close (%)", DefaultValue = 50.0, MinValue = 1, MaxValue = 99, Group = "=== MANAGEMENT ===")]
        public double TpPct { get; set; }

        [Parameter("Trailing behind fast band: start after (R)", DefaultValue = 1.0, MinValue = 0, Group = "=== MANAGEMENT ===")]
        public double TrStartR { get; set; }

        [Parameter("Trailing buffer beyond fast band (x ATR)", DefaultValue = 0.3, MinValue = 0, Group = "=== MANAGEMENT ===")]
        public double TrBuf { get; set; }

        [Parameter("Time stop: check after (bars)", DefaultValue = 15, MinValue = 1, Group = "=== MANAGEMENT ===")]
        public int TsBars { get; set; }

        [Parameter("Time stop: exit if max profit < (R)", DefaultValue = 0.5, MinValue = 0, Group = "=== MANAGEMENT ===")]
        public double TsR { get; set; }

        // ───────── OKNO ─────────
        [Parameter("Start Hour (UTC)", DefaultValue = 7, MinValue = 0, MaxValue = 23, Group = "=== TRADING WINDOW ===")]
        public int StartHour { get; set; }

        [Parameter("End Hour (UTC)", DefaultValue = 15, MinValue = 0, MaxValue = 23, Group = "=== TRADING WINDOW ===")]
        public int EndHour { get; set; }

        [Parameter("Close EOD Everyday", DefaultValue = false, Group = "=== TRADING WINDOW ===")]
        public bool CloseEodEveryday { get; set; }

        [Parameter("Close EOD on FRIDAY", DefaultValue = true, Group = "=== TRADING WINDOW ===")]
        public bool CloseEodFriday { get; set; }

        // ───────── FTMO ─────────
        [Parameter("Enable FTMO Protection", DefaultValue = true, Group = "=== FTMO PROTECTION ===")]
        public bool FtmoProtection { get; set; }

        [Parameter("Max Daily Loss ($, 0 = off)", DefaultValue = 2000.0, MinValue = 0, Group = "=== FTMO PROTECTION ===")]
        public double MaxDailyLossCash { get; set; }

        [Parameter("Max Trades per Day (0 = off)", DefaultValue = 8, MinValue = 0, Group = "=== FTMO PROTECTION ===")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Max Consecutive Losses per Day (0 = off)", DefaultValue = 3, MinValue = 0, Group = "=== FTMO PROTECTION ===")]
        public int MaxConsecLosses { get; set; }

        [Parameter("Day Reset Time Zone (Windows ID)", DefaultValue = "Central European Standard Time", Group = "=== FTMO PROTECTION ===")]
        public string DayResetTimeZone { get; set; }

        // ───────── stan ─────────
        private class Info
        {
            public double E, Rk, Mfe;
            public int Bar;
            public bool Start, TpDone;
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

        private int V => (int)Management;
        private bool HasBe => V == 1 || V == 4 || V == 5;
        private bool HasTp => V == 2 || V == 5;
        private bool HasTr => V == 3 || V == 5;
        private bool HasTs => V == 4 || V == 5;

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

            // po restarcie: przejmij istniejące pozycje bota
            foreach (var p in MyPositions())
                if (p.StopLoss.HasValue)
                    _info[p.Id] = new Info { E = p.EntryPrice, Rk = Math.Abs(p.EntryPrice - p.StopLoss.Value), Bar = Bars.Count - 2, Start = p.Comment == "START" };
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
                if (!_info.TryGetValue(p.Id, out var t) || t.Rk <= 0) continue;
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

            // pozycje bez aktywnego wzorca (np. po restarcie bota) – dalej prowadzone wg wariantu
            if (_dir == 0 && MyPositions().Length > 0) ManageOnBar(i, atr);

            if (_dir != 0)
            {
                int d = _dir;
                _pExt = d == -1 ? Math.Min(_pExt, L(i)) : Math.Max(_pExt, H(i));
                bool against = d == -1 ? C(i) > _fh.Result[i] : C(i) < _fl.Result[i];
                bool hard    = d == -1 ? C(i) > _sh.Result[i] : C(i) < _sl.Result[i];
                _endCnt = against ? _endCnt + 1 : 0;
                bool patEnd = _endCnt >= EndBars || hard;

                if (patEnd)
                {
                    foreach (var p in MyPositions()) ClosePosition(p);
                    Print("Koniec wzorca: zasięg {0:F1} ATR, {1} świec", d * (_pExt - _pEntry) / atr, i - _pStart);
                    _dir = 0;
                    return;
                }

                ManageOnBar(i, atr);

                bool startOpen = MyPositions().Any(p => _info.TryGetValue(p.Id, out var t) && t.Start);
                if (TradePb && _patTraded && startOpen && _pbCnt < PbMax && i - _lastEnt >= PbCooldown
                    && (PbNeedR == 0 || d * (_pExt - _pEntry) / _pRisk >= PbNeedR))
                {
                    bool pb = d == -1
                        ? H(i) >= _fl.Result[i] && C(i) < FM(i) && C(i) < O(i) && FM(i) < FM(i - S3Len)
                        : L(i) <= _fh.Result[i] && C(i) > FM(i) && C(i) > O(i) && FM(i) > FM(i - S3Len);
                    if (pb)
                    {
                        Chart.DrawIcon("tsb_pb_" + i, ChartIconType.Diamond, i, d == -1 ? H(i) + 0.3 * atr : L(i) - 0.3 * atr, d == -1 ? Color.Orange : Color.Aqua);
                        _pbCnt++;
                        _lastEnt = i;
                        if (IsTradeTime() && FtmoAllowsNewTrade())
                            OpenEntry(d, SlLevel(i, d, PbSlLen), false, i);
                    }
                }
                return;
            }

            bool okS = Direction != TsDirection.LongOnly, okL = Direction != TsDirection.ShortOnly;
            bool allS = okS && AllConds(i, -1);
            bool allL = !allS && okL && AllConds(i, 1);
            if (!allS && !allL) return;

            int dir = allS ? -1 : 1;
            double sl = SlLevel(i, dir, SlLen);
            _dir = dir;
            _pStart = i;
            _pEntry = C(i);
            _pRisk = Math.Abs(C(i) - sl);
            _pExt = dir == -1 ? L(i) : H(i);
            _endCnt = 0;
            _pbCnt = 0;
            _lastEnt = i;
            Chart.DrawIcon("tsb_st_" + i, dir == -1 ? ChartIconType.DownArrow : ChartIconType.UpArrow, i,
                dir == -1 ? H(i) + 0.4 * atr : L(i) - 0.4 * atr, dir == -1 ? Color.Red : Color.LimeGreen);
            _patTraded = IsTradeTime() && FtmoAllowsNewTrade() && OpenEntry(dir, sl, true, i);
        }

        private void ManageOnBar(int i, double atr)
        {
            foreach (var p in MyPositions())
            {
                if (!_info.TryGetValue(p.Id, out var t) || t.Rk <= 0) continue;
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
            var r = p.ModifyStopLossPrice(level);
            if (!r.IsSuccessful) Print("Nie udało się przesunąć SL ({0}): {1}", p.Id, r.Error);
        }

        private bool OpenEntry(int d, double slLevel, bool isStart, int i)
        {
            var type = d == -1 ? TradeType.Sell : TradeType.Buy;
            double atr = _atr.Result[i];
            double entry = d == -1 ? Symbol.Bid : Symbol.Ask;
            double dist = Math.Max(d * (entry - slLevel), MinRisk * atr);
            double slPips = dist / Symbol.PipSize;
            if (MaxSlPips > 0 && slPips > MaxSlPips) { Print("Pominięte: SL {0:F1} pips > max", slPips); return false; }

            double units = Symbol.NormalizeVolumeInUnits(RiskCash / (slPips * Symbol.PipValue), RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin) { Print("Pominięte: za mały wolumen dla ryzyka {0} $", RiskCash); return false; }
            units = Math.Min(units, Symbol.VolumeInUnitsMax);

            string comment = isStart ? "START" : "PB";
            var res = ExecuteMarketOrder(type, SymbolName, units, BotLabel, slPips, null, comment);
            if (!res.IsSuccessful) { Print("Błąd otwarcia: {0}", res.Error); return false; }
            var pos = res.Position;
            if (!pos.StopLoss.HasValue)
            {
                Print("Pozycja bez SL – zamykam");
                ClosePosition(pos);
                return false;
            }
            _info[pos.Id] = new Info { E = pos.EntryPrice, Rk = Math.Abs(pos.EntryPrice - pos.StopLoss.Value), Bar = i, Start = isStart };
            _tradesToday++;
            Print("{0} {1} @ {2}, SL {3:F1} pips, {4} units", comment, type, pos.EntryPrice, slPips, units);
            return true;
        }

        // ───────── warunki (jak we wskaźniku) ─────────
        private double H(int i) => Bars.HighPrices[i];
        private double L(int i) => Bars.LowPrices[i];
        private double C(int i) => Bars.ClosePrices[i];
        private double O(int i) => Bars.OpenPrices[i];
        private double FM(int i) => (_fh.Result[i] + _fl.Result[i]) / 2;
        private double SM(int i) => (_sh.Result[i] + _sl.Result[i]) / 2;
        private bool Trend(int i, int d) => d == -1 ? _fh.Result[i] < _sl.Result[i] : _fl.Result[i] > _sh.Result[i];
        private double Gap(int i, int d) => d == -1 ? _sl.Result[i] - _fh.Result[i] : _fl.Result[i] - _sh.Result[i];

        private bool AllConds(int i, int d)
        {
            double atr = _atr.Result[i];
            int used = 0;
            if (Use1) { used++; if (!(d * (SM(i) - SM(i - S1Len)) >= S1Min * atr)) return false; }
            if (Use2)
            {
                used++;
                if (!Trend(i, d)) return false;
                bool fresh = false;
                for (int m = 0; m <= S2Len && i - m - 1 >= 0; m++)
                    if (!Trend(i - m - 1, d)) { fresh = true; break; }
                if (!fresh) return false;
            }
            if (Use3) { used++; if (!(d * (FM(i) - FM(i - S3Len)) >= S3Min * atr)) return false; }
            if (Use4) { used++; if (!(d == -1 ? C(i) < _fl.Result[i] : C(i) > _fh.Result[i])) return false; }
            if (Use5)
            {
                used++;
                bool touched = false;
                for (int k = 0; k <= S5Len && i - k >= 0; k++)
                    if (d == -1 ? H(i - k) >= _sl.Result[i - k] : L(i - k) <= _sh.Result[i - k]) { touched = true; break; }
                if (!touched) return false;
                for (int k = 0; k < S5Stay && i - k >= 0; k++)
                    if (d == -1 ? C(i - k) > _sh.Result[i - k] : C(i - k) < _sl.Result[i - k]) return false;
            }
            if (Use6) { used++; if (!(Gap(i, d) > Gap(i - S6Len, d))) return false; }
            if (Use7) { used++; if (!HtfTrend(i, d)) return false; }
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
            double atr = _atr.Result[i], hi = double.MinValue, lo = double.MaxValue;
            for (int k = 0; k < len && i - k >= 0; k++) { hi = Math.Max(hi, H(i - k)); lo = Math.Min(lo, L(i - k)); }
            double raw = d == -1 ? hi + SlBuf * atr : lo - SlBuf * atr;
            double r = Math.Max(Math.Abs(raw - C(i)), MinRisk * atr);
            return C(i) - d * r;
        }

        private Position[] MyPositions() =>
            Positions.Where(p => p.SymbolName == SymbolName && p.Label == BotLabel).ToArray();

        private bool IsTradeTime()
        {
            int h = Server.Time.Hour;
            if (StartHour == EndHour) return true;
            return StartHour < EndHour ? h >= StartHour && h < EndHour : h >= StartHour || h < EndHour;
        }

        // ───────── FTMO PROTECTION ─────────
        private DateTime LocalNow() =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(Server.Time, DateTimeKind.Utc), _dayTz);

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
                Block($"daily loss {dayPnl:F0} $");
                foreach (var p in MyPositions()) ClosePosition(p);
            }
        }

        private bool FtmoAllowsNewTrade()
        {
            if (!FtmoProtection) return true;
            if (_dayBlocked) return false;
            if (MaxTradesPerDay > 0 && _tradesToday >= MaxTradesPerDay) { Block($"{_tradesToday} trades today"); return false; }
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
                Block($"{_consecLosses} losses in a row");
        }
    }
}
