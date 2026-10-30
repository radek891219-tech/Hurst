// HTS + Hurst – cBot (te same zasady co wskaźnik HTS_Hurst_Signals)
//  • SHORT: szybka wstęga HTS (33) cała pod wolną (144), odstęp wstęg ≥ min. ATR i rośnie,
//    zamknięta świeca dotknęła górnego pasma Hursta, cena pod wolną wstęgą
//  • LONG: odwrotnie; gdy wstęgi się schodzą – bot nie otwiera pozycji
//  • wejście po rynku na otwarciu następnej świecy, SL za ekstremalnym pasmem Hursta, TP w R / CMA / przeciwne pasmo
//  • wielkość pozycji z ryzyka w $, okno handlu (UTC), zamykanie w piątek, ochrona FTMO
using System;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum HhBand { Inner, Outer, Extreme }
    public enum HhTpMode { RMultiple, HurstCMA, OppositeOuterBand }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HTS_Hurst_Bot : Robot
    {
        [Parameter("Bot Label", DefaultValue = "HTS_HURST", Group = "=== SYSTEM ===")]
        public string BotLabel { get; set; }

        // ───────── HTS ─────────
        [Parameter("Fast Band", DefaultValue = 33, MinValue = 2, Group = "=== HTS BANDS ===")]
        public int FastLen { get; set; }

        [Parameter("Slow Band", DefaultValue = 144, MinValue = 2, Group = "=== HTS BANDS ===")]
        public int SlowLen { get; set; }

        [Parameter("MA Type", DefaultValue = MovingAverageType.WilderSmoothing, Group = "=== HTS BANDS ===")]
        public MovingAverageType MaType { get; set; }

        [Parameter("Min Band Gap (x ATR)", DefaultValue = 0.3, MinValue = 0, Group = "=== DIVERGENCE ===")]
        public double MinGapAtr { get; set; }

        [Parameter("Require Diverging Bands", DefaultValue = true, Group = "=== DIVERGENCE ===")]
        public bool NeedExpanding { get; set; }

        [Parameter("Compare Gap With (bars back)", DefaultValue = 10, MinValue = 1, Group = "=== DIVERGENCE ===")]
        public int ExpLookback { get; set; }

        // ───────── HURST ─────────
        [Parameter("Hurst CMA Length", DefaultValue = 10, MinValue = 2, Group = "=== HURST BANDS ===")]
        public int HurstLen { get; set; }

        [Parameter("ATR Period", DefaultValue = 100, MinValue = 1, Group = "=== HURST BANDS ===")]
        public int AtrLen { get; set; }

        [Parameter("Inner Band (x ATR)", DefaultValue = 1.6, Group = "=== HURST BANDS ===")]
        public double InnerK { get; set; }

        [Parameter("Outer Band (x ATR)", DefaultValue = 2.6, Group = "=== HURST BANDS ===")]
        public double OuterK { get; set; }

        [Parameter("Extreme Band (x ATR)", DefaultValue = 4.2, Group = "=== HURST BANDS ===")]
        public double ExtK { get; set; }

        [Parameter("Signal Band", DefaultValue = HhBand.Outer, Group = "=== HURST BANDS ===")]
        public HhBand TouchBand { get; set; }

        // ───────── SYGNAŁY ─────────
        [Parameter("Short Only Below Slow Band (Long Above)", DefaultValue = true, Group = "=== SIGNALS ===")]
        public bool NeedSide { get; set; }

        [Parameter("Min Bars Between Signals", DefaultValue = 10, MinValue = 0, Group = "=== SIGNALS ===")]
        public int CooldownBars { get; set; }

        [Parameter("Trade Direction (0=Both, 1=Long only, 2=Short only)", DefaultValue = 0, MinValue = 0, MaxValue = 2, Group = "=== SIGNALS ===")]
        public int TradeDirectionMode { get; set; }

        // ───────── RYZYKO ─────────
        [Parameter("Risk per Trade ($)", DefaultValue = 250.0, MinValue = 1.0, Group = "=== RISK ===")]
        public double RiskCash { get; set; }

        [Parameter("SL Buffer Beyond Extreme Band (x ATR)", DefaultValue = 0.3, MinValue = 0, Group = "=== RISK ===")]
        public double SlBufferAtr { get; set; }

        [Parameter("Max SL (pips, 0 = off)", DefaultValue = 0.0, MinValue = 0, Group = "=== RISK ===")]
        public double MaxSlPips { get; set; }

        [Parameter("TP Mode", DefaultValue = HhTpMode.RMultiple, Group = "=== RISK ===")]
        public HhTpMode TpMode { get; set; }

        [Parameter("TP (R multiple)", DefaultValue = 2.0, MinValue = 0.1, Group = "=== RISK ===")]
        public double TpR { get; set; }

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

        [Parameter("Max Trades per Day (0 = off)", DefaultValue = 4, MinValue = 0, Group = "=== FTMO PROTECTION ===")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Max Consecutive Losses per Day (0 = off)", DefaultValue = 3, MinValue = 0, Group = "=== FTMO PROTECTION ===")]
        public int MaxConsecLosses { get; set; }

        [Parameter("Day Reset Time Zone (Windows ID)", DefaultValue = "Central European Standard Time", Group = "=== FTMO PROTECTION ===")]
        public string DayResetTimeZone { get; set; }

        private MovingAverage _fh, _fl, _sh, _sl;
        private AverageTrueRange _atr;
        private SimpleMovingAverage _med;
        private int _disp;
        private int _lastSignalIndex = -100000;

        private TimeZoneInfo _dayTz;
        private DateTime _ftmoDay;
        private double _dayStartBalance;
        private int _tradesToday;
        private int _consecLosses;
        private bool _dayBlocked;
        private string _dayBlockReason = "";

        protected override void OnStart()
        {
            _fh  = Indicators.MovingAverage(Bars.HighPrices, FastLen, MaType);
            _fl  = Indicators.MovingAverage(Bars.LowPrices, FastLen, MaType);
            _sh  = Indicators.MovingAverage(Bars.HighPrices, SlowLen, MaType);
            _sl  = Indicators.MovingAverage(Bars.LowPrices, SlowLen, MaType);
            _atr = Indicators.AverageTrueRange(AtrLen, MovingAverageType.WilderSmoothing);
            _med = Indicators.SimpleMovingAverage(Bars.MedianPrices, HurstLen);
            _disp = HurstLen / 2 + 1;

            try { _dayTz = TimeZoneInfo.FindSystemTimeZoneById(DayResetTimeZone); }
            catch (Exception) { _dayTz = TimeZoneInfo.Utc; }
            StartDay(LocalNow().Date);
            Positions.Closed += OnPositionClosed;
        }

        protected override void OnTick()
        {
            CheckFtmo();

            var pos = MyPositions();
            if (pos.Length == 0 || IsTradeTime()) return;
            bool friday = Server.Time.DayOfWeek == DayOfWeek.Friday;
            bool afterEnd = StartHour < EndHour ? Server.Time.Hour >= EndHour : true;
            if (CloseEodEveryday || (CloseEodFriday && friday && afterEnd))
                foreach (var p in pos) ClosePosition(p);
        }

        protected override void OnBar()
        {
            int i = Bars.Count - 2;   // ostatnia zamknięta świeca
            if (i < Math.Max(SlowLen, AtrLen) + ExpLookback + _disp + HurstLen + 2) return;

            bool sigS = RawSignal(i, -1) && !RawSignal(i - 1, -1);
            bool sigL = RawSignal(i, 1) && !RawSignal(i - 1, 1);
            if (!sigS && !sigL) return;

            double atr = _atr.Result[i];
            if (sigS) Chart.DrawIcon("hh_s_" + i, ChartIconType.DownArrow, i, Bars.HighPrices[i] + 0.3 * atr, Color.Red);
            if (sigL) Chart.DrawIcon("hh_l_" + i, ChartIconType.UpArrow, i, Bars.LowPrices[i] - 0.3 * atr, Color.LimeGreen);

            if (i - _lastSignalIndex <= CooldownBars) return;
            _lastSignalIndex = i;

            if (MyPositions().Length > 0) return;
            if (!IsTradeTime() || !FtmoAllowsNewTrade()) return;
            if (sigS && TradeDirectionMode == 1) return;
            if (sigL && TradeDirectionMode == 2) return;

            Open(sigS ? TradeType.Sell : TradeType.Buy, i);
        }

        private double Gap(int i)
        {
            double fh = _fh.Result[i], fl = _fl.Result[i], sh = _sh.Result[i], sl = _sl.Result[i];
            if (fl > sh) return fl - sh;
            if (fh < sl) return sl - fh;
            return 0.0;
        }

        // dir = -1 short, 1 long
        private bool RawSignal(int i, int dir)
        {
            double atr = _atr.Result[i];
            if (atr <= 0 || i - _disp < 0) return false;

            double gap = Gap(i), gapPrev = Gap(i - ExpLookback);
            bool up = _fl.Result[i] > _sh.Result[i];
            bool dn = _fh.Result[i] < _sl.Result[i];
            bool active = gap / atr >= MinGapAtr && (!NeedExpanding || gap > gapPrev);

            double cma = _med.Result[i - _disp];
            double k = TouchBand == HhBand.Inner ? InnerK : TouchBand == HhBand.Outer ? OuterK : ExtK;

            if (dir == -1)
                return dn && active && Bars.HighPrices[i] >= cma + k * atr && (!NeedSide || Bars.ClosePrices[i] < _sl.Result[i]);
            return up && active && Bars.LowPrices[i] <= cma - k * atr && (!NeedSide || Bars.ClosePrices[i] > _sh.Result[i]);
        }

        private void Open(TradeType type, int i)
        {
            int sign = type == TradeType.Buy ? 1 : -1;
            double atr   = _atr.Result[i];
            double cma   = _med.Result[i - _disp];
            double entry = type == TradeType.Buy ? Symbol.Ask : Symbol.Bid;

            double sl = sign == -1
                ? Math.Max(Bars.HighPrices[i], cma + ExtK * atr) + SlBufferAtr * atr
                : Math.Min(Bars.LowPrices[i], cma - ExtK * atr) - SlBufferAtr * atr;
            double slDist = sign * (entry - sl);
            if (slDist <= 0) return;
            double slPips = slDist / Symbol.PipSize;
            if (MaxSlPips > 0 && slPips > MaxSlPips) return;

            double tp = entry + sign * TpR * slDist;
            if (TpMode == HhTpMode.HurstCMA) tp = cma;
            else if (TpMode == HhTpMode.OppositeOuterBand) tp = sign == -1 ? cma - OuterK * atr : cma + OuterK * atr;
            if (sign * (tp - entry) <= 0) tp = entry + sign * TpR * slDist;
            double tpPips = sign * (tp - entry) / Symbol.PipSize;

            double units = Symbol.NormalizeVolumeInUnits(RiskCash / (slPips * Symbol.PipValue), RoundingMode.Down);
            if (units < Symbol.VolumeInUnitsMin) return;
            units = Math.Min(units, Symbol.VolumeInUnitsMax);

            var res = ExecuteMarketOrder(type, SymbolName, units, BotLabel, slPips, tpPips);
            if (res.IsSuccessful)
            {
                _tradesToday++;
                Print("{0} @ {1}, SL {2:F1} pips, TP {3:F1} pips", type, res.Position.EntryPrice, slPips, tpPips);
            }
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
            _dayBlockReason = "";
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
            _dayBlockReason = reason;
            Print("FTMO PROTECTION – entries blocked until next day: {0}", reason);
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p.SymbolName != SymbolName || p.Label != BotLabel) return;
            _consecLosses = p.NetProfit < 0 ? _consecLosses + 1 : 0;
            if (FtmoProtection && !_dayBlocked && MaxConsecLosses > 0 && _consecLosses >= MaxConsecLosses)
                Block($"{_consecLosses} losses in a row");
        }
    }
}
