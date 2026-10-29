# Hurst [LazyBear] – wersja pod 1m (TradingView, Pine Script v5)

Dwa skrypty:

| Plik | Co robi |
|---|---|
| `HurstBands_1m.pine` | Pasma Hursta LazyBeara na wykresie + wykładnik Hursta (reżim rynku) + filtr wyższego TF + sygnały + alerty + tabela |
| `HurstOscillator_1m.pine` | Oscylator Hursta LazyBeara w osobnym panelu (te same poziomy co pasma) |
| `HurstWaves_1m.pine` | Miernik fal: średnia/mediana ostatnich N fal w górę i w dół (pkt, $, świece), postęp obecnej fali, prognoza, wejście na końcu typowej korekty |
| `HurstScalper_1m.pine` | Szybki scalper na MNQ/NQ: wejścia z trendem i z odbicia wg reżimu Hursta, TP/SL w $, symulacja wyników po kosztach, dzienny limit |

## Instalacja
TradingView → **Pine Editor** → wklej zawartość pliku → **Add to chart**. Powtórz dla drugiego pliku.

## Co zmieniono względem oryginału LazyBeara pod 1m
1. **Szerokość pasm w ATR** (domyślnie). Pasma 1.6% / 2.6% / 4.2% z oryginału na 1m są za szerokie,
   więc cena prawie nigdy ich nie dotyka. Teraz to 1.6 / 2.6 / 4.2 × ATR(100). Tryb procentowy dalej jest dostępny.
2. **Wykładnik Hursta H** (metoda R/S na 4 skalach, korekta Anisa-Lloyda, skalibrowana Monte Carlo):
   - `H < 0.47` → rynek **wraca do średniej** (fioletowe tło) → działają odbicia od pasm (**MR**),
   - `H > 0.53` → rynek w **trendzie** (niebieskie tło) → działają wybicia (**BO**), odbicia są wyłączone.
3. **Filtr trendu z 15m** (EMA 50, bez repaintu): longi tylko nad EMA, shorty tylko pod nią.
4. **Filtr sesji NY** (09:35–15:55, pod Nasdaq) – sygnały tylko w głównej sesji.
5. **Sygnały na zamkniętych świecach** (bez repaintu) + odstęp między sygnałami, żeby nie spamowały.
6. **Tabela**: H, reżim, trend z wyższego TF, odległość ceny od środka pasm w ATR, status sesji.

## Sygnały
- **MR ▲ / ▼** – knot wychodzi za zewnętrzne pasmo, a świeca zamyka się z powrotem w środku. Cel: CMA (szara linia).
- **BO** – zamknięcie za zewnętrznym pasmem w reżimie trendu, zgodnie z trendem z 15m.

## Alerty
Dodaj alert → warunek: wskaźnik → wybierz `MR Long`, `MR Short`, `BO Long`, `BO Short` albo `Dowolny sygnał`,
częstotliwość **Once Per Bar Close**. Można też wybrać „Any alert() function call”.

## Uwagi
- Środkowa linia (CMA) jest z definicji opóźniona o ok. `length/2 + 1` świec (tak jak u LazyBeara),
  dzięki temu nic się nie przerysowuje.
- Okno R/S 128 na 1m to ok. 2 godziny historii. Na bardzo szybkich rynkach (krypto) możesz spróbować 64,
  na spokojniejszych 256.
- To narzędzie analityczne, nie gotowa strategia. Przetestuj ustawienia na swoim instrumencie, zanim zaczniesz nim handlować.
