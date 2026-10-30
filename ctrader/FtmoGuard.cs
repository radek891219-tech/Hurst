// FTMO Guard – strażnik limitów dla innego cBota (np. HTS_Strategy_Tester_II)
//  • dzienna maks. strata liczona tak jak FTMO: equity względem salda z początku dnia (północ CET/CEST)
//  • po przekroczeniu limitu zamyka pozycje i natychmiast zamyka każdą nową pozycję do końca dnia
//  • opcjonalnie: dzienny cel zysku, maks. liczba transakcji dziennie, maks. strat z rzędu
//  • zamyka pozycje w piątek przed weekendem
// Uruchom go na dowolnym wykresie obok bota, którego ma pilnować (na tym samym koncie).
// Uwaga: w backteście cTradera działa tylko jeden bot naraz, więc strażnik chroni konto demo/live, nie backtest.
using System;
using System.Linq;
using cAlgo.API;

namespace cAlgo.Robots
{
    [Robot(TimeZone = TimeZones.UTC, AccessRights = AccessRights.None)]
    public class FtmoGuard : Robot
    {
        [Parameter("Etykieta pilnowanego bota (puste = wszystkie)", DefaultValue = "HTS_for_FTMO", Group = "Filtr")]
        public string WatchLabel { get; set; }

        [Parameter("Przy limicie straty zamknij WSZYSTKIE pozycje na koncie", DefaultValue = true, Group = "Filtr")]
        public bool CloseAllOnLoss { get; set; }

        [Parameter("Dzienna maks. strata ($)", DefaultValue = 3000, MinValue = 0, Group = "Limity")]
        public double MaxDailyLoss { get; set; }

        [Parameter("Dzienny cel zysku ($, 0 = wył.)", DefaultValue = 0, MinValue = 0, Group = "Limity")]
        public double DailyTarget { get; set; }

        [Parameter("Maks. transakcji dziennie (0 = wył.)", DefaultValue = 4, MinValue = 0, Group = "Limity")]
        public int MaxTradesPerDay { get; set; }

        [Parameter("Maks. strat z rzędu w ciągu dnia (0 = wył.)", DefaultValue = 3, MinValue = 0, Group = "Limity")]
        public int MaxConsecLosses { get; set; }

        [Parameter("Saldo na początku dnia ($, 0 = automatycznie)", DefaultValue = 0, MinValue = 0, Group = "Limity")]
        public double ManualDayStartBalance { get; set; }

        [Parameter("Strefa czasowa dnia (ID Windows)", DefaultValue = "Central European Standard Time", Group = "Czas")]
        public string DayTimeZoneId { get; set; }

        [Parameter("Zamykaj pozycje w piątek", DefaultValue = true, Group = "Czas")]
        public bool FridayClose { get; set; }

        [Parameter("Piątek: godzina zamknięcia (czas strefy dnia)", DefaultValue = 21, MinValue = 0, MaxValue = 23, Group = "Czas")]
        public int FridayCloseHour { get; set; }

        [Parameter("Piątek: minuta zamknięcia", DefaultValue = 30, MinValue = 0, MaxValue = 59, Group = "Czas")]
        public int FridayCloseMinute { get; set; }

        private TimeZoneInfo _dayTz;
        private DateTime _day;
        private double _dayStartBalance;
        private int _tradesToday;
        private int _consecLosses;
        private bool _blocked;
        private string _blockReason = "";

        protected override void OnStart()
        {
            try
            {
                _dayTz = TimeZoneInfo.FindSystemTimeZoneById(DayTimeZoneId);
            }
            catch (Exception)
            {
                Print("Nie znaleziono strefy czasowej '{0}', używam UTC.", DayTimeZoneId);
                _dayTz = TimeZoneInfo.Utc;
            }

            StartNewDay(LocalNow().Date);
            if (ManualDayStartBalance > 0)
                _dayStartBalance = ManualDayStartBalance;

            Positions.Opened += OnPositionOpened;
            Positions.Closed += OnPositionClosed;
            Timer.Start(TimeSpan.FromSeconds(1));
            Check();
        }

        protected override void OnTick()
        {
            Check();
        }

        protected override void OnTimer()
        {
            Check();
        }

