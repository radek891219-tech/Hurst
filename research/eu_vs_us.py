"""
Europa vs USA: czy sesja amerykańska odwraca (albo kontynuuje) ruch Europy na DAX / GER40?

Dla każdego dnia:
  ruch Europy = od startu Europy (domyślnie 09:00 Berlin) do otwarcia USA (09:30 Nowy Jork),
  ruch USA    = od otwarcia USA do końca sesji USA (domyślnie 16:00 Nowy Jork).
Wynik: korelacja, ile USA robi na 1 % Europy, co robi USA po dużym ruchu Europy w górę / w dół,
osobno dla każdego roku (czy zależność jest stała) i na dwóch połowach danych.

Użycie:
  python research/eu_vs_us.py --data dax_1m.csv --src-tz UTC
  python research/eu_vs_us.py --data dax_1m.csv --src-tz UTC --us-end 11:30     # tylko pierwsze 2 h USA
  python research/eu_vs_us.py --synthetic                                      # kontrola: losowy rynek → brak zależności
"""
import argparse
import os
import sys

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hts_backtest import load_csv, synthetic  # noqa: E402


def daily_moves(df, eu_start="09:00", us_open="09:30", us_end="16:00"):
    c = df["close"]
    o = df["open"]
    ber = df.index.tz_convert("Europe/Berlin")
    ny = df.index.tz_convert("America/New_York")
    day = pd.Series(ny.date, index=df.index)
    hb = pd.Series(ber.strftime("%H:%M"), index=df.index)
    hn = pd.Series(ny.strftime("%H:%M"), index=df.index)
    rows = []
    for d, idx in day.groupby(day).groups.items():
        e = idx[hb[idx] >= eu_start]
        u = idx[(hn[idx] >= us_open) & (hn[idx] < us_end)]
        if len(e) == 0 or len(u) < 5:
            continue
        eu_o = o[e[0]]
        us_o = o[u[0]]
        us_c = c[u[-1]]
        if e[0] >= u[0]:
            continue
        rows.append((pd.Timestamp(d), 100 * (us_o / eu_o - 1), 100 * (us_c / us_o - 1)))
    return pd.DataFrame(rows, columns=["day", "eu", "us"]).set_index("day")


def tstat(x):
    x = np.asarray(x)
    return x.mean() / (x.std(ddof=1) / np.sqrt(len(x))) if len(x) > 2 and x.std() > 0 else float("nan")


def report(m, k=1.0, title=""):
    thr = k * m["eu"].abs().mean()
    r = m["eu"].corr(m["us"])
    n = len(m)
    tr = r * np.sqrt((n - 2) / (1 - r * r)) if n > 2 else float("nan")
    beta = np.cov(m["eu"], m["us"])[0, 1] / m["eu"].var()
    up, dn, sm = m[m.eu >= thr], m[m.eu <= -thr], m[m.eu.abs() < thr]
    print(f"{title}dni={n}  korelacja r={r:+.3f} (t={tr:+.1f})  na 1% Europy USA: {beta:+.3f}%  próg dużego ruchu: {thr:.2f}%")
    for name, g, rev in (("Europa mocno w górę", up, (up.us < 0)), ("Europa mocno w dół", dn, (dn.us > 0)), ("Europa mały ruch", sm, (sm.us < 0))):
        if len(g) > 2:
            print(f"   {name:20s} n={len(g):4d}  śr. USA={g.us.mean():+.3f}%  w drugą stronę={100 * rev.mean():3.0f}%  t={tstat(g.us):+.1f}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data")
    ap.add_argument("--src-tz", default="UTC")
    ap.add_argument("--synthetic", action="store_true")
    ap.add_argument("--eu-start", default="09:00", help="start Europy, czas Berlina")
    ap.add_argument("--us-end", default="16:00", help="koniec sesji USA, czas Nowego Jorku")
    ap.add_argument("--k", type=float, default=1.0, help="duży ruch = |ruch| >= k * średni |ruch|")
    a = ap.parse_args()
    if a.synthetic:
        df = synthetic(days=1000)
    elif a.data:
        df = load_csv(a.data, a.src_tz)
    else:
        ap.error("podaj --data plik.csv albo --synthetic")
    m = daily_moves(df, a.eu_start, "09:30", a.us_end)
    if len(m) < 30:
        sys.exit(f"Za mało dni z obiema sesjami: {len(m)}")
    report(m, a.k, "CAŁOŚĆ  ")
    h = len(m) // 2
    report(m.iloc[:h], a.k, "1. POŁOWA ")
    report(m.iloc[h:], a.k, "2. POŁOWA ")
    for y, g in m.groupby(m.index.year):
        if len(g) >= 30:
            report(g, a.k, f"{y}  ")
    print("\n|t| < 2 = nieodróżnialne od przypadku. Zależność jest coś warta tylko, gdy ma ten sam znak w obu połowach i w większości lat.")


if __name__ == "__main__":
    main()
