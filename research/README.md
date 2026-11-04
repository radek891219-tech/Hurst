# Badanie strategii HTS na danych 1m

`hts_backtest.py` – backtester logiki HTS (cBot cTrader) i HTS Scale-In w Pythonie.

## Dane
Plik CSV ze świecami 1m GER40 / DAX, najlepiej 2–5 lat. Obsługiwane formaty:
- HistData: `20230102 090000;open;high;low;close;vol` (strefa `Etc/GMT+5`),
- Dukascopy: `Gmt time,Open,High,Low,Close,Volume`,
- eksport TradingView: `time,open,high,low,close`,
- MT4/MT5: `Date,Time,Open,High,Low,Close,...`.

## Uruchomienie
```
python research/hts_backtest.py --data dax_1m.csv --src-tz UTC              # porównanie wariantów
python research/hts_backtest.py --data dax_1m.csv --src-tz UTC --optimize   # optymalizacja + test poza próbą
python research/hts_backtest.py --synthetic                                 # kontrola na losowym rynku
```

## Jak czytać wyniki
- Optymalizacja wybiera ustawienia tylko na pierwszych 60% danych (trening) i pokazuje ich wynik na ostatnich 40% (test).
  Liczy się wynik na **teście** – trening zawsze wygląda dobrze.
- `t` to istotność średniego wyniku w R. Poniżej ~2 wynik nie odróżnia się od przypadku.
- Kontrola na losowym rynku musi dawać wyniki wokół zera – jeśli nie, backtester ma błąd (zagląda w przyszłość).

## Wczesne wykrywanie trendu (`hts_trendstart.py`)
Ta sama logika co `HTS_TrendStart.pine`: starty, dokładki i 6 wariantów zarządzania pozycją na tych samych sygnałach.
```
python research/hts_trendstart.py --data dax_1m.csv --src-tz UTC --cost 1.5   # koszt na wejście w punktach
python research/hts_trendstart.py --synthetic                                 # kontrola: losowy rynek
```
Wynik dzielony na pierwszą połowę (trening) i drugą (test). Na losowym rynku wszystkie warianty dają ok. 0 R
(lekko poniżej, bo SL liczony jest przed TP), a po kosztach są na minusie – zarządzanie zmienia obsunięcie
i skuteczność, ale nie tworzy przewagi.

## Europa vs USA (`eu_vs_us.py`)
Czy sesja amerykańska odwraca albo kontynuuje ruch Europy na DAX / GER40 – na długich danych 1m, z wynikiem dla każdego roku.
```
python research/eu_vs_us.py --data dax_1m.csv --src-tz UTC
python research/eu_vs_us.py --data dax_1m.csv --src-tz UTC --us-end 11:30   # tylko pierwsze 2 h USA
python research/eu_vs_us.py --synthetic                                    # kontrola: losowy rynek
```
Na losowym rynku korelacja wychodzi ok. 0, a pojedyncze |t| ≈ 2 w jednej grupie trafia się przypadkiem –
dlatego zależność liczy się dopiero, gdy ma ten sam znak w obu połowach danych i w większości lat.

## Okresy średnich HTS na DAX (`hts_ma_grid.py`)
```
python research/hts_ma_grid.py --data "GRXEUR/DAT_ASCII_GRXEUR_M1_*.csv" --src-tz Etc/GMT+5
```
Wynik na DAX 1m z HistData (GRXEUR, XI 2010 – XII 2018), logika cBota HTS (1 wejście, SL 0.5 %, TP1 1.6R, trailing, 7–15 UTC, koszt 1.5 pkt),
578 zestawów: pasma H1 i m1 z {13, 21, 33, 50, 89} / {89, 144, 200, 288}, Wilder i EMA. Wybór na 2010–2015, test na 2016–2018:
- trening: 81 % zestawów na plus (silny trend DAX), ale tylko 2 % istotnie (t ≥ 2) – tyle, ile daje przypadek przy 578 próbach,
- test: 2 % zestawów na plus, 0 % istotnie; 5 najlepszych z treningu średnio −0.08 R na transakcję, każdy na minusie w każdym roku testu,
- domyślne 33/144 + 33/144: trening +0.11 R (t 1.2), test −0.12 R (t −1.3),
- korelacja wyniku trening ↔ test między zestawami +0.24 – wybór średnich na historii prawie nic nie mówi o przyszłości.
Wniosek: zmiana okresów średnich nie daje HTS przewagi na DAX; problem leży w samych sygnałach wejścia, nie w długości średnich.