        private DateTime LocalNow()
        {
            return TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(Server.Time, DateTimeKind.Utc), _dayTz);
        }

        private void StartNewDay(DateTime day)
        {
            _day = day;
            _dayStartBalance = Account.Balance;
            _tradesToday = 0;
            _consecLosses = 0;
            _blocked = false;
            _blockReason = "";
            Print("Nowy dzień {0:yyyy-MM-dd}, saldo startowe {1:F2}", day, _dayStartBalance);
        }

        private bool IsWatched(Position p)
        {
            return string.IsNullOrEmpty(WatchLabel) || (p.Label != null && p.Label.Contains(WatchLabel));
        }

        private bool IsWatched(PendingOrder o)
        {
            return string.IsNullOrEmpty(WatchLabel) || (o.Label != null && o.Label.Contains(WatchLabel));
        }

        private void Check()
        {
            var now = LocalNow();
            if (now.Date != _day)
                StartNewDay(now.Date);

            double dayPnl = Account.Equity - _dayStartBalance;

            if (!_blocked && MaxDailyLoss > 0 && dayPnl <= -MaxDailyLoss)
                Block(string.Format("limit straty dziennej ({0:F0} $)", dayPnl), CloseAllOnLoss);

            if (!_blocked && DailyTarget > 0 && dayPnl >= DailyTarget)
                Block(string.Format("dzienny cel osiągnięty ({0:F0} $)", dayPnl), false);

            if (FridayClose && now.DayOfWeek == DayOfWeek.Friday &&
                now.TimeOfDay >= new TimeSpan(FridayCloseHour, FridayCloseMinute, 0))
            {
                if (!_blocked)
                    Block("zamknięcie przed weekendem", false);
                else
                    CloseWatched(false);
            }

            DrawStatus(dayPnl);
        }

        private void Block(string reason, bool closeEverything)
        {
            _blocked = true;
            _blockReason = reason;
            Print("BLOKADA: {0}", reason);
            CloseWatched(closeEverything);
        }

        private void CloseWatched(bool closeEverything)
        {
            foreach (var p in Positions.ToArray())
            {
                if (closeEverything || IsWatched(p))
                    ClosePosition(p);
            }
            foreach (var o in PendingOrders.ToArray())
            {
                if (closeEverything || IsWatched(o))
                    CancelPendingOrder(o);
            }
        }

        private void OnPositionOpened(PositionOpenedEventArgs args)
        {
            var p = args.Position;
            if (!IsWatched(p))
                return;

            _tradesToday++;

            // Przy limicie transakcji zamykamy tylko nadmiarową pozycję, otwarte wcześniej zostają
            if (!_blocked && MaxTradesPerDay > 0 && _tradesToday > MaxTradesPerDay)
            {
                _blocked = true;
                _blockReason = string.Format("limit {0} transakcji dziennie", MaxTradesPerDay);
                Print("BLOKADA: {0}", _blockReason);
            }

            if (_blocked)
            {
                Print("Zamykam nową pozycję {0} – aktywna blokada: {1}", p.Id, _blockReason);
                ClosePosition(p);
            }
        }

        private void OnPositionClosed(PositionClosedEventArgs args)
        {
            var p = args.Position;
            if (!IsWatched(p))
                return;

            _consecLosses = p.NetProfit < 0 ? _consecLosses + 1 : 0;

            if (!_blocked && MaxConsecLosses > 0 && _consecLosses >= MaxConsecLosses)
                Block(string.Format("{0} strat z rzędu", _consecLosses), false);
        }

        private void DrawStatus(double dayPnl)
        {
            string text = string.Format(
                "FTMO Guard\nDziś: {0:F2} $ (limit -{1:F0} $)\nTransakcje: {2}{3}\nStraty z rzędu: {4}\nStatus: {5}",
                dayPnl, MaxDailyLoss, _tradesToday,
                MaxTradesPerDay > 0 ? " / " + MaxTradesPerDay : "",
                _consecLosses,
                _blocked ? "STOP – " + _blockReason : "OK");
            Chart.DrawStaticText("ftmo_guard", text, VerticalAlignment.Top, HorizontalAlignment.Right, _blocked ? Color.OrangeRed : Color.LimeGreen);
        }
    }
}
