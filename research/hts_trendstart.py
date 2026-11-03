"""
Test wskaźnika HTS_TrendStart.pine (wczesne wykrywanie trendu) na świecach 1m.

Ta sama logika co w Pine: 6 warunków startu (7. – trend z wyższego TF – pominięty), wzorzec trwa do N zamknięć
za szybką wstęgą, dokładki z cofnięcia do szybkiej wstęgi tylko do wygrywającej pozycji, i 6 wariantów
zarządzania pozycją na tych samych sygnałach. Wejście i wyjście po cenie zamknięcia, SL przed TP na jednej świecy.

Użycie:
  python research/hts_trendstart.py --data plik.csv --src-tz UTC
  python research/hts_trendstart.py --synthetic            # kontrola: losowy rynek → wyniki wokół zera minus koszt
Na danych: patrz wynik na drugiej połowie (test) – pierwsza służy do ustawiania parametrów.
"""
import argparse
import os
import sys

import numpy as np
import pandas as pd

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from hts_backtest import load_csv, synthetic, rma  # noqa: E402

P = dict(fast=33, slow=144, atr=100,
         s1=30, s1min=0.15, s2=40, s3=10, s3min=0.4, s5=60, s5stay=20, s6=5,
         end_bars=3, pb=True, pb_cd=10, pb_max=4, pb_need=1.0,
         sl_len=20, pb_sl_len=5, sl_buf=0.2, min_risk=0.5, cost=0.0,
         be=1.0, tp=2.0, tp_pct=50.0, tr_start=1.0, tr_buf=0.3, ts_bars=15, ts_r=0.5)
NAMES = ["1. Bazowy", "2. BE", "3. Częściowe TP + BE", "4. Trailing po szybkiej", "5. Stop czasowy + BE", "6. Wszystko razem"]
BE, TP, TR, TS = {1, 4, 5}, {2, 5}, {3, 5}, {4, 5}


def barssince(cond):
    idx = np.arange(len(cond))
    last = np.where(cond, idx, -1)
    last = np.maximum.accumulate(last)
    return np.where(last >= 0, idx - last, 100000)


