# cTrader: HTS_Strategy_Tester_II pod FTMO (GER40 m1)

| Plik | Co to jest |
|---|---|
| `FTMO_HTS_GER40.cash_m1_poprawione.cbotset` | Ustawienia HTS z poprawkami (import w cTraderze) |
| `FtmoGuard.cs` | Osobny cBot-strażnik limitów FTMO, działa obok HTS |

## Zmiany w ustawieniach HTS
| Parametr | Było | Jest | Dlaczego |
|---|---|---|---|
| RiskCash | 970 | 500 | 5 strat z rzędu przy 970 $ to prawie cały dzienny limit FTMO na koncie 100k |
| StartHour / EndHour (UTC) | 2 / 22 | 7 / 15 | Handel w głównej sesji DAX zamiast nocnego szumu i szerszego spreadu |
| WindowBlockHours | 2 | 1 | Przy krótszym oknie ostatnie wejścia do 14:00 UTC |
| EodCloseFriday | false | true | Bez trzymania pozycji przez weekend (wymóg konta FTMO typu Normal) |

Reszta bez zmian. Trailing (15 $ / 15 $) zostawiony – trzeba sprawdzić w backteście, czy nie zamyka pozycji tuż po wejściu.

## FTMO Guard
- Liczy dzienny wynik jak FTMO: equity minus saldo z północy czasu CET/CEST.
- Po przekroczeniu limitu straty zamyka pozycje i natychmiast zamyka każdą nową pozycję do końca dnia.
- Opcjonalnie: dzienny cel, limit transakcji dziennie, limit strat z rzędu, zamknięcie w piątek.
- Pilnuje pozycji z etykietą zawierającą `HTS_for_FTMO` (parametr do zmiany).
- Działa na koncie demo/live. W backteście cTradera chodzi tylko jeden bot, więc tam limitu nie ma.
