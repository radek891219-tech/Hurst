// HTS Grid – uśrednianie na wstęgach HTS (cBot / strategia dla cTradera)
//  Zasady (metoda „grid to uśrednianie”, z planem przed pierwszym zleceniem):
//   • wstęgi HTS z High i Low: szybka 33, wolna 144; trend = szybka cała nad wolną (long) / cała pod wolną (short),
//     gramy tylko w kierunku crossa,
//   • koszyk zleceń LIMIT od szybkiej do wolnej wstęgi (long: górna krawędź szybkiej → górna krawędź wolnej),
//     kolejne zlecenia mogą być większe (np. 1/3 na szybkiej + 2/3 na wolnej),
//   • wspólny SL: szerokość wolnej wstęgi za wolną wstęgą,
//   • wielkości tak, by przy wypełnieniu CAŁEGO koszyka strata na SL = Risk per Basket ($) – „wiem, ile stracę”,
//   • dopóki nic się nie wypełniło, zlecenia idą za wstęgami (co świecę); po pierwszym wypełnieniu poziomy są stałe,
//   • cel: R-krotność FAKTYCZNEJ straty wypełnionych zleceń, liczony od średniej ceny (ustawiany jako TP na pozycjach),
//   • unieważnienie: cross w drugą stronę zamyka wszystko i kasuje zlecenia,
//   • po SL nowy koszyk dopiero po nowym crossie (wstęgi muszą się znowu rozejść),
//   • bezpiecznik konta: dzienny limit straty, limit koszyków na dzień, okno handlu, zamykanie w piątek.
//  Bez dokładania ponad plan, bez martyngału, zawsze z SL.
using System;
using System.Collections.Generic;
using System.Linq;
using cAlgo.API;
using cAlgo.API.Indicators;
using cAlgo.API.Internals;