def run(df, p):
    o, h, l, c = (df[k].values for k in ("open", "high", "low", "close"))
    H, L, C = df["high"], df["low"], df["close"]
    fH, fL, sH, sL = (rma(x, n).values for x, n in ((H, p["fast"]), (L, p["fast"]), (H, p["slow"]), (L, p["slow"])))
    tr = pd.concat([H - L, (H - C.shift()).abs(), (L - C.shift()).abs()], axis=1).max(axis=1)
    atr = rma(tr, p["atr"]).values
    fM, sM = (fH + fL) / 2, (sH + sL) / 2
    sh = lambda a, k: np.r_[np.full(k, np.nan), a[:-k]]
    dn, up = fH < sL, fL > sH
    gD, gU = sL - fH, fL - sH
    cS = [sM - sh(sM, p["s1"]) <= -p["s1min"] * atr,
          dn & (barssince(dn & ~np.r_[False, dn[:-1]]) <= p["s2"]),
          fM - sh(fM, p["s3"]) <= -p["s3min"] * atr,
          c < fL,
          (barssince(h >= sL) <= p["s5"]) & (barssince(c > sH) >= p["s5stay"]),
          gD > sh(gD, p["s6"])]
    cL = [sM - sh(sM, p["s1"]) >= p["s1min"] * atr,
          up & (barssince(up & ~np.r_[False, up[:-1]]) <= p["s2"]),
          fM - sh(fM, p["s3"]) >= p["s3min"] * atr,
          c > fH,
          (barssince(l <= sH) <= p["s5"]) & (barssince(c < sL) >= p["s5stay"]),
          gU > sh(gU, p["s6"])]
    allS = np.logical_and.reduce(cS)
    allL = np.logical_and.reduce(cL)
    hiS = H.rolling(p["sl_len"]).max().values
    loS = L.rolling(p["sl_len"]).min().values
    hiP = H.rolling(p["pb_sl_len"]).max().values
    loP = L.rolling(p["pb_sl_len"]).min().values

    res = [[] for _ in NAMES]          # (bar, kind, R) dla każdego wariantu
    pats = []
    d = 0
    trades = []                        # dict: e, rk, kind, bar, mfe, st: [[sl, rem, real, open] * 6]

    def open_trade(i, kind, s):
        trades.append(dict(e=c[i], rk=abs(c[i] - s), kind=kind, bar=i, mfe=0.0, st=[[s, 1.0, 0.0, True] for _ in NAMES]))

    def sl_for(i, dd, hi, lo):
        raw = hi[i] + p["sl_buf"] * atr[i] if dd == -1 else lo[i] - p["sl_buf"] * atr[i]
        return c[i] - dd * max(abs(raw - c[i]), p["min_risk"] * atr[i])

    warm = p["slow"] * 3
    for i in range(warm, len(c)):
        if d != 0:
            ext = min(ext, l[i]) if d == -1 else max(ext, h[i])
            against = c[i] > fH[i] if d == -1 else c[i] < fL[i]
            hard = c[i] > sH[i] if d == -1 else c[i] < sL[i]
            endc = endc + 1 if against else 0
            pat_end = endc >= p["end_bars"] or hard
            for t in trades:
                e, rk = t["e"], t["rk"]
                t["mfe"] = max(t["mfe"], d * ((l[i] if d == -1 else h[i]) - e) / rk)
                mfe, age = t["mfe"], i - t["bar"]
                for v, s in enumerate(t["st"]):
                    if not s[3]:
                        continue
                    sl, rem, rl = s[0], s[1], s[2]
                    cl = False
                    if (h[i] >= sl) if d == -1 else (l[i] <= sl):
                        rl += rem * d * (sl - e) / rk
                        cl = True
                    else:
                        tight = (lambda a, b: min(a, b)) if d == -1 else (lambda a, b: max(a, b))
                        if v in TP and rem > 0.999 and mfe >= p["tp"]:
                            rl += p["tp_pct"] / 100 * p["tp"]
                            rem -= p["tp_pct"] / 100
                            sl = tight(sl, e)
                        if v in BE and mfe >= p["be"]:
                            sl = tight(sl, e)
                        if v in TR and mfe >= p["tr_start"]:
                            sl = tight(sl, fH[i] + p["tr_buf"] * atr[i] if d == -1 else fL[i] - p["tr_buf"] * atr[i])
                        if v in TS and age >= p["ts_bars"] and mfe < p["ts_r"]:
                            cl = True
                        if pat_end or ((c[i] >= sl) if d == -1 else (c[i] <= sl)):
                            cl = True
                        if cl:
                            rl += rem * d * (c[i] - e) / rk
                    if cl:
                        s[3] = False
                        res[v].append((i, t["kind"], rl - p["cost"] / rk))
                    else:
                        s[0], s[1], s[2] = sl, rem, rl
            trades = [t for t in trades if any(s[3] for s in t["st"])]
            if pat_end:
                pats.append(d * (ext - entry) / atr0)
                d = 0
                trades = []
            elif p["pb"] and npb < p["pb_max"] and i - last >= p["pb_cd"] and (p["pb_need"] == 0 or d * (ext - entry) / risk0 >= p["pb_need"]):
                if d == -1:
                    pb = h[i] >= fL[i] and c[i] < fM[i] and c[i] < o[i] and fM[i] < fM[i - p["s3"]]
                else:
                    pb = l[i] <= fH[i] and c[i] > fM[i] and c[i] > o[i] and fM[i] > fM[i - p["s3"]]
                if pb:
                    open_trade(i, 1, sl_for(i, d, hiP, loP))
                    npb += 1
                    last = i
        elif allS[i] or allL[i]:
            d = -1 if allS[i] else 1
            s0 = sl_for(i, d, hiS, loS)
            entry, risk0, atr0, ext, endc, npb, last = c[i], abs(c[i] - s0), atr[i], (l[i] if d == -1 else h[i]), 0, 0, i
            open_trade(i, 0, s0)
    return res, np.array(pats)


def summary(rows, n_bars, split):
    out = []
    for part, lo, hi in (("trening", 0, split), ("test", split, n_bars)):
        r = np.array([x[2] for x in rows if lo <= x[0] < hi])
        if len(r) == 0:
            out.append(f"{part}: brak")
            continue
        cum = np.cumsum(r)
        dd = np.max(np.maximum.accumulate(np.r_[0, cum])[1:] - cum)
        t = r.mean() / (r.std(ddof=1) / np.sqrt(len(r))) if len(r) > 1 and r.std() > 0 else 0
        out.append(f"{part}: n={len(r):4d} zysk.={100 * (r > 0).mean():3.0f}% suma={r.sum():7.1f}R śr={r.mean():+.3f}R t={t:+.1f} maxDD={dd:5.1f}R")
    return " | ".join(out)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data")
    ap.add_argument("--src-tz", default="UTC")
    ap.add_argument("--synthetic", action="store_true")
    ap.add_argument("--cost", type=float, default=None, help="koszt na wejście w punktach ceny (spread + prowizja)")
    ap.add_argument("--no-pb", action="store_true", help="bez dokładek")
    a = ap.parse_args()
    p = dict(P)
    if a.cost is not None:
        p["cost"] = a.cost
    if a.no_pb:
        p["pb"] = False
    if a.synthetic:
        df = synthetic(days=250)
    elif a.data:
        df = load_csv(a.data, a.src_tz)
    else:
        ap.error("podaj --data plik.csv albo --synthetic")
    res, pats = run(df, p)
    split = len(df) // 2
    print(f"Świec: {len(df)}  wzorców: {len(pats)}  śr. zasięg: {pats.mean() if len(pats) else 0:.1f} ATR  "
          f"duże (≥6 ATR): {100 * (pats >= 6).mean() if len(pats) else 0:.0f}%  koszt: {p['cost']} pkt")
    for v, name in enumerate(NAMES):
        print(f"{name:26s} {summary(res[v], len(df), split)}")


if __name__ == "__main__":
    main()
