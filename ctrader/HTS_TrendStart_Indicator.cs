// HTS – wczesne wykrywanie trendu – wskaźnik dla cTradera (odpowiednik HTS_TrendStart.pine)
//  Warunki startu (każdy można wyłączyć):
//   1. wolna wstęga już opada / rośnie,
//   2. szybka wstęga cała pod / nad wolną – świeże przecięcie,
//   3. szybka wstęga mocno opada / rośnie,
//   4. zamknięcie za szybką wstęgą,
//   5. wcześniej odbicie od wolnej wstęgi (niższy szczyt / wyższy dołek),
//   6. odstęp między wstęgami rośnie,
//   7. (opcja) trend HTS na wyższym interwale zgodny.
//  • kółko = PRZYGOTOWANIE (brakuje 1 warunku), strzałka + START = wszystkie spełnione na zamkniętej świecy,
//  • ramka rysuje się na bieżąco, dopóki trwa wzorzec; koniec = N zamknięć po drugiej stronie szybkiej wstęgi,
//  • romb = dokładka (cofnięcie do szybkiej wstęgi i odrzucenie, tylko gdy START jest już na plusie),
//  • panel: checklista warunków, statystyka wzorców, porównanie 6 sposobów zarządzania pozycją, kapitał przy % ryzyka.
//  Wszystko liczone na zamkniętych świecach – nic się nie przerysowuje.
using System;
using System.Collections.Generic;
using System.Text;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Indicators
{
    public enum TsDirection { Both, ShortOnly, LongOnly }
    public enum TsManagement { Base, BreakEven, PartialTpBE, TrailingFastBand, TimeStopBE, AllCombined }

    [Indicator(IsOverlay = true, TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HTS_TrendStart : Indicator
    {
        // ───────── WSTĘGI ─────────
        [Parameter("Fast Band", DefaultValue = 33, MinValue = 2, Group = "HTS bands")]
        public int FastLen { get; set; }

        [Parameter("Slow Band", DefaultValue = 144, MinValue = 2, Group = "HTS bands")]
        public int SlowLen { get; set; }

        [Parameter("MA Type", DefaultValue = MovingAverageType.WilderSmoothing, Group = "HTS bands")]
        public MovingAverageType MaType { get; set; }

        [Parameter("ATR Period (distance unit)", DefaultValue = 100, MinValue = 1, Group = "HTS bands")]
        public int AtrLen { get; set; }

        // ───────── WARUNKI ─────────
        [Parameter("1. Slow band sloping", DefaultValue = true, Group = "Start conditions")]
        public bool Use1 { get; set; }

        [Parameter("   slow slope lookback (bars)", DefaultValue = 30, MinValue = 1, Group = "Start conditions")]
        public int S1Len { get; set; }

        [Parameter("   min slow change (x ATR)", DefaultValue = 0.15, MinValue = 0, Group = "Start conditions")]
        public double S1Min { get; set; }

        [Parameter("2. Fresh cross: fast fully beyond slow", DefaultValue = true, Group = "Start conditions")]
        public bool Use2 { get; set; }

        [Parameter("   max bars since cross", DefaultValue = 40, MinValue = 1, Group = "Start conditions")]
        public int S2Len { get; set; }

        [Parameter("3. Fast band sloping strongly", DefaultValue = true, Group = "Start conditions")]
        public bool Use3 { get; set; }

        [Parameter("   fast slope lookback (bars)", DefaultValue = 10, MinValue = 1, Group = "Start conditions")]
        public int S3Len { get; set; }

        [Parameter("   min fast change (x ATR)", DefaultValue = 0.4, MinValue = 0, Group = "Start conditions")]
        public double S3Min { get; set; }

        [Parameter("4. Close beyond fast band", DefaultValue = true, Group = "Start conditions")]
        public bool Use4 { get; set; }

        [Parameter("5. Earlier rejection at slow band", DefaultValue = true, Group = "Start conditions")]
        public bool Use5 { get; set; }

        [Parameter("   slow band touched within (bars)", DefaultValue = 60, MinValue = 1, Group = "Start conditions")]
        public int S5Len { get; set; }

        [Parameter("   no close beyond slow for (bars)", DefaultValue = 20, MinValue = 1, Group = "Start conditions")]
        public int S5Stay { get; set; }

        [Parameter("6. Band gap growing", DefaultValue = true, Group = "Start conditions")]
        public bool Use6 { get; set; }

        [Parameter("   compare gap with (bars back)", DefaultValue = 5, MinValue = 1, Group = "Start conditions")]
        public int S6Len { get; set; }

        [Parameter("7. Higher TF HTS trend agrees", DefaultValue = false, Group = "Start conditions")]
        public bool Use7 { get; set; }

        [Parameter("   higher timeframe", DefaultValue = "Hour", Group = "Start conditions")]
        public TimeFrame HtfTf { get; set; }

        [Parameter("Early warning when missing at most", DefaultValue = 1, MinValue = 1, Group = "Start conditions")]
        public int PreMissing { get; set; }

        [Parameter("Direction", DefaultValue = TsDirection.Both, Group = "Start conditions")]
        public TsDirection Direction { get; set; }

        // ───────── WZORZEC I DOKŁADKI ─────────
        [Parameter("End: closes beyond fast band in a row", DefaultValue = 3, MinValue = 1, Group = "Pattern & add-ons")]
        public int EndBars { get; set; }

        [Parameter("Add-ons on pullback to fast band", DefaultValue = true, Group = "Pattern & add-ons")]
        public bool ShowPb { get; set; }

        [Parameter("   min bars since last entry", DefaultValue = 10, MinValue = 1, Group = "Pattern & add-ons")]
        public int PbCooldown { get; set; }

        [Parameter("   max add-ons per pattern", DefaultValue = 4, MinValue = 0, Group = "Pattern & add-ons")]
        public int PbMax { get; set; }

        [Parameter("   only when START profit >= (R, 0 = always)", DefaultValue = 1.0, MinValue = 0, Group = "Pattern & add-ons")]
        public double PbNeedR { get; set; }

        // ───────── SL I KOSZTY ─────────
        [Parameter("START SL: beyond extreme of last (bars)", DefaultValue = 20, MinValue = 1, Group = "SL & costs")]
        public int SlLen { get; set; }

        [Parameter("Add-on SL: beyond extreme of last (bars)", DefaultValue = 5, MinValue = 1, Group = "SL & costs")]
        public int PbSlLen { get; set; }

        [Parameter("SL buffer (x ATR)", DefaultValue = 0.2, MinValue = 0, Group = "SL & costs")]
        public double SlBuf { get; set; }

        [Parameter("Min risk (x ATR)", DefaultValue = 0.5, MinValue = 0.05, Group = "SL & costs")]
        public double MinRisk { get; set; }

        [Parameter("Cost per entry (pips: spread + commission)", DefaultValue = 0.0, MinValue = 0, Group = "SL & costs")]
        public double CostPips { get; set; }

        [Parameter("Big trend = reach at least (x ATR)", DefaultValue = 6.0, MinValue = 0.5, Group = "SL & costs")]
        public double BigK { get; set; }

        // ───────── ZARZĄDZANIE ─────────
        [Parameter("BE: SL to entry after profit (R)", DefaultValue = 1.0, MinValue = 0.1, Group = "Management comparison")]
        public double BeR { get; set; }

        [Parameter("Partial TP at (R)", DefaultValue = 2.0, MinValue = 0.1, Group = "Management comparison")]
        public double TpR { get; set; }

        [Parameter("Partial TP: close (%)", DefaultValue = 50.0, MinValue = 1, MaxValue = 99, Group = "Management comparison")]
        public double TpPct { get; set; }

        [Parameter("Trailing behind fast band: start after (R)", DefaultValue = 1.0, MinValue = 0, Group = "Management comparison")]
        public double TrStartR { get; set; }

        [Parameter("Trailing buffer beyond fast band (x ATR)", DefaultValue = 0.3, MinValue = 0, Group = "Management comparison")]
        public double TrBuf { get; set; }

        [Parameter("Time stop: check after (bars)", DefaultValue = 15, MinValue = 1, Group = "Management comparison")]
        public int TsBars { get; set; }

        [Parameter("Time stop: exit if max profit < (R)", DefaultValue = 0.5, MinValue = 0, Group = "Management comparison")]
        public double TsR { get; set; }

        [Parameter("Variant for panel and equity", DefaultValue = TsManagement.AllCombined, Group = "Management comparison")]
        public TsManagement SelVariant { get; set; }

        [Parameter("Risk per entry (% of equity)", DefaultValue = 0.5, MinValue = 0.01, Group = "Management comparison")]
        public double RiskPct { get; set; }

        // ───────── WYGLĄD ─────────
        [Parameter("Pattern box", DefaultValue = true, Group = "Display")]
        public bool ShowBox { get; set; }

        [Parameter("End label (reach)", DefaultValue = true, Group = "Display")]
        public bool ShowEnd { get; set; }

        [Parameter("Panel", DefaultValue = true, Group = "Display")]
        public bool ShowPanel { get; set; }

        [Parameter("Comparison table", DefaultValue = true, Group = "Display")]
        public bool ShowCmp { get; set; }

        [Output("HTS Fast High", LineColor = "Teal")]
        public IndicatorDataSeries FastHigh { get; set; }

        [Output("HTS Fast Low", LineColor = "Teal")]
        public IndicatorDataSeries FastLow { get; set; }

        [Output("HTS Slow High", LineColor = "IndianRed")]
        public IndicatorDataSeries SlowHigh { get; set; }

        [Output("HTS Slow Low", LineColor = "IndianRed")]
        public IndicatorDataSeries SlowLow { get; set; }

        // ───────── stan ─────────
        private const int NV = 6;
        private static readonly string[] VarNames = { "1. Bazowy", "2. BE", "3. Częściowe TP + BE", "4. Trailing po szybkiej", "5. Stop czasowy + BE", "6. Wszystko razem" };
        private static readonly string[] CondNames = { "Wolna wstęga nachylona", "Świeże przecięcie wstęg", "Szybka mocno nachylona", "Zamknięcie za szybką", "Odbicie od wolnej", "Wstęgi się rozchodzą", "Trend wyższego TF" };

        private class Trade
        {
            public double E, Rk, Mfe;
            public int Kind, Bar;
            public double[] Sl = new double[NV], Rem = new double[NV], Real = new double[NV];
            public bool[] Open = new bool[NV];
        }

        private MovingAverage _fh, _fl, _sh, _sl, _hfh, _hfl, _hsh, _hsl;
        private AverageTrueRange _atr;
        private Bars _htf;
        private int _done = -1, _warm;

        private int _dir, _pStart, _endCnt, _pbCnt, _lastEnt;
        private double _pEntry, _pRisk, _pAtr, _pExt, _pSl;
        private string _boxName;
        private readonly List<Trade> _trades = new List<Trade>();
        private bool _prevPrepS, _prevPrepL;

        private readonly double[] _vN = new double[NV], _vW = new double[NV], _vSum = new double[NV], _vPeak = new double[NV],
                                  _vDD = new double[NV], _vStr = new double[NV], _vMStr = new double[NV];
        private readonly double[] _kN = new double[2], _kW = new double[2], _kSum = new double[2];
        private double _eq = 100, _eqPk = 100, _eqDD;
        private int _nPat, _nBig;
        private double _sumMf;

        protected override void Initialize()
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
        }

        public override void Calculate(int index)
        {
            FastHigh[index] = _fh.Result[index];
            FastLow[index]  = _fl.Result[index];
            SlowHigh[index] = _sh.Result[index];
            SlowLow[index]  = _sl.Result[index];

            // tylko zamknięte świece: index - 1
            for (int i = _done + 1; i <= index - 1; i++)
                ProcessBar(i);
            if (index - 1 > _done) _done = index - 1;

            if (IsLastBar && _done >= _warm) DrawPanels(_done);
        }

        // ───────── pomocnicze ─────────
        private double H(int i) => Bars.HighPrices[i];
        private double L(int i) => Bars.LowPrices[i];
        private double C(int i) => Bars.ClosePrices[i];
        private double O(int i) => Bars.OpenPrices[i];
        private double FM(int i) => (_fh.Result[i] + _fl.Result[i]) / 2;
        private double SM(int i) => (_sh.Result[i] + _sl.Result[i]) / 2;
        private bool Trend(int i, int d) => d == -1 ? _fh.Result[i] < _sl.Result[i] : _fl.Result[i] > _sh.Result[i];
        private double Gap(int i, int d) => d == -1 ? _sl.Result[i] - _fh.Result[i] : _fl.Result[i] - _sh.Result[i];
        private static double Tight(int d, double a, double b) => d == -1 ? Math.Min(a, b) : Math.Max(a, b);
        private static bool HasBe(int v) => v == 1 || v == 4 || v == 5;
        private static bool HasTp(int v) => v == 2 || v == 5;
        private static bool HasTr(int v) => v == 3 || v == 5;
        private static bool HasTs(int v) => v == 4 || v == 5;
        private bool[] Uses => new[] { Use1, Use2, Use3, Use4, Use5, Use6, Use7 };

        private bool[] Conds(int i, int d)
        {
            double atr = _atr.Result[i];
            var c = new bool[7];
            c[0] = d * (SM(i) - SM(i - S1Len)) >= S1Min * atr;
            bool fresh = false;
            if (Trend(i, d))
                for (int m = 0; m <= S2Len && i - m - 1 >= 0; m++)
                    if (!Trend(i - m - 1, d)) { fresh = true; break; }
            c[1] = fresh;
            c[2] = d * (FM(i) - FM(i - S3Len)) >= S3Min * atr;
            c[3] = d == -1 ? C(i) < _fl.Result[i] : C(i) > _fh.Result[i];
            bool touched = false, stayed = true;
            for (int k = 0; k <= S5Len && i - k >= 0; k++)
                if (d == -1 ? H(i - k) >= _sl.Result[i - k] : L(i - k) <= _sh.Result[i - k]) { touched = true; break; }
            for (int k = 0; k < S5Stay && i - k >= 0; k++)
                if (d == -1 ? C(i - k) > _sh.Result[i - k] : C(i - k) < _sl.Result[i - k]) { stayed = false; break; }
            c[4] = touched && stayed;
            c[5] = Gap(i, d) > Gap(i - S6Len, d);
            c[6] = HtfTrend(i, d);
            return c;
        }

        private bool HtfTrend(int i, int d)
        {
            if (!Use7 || _htf == null) return false;
            int h = _htf.OpenTimes.GetIndexByTime(Bars.OpenTimes[i]) - 1;   // ostatnia zamknięta świeca wyższego TF
            if (h < SlowLen) return false;
            return d == -1 ? _hfh.Result[h] < _hsl.Result[h] : _hfl.Result[h] > _hsh.Result[h];
        }

        private int Score(bool[] c, out int total)
        {
            var u = Uses;
            int s = 0;
            total = 0;
            for (int k = 0; k < 7; k++)
            {
                if (!u[k]) continue;
                total++;
                if (c[k]) s++;
            }
            return s;
        }

        private double SlLevel(int i, int d, int len)
        {
            double atr = _atr.Result[i], hi = double.MinValue, lo = double.MaxValue;
            for (int k = 0; k < len && i - k >= 0; k++) { hi = Math.Max(hi, H(i - k)); lo = Math.Min(lo, L(i - k)); }
            double raw = d == -1 ? hi + SlBuf * atr : lo - SlBuf * atr;
            double r = Math.Max(Math.Abs(raw - C(i)), MinRisk * atr);
            return C(i) - d * r;
        }

        private void OpenTrade(int i, int kind, double sl)
        {
            var t = new Trade { E = C(i), Rk = Math.Abs(C(i) - sl), Kind = kind, Bar = i };
            for (int v = 0; v < NV; v++) { t.Sl[v] = sl; t.Rem[v] = 1; t.Open[v] = true; }
            _trades.Add(t);
        }

        private void Record(int v, int kind, double r)
        {
            _vN[v]++;
            if (r > 0) _vW[v]++;
            _vSum[v] += r;
            _vPeak[v] = Math.Max(_vPeak[v], _vSum[v]);
            _vDD[v] = Math.Max(_vDD[v], _vPeak[v] - _vSum[v]);
            _vStr[v] = r < 0 ? _vStr[v] + 1 : 0;
            _vMStr[v] = Math.Max(_vMStr[v], _vStr[v]);
            if (v != (int)SelVariant) return;
            _kN[kind]++;
            if (r > 0) _kW[kind]++;
            _kSum[kind] += r;
            _eq *= 1 + RiskPct / 100 * r;
            _eqPk = Math.Max(_eqPk, _eq);
            _eqDD = Math.Max(_eqDD, 100 * (_eqPk - _eq) / _eqPk);
        }

        // ───────── logika na zamkniętej świecy ─────────
        private void ProcessBar(int i)
        {
            if (i < _warm) return;
            double atr = _atr.Result[i];
            if (double.IsNaN(atr) || atr <= 0) return;

            if (_dir != 0)
            {
                int d = _dir;
                _pExt = d == -1 ? Math.Min(_pExt, L(i)) : Math.Max(_pExt, H(i));
                if (ShowBox && _boxName != null)
                {
                    var rect = Chart.DrawRectangle(_boxName, _pStart, _pSl, i, _pExt, d == -1 ? Color.FromArgb(45, 220, 60, 60) : Color.FromArgb(45, 40, 170, 90));
                    rect.IsFilled = true;
                    rect.LineStyle = LineStyle.Dots;
                }
                bool against = d == -1 ? C(i) > _fh.Result[i] : C(i) < _fl.Result[i];
                bool hard    = d == -1 ? C(i) > _sh.Result[i] : C(i) < _sl.Result[i];
                _endCnt = against ? _endCnt + 1 : 0;
                bool patEnd = _endCnt >= EndBars || hard;

                ManageTrades(i, d, atr, patEnd);

                if (patEnd)
                {
                    double mfe = d * (_pExt - _pEntry) / _pAtr;
                    _nPat++;
                    if (mfe >= BigK) _nBig++;
                    _sumMf += mfe;
                    if (ShowEnd)
                        Chart.DrawText("ts_end_" + i, string.Format("koniec {0:F1} ATR · {1} św.", mfe, i - _pStart), i,
                            d == -1 ? L(i) - 0.5 * atr : H(i) + 0.5 * atr, Color.Gray);
                    _dir = 0;
                    _boxName = null;
                }
                else if (ShowPb && _pbCnt < PbMax && i - _lastEnt >= PbCooldown && (PbNeedR == 0 || d * (_pExt - _pEntry) / _pRisk >= PbNeedR))
                {
                    bool pb = d == -1
                        ? H(i) >= _fl.Result[i] && C(i) < FM(i) && C(i) < O(i) && FM(i) < FM(i - S3Len)
                        : L(i) <= _fh.Result[i] && C(i) > FM(i) && C(i) > O(i) && FM(i) > FM(i - S3Len);
                    if (pb)
                    {
                        OpenTrade(i, 1, SlLevel(i, d, PbSlLen));
                        _pbCnt++;
                        _lastEnt = i;
                        Chart.DrawIcon("ts_pb_" + i, ChartIconType.Diamond, i, d == -1 ? H(i) + 0.3 * atr : L(i) - 0.3 * atr, d == -1 ? Color.Orange : Color.Aqua);
                    }
                }
                _prevPrepS = _prevPrepL = false;
                return;
            }

            var cs = Conds(i, -1);
            var cl = Conds(i, 1);
            int sS = Score(cs, out int total);
            int sL = Score(cl, out _);
            bool okS = Direction != TsDirection.LongOnly, okL = Direction != TsDirection.ShortOnly;
            bool allS = total > 0 && sS == total && okS;
            bool allL = total > 0 && sL == total && okL;

            if (allS || allL)
            {
                int d = allS ? -1 : 1;
                double sl = SlLevel(i, d, SlLen);
                _dir = d;
                _pStart = i;
                _pEntry = C(i);
                _pRisk = Math.Abs(C(i) - sl);
                _pAtr = atr;
                _pExt = d == -1 ? L(i) : H(i);
                _pSl = sl;
                _endCnt = 0;
                _pbCnt = 0;
                _lastEnt = i;
                OpenTrade(i, 0, sl);
                _boxName = ShowBox ? "ts_box_" + i : null;
                if (_boxName != null)
                {
                    var rect = Chart.DrawRectangle(_boxName, i, sl, i, _pExt, d == -1 ? Color.FromArgb(45, 220, 60, 60) : Color.FromArgb(45, 40, 170, 90));
                    rect.IsFilled = true;
                    rect.LineStyle = LineStyle.Dots;
                }
                double y = d == -1 ? H(i) + 0.4 * atr : L(i) - 0.4 * atr;
                Chart.DrawIcon("ts_st_" + i, d == -1 ? ChartIconType.DownArrow : ChartIconType.UpArrow, i, y, d == -1 ? Color.Red : Color.LimeGreen);
                Chart.DrawText("ts_stt_" + i, "START", i, d == -1 ? y + 0.6 * atr : y - 0.6 * atr, d == -1 ? Color.Red : Color.LimeGreen);
                _prevPrepS = _prevPrepL = false;
                return;
            }

            bool prepS = okS && total > 0 && sS >= total - PreMissing;
            bool prepL = okL && total > 0 && sL >= total - PreMissing;
            if (prepS && !_prevPrepS)
                Chart.DrawIcon("ts_prs_" + i, ChartIconType.Circle, i, H(i) + 0.3 * atr, Color.Orange);
            if (prepL && !_prevPrepL)
                Chart.DrawIcon("ts_prl_" + i, ChartIconType.Circle, i, L(i) - 0.3 * atr, Color.Orange);
            _prevPrepS = prepS;
            _prevPrepL = prepL;
        }

        private void ManageTrades(int i, int d, double atr, bool patEnd)
        {
            double cost = CostPips * Symbol.PipSize;
            for (int n = _trades.Count - 1; n >= 0; n--)
            {
                var t = _trades[n];
                t.Mfe = Math.Max(t.Mfe, d * ((d == -1 ? L(i) : H(i)) - t.E) / t.Rk);
                int age = i - t.Bar;
                bool any = false;
                for (int v = 0; v < NV; v++)
                {
                    if (!t.Open[v]) continue;
                    double sl = t.Sl[v], rem = t.Rem[v], rl = t.Real[v];
                    bool cl = false;
                    if (d == -1 ? H(i) >= sl : L(i) <= sl)          // SL z poprzedniej świecy – przed TP, ostrożnie
                    {
                        rl += rem * d * (sl - t.E) / t.Rk;
                        cl = true;
                    }
                    else
                    {
                        if (HasTp(v) && rem > 0.999 && t.Mfe >= TpR)
                        {
                            rl += TpPct / 100 * TpR;
                            rem -= TpPct / 100;
                            sl = Tight(d, sl, t.E);
                        }
                        if (HasBe(v) && t.Mfe >= BeR) sl = Tight(d, sl, t.E);
                        if (HasTr(v) && t.Mfe >= TrStartR)
                            sl = Tight(d, sl, d == -1 ? _fh.Result[i] + TrBuf * atr : _fl.Result[i] - TrBuf * atr);
                        if (HasTs(v) && age >= TsBars && t.Mfe < TsR) cl = true;
                        if (patEnd || (d == -1 ? C(i) >= sl : C(i) <= sl)) cl = true;   // nowy SL za ceną → wyjście po rynku
                        if (cl) rl += rem * d * (C(i) - t.E) / t.Rk;
                    }
                    if (cl)
                    {
                        t.Open[v] = false;
                        Record(v, t.Kind, rl - cost / t.Rk);
                    }
                    else
                    {
                        t.Sl[v] = sl;
                        t.Rem[v] = rem;
                        t.Real[v] = rl;
                        any = true;
                    }
                }
                if (!any) _trades.RemoveAt(n);
            }
            if (patEnd) _trades.Clear();
        }

        // ───────── panele ─────────
        private void DrawPanels(int i)
        {
            if (ShowPanel)
            {
                var cs = Conds(i, -1);
                var cl = Conds(i, 1);
                int sS = Score(cs, out int total);
                int sL = Score(cl, out _);
                int cd = _dir != 0 ? _dir
                    : Direction == TsDirection.ShortOnly ? -1 : Direction == TsDirection.LongOnly ? 1
                    : sS > sL ? -1 : sL > sS ? 1 : FM(i) < SM(i) ? -1 : 1;
                var c = cd == -1 ? cs : cl;
                int sc = cd == -1 ? sS : sL;
                var u = Uses;
                var sb = new StringBuilder();
                sb.AppendLine("HTS START – kandydat: " + (cd == -1 ? "SHORT" : "LONG"));
                for (int k = 0; k < 7; k++)
                    sb.AppendLine(string.Format("{0}. {1}: {2}", k + 1, CondNames[k], !u[k] ? "wył." : c[k] ? "✔" : "✘"));
                sb.AppendLine(string.Format("Gotowość: {0} / {1}", sc, total));
                string st = _dir != 0 ? string.Format("WZORZEC TRWA ({0} św., {1:F1} ATR)", i - _pStart, _dir * (_pExt - _pEntry) / _pAtr)
                          : (cd == -1 ? _prevPrepS : _prevPrepL) ? "PRZYGOTOWANIE" : "CZEKAM";
                sb.AppendLine("Status: " + st);
                sb.AppendLine(string.Format("Wzorce: {0}, duże (≥ {1:F1} ATR): {2}{3}", _nPat, BigK, _nBig, _nPat > 0 ? string.Format(" = {0:F0} %", 100.0 * _nBig / _nPat) : ""));
                sb.AppendLine(_nPat > 0 ? string.Format("Śr. zasięg wzorca: {0:F1} ATR", _sumMf / _nPat) : "Śr. zasięg wzorca: –");
                sb.AppendLine("Wariant: " + VarNames[(int)SelVariant]);
                for (int k = 0; k < 2; k++)
                    sb.AppendLine(string.Format("{0}: {1} wejść, zysk. {2}, {3:F1} R", k == 0 ? "START" : "Dokładki", _kN[k],
                        _kN[k] > 0 ? string.Format("{0:F0} %", 100.0 * _kW[k] / _kN[k]) : "–", _kSum[k]));
                sb.AppendLine(string.Format("Kapitał przy {0:F2} % ryzyka: {1:+0.0;-0.0} %, max obsunięcie {2:F1} %", RiskPct, _eq - 100, _eqDD));
                Chart.DrawStaticText("ts_panel", sb.ToString(), VerticalAlignment.Top, HorizontalAlignment.Right, Color.Silver);
            }
            if (ShowCmp)
            {
                var sb = new StringBuilder();
                sb.AppendLine("Porównanie zarządzania (wejść | zysk. | suma R | śr. R | max DD R | seria strat)");
                for (int v = 0; v < NV; v++)
                    sb.AppendLine(string.Format("{0}{1}:  {2} | {3} | {4:F1} | {5} | {6:F1} | {7}",
                        v == (int)SelVariant ? "► " : "", VarNames[v], _vN[v],
                        _vN[v] > 0 ? string.Format("{0:F0} %", 100.0 * _vW[v] / _vN[v]) : "–",
                        _vSum[v], _vN[v] > 0 ? (_vSum[v] / _vN[v]).ToString("+0.00;-0.00") : "–", _vDD[v], _vMStr[v]));
                Chart.DrawStaticText("ts_cmp", sb.ToString(), VerticalAlignment.Bottom, HorizontalAlignment.Right, Color.Silver);
            }
        }
    }
}
