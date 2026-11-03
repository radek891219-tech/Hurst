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
