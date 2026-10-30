// HTS + Hurst – wskaźnik dla cTradera (odpowiednik HTS_Hurst_Signals.pine)
//  • wstęgi HTS: szybka (33) i wolna (144) z High i Low
//  • pasma Hursta (LazyBear): CMA = SMA z (H+L)/2 przesunięta o długość/2+1, pasma w ATR
//  • SHORT: szybka wstęga cała pod wolną, odstęp ≥ min. ATR i rośnie, cena dotyka górnego pasma Hursta
//  • LONG: odwrotnie; gdy wstęgi się schodzą – brak sygnałów
using System;
using cAlgo.API;
using cAlgo.API.Indicators;

namespace cAlgo.Indicators
{
    public enum HhSignalBand { Inner, Outer, Extreme }

    [Indicator(IsOverlay = true, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HTS_Hurst_Signals : Indicator
    {
        [Parameter("Fast Band", DefaultValue = 33, MinValue = 2, Group = "HTS bands")]
        public int FastLen { get; set; }

        [Parameter("Slow Band", DefaultValue = 144, MinValue = 2, Group = "HTS bands")]
        public int SlowLen { get; set; }

        [Parameter("MA Type", DefaultValue = MovingAverageType.WilderSmoothing, Group = "HTS bands")]
        public MovingAverageType MaType { get; set; }

        [Parameter("Min Band Gap (x ATR)", DefaultValue = 0.3, MinValue = 0, Group = "Divergence")]
        public double MinGapAtr { get; set; }

        [Parameter("Require Diverging Bands", DefaultValue = true, Group = "Divergence")]
        public bool NeedExpanding { get; set; }

        [Parameter("Compare Gap With (bars back)", DefaultValue = 10, MinValue = 1, Group = "Divergence")]
        public int ExpLookback { get; set; }

        [Parameter("Hurst CMA Length", DefaultValue = 10, MinValue = 2, Group = "Hurst bands")]
        public int HurstLen { get; set; }

        [Parameter("ATR Period", DefaultValue = 100, MinValue = 1, Group = "Hurst bands")]
        public int AtrLen { get; set; }

        [Parameter("Inner Band (x ATR)", DefaultValue = 1.6, Group = "Hurst bands")]
        public double InnerK { get; set; }

        [Parameter("Outer Band (x ATR)", DefaultValue = 2.6, Group = "Hurst bands")]
        public double OuterK { get; set; }

        [Parameter("Extreme Band (x ATR)", DefaultValue = 4.2, Group = "Hurst bands")]
        public double ExtK { get; set; }

        [Parameter("Signal Band", DefaultValue = HhSignalBand.Outer, Group = "Signals")]
        public HhSignalBand TouchBand { get; set; }

        [Parameter("Short Only Below Slow Band (Long Above)", DefaultValue = true, Group = "Signals")]
        public bool NeedSide { get; set; }

        [Output("HTS Fast High", LineColor = "DodgerBlue")]
        public IndicatorDataSeries FastHigh { get; set; }

        [Output("HTS Fast Low", LineColor = "DodgerBlue")]
        public IndicatorDataSeries FastLow { get; set; }

        [Output("HTS Slow High", LineColor = "OrangeRed")]
        public IndicatorDataSeries SlowHigh { get; set; }

        [Output("HTS Slow Low", LineColor = "OrangeRed")]
        public IndicatorDataSeries SlowLow { get; set; }

        [Output("Hurst CMA", LineColor = "Gray")]
        public IndicatorDataSeries Cma { get; set; }

        [Output("Hurst Upper Inner", LineColor = "DarkGray", LineStyle = LineStyle.Dots)]
        public IndicatorDataSeries UpInner { get; set; }

        [Output("Hurst Lower Inner", LineColor = "DarkGray", LineStyle = LineStyle.Dots)]
        public IndicatorDataSeries LoInner { get; set; }

        [Output("Hurst Upper Outer", LineColor = "Red", Thickness = 2)]
        public IndicatorDataSeries UpOuter { get; set; }

        [Output("Hurst Lower Outer", LineColor = "LimeGreen", Thickness = 2)]
        public IndicatorDataSeries LoOuter { get; set; }

        [Output("Hurst Upper Extreme", LineColor = "Red", PlotType = PlotType.Points)]
        public IndicatorDataSeries UpExt { get; set; }

        [Output("Hurst Lower Extreme", LineColor = "LimeGreen", PlotType = PlotType.Points)]
        public IndicatorDataSeries LoExt { get; set; }

        private MovingAverage _fh, _fl, _sh, _sl;
        private AverageTrueRange _atr;
        private SimpleMovingAverage _med;
        private int _disp;

        protected override void Initialize()
        {
            _fh  = Indicators.MovingAverage(Bars.HighPrices, FastLen, MaType);
            _fl  = Indicators.MovingAverage(Bars.LowPrices, FastLen, MaType);
            _sh  = Indicators.MovingAverage(Bars.HighPrices, SlowLen, MaType);
            _sl  = Indicators.MovingAverage(Bars.LowPrices, SlowLen, MaType);
            _atr = Indicators.AverageTrueRange(AtrLen, MovingAverageType.WilderSmoothing);
            _med = Indicators.SimpleMovingAverage(Bars.MedianPrices, HurstLen);
            _disp = HurstLen / 2 + 1;
        }

        private double Gap(int i)
        {
            double fh = _fh.Result[i], fl = _fl.Result[i], sh = _sh.Result[i], sl = _sl.Result[i];
            if (fl > sh) return fl - sh;
            if (fh < sl) return sl - fh;
            return 0.0;
        }

        public override void Calculate(int index)
        {
            FastHigh[index] = _fh.Result[index];
            FastLow[index]  = _fl.Result[index];
            SlowHigh[index] = _sh.Result[index];
            SlowLow[index]  = _sl.Result[index];

            if (index < _disp + HurstLen) return;
            double atr = _atr.Result[index];
            double cma = _med.Result[index - _disp];
            Cma[index]     = cma;
            UpInner[index] = cma + InnerK * atr;
            LoInner[index] = cma - InnerK * atr;
            UpOuter[index] = cma + OuterK * atr;
            LoOuter[index] = cma - OuterK * atr;
            UpExt[index]   = cma + ExtK * atr;
            LoExt[index]   = cma - ExtK * atr;

            if (index < Math.Max(SlowLen, AtrLen) + ExpLookback + 2) return;

            bool sigS = RawSignal(index, -1) && !RawSignal(index - 1, -1);
            bool sigL = RawSignal(index, 1) && !RawSignal(index - 1, 1);

            if (sigS)
                Chart.DrawIcon("hh_s_" + index, ChartIconType.DownArrow, index, Bars.HighPrices[index] + 0.3 * atr, Color.Red);
            else
                Chart.RemoveObject("hh_s_" + index);

            if (sigL)
                Chart.DrawIcon("hh_l_" + index, ChartIconType.UpArrow, index, Bars.LowPrices[index] - 0.3 * atr, Color.LimeGreen);
            else
                Chart.RemoveObject("hh_l_" + index);
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
            double k = TouchBand == HhSignalBand.Inner ? InnerK : TouchBand == HhSignalBand.Outer ? OuterK : ExtK;

            if (dir == -1)
                return dn && active && Bars.HighPrices[i] >= cma + k * atr && (!NeedSide || Bars.ClosePrices[i] < _sl.Result[i]);
            return up && active && Bars.LowPrices[i] <= cma - k * atr && (!NeedSide || Bars.ClosePrices[i] > _sh.Result[i]);
        }
    }
}