namespace cAlgo.Robots
{
    public enum HgDirection { Both, LongOnly, ShortOnly }
    public enum HgExit { FixedTarget, TrailBehindFastBand, TrailDistanceR }
    public enum HgTrailFrom { FullBasket, AnyFill }

    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class HTS_Grid_Bot : Robot
    {
        [Parameter("Bot Label", DefaultValue = "HTS_GRID", Group = "System")]
        public string BotLabel { get; set; }

        [Parameter("Log", DefaultValue = true, Group = "System")]
        public bool LogOn { get; set; }

        // ───────── WSTĘGI ─────────
        [Parameter("Fast Band", DefaultValue = 33, MinValue = 2, Group = "HTS Bands")]
        public int FastLen { get; set; }

        [Parameter("Slow Band", DefaultValue = 144, MinValue = 2, Group = "HTS Bands")]
        public int SlowLen { get; set; }

        [Parameter("MA Type", DefaultValue = MovingAverageType.WilderSmoothing, Group = "HTS Bands")]
        public MovingAverageType MaType { get; set; }

        [Parameter("Direction", DefaultValue = HgDirection.Both, Group = "HTS Bands")]
        public HgDirection Direction { get; set; }

        // ───────── KOSZYK ─────────
        [Parameter("Orders in Basket", DefaultValue = 2, MinValue = 1, MaxValue = 20, Group = "Grid")]
        public int Levels { get; set; }

        [Parameter("Last Order Size vs First (x)", DefaultValue = 2.0, MinValue = 0.2, MaxValue = 10, Group = "Grid")]
        public double LastMult { get; set; }

        [Parameter("Zone End (0 = fast band, 1 = slow band)", DefaultValue = 1.0, MinValue = 0, MaxValue = 2, Group = "Grid")]
        public double ZoneEnd { get; set; }

        [Parameter("SL Beyond Slow Band (x slow band width)", DefaultValue = 1.0, MinValue = 0.1, MaxValue = 5, Group = "Grid")]
        public double SlWidthMult { get; set; }

        [Parameter("New Basket After SL Only After New Cross", DefaultValue = true, Group = "Grid")]
        public bool NeedNewCross { get; set; }

        // ───────── RYZYKO I CEL ─────────
        [Parameter("Risk per Basket ($, full fill at SL)", DefaultValue = 200.0, MinValue = 1, Group = "Risk")]
        public double RiskCash { get; set; }

        [Parameter("Target (R of filled risk, 0 = exit on cross only)", DefaultValue = 3.0, MinValue = 0, Group = "Risk")]
        public double TpR { get; set; }

        [Parameter("Max SL (pips, 0 = off)", DefaultValue = 0.0, MinValue = 0, Group = "Risk")]
        public double MaxSlPips { get; set; }

        // ───────── WYJŚCIE: STAŁY CEL ALBO TRAILING ─────────
        [Parameter("Exit Mode", DefaultValue = HgExit.FixedTarget, Group = "Exit")]
        public HgExit ExitMode { get; set; }

        [Parameter("Trail From (full basket / any fill)", DefaultValue = HgTrailFrom.FullBasket, Group = "Exit")]
        public HgTrailFrom TrailFrom { get; set; }

        [Parameter("Trail Start After Profit (R)", DefaultValue = 1.0, MinValue = 0, Group = "Exit")]
        public double TrailStartR { get; set; }

        [Parameter("Trail Distance (R, distance mode)", DefaultValue = 1.0, MinValue = 0.1, Group = "Exit")]
        public double TrailDistR { get; set; }

        [Parameter("Trail Buffer Beyond Fast Band (R, band mode)", DefaultValue = 0.0, MinValue = 0, Group = "Exit")]
        public double TrailBufR { get; set; }

        // ───────── OKNO ─────────
        [Parameter("Start Hour (UTC, = End Hour → always)", DefaultValue = 0, MinValue = 0, MaxValue = 23, Group = "Trading Window")]
        public int StartHour { get; set; }

        [Parameter("End Hour (UTC)", DefaultValue = 0, MinValue = 0, MaxValue = 23, Group = "Trading Window")]
        public int EndHour { get; set; }

        [Parameter("Cancel Unfilled Basket Outside Window", DefaultValue = true, Group = "Trading Window")]
        public bool CancelOutside { get; set; }

        [Parameter("Close All on Friday (hour UTC, -1 = off)", DefaultValue = 19, MinValue = -1, MaxValue = 23, Group = "Trading Window")]
        public int FridayCloseHour { get; set; }

        // ───────── BEZPIECZNIK ─────────
        [Parameter("Equity Guard: Max Daily Loss ($, 0 = off)", DefaultValue = 1000.0, MinValue = 0, Group = "Equity Guard")]
        public double MaxDailyLossCash { get; set; }

        [Parameter("Max Baskets per Day (0 = off)", DefaultValue = 4, MinValue = 0, Group = "Equity Guard")]
        public int MaxBasketsPerDay { get; set; }

        [Parameter("Day Reset Time Zone (Windows ID)", DefaultValue = "Central European Standard Time", Group = "Equity Guard")]
        public string DayResetTimeZone { get; set; }

        // ───────── stan ─────────
        private MovingAverage _fh, _fl, _sh, _sl;
        private int _dir;            // kierunek koszyka (0 = brak)
        private bool _frozen;        // po pierwszym wypełnieniu
        private int _blockedDir;     // po SL: kierunek zablokowany do nowego crossu
        private bool _closing;
        private double _sl0;         // SL koszyka z chwili pierwszego wypełnienia (1R)
        private double _best;        // najlepsza cena od startu trailingu

        private TimeZoneInfo _dayTz;
        private DateTime _day;
        private double _dayStartBalance;
        private int _basketsToday;
        private bool _dayBlocked;

        private int nBaskets, nTp, nSl, nCross;

        protected override void OnStart()
        {
            _fh = Indicators.MovingAverage(Bars.HighPrices, FastLen, MaType);
            _fl = Indicators.MovingAverage(Bars.LowPrices, FastLen, MaType);
            _sh = Indicators.MovingAverage(Bars.HighPrices, SlowLen, MaType);
            _sl = Indicators.MovingAverage(Bars.LowPrices, SlowLen, MaType);

            try { _dayTz = TimeZoneInfo.FindSystemTimeZoneById(DayResetTimeZone); }
            catch (Exception) { _dayTz = TimeZoneInfo.Utc; }
            StartDay(LocalNow().Date);

            Positions.Opened += OnOpened;
            Positions.Closed += OnClosed;

            // po restarcie: przejmij otwarte pozycje bota jako zamrożony koszyk
            var pos = MyPositions();
            if (pos.Length > 0)
            {
                _dir = pos[0].TradeType == TradeType.Buy ? 1 : -1;
                _frozen = true;
                _sl0 = pos[0].StopLoss.HasValue ? pos[0].StopLoss.Value : 0;
                _best = double.NaN;
            }
            else
            {
                CancelPendings();
            }

            Print("HTS Grid: start. Koszyk {0} zleceń (ostatnie {1}x pierwszego), strefa szybka→{2:F2}, SL {3}x szerokości wolnej za wolną, ryzyko {4} $, cel {5}R",
                Levels, LastMult, ZoneEnd, SlWidthMult, RiskCash, TpR);
        }

        protected override void OnStop()
        {
            Print("HTS Grid: koszyków {0}, cel {1}, SL {2}, zamknięte crossem {3}", nBaskets, nTp, nSl, nCross);
        }

        protected override void OnTick()
        {
            CheckDay();

            if (FridayCloseHour >= 0 && Server.Time.DayOfWeek == DayOfWeek.Friday && Server.Time.Hour >= FridayCloseHour
                && (_dir != 0 || MyPositions().Length > 0))
                CloseBasket("piątek");
        }

        protected override void OnBar()
        {
            int i = Bars.Count - 2;
            if (i < SlowLen * 3) return;
            CheckDay();

            int tr = Trend(i);
            if (_blockedDir != 0 && tr != _blockedDir) _blockedDir = 0;

            // unieważnienie: cross w drugą stronę / koniec trendu
            if (_dir != 0 && tr != _dir)
            {
                if (MyPositions().Length > 0) nCross++;
                CloseBasket("cross w drugą stronę");
            }

            bool friday = FridayCloseHour >= 0 && Server.Time.DayOfWeek == DayOfWeek.Friday && Server.Time.Hour >= FridayCloseHour;

            // nowy koszyk
            if (_dir == 0 && MyPositions().Length == 0 && tr != 0 && tr != _blockedDir && DirOk(tr)
                && IsTradeTime() && !friday && !_dayBlocked && (MaxBasketsPerDay == 0 || _basketsToday < MaxBasketsPerDay))
            {
                _dir = tr;
                _frozen = false;
                if (PlaceBasket(i, true))
                {
                    _basketsToday++;
                    nBaskets++;
                }
                else
                {
                    _dir = 0;
                }
                return;
            }

            // koszyk z wypełnieniami: trailing (jeśli włączony)
            if (_dir != 0 && _frozen)
            {
                Trail(i);
                return;
            }

            // koszyk bez wypełnień: zlecenia idą za wstęgami
            if (_dir != 0 && !_frozen)
            {
                if ((CancelOutside && !IsTradeTime()) || friday || _dayBlocked)
                {
                    CancelPendings();
                    _dir = 0;
                    return;
                }
                CancelPendings();
                if (!PlaceBasket(i, false)) _dir = 0;
            }
        }

        // ───────── koszyk ─────────
        private bool PlaceBasket(int i, bool isNew)
        {
            int d = _dir;
            double width = _sh.Result[i] - _sl.Result[i];
            double a = d == 1 ? _fh.Result[i] : _fl.Result[i];      // szybka wstęga – pierwsza pozycja
            double b = d == 1 ? _sh.Result[i] : _sl.Result[i];      // wolna wstęga – ostatnia dokładka
            double sl = d == 1 ? _sl.Result[i] - SlWidthMult * width : _sh.Result[i] + SlWidthMult * width;
            if (width <= 0) return false;

            double wSum = 0;
            var w = new double[Levels];
            for (int k = 0; k < Levels; k++)
            {
                w[k] = Levels == 1 ? 1.0 : 1.0 + (LastMult - 1.0) * k / (Levels - 1);
                wSum += w[k];
            }

            double refPx = d == 1 ? Symbol.Ask : Symbol.Bid;
            var type = d == 1 ? TradeType.Buy : TradeType.Sell;
            int placed = 0;
            double totalLoss = 0;
            for (int k = 0; k < Levels; k++)
            {
                double p = Levels == 1 ? 0.0 : ZoneEnd * k / (Levels - 1);
                double lv = a + (b - a) * p;
                lv = Math.Round(lv / Symbol.TickSize) * Symbol.TickSize;
                if (d == 1 ? lv >= refPx - Symbol.TickSize : lv <= refPx + Symbol.TickSize) continue;   // cena już za poziomem
                double dist = d * (lv - sl);
                if (dist <= 0) continue;
                double slPips = dist / Symbol.PipSize;
                if (MaxSlPips > 0 && slPips > MaxSlPips) continue;
                double units = Symbol.NormalizeVolumeInUnits(RiskCash * (w[k] / wSum) / (slPips * Symbol.PipValue), RoundingMode.Down);
                if (units < Symbol.VolumeInUnitsMin) continue;
                units = Math.Min(units, Symbol.VolumeInUnitsMax);
                var r = PlaceLimitOrder(type, SymbolName, units, lv, BotLabel, slPips, null);
                if (r.IsSuccessful)
                {
                    placed++;
                    totalLoss += slPips * Symbol.PipValue * units;
                }
                else if (LogOn) Print("Zlecenie {0} odrzucone: {1}", k + 1, r.Error);
            }
            if (isNew && LogOn && placed > 0)
                Print("Koszyk {0}: {1} zleceń, SL {2}, łączna strata przy pełnym wypełnieniu {3:F0} $",
                    d == 1 ? "LONG" : "SHORT", placed, Math.Round(sl / Symbol.TickSize) * Symbol.TickSize, totalLoss);
            return placed > 0;
        }

        private void OnOpened(PositionOpenedEventArgs args)
        {
            var p = args.Position;
            if (p.SymbolName != SymbolName || p.Label != BotLabel) return;
            if (!_frozen)
            {
                _sl0 = p.StopLoss.HasValue ? p.StopLoss.Value : 0;
                _best = double.NaN;
            }
            _frozen = true;
            UpdateTarget();
        }

        private bool TrailingActive()
        {
            if (ExitMode == HgExit.FixedTarget) return false;
            if (TrailFrom == HgTrailFrom.AnyFill) return true;
            return !PendingOrders.Any(x => x.SymbolName == SymbolName && x.Label == BotLabel);   // cały koszyk wypełniony
        }

        // średnia cena, suma wolumenu i 1R w cenie (odległość średnia → SL z pierwszego wypełnienia)
        private bool BasketAvg(Position[] pos, int d, out double avg, out double unitR)
        {
            double u = 0, ue = 0;
            foreach (var p in pos)
            {
                u += p.VolumeInUnits;
                ue += p.VolumeInUnits * p.EntryPrice;
            }
            avg = u > 0 ? ue / u : 0;
            unitR = d * (avg - _sl0);
            return u > 0 && unitR > 0;
        }

        // trailing na zamkniętej świecy: SL za szybką wstęgą albo X R za najlepszą ceną, tylko w stronę zysku
        private void Trail(int i)
        {
            if (!TrailingActive()) return;
            var pos = MyPositions();
            if (pos.Length == 0) return;
            int d = pos[0].TradeType == TradeType.Buy ? 1 : -1;
            double avg, unitR;
            if (!BasketAvg(pos, d, out avg, out unitR)) return;

            double ext = d == 1 ? Bars.HighPrices[i] : Bars.LowPrices[i];
            _best = double.IsNaN(_best) ? ext : (d == 1 ? Math.Max(_best, ext) : Math.Min(_best, ext));
            if (d * (_best - avg) / unitR < TrailStartR) return;

            double cand = ExitMode == HgExit.TrailBehindFastBand
                ? (d == 1 ? _fl.Result[i] : _fh.Result[i]) - d * TrailBufR * unitR
                : _best - d * TrailDistR * unitR;
            cand = Math.Round(cand / Symbol.TickSize) * Symbol.TickSize;
            double px = d == 1 ? Symbol.Bid : Symbol.Ask;
            if (d == 1 ? cand >= px : cand <= px)
            {
                CloseBasket("trailing – cena już za nowym SL");
                return;
            }
            foreach (var p in pos)
            {
                if (p.StopLoss.HasValue && (d == 1 ? cand <= p.StopLoss.Value : cand >= p.StopLoss.Value)) continue;
                var r = ModifyPosition(p, cand, p.TakeProfit, ProtectionType.Absolute);
                if (!r.IsSuccessful && LogOn) Print("Trailing: nie udało się przesunąć SL: {0}", r.Error);
            }
        }

        // cel = R-krotność faktycznej straty wypełnionych zleceń, od średniej ceny; ten sam TP na wszystkich pozycjach
        private void UpdateTarget()
        {
            var pos = MyPositions();
            if (pos.Length == 0) return;
            if (TrailingActive())
            {
                // trailing zamiast stałego celu: zdejmij TP ze wszystkich pozycji
                foreach (var p in pos)
                    if (p.TakeProfit.HasValue)
                    {
                        var r0 = ModifyPosition(p, p.StopLoss, null, ProtectionType.Absolute);
                        if (!r0.IsSuccessful && LogOn) Print("Nie udało się zdjąć celu: {0}", r0.Error);
                    }
                if (LogOn) Print("Wypełnione {0} zlecenia – wyjście trailingiem ({1})", pos.Length, ExitMode);
                return;
            }
            if (TpR <= 0) return;
            int d = pos[0].TradeType == TradeType.Buy ? 1 : -1;
            double u = 0, ue = 0, risk = 0;
            foreach (var p in pos)
            {
                if (!p.StopLoss.HasValue) continue;
                u += p.VolumeInUnits;
                ue += p.VolumeInUnits * p.EntryPrice;
                risk += p.VolumeInUnits * d * (p.EntryPrice - p.StopLoss.Value);
            }
            if (u <= 0 || risk <= 0) return;
            double avg = ue / u;
            double tp = avg + d * TpR * risk / u;
            tp = Math.Round(tp / Symbol.TickSize) * Symbol.TickSize;
            double px = d == 1 ? Symbol.Bid : Symbol.Ask;
            if (d == 1 ? px >= tp : px <= tp)
            {
                CloseBasket("cel osiągnięty");
                nTp++;
                return;
            }
            foreach (var p in pos)
            {
                var r = ModifyPosition(p, p.StopLoss, tp, ProtectionType.Absolute);
                if (!r.IsSuccessful && LogOn) Print("Nie udało się ustawić celu: {0}", r.Error);
            }
            if (LogOn) Print("Wypełnione {0} zlecenia, średnia {1}, cel {2}, faktyczna strata na SL {3:F0} $",
                pos.Length, Math.Round(avg / Symbol.TickSize) * Symbol.TickSize, tp, risk / Symbol.PipSize * Symbol.PipValue);
        }

        private void OnClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (p.SymbolName != SymbolName || p.Label != BotLabel) return;
            // zamknięcia zlecone przez bota obsługuje CloseBasket; tu tylko SL / cel / stop out po stronie serwera
            if (_closing || args.Reason == PositionCloseReason.Closed) return;
            if (MyPositions().Length > 0) return;          // koszyk jeszcze trwa

            // koszyk zakończony: skasuj resztę zleceń
            CancelPendings();
            if (args.Reason == PositionCloseReason.TakeProfit)
            {
                nTp++;
                if (LogOn) Print("Koszyk na celu");
            }
            else if (p.NetProfit >= 0)
            {
                nTp++;
                if (LogOn) Print("Koszyk zamknięty trailingiem na plusie");
            }
            else
            {
                nSl++;
                if (NeedNewCross) _blockedDir = p.TradeType == TradeType.Buy ? 1 : -1;
                if (LogOn) Print("Koszyk na SL – czekam na nowy cross");
            }
            _dir = 0;
            _frozen = false;
        }

