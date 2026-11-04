"""
HTS Grid – uśrednianie na wstęgach HTS (metoda z odcinka „grid to uśrednianie”).

Zasady:
  • wstęgi HTS z High i Low: szybka 33, wolna 144 (Wilder), na interwale testu,
  • trend: szybka wstęga cała nad wolną (long) / cała pod wolną (short) – gramy tylko w kierunku crossa,
  • koszyk zleceń limit od szybkiej do wolnej wstęgi (long: górna krawędź szybkiej → górna krawędź wolnej),
    dopóki nic się nie wypełniło, zlecenia idą za wstęgami; po pierwszym wypełnieniu poziomy i SL są zamrożone,
  • wspólny SL: szerokość wolnej wstęgi za wolną wstęgą (long: dół wolnej − szerokość wolnej),
  • wielkości tak, by przy wypełnieniu CAŁEGO koszyka strata na SL = planowane ryzyko (1R),
  • cel: R-krotność FAKTYCZNEJ straty wypełnionych zleceń (np. 3R), liczony od średniej ceny,
  • unieważnienie: cross w drugą stronę zamyka wszystko i kasuje zlecenia,
  • po SL nowy koszyk dopiero po nowym crossie (wstęgi muszą się znowu rozejść),
  • koszt: X punktów na każde wypełnione zlecenie (proporcjonalnie do wielkości).
Na jednej świecy ostrożnie: wypełnienia → SL → cel dopiero od następnej świecy po ostatnim wypełnieniu.
Wynik w R planowanego ryzyka (jak pojedyncze wejście z tym samym ryzykiem).

Użycie:
  python research/hts_grid.py --data "GRXEUR/*.csv" --src-tz Etc/GMT+5
  python research/hts_grid.py --synthetic        # kontrola: losowy rynek → ok. 0R minus koszt
"""
import argparse
import glob
import math
import os
import sys

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hts_backtest import load_csv, rma, synthetic  # noqa: E402

VARIANTS = {
    "1 wejście: szybka":          ([0.0], [1.0]),
    "1 wejście: wolna":           ([1.0], [1.0]),
    "2 poziomy 1/3 + 2/3":        ([0.0, 1.0], [1 / 3, 2 / 3]),
    "3 poziomy po równo":         ([0.0, 0.5, 1.0], [1 / 3] * 3),
    "5 poziomów po równo":        ([0.0, 0.25, 0.5, 0.75, 1.0], [0.2] * 5),
}


def resample(df, tf):
    if tf == "1min":
        return df
    o = df.resample(tf, label="left", closed="left").agg({"open": "first", "high": "max", "low": "min", "close": "last"})
    return o.dropna()


