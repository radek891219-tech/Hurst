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

## HTS Grid – uśrednianie na wstęgach (`hts_grid.py`)
```
python research/hts_grid.py --data "GRXEUR/DAT_ASCII_GRXEUR_M1_*.csv" --src-tz Etc/GMT+5
python research/hts_grid.py --synthetic        # kontrola: losowy rynek
```
Jedno wejście kontra koszyk 2 / 3 / 5 zleceń od szybkiej do wolnej wstęgi, wspólny SL (szerokość wolnej za wolną), ryzyko pełnego koszyka = 1R,
cel 2R / 3R od faktycznej straty, cross zamyka, po SL czekanie na nowy cross. DAX 1m z HistData 2010–2018, trening do 2015, test 2016–2018.
- Koszt 1.5 pkt na zlecenie: M5 od −0.07 do −0.18 R, M15 od −0.02 do −0.24 R (jeden wariant +0.06 R na teście, t 0.5), H1 od −0.10 do −0.30 R, H4 głównie na minusie.
- Bez kosztów: M5 od −0.03 do −0.07 R, M15 i H1 dalej na minusie lub wokół zera – nie da się tego zrzucić na spread.
- Grid ≈ pojedyncze wejście (różnice w granicach szumu); pełny koszyk wypełnia się w ~70 % koszyków i wygrywa w 13–24 %.
- D1: 4–6 transakcji na okres – za mało, żeby cokolwiek stwierdzić.
- Losowy rynek (`--synthetic`): wszystkie warianty ok. 0 R minus koszt – backtest nie zagląda w przyszłość.

### Trailing zamiast stałego celu (m1, 89/298, 3 zlecenia 1 : 1.5 : 2, cel 1.7R)
`simulate(..., trail="band"|"r", trail_from="full"|"any", tr_start=1.0, tr_dist=1.0)` – trailing za szybką wstęgą albo X R za najlepszą ceną,
po pełnym koszyku albo po każdym wypełnieniu. DAX 2010–2018 i złoto (Oanda XAU/USD) 2006–2020:
- DAX, koszt 1.5 pkt: stały cel −0.125 R, trailingi od −0.10 do −0.13 R; 0–2 z 9 lat na plus,
- DAX bez kosztu: wszystko od −0.02 do +0.03 R (t ≤ 1.6),
- złoto, koszt 0.30 $: stały cel −0.29 R, trailingi od −0.24 do −0.30 R; 0 z 15 lat na plus,
- złoto bez kosztu: od −0.015 do +0.013 R.
Trailing daje pojedyncze duże wygrane (do +24 R na DAX, +37 R na złocie), ale mniej wygranych – średnia zostaje ok. 0 przed kosztami.
