// HTS_Strategy_Tester_PRO + FTMO PROTECTION
// Zmiany względem oryginału:
//  • ochrona FTMO: dzienny limit straty (equity vs saldo z północy CET/CEST), dzienny cel,
//    maks. transakcji dziennie, maks. strat z rzędu – blokada wejść do końca dnia
//  • poprawka: bot nie zawiesza się w stanie "CAMPAIGN ACTIVE" po zmianie trendu w trakcie pozycji
//  • poprawka: pozycja bez SL jest od razu zamykana (gdy ustawienie SL się nie uda)
//  • poprawka: "Close EOD on FRIDAY" zamyka pozycje po końcu okna w piątek, a nie w piątek o północy
//  • poprawka: pozycje bez etykiety (ręczne) nie wywracają bota
//  • poprawka: po restarcie bota pozycja po TP1 (SL na BE) jest rozpoznawana i dalej trailowana
using System;
using System.Linq;
using System.Collections.Generic;
using System.Globalization;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum SLMode { Swing, BandWidth144, Percent }
    public enum TrendDir { None, Up, Down }
    public enum BotState { WaitTrend, WaitPullback, WaitEntry, InCampaign }
    public enum EntryTriggerMode { MACross, MA4Cross }
    public enum TrailingMode { CashDistance, MATrailing }
    public enum MATrailingLine { FastLowHigh, SlowHighLow, SlowLowHigh }
    public enum CampaignEntryType { Cross, TouchFast, TouchSlow }

    public class TradeContext
    {
        public double OriginalEntry;
        public double Tp1Price;
        public bool Tp1Hit;
        public bool TrailingActive;
        public bool EodBeApplied;
        public double MaxFavorablePrice; 
        public CampaignEntryType EntryType;
    }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HTS_Strategy_Tester_II : Robot
    {
        [Parameter("Bot Label", DefaultValue = "HTS_for_FTMO_H1_m1", Group = "=== SYSTEM ===")]
        public string BotLabel { get; set; }

        [Parameter("Trend TF", DefaultValue = "H1", Group = "=== TIMEFRAMES ===")]
        public TimeFrame TrendTF { get; set; }

        [Parameter("Entry TF", DefaultValue = "Minute", Group = "=== TIMEFRAMES ===")]
        public TimeFrame EntryTF { get; set; }

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

        [Parameter("Enable Cross Position", DefaultValue = true, Group = "=== ENTRY ===")]
        public bool EnableCrossPosition { get; set; }

        [Parameter("Enable Touch Fast (33)", DefaultValue = false, Group = "=== ENTRY ===")]
        public bool EnableTouchFast { get; set; }

        [Parameter("Enable Touch Slow (144)", DefaultValue = false, Group = "=== ENTRY ===")]
        public bool EnableTouchSlow { get; set; }

        [Parameter("Entry Trigger", DefaultValue = EntryTriggerMode.MACross, Group = "=== ENTRY ===")]
        public EntryTriggerMode EntryTrigger { get; set; }

        [Parameter("Max Broker Spread (points)", DefaultValue = 800, Group = "=== ENTRY ===")]
        public double MaxSpread { get; set; }

        [Parameter("Min Price Dist from Band (pts - CROSS)", DefaultValue = 1, Group = "=== ENTRY ===")]
        public double MinDistFromBand { get; set; }

        [Parameter("Risk per Trade ($)", DefaultValue = 970.0, MinValue = 1.0, Group = "=== RISK & SL (CASH) ===")]
        public double RiskCash { get; set; }

        [Parameter("Min SL (pips - for chart geometry)", DefaultValue = 40.0, MinValue = 0.1, Group = "=== RISK & SL (CASH) ===")]
        public double MinSLPips { get; set; }

        [Parameter("Max SL (pips - for chart geometry)", DefaultValue = 250.0, MinValue = 0.1, Group = "=== RISK & SL (CASH) ===")]
        public double MaxSLPips { get; set; }

        [Parameter("SL Calculation Mode", DefaultValue = SLMode.Percent, Group = "=== RISK & SL (CASH) ===")]
        public SLMode SlMode { get; set; }

        [Parameter("SL % from Entry Price (If Percent)", DefaultValue = 0.5, MinValue = 0.01, Group = "=== RISK & SL (CASH) ===")]
        public double SlPercent { get; set; }

        [Parameter("Bars for Swing SL", DefaultValue = 26, MinValue = 2, Group = "=== RISK & SL (CASH) ===")]
        public int SwingBars { get; set; }

        [Parameter("Swing SL Buffer (pips)", DefaultValue = 15.0, MinValue = 0.0, Group = "=== RISK & SL (CASH) ===")]
        public double SwingBufferPips { get; set; }
        
        [Parameter("SL Multiplier 144 (cross only)", DefaultValue = 2.0, MinValue = 0.1, Group = "=== RISK & SL (CASH) ===")]
        public double SlBandWidth144Multiplier { get; set; }

        [Parameter("Take Profit 1 ($)", DefaultValue = 800.0, MinValue = 1.0, Group = "=== POSITION MANAGEMENT ($) ===")]
        public double Tp1Cash { get; set; }

        [Parameter("% Volume to Close at TP1", DefaultValue = 95.0, MinValue = 1.0, MaxValue = 100.0, Group = "=== POSITION MANAGEMENT ($) ===")]
        public double PartialClosePercent { get; set; }

        [Parameter("Trailing Stop Active", DefaultValue = true, Group = "=== POSITION MANAGEMENT ($) ===")]
        public bool TrailingOn { get; set; }

        [Parameter("Trailing Mode", DefaultValue = TrailingMode.CashDistance, Group = "=== POSITION MANAGEMENT ($) ===")]
        public TrailingMode TrailingType { get; set; }

        [Parameter("MA Trailing Line (if MA)", DefaultValue = MATrailingLine.SlowHighLow, Group = "=== POSITION MANAGEMENT ($) ===")]
        public MATrailingLine MaTrailingLine { get; set; }

        [Parameter("Trailing Start Profit ($) [If Cash Mode]", DefaultValue = 15.0, MinValue = 1.0, Group = "=== POSITION MANAGEMENT ($) ===")]
        public double TrailingStartCash { get; set; }

        [Parameter("Trailing Drawdown/Distance ($) [If Cash Mode]", DefaultValue = 15.0, MinValue = 1.0, Group = "=== POSITION MANAGEMENT ($) ===")]
        public double TrailingDistanceCash { get; set; }

        [Parameter("Trailing Buffer (pips) [If MA Mode]", DefaultValue = 15.0, MinValue = 0.1, Group = "=== POSITION MANAGEMENT ($) ===")]
        public double TrailingBufferPips { get; set; }

        [Parameter("Manual TP2 (disables tick touch) [If MA Mode]", DefaultValue = false, Group = "=== POSITION MANAGEMENT ($) ===")]
        public bool ManualTP2 { get; set; }

        [Parameter("Start Hour", DefaultValue = 2, Group = "=== TRADING WINDOW ===")]
        public int StartHour { get; set; }

        [Parameter("End Hour", DefaultValue = 22, Group = "=== TRADING WINDOW ===")]
        public int EndHour { get; set; }

        [Parameter("Block entries N hours before window ends", DefaultValue = 2, MinValue = 0, Group = "=== TRADING WINDOW ===")]
        public int WindowBlockHours { get; set; }

        [Parameter("Close EOD Everyday", DefaultValue = false, Group = "=== TRADING WINDOW ===")]
        public bool EodCloseEveryday { get; set; }

        [Parameter("Close EOD on FRIDAY", DefaultValue = false, Group = "=== TRADING WINDOW ===")]
        public bool EodCloseFriday { get; set; }

        [Parameter("EOD Move SL to BE (if in profit)", DefaultValue = false, Group = "=== TRADING WINDOW ===")]
        public bool EodMoveBe { get; set; }

        [Parameter("Enable FTMO Protection", DefaultValue = true, Group = "=== FTMO PROTECTION ===")]
        public bool FtmoProtection { get; set; }

        [Parameter("Max Daily Loss ($, 0 = off)", DefaultValue = 3000.0, MinValue = 0.0, Group = "=== FTMO PROTECTION ===")]
        public double MaxDailyLossCash { get; set; }

        [Parameter("Daily Profit Target ($, 0 = off)", DefaultValue = 0.0, MinValue = 0.0, Group = "=== FTMO PROTECTION ===")]
        public double DailyTargetCash { get; set; }

        [Parameter("Max Trades per Day (0 = off)", DefaultValue = 4, MinValue = 0, Group = "=== FTMO PROTECTION ===")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Max Consecutive Losses per Day (0 = off)", DefaultValue = 3, MinValue = 0, Group = "=== FTMO PROTECTION ===")]
        public int MaxConsecLosses { get; set; }

        [Parameter("Close Bot Positions on Daily Loss", DefaultValue = true, Group = "=== FTMO PROTECTION ===")]
        public bool CloseOnDailyLoss { get; set; }

        [Parameter("Close ALL Account Positions on Daily Loss", DefaultValue = false, Group = "=== FTMO PROTECTION ===")]
        public bool CloseAllOnDailyLoss { get; set; }

        [Parameter("Day Reset Time Zone (Windows ID)", DefaultValue = "Central European Standard Time", Group = "=== FTMO PROTECTION ===")]
        public string DayResetTimeZone { get; set; }

        [Parameter("Enable Logs", DefaultValue = false, Group = "=== UI & LOGS ===")]
        public bool EnableLogs { get; set; }

        [Parameter("Enable Dashboard", DefaultValue = true, Group = "=== UI & LOGS ===")]
        public bool EnableDashboard { get; set; }

        private Bars _trendBars, _entryBars;
        private MovingAverage _tFastHigh, _tFastLow, _tSlowHigh, _tSlowLow;
        private MovingAverage _eFastHigh, _eFastLow, _eSlowHigh, _eSlowLow;

        private BotState _state = BotState.WaitTrend;
        private TrendDir _trend = TrendDir.None;

        private bool _pullbackDetected = false;
        private DateTime _lastTrendBarTime;
        private DateTime _lastEntryBarTime;
        private bool _previousEntrySignal = false;

        private int _tradeCounter = 1;
        private readonly Dictionary<int, TradeContext> _tradeContexts = new Dictionary<int, TradeContext>();

        private TradeType _campaignDirection;
        private double _campaignStopLoss = double.NaN;
        private bool _campaignCrossTriggered = false;
        private bool _touchFastUsed = false;
        private bool _touchSlowUsed = false;
        private bool _campaignHadPositions = false;

        // FTMO PROTECTION
        private TimeZoneInfo _dayTz;
        private DateTime _ftmoDay;
        private double _dayStartBalance;
        private int _tradesToday;
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

            InitFtmoProtection();
            RecoverState();
        }

        protected override void OnTick()
        {
            bool isTradeTime = IsTradeTime();
            double spreadPoints = Symbol.Spread / Symbol.TickSize;

            CheckFtmoProtection();

            DoWaitTrend();
            SyncTradeContexts();

            var myPositions = GetMyPositions();

            if (_state == BotState.InCampaign && myPositions.Count == 0 &&
                (_campaignHadPositions || double.IsNaN(_campaignStopLoss)))
                ResetAfterCampaignClose("No active campaign positions");

            if (myPositions.Count > 0)
            {
                foreach (var pos in myPositions.ToList())
                {
                    if (!_tradeContexts.TryGetValue(pos.Id, out var ctx))
                    {
                        ctx = ReconstructContext(pos);
                        _tradeContexts[pos.Id] = ctx;
                    }

                    if (!isTradeTime)
                    {
                        bool isFriday = Server.Time.DayOfWeek == DayOfWeek.Friday;
                        bool afterWindowEnd = StartHour < EndHour ? Server.Time.Hour >= EndHour : true;
                        if (EodCloseEveryday || (EodCloseFriday && isFriday && afterWindowEnd))
                        {
                            ClosePosition(pos);
                            continue;
                        }

                        if (EodMoveBe && !ctx.TrailingActive && !ctx.EodBeApplied)
                        {
                            bool inProfit = (pos.TradeType == TradeType.Buy  && Symbol.Bid > ctx.OriginalEntry) ||
                                            (pos.TradeType == TradeType.Sell && Symbol.Ask < ctx.OriginalEntry);
                            bool slWorseThanBe = pos.StopLoss == null ||
                                                 (pos.TradeType == TradeType.Buy  && pos.StopLoss.Value < ctx.OriginalEntry) ||
                                                 (pos.TradeType == TradeType.Sell && pos.StopLoss.Value > ctx.OriginalEntry);
                            if (inProfit && slWorseThanBe)
                            {
                                var beResult = pos.ModifyStopLossPrice(ctx.OriginalEntry);
                                if (beResult.IsSuccessful) ctx.EodBeApplied = true;
                            }
                        }
                    }

                    DoManagePosition(pos, ctx);
                }

                SyncTradeContexts();
                myPositions = GetMyPositions();
                if (myPositions.Count == 0)
                {
                    ResetAfterCampaignClose("All campaign positions closed");
                    UpdateDashboard();
                    return;
                }
            }

            switch (_state)
            {
                case BotState.WaitTrend: break;
                case BotState.WaitPullback:
                    if (_trend != TrendDir.None) DoWaitPullback();
                    break;
                case BotState.WaitEntry:
                    if (_trend != TrendDir.None) { CheckHtfPullbackReset(); DoWaitEntry(isTradeTime, spreadPoints); }
                    break;
                case BotState.InCampaign:
                    DoCampaignTouchEntries(isTradeTime, spreadPoints);
                    break;
            }

            UpdateDashboard();
        }

        private void DoWaitTrend()
        {
            if (_tFastHigh.Result.Count < TrendSlowPeriod + 5) return;
            if (_tSlowLow.Result.Count  < TrendSlowPeriod + 5) return;
            if (_tFastHigh.Result.Count < 2 || _tSlowHigh.Result.Count < 2) return;

            double bHigh = _tFastHigh.Result.Last(1);
            double bLow  = _tFastLow.Result.Last(1);
            double rHigh = _tSlowHigh.Result.Last(1);
            double rLow  = _tSlowLow.Result.Last(1);

            if (double.IsNaN(bHigh) || double.IsNaN(bLow) || double.IsNaN(rHigh) || double.IsNaN(rLow)) return;

            TrendDir prevTrend = _trend;

            if      (bLow > rHigh) _trend = TrendDir.Up;
            else if (bHigh < rLow) _trend = TrendDir.Down;
            else                   _trend = TrendDir.None;

            if (_trend != prevTrend)
            {
                _pullbackDetected = false;
                _previousEntrySignal = false;
                ResetCampaignSetupFlags();

                if (_state == BotState.InCampaign) return;

                if (_trend == TrendDir.None) _state = BotState.WaitTrend;
                else _state = BotState.WaitPullback;
            }
        }

        private void DoWaitPullback()
        {
            if (_trendBars.OpenTimes.Last(0) == _lastTrendBarTime) return;

            double h    = _trendBars.HighPrices.Last(1);
            double l    = _trendBars.LowPrices.Last(1);
            double bHigh = _tFastHigh.Result.Last(1);
            double bLow  = _tFastLow.Result.Last(1);

            bool touched = (_trend == TrendDir.Up   && l <= bHigh) ||
                           (_trend == TrendDir.Down  && h >= bLow);

            if (touched)
            {
                _pullbackDetected = true;
                _state = BotState.WaitEntry;
            }

            _lastTrendBarTime = _trendBars.OpenTimes.Last(0);
        }

        private void DoWaitEntry(bool isTradeTime, double spreadPoints)
        {
            if (_entryBars.OpenTimes.Last(0) == _lastEntryBarTime) return;

            bool signal = EvaluateEntrySignal();

            if (!signal)                      { _previousEntrySignal = false; return; }
            if (signal == _previousEntrySignal) return;

            if (!isTradeTime) return;

            if (WindowBlockHours > 0 && StartHour != EndHour)
            {
                int hour = Server.Time.Hour;
                int hoursRemaining = StartHour < EndHour
                    ? EndHour - hour
                    : hour >= StartHour ? (24 - hour) + EndHour : EndHour - hour;
                if (hoursRemaining <= WindowBlockHours) return;
            }

            if (spreadPoints > MaxSpread) return;

            _previousEntrySignal = true;

            double htfFastHigh  = _tFastHigh.Result.Last(0);
            double htfFastLow   = _tFastLow.Result.Last(0);
            double ltfFastHigh1 = _eFastHigh.Result.Last(1);
            double ltfFastLow1  = _eFastLow.Result.Last(1);

            TradeType tradeType;
            if (_trend == TrendDir.Up)
            {
                tradeType = TradeType.Buy;
                if (Symbol.Ask <= htfFastHigh) return;
                double distance = (Symbol.Ask - ltfFastHigh1) / Symbol.TickSize;
                if (distance < MinDistFromBand) return;
            }
            else
            {
                tradeType = TradeType.Sell;
                if (Symbol.Bid >= htfFastLow) return;
                double distance = (ltfFastLow1 - Symbol.Bid) / Symbol.TickSize;
                if (distance < MinDistFromBand) return;
            }

            double entryPrice       = tradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;
            double calculatedSlPrice = CalcStopLoss(tradeType, entryPrice);

            if (double.IsNaN(calculatedSlPrice)) return;
            if (!ValidateStopLoss(entryPrice, calculatedSlPrice, tradeType, out _, out _)) return;

            StartCampaign(tradeType, calculatedSlPrice);

            bool opened = EnableCrossPosition && TryOpenCampaignPosition(CampaignEntryType.Cross);

            _lastEntryBarTime = _entryBars.OpenTimes.Last(0);

            if (!opened && !EnableTouchFast && !EnableTouchSlow)
                ResetAfterCampaignClose("Cross entry not opened");
        }

        private void StartCampaign(TradeType tradeType, double stopLossPrice)
        {
            _campaignDirection    = tradeType;
            _campaignStopLoss     = stopLossPrice;
            _campaignCrossTriggered = true;
            _touchFastUsed        = false;
            _touchSlowUsed        = false;
            _campaignHadPositions = false;
            _state = BotState.InCampaign;
        }

        private void DoCampaignTouchEntries(bool isTradeTime, double spreadPoints)
        {
            if (!_campaignCrossTriggered || !_pullbackDetected) return;
            if (!isTradeTime || spreadPoints > MaxSpread) return;

            if (WindowBlockHours > 0 && StartHour != EndHour)
            {
                int hour = Server.Time.Hour;
                int hoursRemaining = StartHour < EndHour
                    ? EndHour - hour
                    : hour >= StartHour ? (24 - hour) + EndHour : EndHour - hour;
                if (hoursRemaining <= WindowBlockHours) return;
            }

            if (EnableTouchFast && !_touchFastUsed && TouchConditionMet(CampaignEntryType.TouchFast))
                if (TryOpenCampaignPosition(CampaignEntryType.TouchFast)) _touchFastUsed = true;

            if (EnableTouchSlow && !_touchSlowUsed && TouchConditionMet(CampaignEntryType.TouchSlow))
                if (TryOpenCampaignPosition(CampaignEntryType.TouchSlow)) _touchSlowUsed = true;
        }

        private bool TouchConditionMet(CampaignEntryType entryType)
        {
            if (entryType == CampaignEntryType.TouchFast)
            {
                if (_campaignDirection == TradeType.Buy)  return Symbol.Bid <= _eFastHigh.Result.Last(0);
                else                                       return Symbol.Ask >= _eFastLow.Result.Last(0);
            }
            if (entryType == CampaignEntryType.TouchSlow)
            {
                if (_campaignDirection == TradeType.Buy)  return Symbol.Bid <= _eSlowHigh.Result.Last(0);
                else                                       return Symbol.Ask >= _eSlowLow.Result.Last(0);
            }
            return false;
        }

        private bool TryOpenCampaignPosition(CampaignEntryType entryType)
        {
            if (double.IsNaN(_campaignStopLoss)) return false;
            if (!FtmoAllowsNewTrade()) return false;

            var existingSameType = GetMyPositions().FirstOrDefault(p =>
                _tradeContexts.TryGetValue(p.Id, out var c) && c.EntryType == entryType);
            if (existingSameType != null) return false;

            TradeType tradeType  = _campaignDirection;
            double entryPrice    = tradeType == TradeType.Buy ? Symbol.Ask : Symbol.Bid;

            if (!ValidateStopLoss(entryPrice, _campaignStopLoss, tradeType, out double slDistancePips, out _))
                return false;

            double volumeInUnits = CalcVolumeFromRisk(slDistancePips, out double riskAmount, out double quantityLots);
            if (volumeInUnits <= 0) return false;

            string suffix       = entryType == CampaignEntryType.Cross ? "X" : entryType == CampaignEntryType.TouchFast ? "T33" : "T144";
            string currentLabel = $"{BotLabel}_{_tradeCounter}_{suffix}";

            var result = ExecuteMarketOrder(tradeType, SymbolName, volumeInUnits, currentLabel);
            if (!result.IsSuccessful) return false;

            _tradeCounter++;
            var pos = result.Position;

            var modifyResult = ModifyPosition(pos, _campaignStopLoss, null, ProtectionType.Absolute);
            if (!modifyResult.IsSuccessful)
            {
                Print("SL not set ({0}) – closing position {1}", modifyResult.Error, pos.Id);
                ClosePosition(pos);
                return false;
            }

            _tradesToday++;

            double tp1Pips = Tp1Cash / (pos.VolumeInUnits * Symbol.PipValue);
            double tp1Price = tradeType == TradeType.Buy 
                ? pos.EntryPrice + tp1Pips * Symbol.PipSize 
                : pos.EntryPrice - tp1Pips * Symbol.PipSize;

            _tradeContexts[pos.Id] = new TradeContext
            {
                OriginalEntry           = pos.EntryPrice,
                Tp1Price                = Math.Round(tp1Price, Symbol.Digits),
                Tp1Hit                  = false,
                TrailingActive          = false,
                EodBeApplied            = false,
                MaxFavorablePrice       = pos.EntryPrice,
                EntryType               = entryType
            };
            _campaignHadPositions = true;

            return true;
        }

        private void DoManagePosition(Position pos, TradeContext ctx)
        {
            if (pos.TradeType == TradeType.Buy && Symbol.Bid > ctx.MaxFavorablePrice)
                ctx.MaxFavorablePrice = Symbol.Bid;
            else if (pos.TradeType == TradeType.Sell && Symbol.Ask < ctx.MaxFavorablePrice)
                ctx.MaxFavorablePrice = Symbol.Ask;

            if (!ctx.Tp1Hit)
            {
                bool tp1Reached = (pos.TradeType == TradeType.Buy  && Symbol.Bid >= ctx.Tp1Price) ||
                                  (pos.TradeType == TradeType.Sell && Symbol.Ask <= ctx.Tp1Price);
                if (tp1Reached)
                {
                    double partialVol = Symbol.NormalizeVolumeInUnits(
                        pos.VolumeInUnits * PartialClosePercent / 100.0, RoundingMode.Down);

                    if (partialVol >= Symbol.VolumeInUnitsMin && PartialClosePercent < 100.0)
                    {
                        ClosePosition(pos, partialVol);
                        ctx.Tp1Hit = true;
                        pos = Positions.FirstOrDefault(p => p.Id == pos.Id);
                        if (pos != null)
                        {
                            pos.ModifyTakeProfitPrice(null);
                            pos.ModifyStopLossPrice(ctx.OriginalEntry);
                        }
                        return; // Odtworzono zachowanie z v0.4: Wychodzi natychmiast
                    }
                    else
                    {
                        ClosePosition(pos);
                        _tradeContexts.Remove(pos.Id);
                        return; // Odtworzono zachowanie z v0.4: Trailing nigdy nie włącza się, jeśli TP1 to 100%
                    }
                }
            }

            // ODTWORZONO ZACHOWANIE Z v0.4: Trailing włącza się TYLKO PO zaliczeniu TP1
            if (ctx.Tp1Hit && TrailingOn)
            {
                if (TrailingType == TrailingMode.CashDistance)
                {
                    double currentVolumePipValue = pos.VolumeInUnits * Symbol.PipValue;
                    double startPips = TrailingStartCash / currentVolumePipValue;
                    double distPips  = TrailingDistanceCash / currentVolumePipValue;

                    double currentProfitPips = pos.TradeType == TradeType.Buy 
                        ? (Symbol.Bid - ctx.OriginalEntry) / Symbol.PipSize 
                        : (ctx.OriginalEntry - Symbol.Ask) / Symbol.PipSize;

                    if (!ctx.TrailingActive && currentProfitPips >= startPips)
                        ctx.TrailingActive = true;

                    if (ctx.TrailingActive)
                    {
                        double newSL;
                        if (pos.TradeType == TradeType.Buy)
                        {
                            newSL = Math.Round(ctx.MaxFavorablePrice - (distPips * Symbol.PipSize), Symbol.Digits);
                            if ((pos.StopLoss == null || newSL > pos.StopLoss.Value + Symbol.PipSize) && newSL < Symbol.Bid)
                                pos.ModifyStopLossPrice(newSL);
                        }
                        else
                        {
                            newSL = Math.Round(ctx.MaxFavorablePrice + (distPips * Symbol.PipSize), Symbol.Digits);
                            if ((pos.StopLoss == null || newSL < pos.StopLoss.Value - Symbol.PipSize) && newSL > Symbol.Ask)
                                pos.ModifyStopLossPrice(newSL);
                        }
                    }
                }
                else // MATrailing
                {
                    double bMid0 = (_eFastHigh.Result.Last(0) + _eFastLow.Result.Last(0)) / 2.0;

                    if (!ctx.TrailingActive)
                    {
                        bool activate = (pos.TradeType == TradeType.Buy  && bMid0 >= ctx.OriginalEntry) ||
                                        (pos.TradeType == TradeType.Sell && bMid0 <= ctx.OriginalEntry);
                        if (activate) ctx.TrailingActive = true;
                    }

                    if (ctx.TrailingActive)
                    {
                        double trailRef = GetMATrailingReferenceValue(pos.TradeType);

                        if (!ManualTP2)
                        {
                            bool touch = (pos.TradeType == TradeType.Buy  && Symbol.Bid <= trailRef) ||
                                         (pos.TradeType == TradeType.Sell && Symbol.Ask >= trailRef);
                            if (touch)
                            {
                                ClosePosition(pos);
                                _tradeContexts.Remove(pos.Id);
                                return;
                            }
                        }

                        double buffer = TrailingBufferPips * Symbol.PipSize;
                        if (pos.TradeType == TradeType.Buy)
                        {
                            double newSL = Math.Round(trailRef - buffer, Symbol.Digits);
                            if ((pos.StopLoss == null || newSL > pos.StopLoss.Value + Symbol.PipSize) && newSL < Symbol.Bid)
                                pos.ModifyStopLossPrice(newSL);
                        }
                        else
                        {
                            double newSL = Math.Round(trailRef + buffer, Symbol.Digits);
                            if ((pos.StopLoss == null || newSL < pos.StopLoss.Value - Symbol.PipSize) && newSL > Symbol.Ask)
                                pos.ModifyStopLossPrice(newSL);
                        }
                    }
                }
            }
        }

        private double GetMATrailingReferenceValue(TradeType tradeType)
        {
            if (tradeType == TradeType.Buy)
            {
                if (MaTrailingLine == MATrailingLine.FastLowHigh) return _eFastLow.Result.Last(0);
                if (MaTrailingLine == MATrailingLine.SlowHighLow) return _eSlowHigh.Result.Last(0);
                if (MaTrailingLine == MATrailingLine.SlowLowHigh) return _eSlowLow.Result.Last(0);
            }
            else
            {
                if (MaTrailingLine == MATrailingLine.FastLowHigh) return _eFastHigh.Result.Last(0);
                if (MaTrailingLine == MATrailingLine.SlowHighLow) return _eSlowLow.Result.Last(0);
                if (MaTrailingLine == MATrailingLine.SlowLowHigh) return _eSlowHigh.Result.Last(0);
            }
            return _eFastLow.Result.Last(0);
        }

        private double CalcStopLoss(TradeType tradeType, double entryPrice)
        {
            if (SlMode == SLMode.Percent)
            {
                double dist = entryPrice * (SlPercent / 100.0);
                return Math.Round(tradeType == TradeType.Buy ? entryPrice - dist : entryPrice + dist, Symbol.Digits);
            }

            if (SlMode == SLMode.Swing)
            {
                if (_entryBars == null || _entryBars.Count < SwingBars + 2) return double.NaN;
                
                double lowestLow = double.MaxValue, highestHigh = double.MinValue;
                for (int i = 1; i <= SwingBars; i++)
                {
                    double low = _entryBars.LowPrices.Last(i), high = _entryBars.HighPrices.Last(i);
                    if (low < lowestLow)    lowestLow    = low;
                    if (high > highestHigh) highestHigh  = high;
                }
                double buf = SwingBufferPips * Symbol.PipSize;
                return Math.Round(tradeType == TradeType.Buy ? lowestLow - buf : highestHigh + buf, Symbol.Digits);
            }
            
            if (SlMode == SLMode.BandWidth144)
            {
                if (_eSlowHigh == null || _eSlowLow == null || _eSlowHigh.Result.Count < 2 || _eSlowLow.Result.Count < 2)
                    return double.NaN;

                double slowHigh = _eSlowHigh.Result.Last(1);
                double slowLow  = _eSlowLow.Result.Last(1);
                double bandWidth = slowHigh - slowLow;

                if (bandWidth <= 0) return double.NaN;

                double slPrice = tradeType == TradeType.Buy
                    ? slowHigh - bandWidth * SlBandWidth144Multiplier
                   : slowLow  + bandWidth * SlBandWidth144Multiplier;

                return Math.Round(slPrice, Symbol.Digits);
            }
            return double.NaN;
        }

        private bool ValidateStopLoss(double entryPrice, double slPrice, TradeType tradeType,
            out double slDistancePips, out double slDistancePrice)
        {
            slDistancePrice = Math.Abs(entryPrice - slPrice);
            slDistancePips  = slDistancePrice / Symbol.PipSize;

            if (double.IsNaN(slPrice) || slDistancePips <= 0) return false;
            if (tradeType == TradeType.Buy  && slPrice >= entryPrice) return false;
            if (tradeType == TradeType.Sell && slPrice <= entryPrice) return false;
            if (slDistancePips < MinSLPips) return false;
            if (slDistancePips > MaxSLPips) return false;
            
            return true;
        }

        private double CalcVolumeFromRisk(double slDistancePips, out double riskAmount, out double quantityLots)
        {
            riskAmount   = RiskCash;
            quantityLots = 0;

            if (slDistancePips <= 0 || Symbol.PipValue <= 0) return 0;

            double rawUnits = riskAmount / (slDistancePips * Symbol.PipValue);
            quantityLots = Symbol.VolumeInUnitsToQuantity(rawUnits);
            double normUnits = Symbol.NormalizeVolumeInUnits(rawUnits, RoundingMode.Down);
            
            if (rawUnits < Symbol.VolumeInUnitsMin) return 0;
            if (normUnits < Symbol.VolumeInUnitsMin) return 0;
            if (normUnits > Symbol.VolumeInUnitsMax) normUnits = Symbol.VolumeInUnitsMax;
            
            return normUnits;
        }

        private bool IsTradeTime()
        {
            int hour = Server.Time.Hour;
            if (StartHour == EndHour) return true;
            if (StartHour < EndHour)  return hour >= StartHour && hour < EndHour;
            return hour >= StartHour || hour < EndHour;
        }

        private bool EvaluateEntrySignal()
        {
            if (_eFastHigh == null || _eFastLow == null || _eSlowHigh == null || _eSlowLow == null)
                return false;

            if (_eFastHigh.Result.Count < 3 || _eFastLow.Result.Count < 3 ||
                _eSlowHigh.Result.Count < 3 || _eSlowLow.Result.Count < 3)
                return false;

            double fHigh_1 = _eFastHigh.Result.Last(1);
            double fLow_1  = _eFastLow.Result.Last(1);
            double sHigh_1 = _eSlowHigh.Result.Last(1);
            double sLow_1  = _eSlowLow.Result.Last(1);

            double fHigh_2 = _eFastHigh.Result.Last(2);
            double fLow_2  = _eFastLow.Result.Last(2);
            double sHigh_2 = _eSlowHigh.Result.Last(2);
            double sLow_2  = _eSlowLow.Result.Last(2);

            double fMid_1 = (fHigh_1 + fLow_1) / 2.0;
            double sMid_1 = (sHigh_1 + sLow_1) / 2.0;
            if (Math.Abs(fMid_1 - sMid_1) / Symbol.PipSize < MinMaSpreadPips) return false;

            if (EntryTrigger == EntryTriggerMode.MACross)
            {
                double fMid_2 = (fHigh_2 + fLow_2) / 2.0;
                double sMid_2 = (sHigh_2 + sLow_2) / 2.0;

                if (_trend == TrendDir.Up)
                    return fMid_2 <= sMid_2 && fMid_1 > sMid_1;
                else
                    return fMid_2 >= sMid_2 && fMid_1 < sMid_1;
            }
            else 
            {
                if (_trend == TrendDir.Up)
                    return fLow_1 > sHigh_1  
                        && fLow_2 <= sHigh_2; 
                else
                    return fHigh_1 < sLow_1
                        && fHigh_2 >= sLow_2;
            }
        }

        private void CheckHtfPullbackReset()
        {
            double bHigh = _tFastHigh.Result.Last(0);
            double bLow  = _tFastLow.Result.Last(0);

            bool touched = (_trend == TrendDir.Up   && Symbol.Bid <= bHigh) ||
                           (_trend == TrendDir.Down  && Symbol.Ask >= bLow);

            if (touched)
            {
                bool currentSignal = EvaluateEntrySignal();
                if (_previousEntrySignal != currentSignal)
                {
                    _previousEntrySignal = currentSignal;
                }
            }
        }

        private void RecoverState()
        {
            var myPositions = GetMyPositions();
            foreach (var pos in myPositions)
                _tradeContexts[pos.Id] = ReconstructContext(pos);

            if (myPositions.Count > 0)
            {
                _state                = BotState.InCampaign;
                _campaignDirection    = myPositions[0].TradeType;
                _campaignStopLoss     = myPositions[0].StopLoss ?? double.NaN;
                _campaignCrossTriggered = true;
                _pullbackDetected     = true;
                _campaignHadPositions = true; 
            }
        }

        private void ResetAfterCampaignClose(string reason = "")
        {
            _tradeContexts.Clear();
            ResetCampaignSetupFlags();

            _state = _trend == TrendDir.None ? BotState.WaitTrend
                   : _pullbackDetected       ? BotState.WaitEntry
                                             : BotState.WaitPullback;

            _previousEntrySignal = EvaluateEntrySignal();
            if (_entryBars != null && _entryBars.Count > 0)
                _lastEntryBarTime = _entryBars.OpenTimes.Last(0);
        }

        private void ResetCampaignSetupFlags()
        {
            _campaignStopLoss       = double.NaN;
            _campaignCrossTriggered = false;
            _touchFastUsed          = false;
            _touchSlowUsed          = false;
            _campaignHadPositions   = false; 
        }

        // ───────────────────────── FTMO PROTECTION ─────────────────────────
        private void InitFtmoProtection()
        {
            try { _dayTz = TimeZoneInfo.FindSystemTimeZoneById(DayResetTimeZone); }
            catch (Exception)
            {
                Print("Time zone '{0}' not found, using UTC for daily reset", DayResetTimeZone);
                _dayTz = TimeZoneInfo.Utc;
            }

            StartFtmoDay(FtmoLocalNow().Date);
            Positions.Closed += OnFtmoPositionClosed;
        }

        private DateTime FtmoLocalNow() =>
            TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(Server.Time, DateTimeKind.Utc), _dayTz);

        private void StartFtmoDay(DateTime day)
        {
            _ftmoDay         = day;
            _dayStartBalance = Account.Balance;
            _tradesToday     = 0;
            _consecLosses    = 0;
            _dayBlocked      = false;
            _dayBlockReason  = "";
        }

        private bool IsMyPosition(Position p) =>
            p.SymbolName == SymbolName && p.Label != null && p.Label.StartsWith(BotLabel);

        private void CheckFtmoProtection()
        {
            if (!FtmoProtection) return;

            var today = FtmoLocalNow().Date;
            if (today != _ftmoDay) StartFtmoDay(today);

            double dayPnl = Account.Equity - _dayStartBalance;

            if (!_dayBlocked && MaxDailyLossCash > 0 && dayPnl <= -MaxDailyLossCash)
            {
                BlockFtmoDay(string.Format("daily loss {0:F0} $", dayPnl));
                foreach (var p in Positions.ToList())
                    if (CloseAllOnDailyLoss || (CloseOnDailyLoss && IsMyPosition(p)))
                        ClosePosition(p);
            }

            if (!_dayBlocked && DailyTargetCash > 0 && dayPnl >= DailyTargetCash)
                BlockFtmoDay(string.Format("daily target {0:F0} $", dayPnl));
        }

        private bool FtmoAllowsNewTrade()
        {
            if (!FtmoProtection) return true;
            if (_dayBlocked) return false;
            if (MaxTradesPerDay > 0 && _tradesToday >= MaxTradesPerDay)
            {
                BlockFtmoDay(string.Format("{0} trades today", _tradesToday));
                return false;
            }
            return true;
        }

        private void BlockFtmoDay(string reason)
        {
            _dayBlocked     = true;
            _dayBlockReason = reason;
            Print("FTMO PROTECTION – entries blocked until next day: {0}", reason);
        }

        private void OnFtmoPositionClosed(PositionClosedEventArgs args)
        {
            var pos = args.Position;
            if (!FtmoProtection || !IsMyPosition(pos)) return;

            // Wynik całej pozycji razem z częściowym zamknięciem na TP1
            double net = History.Where(h => h.PositionId == pos.Id).Sum(h => h.NetProfit);
            _consecLosses = net < 0 ? _consecLosses + 1 : 0;

            if (!_dayBlocked && MaxConsecLosses > 0 && _consecLosses >= MaxConsecLosses)
                BlockFtmoDay(string.Format("{0} losses in a row", _consecLosses));
        }

        private string FtmoDashboardText()
        {
            if (!FtmoProtection) return "🧯 FTMO protection: OFF";
            double dayPnl = Account.Equity - _dayStartBalance;
            return $"🧯 Today: {dayPnl:F2} $ (limit -{MaxDailyLossCash:F0} $) | Trades: {_tradesToday}" +
                   (MaxTradesPerDay > 0 ? $"/{MaxTradesPerDay}" : "") +
                   $" | Losses in a row: {_consecLosses}\n" +
                   (_dayBlocked ? $"⛔ BLOCKED: {_dayBlockReason}" : "✅ Entries allowed");
        }

        private List<Position> GetMyPositions() =>
            Positions.Where(p => p.SymbolName == SymbolName && p.Label != null && p.Label.StartsWith(BotLabel)).ToList();

        private void SyncTradeContexts()
        {
            var currentIds = GetMyPositions().Select(p => p.Id).ToHashSet();
            foreach (var key in _tradeContexts.Keys.Where(id => !currentIds.Contains(id)).ToList())
                _tradeContexts.Remove(key);
        }

        private TradeContext ReconstructContext(Position pos)
        {
            double tp1Pips = Tp1Cash / (pos.VolumeInUnits * Symbol.PipValue);
            double tp1Price = pos.TradeType == TradeType.Buy 
                ? pos.EntryPrice + tp1Pips * Symbol.PipSize 
                : pos.EntryPrice - tp1Pips * Symbol.PipSize;

            string label = pos.Label ?? string.Empty;
            CampaignEntryType entryType =
                label.EndsWith("_T33")  ? CampaignEntryType.TouchFast  :
                label.EndsWith("_T144") ? CampaignEntryType.TouchSlow  : CampaignEntryType.Cross;

            bool slAtOrBeyondBe = pos.StopLoss.HasValue &&
                ((pos.TradeType == TradeType.Buy  && pos.StopLoss.Value >= pos.EntryPrice) ||
                 (pos.TradeType == TradeType.Sell && pos.StopLoss.Value <= pos.EntryPrice));

            return new TradeContext
            {
                OriginalEntry          = pos.EntryPrice,
                Tp1Price               = Math.Round(tp1Price, Symbol.Digits),
                Tp1Hit                 = slAtOrBeyondBe,
                TrailingActive         = false,
                EodBeApplied           = false,
                MaxFavorablePrice      = pos.TradeType == TradeType.Buy ? Symbol.Bid : Symbol.Ask,
                EntryType              = entryType
            };
        }

        private void UpdateDashboard(string extra = "")
        {
            if (!EnableDashboard) { Chart.RemoveObject("Dashboard"); return; }

            string trendStr = _trend == TrendDir.Up ? "📈 UPTREND" :
                              _trend == TrendDir.Down ? "📉 DOWNTREND" : "⛔ NONE";

            string[] stateLabels = { "Waiting for trend direction", "Waiting for pullback", "Waiting for band cross", "CAMPAIGN ACTIVE" };

            int trendCount = _trendBars?.Count ?? 0;
            int entryCount = _entryBars?.Count ?? 0;
            int posCount   = GetMyPositions().Count;

            string text = $@"=== BOT DASHBOARD ===
🔹 Instrument: {SymbolName} | Trend: {TrendTF} | Entry: {EntryTF}
🔹 Risk: ${RiskCash:F2} | TP1: ${Tp1Cash:F2}
📍 State: {stateLabels[(int)_state]} | Trend: {trendStr}
🛡️ Positions: {posCount} | SL: {(double.IsNaN(_campaignStopLoss) ? "None" : _campaignStopLoss.ToString("F5"))}
{FtmoDashboardText()}
{extra}";

            Chart.DrawStaticText("Dashboard", text, VerticalAlignment.Top, HorizontalAlignment.Left, Color.White);
        }
    }
}