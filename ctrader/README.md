# cTrader: HTS_Strategy_Tester_II pod FTMO (GER40 m1)

| Plik | Co to jest |
|---|---|
| `HTS_Strategy_Tester_FTMO.cs` | Kod bota HTS z wbudowaną ochroną FTMO i poprawkami błędów |
| `FTMO_HTS_GER40.cash_m1_poprawione.cbotset` | Ustawienia z poprawkami (import w cTraderze) |
| `HTS_ScaleIn_FTMO.cs` | Wersja HTS z uśrednianiem wejścia: kilka wejść, wspólny SL, stałe ryzyko kampanii |
| `FtmoGuard.cs` | Opcjonalny osobny strażnik – gdy na koncie chodzi kilka botów |

## Ochrona FTMO (grupa `=== FTMO PROTECTION ===`)
- **Max Daily Loss** – dzienny wynik = equity minus saldo z północy CET/CEST (jak liczy FTMO). Po przekroczeniu: zamknięcie pozycji bota i blokada wejść do końca dnia.
- **Daily Profit Target** – po osiągnięciu blokada wejść (pozycje zostają).
- **Max Trades per Day**, **Max Consecutive Losses per Day** – blokada wejść do końca dnia.
- Działa też w backteście.

## Poprawione błędy
- Zawieszenie w stanie „CAMPAIGN ACTIVE”: gdy trend na H1 zmienił się w trakcie pozycji, a pozycja zamknęła się na SL po stronie serwera, bot nie otwierał już żadnych transakcji do restartu.
- Zawieszenie, gdy wejście cross się nie otworzyło (np. za mały wolumen), a wejścia touch są wyłączone.
- Pozycja bez SL: SL ustawiany był po otwarciu i błąd był ignorowany. Teraz taka pozycja jest od razu zamykana.
- „Close EOD on FRIDAY” zamykał pozycje w piątek o 00:00 UTC (poza oknem), a nie po końcu okna.
- Ręczna pozycja bez etykiety na tym samym symbolu wywracała bota (NullReferenceException).
- Po restarcie bota pozostałe 5% po TP1 nie było rozpoznawane jako „po TP1”, więc trailing się nie włączał.

## Zmiany w ustawieniach
| Parametr | Było | Jest |
|---|---|---|
| RiskCash | 970 | 500 |
| StartHour / EndHour (UTC) | 2 / 22 | 7 / 15 |
| WindowBlockHours | 2 | 1 |
| EodCloseFriday | false | true |

## HTS Scale-In (uśrednianie wejścia)
- Sygnał jak w HTS. Na sygnale: wejście 1 po rynku, wejścia 2..N jako limity co `Step` % drogi do SL.
- Wszystkie wejścia mają wspólny SL. Ryzyko kampanii dzielone jest między wejścia, więc strata przy pełnym wypełnieniu = `Campaign Risk`.
- TP1 liczony od średniej ceny wejścia (R-krotność wypełnionego ryzyka albo stałe $). Na TP1: częściowe zamknięcie, kasowanie niewypełnionych limitów, SL reszty na średnią, trailing.
- Niewypełnione limity kasowane po `Cancel Unfilled Entries After` świecach, poza oknem handlu i przy zmianie trendu.
- FTMO: kampania startuje tylko, jeśli jej pełne ryzyko mieści się w dziennym limicie.
