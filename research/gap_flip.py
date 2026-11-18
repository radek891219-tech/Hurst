"""
Gap Flip – odwracanie pozycji wokół poziomu luki (stop-and-reverse) na DAX / GER40.

Reguły (jeden dzień = jedna sesja, czas Berlina):
  poziom luki L = zamknięcie poprzedniej sesji (domyślnie 17:30), otwarcie sesji = pierwsza świeca od 09:00.
  luka w górę:  cena wraca i zamyka się pod L - bufor  → SHORT,
                potem zamyka się nad L + bufor          → odwrócenie na LONG, pod L - bufor → znów SHORT ... itd.
  luka w dół:   lustrzanie (pierwsze wejście LONG po zamknięciu nad L + bufor).
  Stop = odwrócenie (zamknięcie po drugiej stronie poziomu). Wyjście: TP (k × ATR od wejścia) albo koniec sesji.
  Limit wejść na dzień – po ostatnim wejściu sygnał przeciwny tylko zamyka pozycję.
  Sygnał na zamknięciu świecy, wykonanie na otwarciu następnej (bez zaglądania w przyszłość).
  ATR = średni zakres sesji z ostatnich 20 dni (bez dnia bieżącego); wszystkie odległości w ATR.

Użycie:
  python research/gap_flip.py --data "GRXEUR/DAT_ASCII_GRXEUR_M1_*.csv" --src-tz Etc/GMT+5
  python research/gap_flip.py --data dax_1m.csv --src-tz UTC --cost 1.5
  python research/gap_flip.py --synthetic        # kontrola: losowy rynek → wynik ok. 0 minus koszty
"""
import argparse
import glob
import itertools
import os
import sys

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hts_backtest import load_csv, synthetic  # noqa: E402


def sessions(df, s_open="09:00", s_close="17:30", atr_n=20):
    """Lista dni: (data, L, ATR, open, high, low, close) – tylko świece sesji."""
    ber = df.index.tz_convert("Europe/Berlin")
    hm = ber.strftime("%H:%M")
    mask = (hm >= s_open) & (hm < s_close)
    d = df[mask]
    day = ber[mask].date
    days = []
    for k, idx in pd.Series(np.arange(len(d)), index=day).groupby(level=0):
        a = idx.values
        if len(a) < 60:
            continue
        days.append((pd.Timestamp(k), d["open"].values[a], d["high"].values[a], d["low"].values[a], d["close"].values[a]))
    out = []
    rng = []
    for i, (k, o, h, lo, c) in enumerate(days):
        if i > 0 and len(rng) >= atr_n:
            out.append((k, days[i - 1][4][-1], float(np.mean(rng[-atr_n:])), o, h, lo, c))
        rng.append(h.max() - lo.min())
    return out


def run_day(L, A, o, h, lo, c, p):
    g = o[0] - L
    if abs(g) < p["min_gap"] * A or abs(g) > p["max_gap"] * A:
        return None
    gap_dir = 1 if g > 0 else -1
    buf = p["buf"] * A
    tp = p["tp"] * A
    pos, entry, n_ent, trades = 0, 0.0, 0, []
    pending = None  # pozycja docelowa do otwarcia na następnej świecy
    n = len(o)
    for i in range(n):
        if pending is not None:
            px = o[i]
            if pos != 0:
                trades.append(pos * (px - entry))
                pos = 0
            if pending != 0:
                pos, entry, n_ent = pending, px, n_ent + 1
            pending = None
        if pos != 0 and tp > 0:
            tgt = entry + pos * tp
            if (pos > 0 and h[i] >= tgt) or (pos < 0 and lo[i] <= tgt):
                fill = max(o[i], tgt) if pos > 0 else min(o[i], tgt)
                trades.append(pos * (fill - entry))
                pos = 0
                break  # kierunek się rozwinął – koniec dnia
        if i == n - 1:
            break
        below = c[i] < L - buf
        above = c[i] > L + buf
        if pos == 0 and n_ent == 0:
            if gap_dir > 0 and below:
                pending = -1
            elif gap_dir < 0 and above:
                pending = 1
            elif p["with_gap"] and ((gap_dir > 0 and above) or (gap_dir < 0 and below)) and i == 0:
                pending = gap_dir
        elif pos == -1 and above:
            pending = 1 if n_ent < p["max_ent"] else 0
        elif pos == 1 and below:
            pending = -1 if n_ent < p["max_ent"] else 0
        if pending == 0 and pos == 0:
            pending = None
    if pos != 0:
        trades.append(pos * (c[-1] - entry))
    return trades


def evaluate(days, p, cost):
    rows = []
    for k, L, A, o, h, lo, c in days:
        t = run_day(L, A, o, h, lo, c, p)
        if t:
            pts = np.array(t) - cost
            rows.append((k, len(t), pts.sum(), pts.sum() / A))
    return pd.DataFrame(rows, columns=["day", "n", "pts", "atr"]).set_index("day")