        private void CloseBasket(string reason)
        {
            _closing = true;
            CancelPendings();
            foreach (var p in MyPositions()) ClosePosition(p);
            _closing = false;
            if (_dir != 0 && LogOn) Print("Koszyk zamknięty: {0}", reason);
            _dir = 0;
            _frozen = false;
        }

        private void CancelPendings()
        {
            foreach (var o in PendingOrders.Where(x => x.SymbolName == SymbolName && x.Label == BotLabel).ToArray())
                CancelPendingOrder(o);
        }

        // ───────── pomocnicze ─────────
        private int Trend(int i)
        {
            if (_fl.Result[i] > _sh.Result[i]) return 1;
            if (_fh.Result[i] < _sl.Result[i]) return -1;
            return 0;
        }

        private bool DirOk(int d)
        {
            return Direction == HgDirection.Both || (d == 1 && Direction == HgDirection.LongOnly) || (d == -1 && Direction == HgDirection.ShortOnly);
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

        // ───────── bezpiecznik konta ─────────
        private DateTime LocalNow()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(Server.Time, DateTimeKind.Utc), _dayTz);
        }

        private void StartDay(DateTime day)
        {
            _day = day;
            _dayStartBalance = Account.Balance;
            _basketsToday = 0;
            _dayBlocked = false;
        }

        private void CheckDay()
        {
            var today = LocalNow().Date;
            if (today != _day) StartDay(today);
            if (_dayBlocked || MaxDailyLossCash <= 0) return;
            double dayPnl = Account.Equity - _dayStartBalance;
            if (dayPnl <= -MaxDailyLossCash)
            {
                _dayBlocked = true;
                Print("BEZPIECZNIK: dzienna strata {0:F0} $ – zamykam wszystko i blokuję do jutra", dayPnl);
                CloseBasket("bezpiecznik dzienny");
            }
        }
    }
}