def simulate(df, pos, wts, fast=33, slow=144, tp_r=3.0, cost=1.5, need_new_cross=True,
             trail="off", trail_from="full", tr_start=1.0, tr_dist=1.0, tr_buf=0.0,
             max_per_day=0, fri_close_h=-1, day_tz="Europe/Prague", bands=None,
             partial=0.0, part_be=True, part_buf=0.0):
    """trail: off – stały cel; band – SL za szybką wstęgą; r – SL w odległości tr_dist R za najlepszą ceną.
    trail_from: full – trailing (zamiast celu) tylko po wypełnieniu całego koszyka, any – po każdym wypełnieniu.
    Trailing startuje po zysku tr_start R (od faktycznej straty wypełnionych zleceń) i SL tylko się zacieśnia.
    max_per_day: limit nowych koszyków na dzień (dzień w strefie day_tz, jak w cBocie), 0 = bez limitu.
    fri_close_h: w piątek od tej godziny UTC wszystko zamknięte i brak nowych koszyków (jak w cBocie), -1 = wył.
    bands: (fH, fL, sH, sL) z interwału strategii, wyrównane do df – zlecenia i SL/TP sprawdzane na drobniejszych świecach.
    partial: część koszyka zamykana na celu (np. 0.5); reszta idzie za szybką wstęgą (SL tylko się zacieśnia),
             niewypełnione zlecenia są kasowane; part_be – SL reszty co najmniej na średniej cenie wejścia."""
    o, h, l, c = (df[k].values for k in ("open", "high", "low", "close"))
    H, L = df["high"], df["low"]
    if bands is None:
        fH, fL, sH, sL = (rma(x, n).values for x, n in ((H, fast), (L, fast), (H, slow), (L, slow)))
    else:
        fH, fL, sH, sL = bands       # wstęgi z wyższego interwału, wyrównane do świec df (wykonanie na niższym TF)
    trend = np.where(fL > sH, 1, np.where(fH < sL, -1, 0))
    n = len(c)
    res = []                 # (indeks wejścia, R, liczba wypełnień)
    d = 0                    # kierunek koszyka (0 = brak)
    blocked_dir = 0          # po SL: kierunek zablokowany do nowego crossu
    levels = units = None
    filled = None
    ent = None
    sl = 0.0
    last_fill = -1
    first = -1
    sl0 = None
    best = None
    tp1 = False
    rem = 1.0
    real = 0.0
    loc = df.index.tz_convert(day_tz)
    dayk = (loc.year * 10000 + loc.month * 100 + loc.day).values
    dow = df.index.dayofweek.values
    hr = df.index.hour.values
    cnt_day, cnt = None, 0

    def make(i, dd):
        # poziomy z wartości wstęg na zamkniętej świecy i-1
        a = fH[i - 1] if dd == 1 else fL[i - 1]
        b = sH[i - 1] if dd == 1 else sL[i - 1]
        w = sH[i - 1] - sL[i - 1]
        s = sL[i - 1] - w if dd == 1 else sH[i - 1] + w
        lv = np.array([a + (b - a) * p for p in pos])
        dist = dd * (lv - s)
        if np.any(dist <= 0):
            return None
        u = np.array(wts) / dist          # strata przy pełnym wypełnieniu = 1R
        return lv, u, s

    for i in range(slow * 3, n):
        tr = trend[i - 1]
        if dayk[i] != cnt_day:
            cnt_day, cnt = dayk[i], 0
        if fri_close_h >= 0 and dow[i] == 4 and hr[i] >= fri_close_h:
            if d != 0 and filled.any():
                pnl = real + rem * float(np.sum(units[filled] * d * (o[i] - ent[filled]))) - cost * float(np.sum(units[filled]))
                res.append((first, pnl, int(filled.sum())))
            d = 0
            continue
        if blocked_dir != 0 and tr != blocked_dir:
            blocked_dir = 0              # wstęgi się zeszły albo cross w drugą stronę → nowy cross odblokuje

        if d != 0 and tr != d:
            # unieważnienie: cross w drugą stronę / koniec trendu
            if filled.any():
                pnl = real + rem * float(np.sum(units[filled] * d * (o[i] - ent[filled]))) - cost * float(np.sum(units[filled]))
                res.append((first, pnl, int(filled.sum())))
            d = 0

        if d == 0 and tr != 0 and tr != blocked_dir and (max_per_day == 0 or cnt < max_per_day):
            m = make(i, tr)
            if m is not None:
                cnt += 1
                d = tr
                levels, units, sl = m
                filled = np.zeros(len(levels), bool)
                ent = np.full(len(levels), np.nan)
                last_fill = -1
                sl0 = None
                best = None
                tp1 = False
                rem = 1.0
                real = 0.0

        if d == 0:
            continue

        if not filled.any():
            m = make(i, d)               # zlecenia idą za wstęgami, dopóki nic się nie wypełniło
            if m is None:
                d = 0
                continue
            levels, units, sl = m

        # wypełnienia (tylko poziomy po właściwej stronie ceny zamknięcia poprzedniej świecy)
        for k in range(len(levels)):
            if filled[k] or tp1:
                continue
            lv = levels[k]
            if (d == 1 and c[i - 1] <= lv) or (d == -1 and c[i - 1] >= lv):
                continue                 # cena już za poziomem – takiego limitu się nie stawia
            if (d == 1 and l[i] <= lv) or (d == -1 and h[i] >= lv):
                ent[k] = min(o[i], lv) if d == 1 else max(o[i], lv)
                filled[k] = True
                last_fill = i
                if first < 0 or filled.sum() == 1:
                    first = i
        if not filled.any():
            continue

        if sl0 is None:
            sl0 = sl                     # SL zamrożony przy pierwszym wypełnieniu
        fu = units[filled]
        fe = ent[filled]
        su = float(np.sum(fu))
        risk_f = float(np.sum(fu * d * (fe - sl0)))
        avg = float(np.sum(fu * fe) / su)
        unit_r = risk_f / su             # odległość średnia → SL w cenie (1R wypełnionych zleceń)
        trailing = trail != "off" and (trail_from == "any" or filled.all())
        tp = avg + d * tp_r * unit_r
        exit_px = None
        if (d == 1 and l[i] <= sl) or (d == -1 and h[i] >= sl):
            exit_px = min(o[i], sl) if d == 1 else max(o[i], sl)
        elif not trailing and not tp1 and tp_r > 0 and i > last_fill and ((d == 1 and h[i] >= tp) or (d == -1 and l[i] <= tp)):
            px = max(o[i], tp) if d == 1 else min(o[i], tp)
            if partial > 0:
                # częściowe zamknięcie na celu, reszta za szybką wstęgą
                real += partial * float(np.sum(fu * d * (px - fe)))
                rem = 1.0 - partial
                tp1 = True
                if part_be:
                    sl = max(sl, avg) if d == 1 else min(sl, avg)
            else:
                exit_px = px
        if exit_px is None and tp1:
            cand = (fL[i] if d == 1 else fH[i]) - d * part_buf * unit_r
            sl = max(sl, cand) if d == 1 else min(sl, cand)
        if exit_px is None and trailing:
            best = (h[i] if d == 1 else l[i]) if best is None else (max(best, h[i]) if d == 1 else min(best, l[i]))
            if d * (best - avg) / unit_r >= tr_start:
                if trail == "band":
                    cand = (fL[i] if d == 1 else fH[i]) - d * tr_buf * unit_r
                else:
                    cand = best - d * tr_dist * unit_r
                sl = max(sl, cand) if d == 1 else min(sl, cand)
        if exit_px is not None:
            pnl = real + rem * float(np.sum(fu * d * (exit_px - fe))) - cost * su
            if pnl < 0 and need_new_cross:
                blocked_dir = d
            res.append((first, pnl, int(filled.sum())))
            d = 0
    return res