def tstat(x):
    x = np.asarray(x, float)
    return x.mean() / (x.std(ddof=1) / np.sqrt(len(x))) if len(x) > 2 and x.std() > 0 else float("nan")


def fmt(r):
    if len(r) == 0:
        return "brak dni"
    return (f"dni={len(r):4d} wejść/dzień={r.n.mean():.2f} śr.={r.atr.mean():+.3f} ATR ({r.pts.mean():+6.1f} pkt) "
            f"t={tstat(r.atr):+5.1f} dni+={100 * (r.atr > 0).mean():3.0f}% suma={r.pts.sum():+8.0f} pkt "
            f"maxDD={(r.pts.cumsum().cummax() - r.pts.cumsum()).max():6.0f} pkt")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data")
    ap.add_argument("--src-tz", default="UTC")
    ap.add_argument("--synthetic", action="store_true")
    ap.add_argument("--open", default="09:00", help="start sesji, czas Berlina")
    ap.add_argument("--close", default="17:30", help="koniec sesji, czas Berlina")
    ap.add_argument("--cost", type=float, default=1.5, help="koszt na wejście (spread+prowizja) w punktach")
    ap.add_argument("--min-gap", type=float, default=0.1, help="minimalna luka w ATR")
    ap.add_argument("--max-gap", type=float, default=1.5, help="maksymalna luka w ATR")
    ap.add_argument("--with-gap", action="store_true", help="pierwsze wejście od razu z kierunkiem luki na otwarciu")
    a = ap.parse_args()
    if a.synthetic:
        df = synthetic(days=1500)
    elif a.data:
        files = sorted(glob.glob(a.data)) or [a.data]
        df = pd.concat([load_csv(f, a.src_tz) for f in files]).sort_index()
        df = df[~df.index.duplicated()]
    else:
        ap.error("podaj --data plik.csv albo --synthetic")
    days = sessions(df, a.open, a.close)
    if len(days) < 60:
        sys.exit(f"Za mało dni: {len(days)}")
    h = len(days) // 2
    print(f"Dni z sesją: {len(days)}  ({days[0][0].date()} – {days[-1][0].date()}), trening = 1. połowa, test = 2. połowa, koszt {a.cost} pkt/wejście\n")

    grid = list(itertools.product([0.0, 0.05, 0.1, 0.2], [0.3, 0.6, 1.0, 0.0], [1, 3, 5]))
    res = []
    for buf, tp, me in grid:
        p = dict(buf=buf, tp=tp, max_ent=me, min_gap=a.min_gap, max_gap=a.max_gap, with_gap=a.with_gap)
        tr, te = evaluate(days[:h], p, a.cost), evaluate(days[h:], p, a.cost)
        res.append((p, tr, te))
    print("bufor  TP(ATR)  wejść | TRENING śr. ATR  t    | TEST śr. ATR  t")
    for p, tr, te in res:
        tps = f"{p['tp']:.1f}" if p["tp"] > 0 else "EOD"
        print(f"{p['buf']:5.2f}  {tps:>6}  {p['max_ent']:5d} | {tr.atr.mean():+8.3f} {tstat(tr.atr):+5.1f} | {te.atr.mean():+8.3f} {tstat(te.atr):+5.1f}")

    best = max(res, key=lambda x: tstat(x[1].atr) if len(x[1]) > 30 else -9)
    p, tr, te = best
    print(f"\nNajlepszy na treningu: bufor {p['buf']} ATR, TP {p['tp'] or 'koniec sesji'} ATR, max wejść {p['max_ent']}")
    print("  TRENING", fmt(tr))
    print("  TEST   ", fmt(te))
    allr = pd.concat([tr, te])
    for n, g in allr.groupby(allr.n.clip(upper=5)):
        print(f"  dni z {n}{'+' if n == 5 else ''} wejściami: n={len(g):4d} śr.={g.atr.mean():+.3f} ATR")
    for y, g in allr.groupby(allr.index.year):
        print(f"  {y}: {fmt(g)}")
    tr_t = np.array([tstat(r[1].atr) for r in res])
    te_t = np.array([tstat(r[2].atr) for r in res])
    print(f"\nZestawów na plus w teście: {100 * np.mean(te_t > 0):.0f}%, istotnie (t>=2): {100 * np.mean(te_t >= 2):.0f}%, "
          f"korelacja t trening↔test: {np.corrcoef(tr_t, te_t)[0, 1]:+.2f}")
    print("Liczy się TEST. Przy 48 zestawach 1–2 z t≥2 to przypadek. Na --synthetic wszystko powinno być ok. 0 minus koszt.")


if __name__ == "__main__":
    main()
