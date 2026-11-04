"""
Które okresy średnich HTS działają najlepiej na DAX? Siatka okresów pasm H1 (trend) i m1 (wejście) oraz typu średniej.

Uczciwa procedura:
  • wybór tylko na danych treningowych (domyślnie do końca 2015), ranking po t-stat (istotność), min. liczba transakcji,
  • najlepsze zestawy sprawdzone na danych testowych (od 2016), których wybór nie widział,
  • wynik rok po roku dla zwycięzców i dla ustawień domyślnych 33/144,
  • ile zestawów w ogóle wychodzi na plus na teście (czy to cecha rynku, czy szczęśliwy traf).
Reszta logiki jak cBot HTS (SL 0.5 %, TP1 1.6R z częściowym zamknięciem, trailing, okno 7–15 UTC, koszt 1.5 pkt).

Użycie:
  python research/hts_ma_grid.py --data "katalog/DAT_ASCII_GRXEUR_M1_*.csv" --src-tz Etc/GMT+5
"""
import argparse
import glob
import itertools
import math
import os
import sys
from multiprocessing import Pool

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hts_backtest import DEFAULTS, load_csv, prepare, simulate  # noqa: E402

FAST = [13, 21, 33, 50, 89]
SLOW = [89, 144, 200, 288]
TYPES = ["Wilder", "EMA"]
PAIRS = [(f, s) for f in FAST for s in SLOW if s >= 2 * f]

_DF = {}


def r_stats(camps):
    r = np.array([x[4] for x in camps])
    if len(r) < 3:
        return dict(n=len(r), avg=float("nan"), t=float("nan"), sum=float(r.sum()) if len(r) else 0.0, win=float("nan"))
    t = r.mean() / (r.std(ddof=1) / math.sqrt(len(r))) if r.std() > 0 else float("nan")
    return dict(n=len(r), avg=float(r.mean()), t=float(t), sum=float(r.sum()), win=float((r > 0).mean() * 100))


def job(args):
    typ, (tf, ts), (ef, es) = args
    p = dict(DEFAULTS, ma=typ, t_fast=tf, t_slow=ts, e_fast=ef, e_slow=es)
    out = dict(ma=typ, t_fast=tf, t_slow=ts, e_fast=ef, e_slow=es)
    for name in ("train", "test"):
        camps, _ = simulate(prepare(_DF[name], p), p)
        s = r_stats(camps)
        out.update({f"{name}_{k}": v for k, v in s.items()})
        if name == "test":
            out["test_years"] = {y: round(sum(c[4] for c in camps if c[1].year == y), 1) for y in sorted({c[1].year for c in camps})}
    return out


def init(train, test):
    _DF["train"], _DF["test"] = train, test


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data", required=True, help="plik albo wzorzec, np. 'dane/*.csv'")
    ap.add_argument("--src-tz", default="UTC")
    ap.add_argument("--split", default="2016-01-01")
    ap.add_argument("--min-trades", type=int, default=100)
    ap.add_argument("--out", default="hts_ma_grid.csv")
    a = ap.parse_args()

    files = sorted(glob.glob(a.data))
    df = pd.concat([load_csv(f, a.src_tz) for f in files]).sort_index()
    df = df[~df.index.duplicated()]
    cut = pd.Timestamp(a.split, tz="UTC")
    train, test = df[df.index < cut], df[df.index >= cut]
    print(f"Dane: {df.index[0]:%Y-%m-%d} → {df.index[-1]:%Y-%m-%d}; trening do {cut:%Y-%m-%d}, test od {cut:%Y-%m-%d}")

    combos = list(itertools.product(TYPES, PAIRS, PAIRS))
    print(f"Zestawów: {len(combos)}", file=sys.stderr)
    rows = []
    with Pool(os.cpu_count(), initializer=init, initargs=(train, test)) as pool:
        for j, r in enumerate(pool.imap_unordered(job, combos, chunksize=4)):
            rows.append(r)
            if j % 50 == 0:
                print(f"  {j}/{len(combos)}", file=sys.stderr)
    R = pd.DataFrame(rows)
    R.to_csv(a.out, index=False)

    ok = R[R.train_n >= a.min_trades].sort_values("train_t", ascending=False)
    cols = ["ma", "t_fast", "t_slow", "e_fast", "e_slow", "train_n", "train_avg", "train_t", "test_n", "test_avg", "test_t", "test_sum"]
    pd.set_option("display.width", 200)
    print(f"\nZestawów z min. {a.min_trades} transakcjami na treningu: {len(ok)}")
    print("\nTOP 15 wg treningu (t-stat) → wynik na teście:")
    print(ok[cols].head(15).round(3).to_string(index=False))
    d = R[(R.ma == "Wilder") & (R.t_fast == 33) & (R.t_slow == 144) & (R.e_fast == 33) & (R.e_slow == 144)]
    print("\nUstawienia domyślne Wilder 33/144 + 33/144:")
    print(d[cols].round(3).to_string(index=False))
    print(f"\nNa teście na plus: {(R.test_avg > 0).mean() * 100:.0f}% zestawów; istotnie na plus (t ≥ 2): {(R.test_t >= 2).mean() * 100:.0f}%")
    print(f"Na treningu na plus: {(R.train_avg > 0).mean() * 100:.0f}% zestawów; istotnie (t ≥ 2): {(R.train_t >= 2).mean() * 100:.0f}%")
    top5 = ok.head(5)
    print(f"\nTOP 5 z treningu – średnio na teście: {top5.test_avg.mean():+.3f} R/transakcję")
    print(f"Korelacja wyniku trening ↔ test między zestawami: {R.train_avg.corr(R.test_avg):+.2f}")
    for _, r in ok.head(5).iterrows():
        print(f"  {r.ma} H1 {r.t_fast}/{r.t_slow} m1 {r.e_fast}/{r.e_slow}: test rok po roku (suma R) {r.test_years}")
    for _, r in d.iterrows():
        print(f"  domyślne: test rok po roku (suma R) {r.test_years}")


if __name__ == "__main__":
    main()