def summary(res, idx, split, n_lv):
    out = []
    for name, f in (("trening", lambda t: t < split), ("test", lambda t: t >= split)):
        rows = [(r, k) for (i, r, k) in res if f(idx[i])]
        if len(rows) < 3:
            out.append(f"{name}: n={len(rows)}")
            continue
        r = np.array([x[0] for x in rows])
        k = np.array([x[1] for x in rows])
        t = r.mean() / (r.std(ddof=1) / math.sqrt(len(r))) if r.std() > 0 else float("nan")
        cum = np.cumsum(r)
        dd = float(np.max(np.maximum.accumulate(np.r_[0, cum])[1:] - cum))
        full = k == n_lv
        fw = f" pełny koszyk {100 * full.mean():3.0f}% (wygr. {100 * (r[full] > 0).mean():3.0f}%)" if n_lv > 1 and full.any() else ""
        out.append(f"{name}: n={len(r):4d} śr={r.mean():+.3f}R t={t:+.1f} wygr.={100 * (r > 0).mean():3.0f}% DD={dd:5.1f}R{fw}")
    return " | ".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data")
    ap.add_argument("--src-tz", default="UTC")
    ap.add_argument("--synthetic", action="store_true")
    ap.add_argument("--split", default="2016-01-01")
    ap.add_argument("--tfs", default="5min,15min,60min,240min,1D")
    ap.add_argument("--tp", default="2,3")
    ap.add_argument("--cost", type=float, default=1.5, help="punkty na wypełnione zlecenie (spread + prowizja)")
    a = ap.parse_args()
    if a.synthetic:
        df = synthetic(days=1500)
        split = df.index[len(df) // 2]
    else:
        files = sorted(glob.glob(a.data))
        df = pd.concat([load_csv(f, a.src_tz) for f in files]).sort_index()
        df = df[~df.index.duplicated()]
        split = pd.Timestamp(a.split, tz="UTC")
    print(f"Dane: {df.index[0]:%Y-%m-%d} → {df.index[-1]:%Y-%m-%d}, trening do {split:%Y-%m-%d}, koszt {a.cost} pkt na zlecenie")
    for tf in a.tfs.split(","):
        d = resample(df, tf)
        print(f"\n=== {tf} ({len(d)} świec) ===")
        for tp in (float(x) for x in a.tp.split(",")):
            for name, (pos, wts) in VARIANTS.items():
                res = simulate(d, pos, wts, tp_r=tp, cost=a.cost)
                print(f"  TP {tp:.0f}R  {name:22s} {summary(res, d.index, split, len(pos))}")


if __name__ == "__main__":
    main()
