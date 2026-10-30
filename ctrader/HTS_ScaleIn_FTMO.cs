// HTS_ScaleIn_FTMO – strategia HTS z uśrednianiem wejścia (kilka wejść, wspólny SL, stałe ryzyko kampanii)
//
// Sygnał jak w HTS: trend z pasm 33/144 na Trend TF (H1) → pullback do szybkiego pasma H1 →
// przecięcie pasm na Entry TF (m1). Różnica: zamiast jednej pozycji bot otwiera KAMPANIĘ:
//  • wejście 1 po rynku na sygnale,
//  • wejścia 2..N jako zlecenia limit między wejściem 1 a wspólnym SL (co "Step % of SL distance"),
//  • ryzyko kampanii (Campaign Risk $) dzielone między wejścia – jeśli wypełnią się wszystkie
//    i padnie SL, strata = Campaign Risk. Każda dokładka poprawia średnią cenę wejścia.
//  • TP1 liczony od ŚREDNIEJ ceny wejścia: R-krotność ryzyka już wypełnionych pozycji albo stałe $,
//    na TP1 zamykany jest % każdej pozycji, niewypełnione limity są kasowane, SL → średnia (BE),
//    reszta jedzie z trailingiem w $ (jak w HTS).
//  • ochrona FTMO: dzienny limit straty, wolne miejsce na pełne ryzyko kampanii przed startem,
//    maks. kampanii dziennie, maks. przegranych kampanii z rzędu.
using System;
using System.Linq;
using System.Collections.Generic;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum SiSLMode { Swing, BandWidth144, Percent }
    public enum SiEntryTrigger { MACross, MA4Cross }
    public enum SiWeights { Equal, Increasing, Decreasing }
    public enum SiTpMode { RMultiple, Cash }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HTS_ScaleIn_FTMO : Robot
    {
        // ───────── SYSTEM / TF ─────────
        [Parameter("Bot Label", DefaultValue = "HTS_SI", Group = "=== SYSTEM ===")]
        public string BotLabel { get; set; }

        [Parameter("Trend TF", DefaultValue = "H1", Group = "=== TIMEFRAMES ===")]
        public TimeFrame TrendTF { get; set; }

        [Parameter("Entry TF", DefaultValue = "Minute", Group = "=== TIMEFRAMES ===")]
        public TimeFrame EntryTF { get; set; }

        // ───────── PASMA ─────────
        [Parameter("Trend Fast Period", DefaultValue = 33, Group = "=== TREND TF BANDS ===")]
        public int TrendFastPeriod { get; set; }

        [Parameter("Trend Slow Period", DefaultValue = 144, Group = "=== TREND TF BANDS ===")]
        public int TrendSlowPeriod { get; set; }

        [Parameter("Trend MA Type", DefaultValue = MovingAverageType.WilderSmoothing, Group = "=== TREND TF BANDS ===")]
        public MovingAverageType TrendMAType { get; set; }

        [Parameter("Entry Fast Period", DefaultValue = 33, Group = "=== ENTRY TF BANDS ===")]
        public int EntryFastPeriod { get; set; }

        [Parameter("Entry Slow Period", DefaultValue = 144, Group = "=== ENTRY TF BANDS ===")]
        public int EntrySlowPeriod { get; set; }

        [Parameter("Entry MA Type", DefaultValue = MovingAverageType.WilderSmoothing, Group = "=== ENTRY TF BANDS ===")]
        public MovingAverageType EntryMAType { get; set; }

        [Parameter("Min MA4Cross Spread (pips)", DefaultValue = 0.0, MinValue = 0.0, Group = "=== ENTRY TF BANDS ===")]
        public double MinMaSpreadPips { get; set; }

        // ───────── SYGNAŁ ─────────
        [Parameter("Entry Trigger", DefaultValue = SiEntryTrigger.MACross, Group = "=== ENTRY ===")]
        public SiEntryTrigger EntryTrigger { get; set; }

        [Parameter("Max Broker Spread (points)", DefaultValue = 800, Group = "=== ENTRY ===")]
        public double MaxSpread { get; set; }

        [Parameter("Min Price Dist from Band (pts)", DefaultValue = 1, Group = "=== ENTRY ===")]
        public double MinDistFromBand { get; set; }

        // ───────── SL ─────────
        [Parameter("SL Calculation Mode", DefaultValue = SiSLMode.Percent, Group = "=== STOP LOSS ===")]
        public SiSLMode SlMode { get; set; }

        [Parameter("SL % from Entry Price (If Percent)", DefaultValue = 0.5, MinValue = 0.01, Group = "=== STOP LOSS ===")]
        public double SlPercent { get; set; }

        [Parameter("Bars for Swing SL", DefaultValue = 26, MinValue = 2, Group = "=== STOP LOSS ===")]
        public int SwingBars { get; set; }

        [Parameter("Swing SL Buffer (pips)", DefaultValue = 15.0, MinValue = 0.0, Group = "=== STOP LOSS ===")]
        public double SwingBufferPips { get; set; }

        [Parameter("SL Multiplier 144", DefaultValue = 2.0, MinValue = 0.1, Group = "=== STOP LOSS ===")]
        public double SlBandWidth144Multiplier { get; set; }

        [Parameter("Min SL (pips, entry 1)", DefaultValue = 40.0, MinValue = 0.1, Group = "=== STOP LOSS ===")]
        public double MinSLPips { get; set; }

        [Parameter("Max SL (pips, entry 1)", DefaultValue = 250.0, MinValue = 0.1, Group = "=== STOP LOSS ===")]
        public double MaxSLPips { get; set; }

        // ───────── UŚREDNIANIE ─────────
        [Parameter("Campaign Risk ($, all entries filled)", DefaultValue = 300.0, MinValue = 1.0, Group = "=== SCALE-IN ===")]
        public double CampaignRiskCash { get; set; }

        [Parameter("Number of Entries", DefaultValue = 3, MinValue = 1, MaxValue = 5, Group = "=== SCALE-IN ===")]
        public int EntriesCount { get; set; }

        [Parameter("Step (% of SL distance)", DefaultValue = 25.0, MinValue = 1.0, MaxValue = 90.0, Group = "=== SCALE-IN ===")]
        public double StepPct { get; set; }

        [Parameter("Risk Split", DefaultValue = SiWeights.Equal, Group = "=== SCALE-IN ===")]
        public SiWeights RiskSplit { get; set; }

        [Parameter("Min Dist Level→SL (pips)", DefaultValue = 15.0, MinValue = 0.1, Group = "=== SCALE-IN ===")]
        public double MinLevelSlPips { get; set; }

        [Parameter("Cancel Unfilled Entries After (Entry TF bars)", DefaultValue = 60, MinValue = 1, Group = "=== SCALE-IN ===")]
        public int PendingExpiryBars { get; set; }

        // ───────── ZARZĄDZANIE ─────────
        [Parameter("TP1 Mode", DefaultValue = SiTpMode.RMultiple, Group = "=== POSITION MANAGEMENT ===")]
        public SiTpMode Tp1Mode { get; set; }

        [Parameter("TP1 (R of filled risk)", DefaultValue = 1.6, MinValue = 0.1, Group = "=== POSITION MANAGEMENT ===")]
        public double Tp1R { get; set; }

        [Parameter("TP1 ($, if Cash)", DefaultValue = 800.0, MinValue = 1.0, Group = "=== POSITION MANAGEMENT ===")]
        public double Tp1Cash { get; set; }

        [Parameter("% Volume to Close at TP1", DefaultValue = 95.0, MinValue = 1.0, MaxValue = 100.0, Group = "=== POSITION MANAGEMENT ===")]
        public double PartialClosePercent { get; set; }

        [Parameter("Trailing Stop Active", DefaultValue = true, Group = "=== POSITION MANAGEMENT ===")]
        public bool TrailingOn { get; set; }

        [Parameter("Trailing Start Profit ($)", DefaultValue = 15.0, MinValue = 1.0, Group = "=== POSITION MANAGEMENT ===")]
        public double TrailingStartCash { get; set; }

        [Parameter("Trailing Distance ($)", DefaultValue = 15.0, MinValue = 1.0, Group = "=== POSITION MANAGEMENT ===")]
        public double TrailingDistanceCash { get; set; }

        // ───────── OKNO ─────────
        [Parameter("Start Hour (UTC)", DefaultValue = 7, Group = "=== TRADING WINDOW ===")]
        public int StartHour { get; set; }

        [Parameter("End Hour (UTC)", DefaultValue = 15, Group = "=== TRADING WINDOW ===")]
        public int EndHour { get; set; }

        [Parameter("Block entries N hours before window ends", DefaultValue = 1, MinValue = 0, Group = "=== TRADING WINDOW ===")]
        public int WindowBlockHours { get; set; }

        [Parameter("Cancel Unfilled Entries Outside Window", DefaultValue = true, Group = "=== TRADING WINDOW ===")]
        public bool CancelPendingOutsideWindow { get; set; }

        [Parameter("Close EOD Everyday", DefaultValue = false, Group = "=== TRADING WINDOW ===")]
        public bool EodCloseEveryday { get; set; }

        [Parameter("Close EOD on FRIDAY", DefaultValue = true, Group = "=== TRADING WINDOW ===")]
        public bool EodCloseFriday { get; set; }

        // ───────── FTMO ─────────
        [Parameter("Enable FTMO Protection", DefaultValue = true, Group = "=== FTMO PROTECTION ===")]
        public bool FtmoProtection { get; set; }

        [Parameter("Max Daily Loss ($, 0 = off)", DefaultValue = 3000.0, MinValue = 0.0, Group = "=== FTMO PROTECTION ===")]
        public double MaxDailyLossCash { get; set; }

        [Parameter("Start Campaign Only If Full Risk Fits Daily Limit", DefaultValue = true, Group = "=== FTMO PROTECTION ===")]
        public bool RequireDailyRoom { get; set; }

        [Parameter("Daily Profit Target ($, 0 = off)", DefaultValue = 0.0, MinValue = 0.0, Group = "=== FTMO PROTECTION ===")]
        public double DailyTargetCash { get; set; }

        [Parameter("Max Campaigns per Day (0 = off)", DefaultValue = 3, MinValue = 0, Group = "=== FTMO PROTECTION ===")]
        public int MaxCampaignsPerDay { get; set; }

        [Parameter("Max Losing Campaigns in a Row (0 = off)", DefaultValue = 2, MinValue = 0, Group = "=== FTMO PROTECTION ===")]
        public int MaxConsecLosses { get; set; }

        [Parameter("Day Reset Time Zone (Windows ID)", DefaultValue = "Central European Standard Time", Group = "=== FTMO PROTECTION ===")]
        public string DayResetTimeZone { get; set; }

        [Parameter("Enable Dashboard", DefaultValue = true, Group = "=== UI ===")]
        public bool EnableDashboard { get; set; }

        // ───────── stan ─────────
        private enum TrendDir { None, Up, Down }
        private enum BotState { WaitTrend, WaitPullback, WaitEntry, InCampaign }

        private Bars _trendBars, _entryBars;
        private MovingAverage _tFastHigh, _tFastLow, _tSlowHigh, _tSlowLow;
        private MovingAverage _eFastHigh, _eFastLow, _eSlowHigh, _eSlowLow;

        private BotState _state = BotState.WaitTrend;
        private TrendDir _trend = TrendDir.None;
        private bool _pullbackDetected;
        private DateTime _lastTrendBarTime;
        private DateTime _lastEntryBarTime;
        private bool _previousEntrySignal;

        // kampania
        private int _campaignId;
        private string _campaignPrefix = "";
        private TradeType _campDir;
        private double _campSL = double.NaN;
        private int _campStartBar;
        private bool _campHadPositions;
        private bool _tp1Hit;
        private bool _trailActive;
        private double _maxFav;
        private string _lastAction = "";

        // FTMO
        private TimeZoneInfo _dayTz;
        private DateTime _ftmoDay;
        private double _dayStartBalance;
        private int _campaignsToday;
        private int _consecLosses;
        private bool _dayBlocked;
        private string _dayBlockReason = "";

        protected override void OnStart()
        {
            _trendBars = MarketData.GetBars(TrendTF);
            _entryBars = MarketData.GetBars(EntryTF);

            _tFastHigh = Indicators.MovingAverage(_trendBars.HighPrices, TrendFastPeriod, TrendMAType);
            _tFastLow  = Indicators.MovingAverage(_trendBars.LowPrices,  TrendFastPeriod, TrendMAType);
            _tSlowHigh = Indicators.MovingAverage(_trendBars.HighPrices, TrendSlowPeriod, TrendMAType);
            _tSlowLow  = Indicators.MovingAverage(_trendBars.LowPrices,  TrendSlowPeriod, TrendMAType);

            _eFastHigh = Indicators.MovingAverage(_entryBars.HighPrices, EntryFastPeriod, EntryMAType);
            _eFastLow  = Indicators.MovingAverage(_entryBars.LowPrices,  EntryFastPeriod, EntryMAType);
            _eSlowHigh = Indicators.MovingAverage(_entryBars.HighPrices, EntrySlowPeriod, EntryMAType);
            _eSlowLow  = Indicators.MovingAverage(_entryBars.LowPrices,  EntrySlowPeriod, EntryMAType);

            try { _dayTz = TimeZoneInfo.FindSystemTimeZoneById(DayResetTimeZone); }
            catch (Exception)
            {
                Print("Time zone '{0}' not found, using UTC", DayResetTimeZone);
                _dayTz = TimeZoneInfo.Utc;
            }
            StartFtmoDay(FtmoLocalNow().Date);

            RecoverState();
        }

        protected override void OnTick()
        {
            bool isTradeTime = IsTradeTime();
            double spreadPoints = Symbol.Spread / Symbol.TickSize;

            CheckFtmoProtection();
            UpdateTrend();

            if (_state == BotState.InCampaign)
                ManageCampaign(isTradeTime);

            switch (_state)
            {
                case BotState.WaitPullback:
                    if (_trend != TrendDir.None) DoWaitPullback();
                    break;
                case BotState.WaitEntry:
                    if (_trend != TrendDir.None) DoWaitEntry(isTradeTime, spreadPoints);
                    break;
            }

            UpdateDashboard();
        }

        // ───────────────────────── TREND / PULLBACK / SYGNAŁ (jak w HTS) ─────────────────────────
        private void UpdateTrend()
        {
            if (_tFastHigh.Result.Count < TrendSlowPeriod + 5 || _tSlowLow.Result.Count < TrendSlowPeriod + 5) return;

            double bHigh = _tFastHigh.Result.Last(1);
            double bLow  = _tFastLow.Result.Last(1);
            double rHigh = _tSlowHigh.Result.Last(1);
            double rLow  = _tSlowLow.Result.Last(1);
            if (double.IsNaN(bHigh) || double.IsNaN(bLow) || double.IsNaN(rHigh) || double.IsNaN(rLow)) return;

            TrendDir prev = _trend;
            if      (bLow > rHigh) _trend = TrendDir.Up;
            else if (bHigh < rLow) _trend = TrendDir.Down;
            else                   _trend = TrendDir.None;

            if (_trend == prev) return;

            _pullbackDetected    = false;
            _previousEntrySignal = false;

            // zmiana trendu w trakcie kampanii: nie dokładamy już pod stary kierunek
            if (_state == BotState.InCampaign)
            {
                CancelCampaignPending("trend changed");
                return;
            }
            _state = _trend == TrendDir.None ? BotState.WaitTrend : BotState.WaitPullback;
        }

        private void DoWaitPullback()
        {
            if (_trendBars.OpenTimes.Last(0) == _lastTrendBarTime) return;

            double h     = _trendBars.HighPrices.Last(1);
            double l     = _trendBars.LowPrices.Last(1);
            double bHigh = _tFastHigh.Result.Last(1);
            double bLow  = _tFastLow.Result.Last(1);

            if ((_trend == TrendDir.Up && l <= bHigh) || (_trend == TrendDir.Down && h >= bLow))
            {
                _pullbackDetected = true;
                _state = BotState.WaitEntry;
            }
            _lastTrendBarTime = _trendBars.OpenTimes.Last(0);
        }

        private bool EvaluateEntrySignal()
        {
            if (_eSlowHigh.Result.Count < 3) return false;

            double fHigh1 = _eFastHigh.Result.Last(1), fLow1 = _eFastLow.Result.Last(1);
            double sHigh1 = _eSlowHigh.Result.Last(1), sLow1 = _eSlowLow.Result.Last(1);
            double fHigh2 = _eFastHigh.Result.Last(2), fLow2 = _eFastLow.Result.Last(2);
            double sHigh2 = _eSlowHigh.Result.Last(2), sLow2 = _eSlowLow.Result.Last(2);

            double fMid1 = (fHigh1 + fLow1) / 2.0, sMid1 = (sHigh1 + sLow1) / 2.0;
            if (Math.Abs(fMid1 - sMid1) / Symbol.PipSize < MinMaSpreadPips) return false;

            if (EntryTrigger == SiEntryTrigger.MACross)
            {
                double fMid2 = (fHigh2 + fLow2) / 2.0, sMid2 = (sHigh2 + sLow2) / 2.0;
                return _trend == TrendDir.Up ? fMid2 <= sMid2 && fMid1 > sMid1
                                             : fMid2 >= sMid2 && fMid1 < sMid1;
            }
            return _trend == TrendDir.Up ? fLow1 > sHigh1 && fLow2 <= sHigh2
                                         : fHigh1 < sLow1 && fHigh2 >= sLow2;
        }

        private void DoWaitEntry(bool isTradeTime, double spreadPoints)
        {
            if (_entryBars.OpenTimes.Last(0) == _lastEntryBarTime) return;

            bool signal = EvaluateEntrySignal();
            if (!signal) { _previousEntrySignal = false; return; }
            if (_previousEntrySignal) return;
            if (!isTradeTime || !WindowAllowsEntry()) return;
            if (spreadPoints > MaxSpread) return;

            _previousEntrySignal = true;
            _lastEntryBarTime = _entryBars.OpenTimes.Last(0);

            TradeType dir = _trend == TrendDir.Up ? TradeType.Buy : TradeType.Sell;
            if (dir == TradeType.Buy)
            {
                if (Symbol.Ask <= _tFastHigh.Result.Last(0)) return;
                if ((Symbol.Ask - _eFastHigh.Result.Last(1)) / Symbol.TickSize < MinDistFromBand) return;
            }
            else
            {
                if (Symbol.Bid >= _tFastLow.Result.Last(0)) return;
                if ((_eFastLow.Result.Last(1) - Symbol.Bid) / Symbol.TickSize < MinDistFromBand) return;
            }

            if (!FtmoAllowsNewCampaign()) return;

            double entry = dir == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double sl = CalcStopLoss(dir, entry);
            if (double.IsNaN(sl)) return;

            double slPips = Math.Abs(entry - sl) / Symbol.PipSize;
            if ((dir == TradeType.Buy && sl >= entry) || (dir == TradeType.Sell && sl <= entry)) return;
            if (slPips < MinSLPips || slPips > MaxSLPips) return;

            StartCampaign(dir, entry, sl);
        }

        private double CalcStopLoss(TradeType dir, double entry)
        {
            if (SlMode == SiSLMode.Percent)
            {
                double dist = entry * SlPercent / 100.0;
                return Math.Round(dir == TradeType.Buy ? entry - dist : entry + dist, Symbol.Digits);
            }
            if (SlMode == SiSLMode.Swing)
            {
                if (_entryBars.Count < SwingBars + 2) return double.NaN;
                double lo = double.MaxValue, hi = double.MinValue;
                for (int i = 1; i <= SwingBars; i++)
                {
                    lo = Math.Min(lo, _entryBars.LowPrices.Last(i));
                    hi = Math.Max(hi, _entryBars.HighPrices.Last(i));
                }
                double buf = SwingBufferPips * Symbol.PipSize;
                return Math.Round(dir == TradeType.Buy ? lo - buf : hi + buf, Symbol.Digits);
            }
            double sHigh = _eSlowHigh.Result.Last(1), sLow = _eSlowLow.Result.Last(1);
            double bw = sHigh - sLow;
            if (bw <= 0) return double.NaN;
            return Math.Round(dir == TradeType.Buy ? sHigh - bw * SlBandWidth144Multiplier
                                                   : sLow + bw * SlBandWidth144Multiplier, Symbol.Digits);
        }

        // ───────────────────────── KAMPANIA ─────────────────────────
        private void StartCampaign(TradeType dir, double entry, double sl)
        {
            _campaignId++;
            _campaignPrefix   = $"{BotLabel}_{Server.Time:yyMMddHHmm}_{_campaignId}_";
            _campDir          = dir;
            _campSL           = sl;
            _campStartBar     = _entryBars.Count;
            _campHadPositions = false;
            _tp1Hit           = false;
            _trailActive      = false;
            _maxFav           = entry;
            _state            = BotState.InCampaign;
            _campaignsToday++;

            int sign = dir == TradeType.Buy ? 1 : -1;
            double dist = Math.Abs(entry - sl);

            // poziomy wejść i podział ryzyka
            var levels  = new List<double>();
            var weights = new List<double>();
            for (int k = 0; k < EntriesCount; k++)
            {
                double level = k == 0 ? entry : Math.Round(entry - sign * dist * StepPct * k / 100.0, Symbol.Digits);
                if (k > 0 && (k * StepPct >= 100.0 || Math.Abs(level - sl) / Symbol.PipSize < MinLevelSlPips)) break;
                levels.Add(level);
                weights.Add(RiskSplit == SiWeights.Equal ? 1.0 : RiskSplit == SiWeights.Increasing ? k + 1 : EntriesCount - k);
            }
            double wSum = weights.Sum();

            for (int k = 0; k < levels.Count; k++)
            {
                double levelSlPips = Math.Abs(levels[k] - sl) / Symbol.PipSize;
                double risk = CampaignRiskCash * weights[k] / wSum;
                double units = Symbol.NormalizeVolumeInUnits(risk / (levelSlPips * Symbol.PipValue), RoundingMode.Down);
                if (units < Symbol.VolumeInUnitsMin) continue;
                units = Math.Min(units, Symbol.VolumeInUnitsMax);

                string label = _campaignPrefix + (k + 1);
                if (k == 0)
                {
                    var res = ExecuteMarketOrder(dir, SymbolName, units, label, levelSlPips, null);
                    if (!res.IsSuccessful) continue;
                    // SL dokładnie na poziomie kampanii (wejście po rynku mogło się przesunąć o poślizg)
                    var mod = ModifyPosition(res.Position, sl, null, ProtectionType.Absolute);
                    if (!mod.IsSuccessful)
                    {
                        Print("SL not set – closing {0}", res.Position.Id);
                        ClosePosition(res.Position);
                        continue;
                    }
                    _campHadPositions = true;
                }
                else
                {
                    PlaceLimitOrder(dir, SymbolName, units, levels[k], label, levelSlPips, null);
                }
            }

            _lastAction = $"Campaign {_campaignId}: {dir} {levels.Count} entries, SL {sl}";
            Print(_lastAction);

            if (!GetCampaignPositions().Any() && !GetCampaignPending().Any())
                EndCampaign("nothing opened");
        }

        private void ManageCampaign(bool isTradeTime)
        {
            var positions = GetCampaignPositions();
            var pending   = GetCampaignPending();

            if (positions.Count > 0) _campHadPositions = true;

            // koniec kampanii: wszystko zamknięte (SL / trailing / ręcznie)
            if (positions.Count == 0 && (_campHadPositions || pending.Count == 0))
            {
                EndCampaign("all positions closed");
                return;
            }

            // EOD / piątek
            if (!isTradeTime)
            {
                bool afterEnd = StartHour < EndHour ? Server.Time.Hour >= EndHour : true;
                bool friday = Server.Time.DayOfWeek == DayOfWeek.Friday;
                if (EodCloseEveryday || (EodCloseFriday && friday && afterEnd))
                {
                    CloseCampaign("EOD close");
                    return;
                }
                if (CancelPendingOutsideWindow) CancelCampaignPending("outside window");
            }

            // wygaśnięcie niewypełnionych dokładek
            if (pending.Count > 0 && _entryBars.Count - _campStartBar >= PendingExpiryBars)
                CancelCampaignPending("expired");

            if (positions.Count == 0) return;

            // średnia cena, wolumen i ryzyko wypełnionych pozycji
            double vol = positions.Sum(p => p.VolumeInUnits);
            double avg = positions.Sum(p => p.EntryPrice * p.VolumeInUnits) / vol;
            int sign = _campDir == TradeType.Buy ? 1 : -1;
            double price = _campDir == TradeType.Buy ? Symbol.Bid : Symbol.Ask;

            _maxFav = sign == 1 ? Math.Max(_maxFav, price) : Math.Min(_maxFav, price);

            if (!_tp1Hit)
            {
                double filledRisk = positions.Sum(p => Math.Abs(p.EntryPrice - _campSL) / Symbol.PipSize * Symbol.PipValue * p.VolumeInUnits);
                double target = Tp1Mode == SiTpMode.RMultiple ? Tp1R * filledRisk : Tp1Cash;
                double tp1 = avg + sign * target / (vol * Symbol.PipValue) * Symbol.PipSize;

                if ((sign == 1 && price >= tp1) || (sign == -1 && price <= tp1))
                {
                    CancelCampaignPending("TP1");
                    foreach (var p in positions)
                    {
                        double part = Symbol.NormalizeVolumeInUnits(p.VolumeInUnits * PartialClosePercent / 100.0, RoundingMode.Down);
                        if (PartialClosePercent >= 100.0 || p.VolumeInUnits - part < Symbol.VolumeInUnitsMin)
                            ClosePosition(p);
                        else if (part >= Symbol.VolumeInUnitsMin)
                            ClosePosition(p, part);
                    }
                    _tp1Hit = true;

                    // SL reszty na średnią cenę kampanii (BE)
                    double be = Math.Round(avg, Symbol.Digits);
                    foreach (var p in GetCampaignPositions())
                        if (p.StopLoss == null || (sign == 1 ? be > p.StopLoss.Value : be < p.StopLoss.Value))
                            p.ModifyStopLossPrice(be);

                    _lastAction = $"TP1 at {price}, rest to BE {be}";
                    Print(_lastAction);
                }
                return;
            }

            // trailing reszty po TP1 (jak w HTS: w $ na pozostały wolumen)
            if (!TrailingOn) return;
            double startPips = TrailingStartCash / (vol * Symbol.PipValue);
            double distPips  = TrailingDistanceCash / (vol * Symbol.PipValue);
            double profitPips = sign * (price - avg) / Symbol.PipSize;

            if (!_trailActive && profitPips >= startPips) _trailActive = true;
            if (!_trailActive) return;

            double newSl = Math.Round(_maxFav - sign * distPips * Symbol.PipSize, Symbol.Digits);
            foreach (var p in positions)
            {
                bool better = p.StopLoss == null || (sign == 1 ? newSl > p.StopLoss.Value + Symbol.PipSize : newSl < p.StopLoss.Value - Symbol.PipSize);
                bool valid  = sign == 1 ? newSl < Symbol.Bid : newSl > Symbol.Ask;
                if (better && valid) p.ModifyStopLossPrice(newSl);
            }
        }

        private void CloseCampaign(string reason)
        {
            CancelCampaignPending(reason);
            foreach (var p in GetCampaignPositions()) ClosePosition(p);
            EndCampaign(reason);
        }

        private void CancelCampaignPending(string reason)
        {
            var pending = GetCampaignPending();
            if (pending.Count == 0) return;
            foreach (var o in pending) CancelPendingOrder(o);
            Print("Unfilled entries cancelled: {0}", reason);
        }

        private void EndCampaign(string reason)
        {
            CancelCampaignPending(reason);

            if (_campHadPositions && _campaignPrefix != "")
            {
                double net = History.Where(h => h.Label != null && h.Label.StartsWith(_campaignPrefix)).Sum(h => h.NetProfit);
                _consecLosses = net < 0 ? _consecLosses + 1 : 0;
                _lastAction = $"Campaign {_campaignId} closed ({reason}): {net:F2} $";
                Print(_lastAction);
                if (FtmoProtection && !_dayBlocked && MaxConsecLosses > 0 && _consecLosses >= MaxConsecLosses)
                    BlockFtmoDay($"{_consecLosses} losing campaigns in a row");
            }

            _campSL = double.NaN;
            _campHadPositions = false;
            _tp1Hit = false;
            _trailActive = false;

            _state = _trend == TrendDir.None ? BotState.WaitTrend
                   : _pullbackDetected       ? BotState.WaitEntry
                                             : BotState.WaitPullback;
            _previousEntrySignal = EvaluateEntrySignal();
            _lastEntryBarTime = _entryBars.OpenTimes.Last(0);
        }

        private List<Position> GetCampaignPositions() =>
            Positions.Where(p => p.SymbolName == SymbolName && p.Label != null && _campaignPrefix != "" && p.Label.StartsWith(_campaignPrefix)).ToList();

        private List<PendingOrder> GetCampaignPending() =>
            PendingOrders.Where(o => o.SymbolName == SymbolName && o.Label != null && _campaignPrefix != "" && o.Label.StartsWith(_campaignPrefix)).ToList();

        private void RecoverState()
        {
            var pos = Positions.FirstOrDefault(p => p.SymbolName == SymbolName && p.Label != null && p.Label.StartsWith(BotLabel + "_"));
            var ord = PendingOrders.FirstOrDefault(o => o.SymbolName == SymbolName && o.Label != null && o.Label.StartsWith(BotLabel + "_"));
            string label = pos?.Label ?? ord?.Label;
            if (label == null) return;

            // prefiks kampanii = etykieta bez numeru wejścia
            _campaignPrefix   = label.Substring(0, label.LastIndexOf('_') + 1);
            _campDir          = pos?.TradeType ?? ord.TradeType;
            _campSL           = pos?.StopLoss ?? double.NaN;
            _campStartBar     = _entryBars.Count;
            _campHadPositions = pos != null;
            _maxFav           = _campDir == TradeType.Buy ? Symbol.Bid : Symbol.Ask;
            _pullbackDetected = true;
            _state            = BotState.InCampaign;

            // SL na średniej lub lepiej = TP1 już był
            var all = GetCampaignPositions();
            if (all.Count > 0 && all.All(p => p.StopLoss.HasValue))
            {
                double avg = all.Sum(p => p.EntryPrice * p.VolumeInUnits) / all.Sum(p => p.VolumeInUnits);
                _tp1Hit = _campDir == TradeType.Buy ? all.All(p => p.StopLoss.Value >= avg - Symbol.PipSize)
                                                    : all.All(p => p.StopLoss.Value <= avg + Symbol.PipSize);
            }
            Print("Recovered campaign {0}", _campaignPrefix);
        }

        // ───────────────────────── OKNO HANDLU ─────────────────────────
        private bool IsTradeTime()
        {
            int hour = Server.Time.Hour;
            if (StartHour == EndHour) return true;
            if (StartHour < EndHour)  return hour >= StartHour && hour < EndHour;
            return hour >= StartHour || hour < EndHour;
        }

        private bool WindowAllowsEntry()
        {
            if (WindowBlockHours <= 0 || StartHour == EndHour) return true;
            int hour = Server.Time.Hour;
            int remaining = StartHour < EndHour ? EndHour - hour
                          : hour >= StartHour ? (24 - hour) + EndHour : EndHour - hour;
            return remaining > WindowBlockHours;
        }

        // ───────────────────────── FTMO PROTECTION ─────────────────────────
        private DateTime FtmoLocalNow() =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(Server.Time, DateTimeKind.Utc), _dayTz);

        private void StartFtmoDay(DateTime day)
        {
            _ftmoDay         = day;
            _dayStartBalance = Account.Balance;
            _campaignsToday  = 0;
            _consecLosses    = 0;
            _dayBlocked      = false;
            _dayBlockReason  = "";
        }

        private double DayPnl() => Account.Equity - _dayStartBalance;

        private void CheckFtmoProtection()
        {
            if (!FtmoProtection) return;

            var today = FtmoLocalNow().Date;
            if (today != _ftmoDay) StartFtmoDay(today);

            double dayPnl = DayPnl();
            if (!_dayBlocked && MaxDailyLossCash > 0 && dayPnl <= -MaxDailyLossCash)
            {
                BlockFtmoDay($"daily loss {dayPnl:F0} $");
                if (_state == BotState.InCampaign) CloseCampaign("FTMO daily loss");
            }
            if (!_dayBlocked && DailyTargetCash > 0 && dayPnl >= DailyTargetCash)
                BlockFtmoDay($"daily target {dayPnl:F0} $");
        }

        private bool FtmoAllowsNewCampaign()
        {
            if (!FtmoProtection) return true;
            if (_dayBlocked) return false;
            if (MaxCampaignsPerDay > 0 && _campaignsToday >= MaxCampaignsPerDay)
            {
                BlockFtmoDay($"{_campaignsToday} campaigns today");
                return false;
            }
            // pełne ryzyko kampanii musi zmieścić się w dziennym limicie
            if (RequireDailyRoom && MaxDailyLossCash > 0 && DayPnl() - CampaignRiskCash <= -MaxDailyLossCash)
                return false;
            return true;
        }

        private void BlockFtmoDay(string reason)
        {
            _dayBlocked = true;
            _dayBlockReason = reason;
            Print("FTMO PROTECTION – entries blocked until next day: {0}", reason);
        }

        // ───────────────────────── DASHBOARD ─────────────────────────
        private void UpdateDashboard()
        {
            if (!EnableDashboard) { Chart.RemoveObject("SiDashboard"); return; }

            string trendStr = _trend == TrendDir.Up ? "UPTREND" : _trend == TrendDir.Down ? "DOWNTREND" : "NONE";
            string[] states = { "Waiting for trend", "Waiting for H1 pullback", "Waiting for band cross", "CAMPAIGN ACTIVE" };

            var positions = GetCampaignPositions();
            int pendingCount = GetCampaignPending().Count;
            string camp = "–";
            if (positions.Count > 0)
            {
                double vol = positions.Sum(p => p.VolumeInUnits);
                double avg = positions.Sum(p => p.EntryPrice * p.VolumeInUnits) / vol;
                double open = positions.Sum(p => p.NetProfit);
                camp = $"filled {positions.Count}, waiting {pendingCount} | avg {avg.ToString("F" + Symbol.Digits)} | SL {_campSL.ToString("F" + Symbol.Digits)} | P/L {open:F2} $" + (_tp1Hit ? " | after TP1" : "");
            }
            else if (pendingCount > 0)
                camp = $"waiting entries: {pendingCount}";

            string text =
                $"=== HTS SCALE-IN ===\n" +
                $"{SymbolName} | Trend: {TrendTF} {trendStr} | Entry: {EntryTF}\n" +
                $"State: {states[(int)_state]}\n" +
                $"Campaign risk: {CampaignRiskCash:F0} $ | Entries: {EntriesCount} co {StepPct:F0}% SL | TP1: " +
                (Tp1Mode == SiTpMode.RMultiple ? $"{Tp1R:F1}R" : $"{Tp1Cash:F0} $") + "\n" +
                $"Campaign: {camp}\n" +
                (FtmoProtection
                    ? $"Today: {DayPnl():F2} $ (limit -{MaxDailyLossCash:F0}) | Campaigns: {_campaignsToday}" +
                      (MaxCampaignsPerDay > 0 ? $"/{MaxCampaignsPerDay}" : "") + $" | Losses in a row: {_consecLosses}\n" +
                      (_dayBlocked ? $"BLOCKED: {_dayBlockReason}" : "Entries allowed")
                    : "FTMO protection: OFF") +
                (_lastAction != "" ? "\nLast: " + _lastAction : "");

            Chart.DrawStaticText("SiDashboard", text, VerticalAlignment.Top, HorizontalAlignment.Left, Color.White);
        }
    }
}
