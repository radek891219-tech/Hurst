"""
Backtester strategii HTS / HTS Scale-In na świecach 1m (np. GER40 / DAX).

Logika jak w cBocie HTS_Strategy_Tester (cTrader) i w HTS_ScaleIn_Strategy.pine:
  • trend: pasma 33/144 z High i Low (Wilder) na H1, liczone na zamkniętej świecy H1,
  • (opcjonalnie) pullback: zamknięta świeca H1 dotyka szybkiego pasma,
  • sygnał na 1m: przecięcie środków pasm (Cross) i/lub dotknięcie pasma 33 (Touch),
  • filtr: cena po właściwej stronie bieżącego (rozwijającego się) szybkiego pasma H1,
  • kampania: E1 po rynku na otwarciu następnej świecy + limity co step% drogi do wspólnego SL,
    wielkości tak, by przy pełnym wypełnieniu strata = ryzyko kampanii,
  • TP1 od średniej (R-krotność wypełnionego ryzyka), częściowe zamknięcie, SL reszty na BE, trailing w $,
  • okno handlu w UTC, zamykanie w piątek, ochrona FTMO, koszt spreadu na każde wejście.
Na jednej świecy kolejność jest ostrożna: wypełnienia limitów → SL → TP1.

Użycie:
  python research/hts_backtest.py --data plik.csv --src-tz UTC                 # jeden test
  python research/hts_backtest.py --data plik.csv --src-tz UTC --optimize      # optymalizacja + test poza próbą
  python research/hts_backtest.py --synthetic                                  # kontrola: losowy rynek
Formaty CSV: HistData (20230102 090000;o;h;l;c;v), Dukascopy (Gmt time,Open,...), eksport TradingView
(time,open,high,low,close), MT4/MT5 (Date,Time,Open,...).
"""
import argparse
import itertools
import math
import sys

import numpy as np
import pandas as pd

DEFAULTS = dict(
    t_fast=33, t_slow=144, e_fast=33, e_slow=144,
    use_cross=True, use_touch=False, need_pb=True, trig="MACross",
    sl_pct=0.5, min_sl=40.0, max_sl=250.0, pip=1.0,
    risk=300.0, n_entries=1, step=15.0, min_lvl_sl=15.0, expiry=60,
    tp1_r=1.6, part=95.0, trail=True, tr_start=15.0, tr_dist=15.0,
    start_h=7, end_h=15, block_h=1, eod_every=False, eod_fri=True,
    ftmo=True, max_day_loss=3000.0, max_camps=6, max_losses=2,
    cost_pts=1.5,
)


# ───────────────────────── Dane ─────────────────────────
def load_csv(path, src_tz="UTC"):
    with open(path, "r", encoding="utf-8-sig", errors="ignore") as f:
        first = f.readline().strip()
    if ";" in first and first[:8].isdigit():
        df = pd.read_csv(path, sep=";", header=None, names=["dt", "open", "high", "low", "close", "vol"])
        dt = pd.to_datetime(df["dt"], format="%Y%m%d %H%M%S")
    else:
        df = pd.read_csv(path, sep=None, engine="python")
        cols = {c.lower().strip(): c for c in df.columns}
        if "date" in cols and "time" in cols:
            dt = pd.to_datetime(df[cols["date"]].astype(str) + " " + df[cols["time"]].astype(str))
        else:
            tkey = next((k for k in ("gmt time", "local time", "datetime", "date time", "timestamp", "time", "date") if k in cols), None)
            tcol = cols[tkey] if tkey else df.columns[0]
            s = df[tcol]
            if np.issubdtype(s.dtype, np.number):
                dt = pd.to_datetime(s, unit="s" if s.iloc[0] < 1e11 else "ms", utc=True)
            else:
                s = s.astype(str).str.replace(r"\s*(GMT|UTC)[+-]?\d*$", "", regex=True)
                dt = pd.to_datetime(s, dayfirst=bool(s.iloc[0][:3].count(".") or s.iloc[0][2:3] == "."), utc=s.iloc[0].endswith("Z"))
        df = df.rename(columns={cols.get(k, k): k for k in ("open", "high", "low", "close")})
    out = pd.DataFrame({k: df[k].astype(float).values for k in ("open", "high", "low", "close")}, index=pd.DatetimeIndex(dt))
    if out.index.tz is None:
        out.index = out.index.tz_localize(src_tz, ambiguous="NaT", nonexistent="shift_forward")
    out.index = out.index.tz_convert("UTC")
    out = out[~out.index.isna()].sort_index()
    out = out[~out.index.duplicated()]
    return out


def synthetic(days=250, seed=1):
    """Losowy rynek 1m (bez przewagi) z sesjami dziennymi – do sprawdzenia, czy backtester nie zagląda w przyszłość."""
    rng = np.random.default_rng(seed)
    idx = pd.date_range("2024-01-01", periods=days * 1440, freq="1min", tz="UTC")
    idx = idx[(idx.dayofweek < 5) & (idx.hour >= 1) & (idx.hour < 21)]
    vol = np.where((idx.hour >= 7) & (idx.hour < 16), 6.0, 3.0)
    close = 16000 + np.cumsum(rng.standard_normal(len(idx)) * vol)
    op = np.r_[close[0], close[:-1]]
    wick = np.abs(rng.standard_normal((2, len(idx)))) * vol * 0.6
    return pd.DataFrame({"open": op, "high": np.maximum(op, close) + wick[0], "low": np.minimum(op, close) - wick[1], "close": close}, index=idx)


# ───────────────────────── Wskaźniki ─────────────────────────
def rma(s, n):
    return s.ewm(alpha=1.0 / n, adjust=False).mean()


def prepare(df, p):
    """Wszystko, co nie zależy od parametrów kampanii – liczone raz."""
    o, h, l, c = df["open"], df["high"], df["low"], df["close"]
    eFH, eFL, eSH, eSL = rma(h, p["e_fast"]), rma(l, p["e_fast"]), rma(h, p["e_slow"]), rma(l, p["e_slow"])
    fMid, sMid = (eFH + eFL) / 2, (eSH + eSL) / 2

    h1 = df.resample("60min", label="left", closed="left").agg({"high": "max", "low": "min"}).dropna()
    hFH, hFL, hSH, hSL = rma(h1["high"], p["t_fast"]), rma(h1["low"], p["t_fast"]), rma(h1["high"], p["t_slow"]), rma(h1["low"], p["t_slow"])
    key = df.index.floor("60min")
    prev = lambda s: s.shift(1).reindex(key).values  # wartość z ostatniej ZAMKNIĘTEJ świecy H1
    tFHc, tFLc, tSHc, tSLc = prev(hFH), prev(hFL), prev(hSH), prev(hSL)
    tHc, tLc = prev(h1["high"]), prev(h1["low"])
    runHi = h.groupby(key).cummax().values
    runLo = l.groupby(key).cummin().values
    tFH0 = tFHc + (runHi - tFHc) / p["t_fast"]   # rozwijająca się wartość (jak Last(0) w bocie)
    tFL0 = tFLc + (runLo - tFLc) / p["t_fast"]
    trend = np.where(tFLc > tSHc, 1, np.where(tFHc < tSLc, -1, 0))
    trend[np.isnan(tSLc)] = 0

    utc = df.index
    local = utc.tz_convert("Europe/Prague")
    A = dict(
        o=o.values, h=h.values, l=l.values, c=c.values,
        eFH=eFH.values, eFL=eFL.values, eSH=eSH.values, eSL=eSL.values, fMid=fMid.values, sMid=sMid.values,
        tFH0=tFH0, tFL0=tFL0, tFHc=tFHc, tFLc=tFLc, tHc=tHc, tLc=tLc, trend=trend,
        htf=key.asi8, hour=utc.hour.values, hour_close=(utc + pd.Timedelta(minutes=1)).hour.values,
        dow=utc.dayofweek.values, day=(local.year * 10000 + local.month * 100 + local.day).values,
        cross_up=((fMid.shift(1) <= sMid.shift(1)) & (fMid > sMid)).values,
        cross_dn=((fMid.shift(1) >= sMid.shift(1)) & (fMid < sMid)).values,
        x4_up=((eFL > eSH) & (eFL.shift(1) <= eSH.shift(1))).values,
        x4_dn=((eFH < eSL) & (eFH.shift(1) >= eSL.shift(1))).values,
        touch_up=((fMid > sMid) & (l <= eFH) & (c > eFH) & (c > o)).values,
        touch_dn=((fMid < sMid) & (h >= eFL) & (c < eFL) & (c < o)).values,
        dates=utc,
    )
    A["warm"] = int(np.argmax(~np.isnan(tSLc))) + p["e_slow"]
    return A


# ───────────────────────── Symulacja ─────────────────────────
def in_win(hh, p):
    s, e = p["start_h"], p["end_h"]
    return True if s == e else (s <= hh < e) if s < e else (hh >= s or hh < e)


def block_ok(hh, p):
    s, e, b = p["start_h"], p["end_h"], p["block_h"]
    if b <= 0 or s == e:
        return True
    rem = e - hh if s < e else ((24 - hh) + e if hh >= s else e - hh)
    return rem > b


def simulate(A, p):
    o, h, l, c = A["o"], A["h"], A["l"], A["c"]
    n = len(o)
    cross_up = A["cross_up"] if p["trig"] == "MACross" else A["x4_up"]
    cross_dn = A["cross_dn"] if p["trig"] == "MACross" else A["x4_dn"]
    sig_up = (cross_up & p["use_cross"]) | (A["touch_up"] & p["use_touch"])
    sig_dn = (cross_dn & p["use_cross"]) | (A["touch_dn"] & p["use_touch"])
    pip, risk, cost = p["pip"], p["risk"], p["cost_pts"]

    trend = state = 0
    pull = prev_sig = False
    last_pb = None
    day = None
    day_real = 0.0
    camps_today = consec = 0
    blocked = False
    camps = []
    day_pnl_close = {}

    in_c = False
    pend_e1 = False
    lv = []; u = []; filled = []; live = []; ent = []
    cdir = 0; csl = csl0 = 0.0; cstart = 0; tp1_hit = trail_act = False; max_fav = 0.0
    rem = 1.0; c_real = 0.0; c_cost = 0.0; c_fills = 0; c_day = None; c_kind = ""

    def agg():
        V = s = fr = 0.0
        for k in range(len(lv)):
            if filled[k]:
                V += u[k]; s += u[k] * ent[k]; fr += u[k] * abs(ent[k] - csl0)
        return V, (s / V if V > 0 else float("nan")), fr

    for i in range(A["warm"], n):
        d = A["day"][i]
        if d != day:
            if day is not None:
                day_pnl_close[day] = day_real
            day = d; day_real = 0.0; camps_today = consec = 0; blocked = False

        tn = A["trend"][i]
        if tn != trend:
            trend = tn
            pull = not p["need_pb"]
            prev_sig = False
            if in_c:
                live = [False] * len(live)
            else:
                state = 0 if trend == 0 else (1 if p["need_pb"] else 2)

        closed = False
        exit_px = float("nan")
        if in_c:
            if pend_e1:
                ent[0] = o[i]; filled[0] = True; pend_e1 = False
                c_cost += cost * u[0]; c_fills = 1
            hh = A["hour"][i]
            out = not in_win(hh, p)
            after_end = hh >= p["end_h"] if p["start_h"] < p["end_h"] else True
            if out and (p["eod_every"] or (p["eod_fri"] and A["dow"][i] == 4 and after_end)):
                exit_px, closed = o[i], True
            if out or i - cstart >= p["expiry"]:
                live = [False] * len(live)
            if not closed and not tp1_hit:
                for k in range(1, len(lv)):
                    if live[k] and ((cdir == 1 and l[i] <= lv[k]) or (cdir == -1 and h[i] >= lv[k])):
                        ent[k] = min(o[i], lv[k]) if cdir == 1 else max(o[i], lv[k])
                        filled[k] = True; live[k] = False
                        c_cost += cost * u[k]; c_fills += 1
            if not closed and ((cdir == 1 and l[i] <= csl) or (cdir == -1 and h[i] >= csl)):
                exit_px = min(o[i], csl) if cdir == 1 else max(o[i], csl)
                closed = True
            tp_now = False
            if not closed and not tp1_hit:
                V, avg, fr = agg()
                tp1 = avg + cdir * p["tp1_r"] * fr / V
                if (cdir == 1 and h[i] >= tp1) or (cdir == -1 and l[i] <= tp1):
                    if p["part"] < 100:
                        part = V * (tp1 - avg) * cdir * p["part"] / 100
                        c_real += part; day_real += part
                        rem = 1 - p["part"] / 100
                        tp1_hit = tp_now = True
                        csl = avg
                        live = [False] * len(live)
                    else:
                        exit_px, closed = tp1, True
            if not closed:
                max_fav = max(max_fav, h[i]) if cdir == 1 else min(max_fav, l[i])
            if not closed and tp1_hit and p["trail"] and not tp_now:
                V, avg, _ = agg()
                Vr = V * rem
                prof = h[i] - avg if cdir == 1 else avg - l[i]
                if not trail_act and prof >= p["tr_start"] / Vr:
                    trail_act = True
                if trail_act:
                    nsl = max_fav - cdir * p["tr_dist"] / Vr
                    better = nsl > csl + pip if cdir == 1 else nsl < csl - pip
                    valid = nsl < c[i] if cdir == 1 else nsl > c[i]
                    if better and valid:
                        csl = nsl

        open_pnl = 0.0
        if in_c and not closed:
            V, avg, _ = agg()
            open_pnl = V * rem * (c[i] - avg) * cdir
        day_pnl = day_real - (c_cost if in_c else 0.0) + open_pnl
        if p["ftmo"] and not blocked and p["max_day_loss"] > 0 and day_pnl <= -p["max_day_loss"]:
            blocked = True
            if in_c and not closed:
                exit_px, closed = c[i], True

        if closed:
            V, avg, _ = agg()
            rest = V * rem * (exit_px - avg) * cdir
            c_real += rest
            total = c_real - c_cost
            day_real += rest - c_cost
            camps.append((c_day, A["dates"][cstart], total, c_fills, total / risk, c_kind, tp1_hit))
            consec = consec + 1 if total < 0 else 0
            if p["ftmo"] and p["max_losses"] > 0 and consec >= p["max_losses"]:
                blocked = True
            in_c = False
            state = 0 if trend == 0 else (2 if pull else 1)
            prev_sig = bool(sig_up[i - 1]) if trend == 1 else bool(sig_dn[i - 1]) if trend == -1 else False

        if state == 1 and trend != 0 and A["htf"][i] != last_pb:
            if (trend == 1 and A["tLc"][i] <= A["tFHc"][i]) or (trend == -1 and A["tHc"][i] >= A["tFLc"][i]):
                pull = True; state = 2
            last_pb = A["htf"][i]

        if state == 2 and trend != 0 and not in_c and not closed and i + 1 < n:
            sig = sig_up[i] if trend == 1 else sig_dn[i]
            if not sig:
                prev_sig = False
            elif not prev_sig and in_win(A["hour_close"][i], p) and block_ok(A["hour_close"][i], p):
                prev_sig = True
                dr = trend
                price_ok = (c[i] > A["tFH0"][i] and c[i] - A["eFH"][i] > 0) if dr == 1 else (c[i] < A["tFL0"][i] and A["eFL"][i] - c[i] > 0)
                ftmo_ok = not p["ftmo"] or (not blocked and (p["max_camps"] == 0 or camps_today < p["max_camps"])
                                            and (p["max_day_loss"] == 0 or day_pnl - risk > -p["max_day_loss"]))
                sl = c[i] - dr * c[i] * p["sl_pct"] / 100
                dist = abs(c[i] - sl)
                if price_ok and ftmo_ok and p["min_sl"] <= dist / pip <= p["max_sl"]:
                    lv = []
                    for k in range(p["n_entries"]):
                        lvk = c[i] - dr * dist * p["step"] * k / 100
                        if k > 0 and (k * p["step"] >= 100 or abs(lvk - sl) / pip < p["min_lvl_sl"]):
                            break
                        lv.append(lvk)
                    m = len(lv)
                    u = [risk / m / abs(x - sl) for x in lv]
                    filled = [False] * m; live = [k > 0 for k in range(m)]; ent = [float("nan")] * m
                    in_c = pend_e1 = True
                    cdir, csl, csl0, cstart = dr, sl, sl, i + 1
                    tp1_hit = trail_act = False
                    max_fav = c[i]; rem = 1.0; c_real = c_cost = 0.0; c_fills = 0; c_day = d
                    c_kind = "X" if (cross_up[i] if dr == 1 else cross_dn[i]) and p["use_cross"] else "T"
                    camps_today += 1
                    state = 3
        day_pnl_close[day] = day_real + (open_pnl if in_c else 0.0)

    return camps, day_pnl_close


# ───────────────────────── Statystyki ─────────────────────────
def stats(camps, days, risk):
    if not camps:
        return dict(n=0, net=0.0, pf=float("nan"), win=float("nan"), exp_r=float("nan"), max_dd=0.0, worst_day=0.0, t_stat=float("nan"))
    pnl = np.array([x[2] for x in camps])
    eq = np.cumsum(pnl)
    dd = np.max(np.maximum.accumulate(np.r_[0, eq]) - np.r_[0, eq])
    gw, gl = pnl[pnl > 0].sum(), -pnl[pnl < 0].sum()
    r = pnl / risk
    t = r.mean() / (r.std(ddof=1) / math.sqrt(len(r))) if len(r) > 2 and r.std() > 0 else float("nan")
    return dict(n=len(pnl), net=float(pnl.sum()), pf=float(gw / gl) if gl > 0 else float("inf"),
                win=float((pnl > 0).mean() * 100), exp_r=float(r.mean()), max_dd=float(dd),
                worst_day=float(min(days.values())) if days else 0.0, t_stat=float(t))


def fmt(s):
    return (f"kampanie {s['n']:4d} | netto {s['net']:9.0f} $ | PF {s['pf']:5.2f} | skut. {s['win']:5.1f}% | "
            f"średnio {s['exp_r']:+.3f}R | t={s['t_stat']:+.2f} | max DD {s['max_dd']:7.0f} $ | najgorszy dzień {s['worst_day']:7.0f} $")


def run(A, p):
    camps, days = simulate(A, p)
    return stats(camps, days, p["risk"]), camps


# ───────────────────────── Optymalizacja ─────────────────────────
GRID = dict(
    need_pb=[True, False],
    use_touch=[False, True],
    trig=["MACross", "MA4Cross"],
    n_entries=[1, 3, 5],
    step=[15.0, 25.0],
    tp1_r=[0.8, 1.2, 1.6, 2.5],
    part=[95.0, 50.0],
    sl_pct=[0.3, 0.5],
    start_h=[7, 13],
)


def optimize(df, base, train_frac=0.6, min_trades=60, top=10):
    cut = df.index[int(len(df) * train_frac)]
    A_tr, A_te = prepare(df[df.index < cut], base), prepare(df[df.index >= cut], base)
    keys = list(GRID)
    rows = []
    combos = list(itertools.product(*GRID.values()))
    for j, vals in enumerate(combos):
        p = dict(base, **dict(zip(keys, vals)))
        if p["n_entries"] == 1 and p["step"] != GRID["step"][0]:
            continue
        p["end_h"] = 15 if p["start_h"] == 7 else 19
        s, _ = run(A_tr, p)
        if s["n"] >= min_trades:
            rows.append((s, p))
        if j % 50 == 0:
            print(f"  {j}/{len(combos)}", file=sys.stderr)
    # wybór: średni wynik w R ważony istotnością (t-stat), a nie sam zysk – mniej dopasowania do szumu
    rows.sort(key=lambda x: x[0]["t_stat"] if not math.isnan(x[0]["t_stat"]) else -99, reverse=True)
    print(f"\nPodział: trening do {cut:%Y-%m-%d}, test od {cut:%Y-%m-%d}. Sprawdzono {len(combos)} zestawów, {len(rows)} z min. {min_trades} kampaniami.")
    print("\nNAJLEPSZE NA TRENINGU → ich wynik na TEŚCIE (dane, których optymalizacja nie widziała):")
    out = []
    for s, p in rows[:top]:
        st, _ = run(A_te, p)
        desc = {k: p[k] for k in keys}
        print(f"\n{desc}\n  trening: {fmt(s)}\n  test   : {fmt(st)}")
        out.append((desc, s, st))
    return out


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("--data")
    ap.add_argument("--src-tz", default="UTC", help="strefa czasowa w pliku, np. UTC, Europe/Berlin, Etc/GMT+5 (HistData)")
    ap.add_argument("--synthetic", action="store_true")
    ap.add_argument("--optimize", action="store_true")
    a = ap.parse_args()

    df = synthetic() if a.synthetic or not a.data else load_csv(a.data, a.src_tz)
    print(f"Dane: {len(df)} świec 1m, {df.index[0]:%Y-%m-%d} → {df.index[-1]:%Y-%m-%d}")
    base = dict(DEFAULTS)
    if a.optimize:
        optimize(df, base)
        return
    A = prepare(df, base)
    for name, extra in [("HTS oryginał (1 wejście, TP1 0.82R)", dict(n_entries=1, tp1_r=800 / 970)),
                        ("HTS (1 wejście, TP1 1.6R)", dict(n_entries=1)),
                        ("HTS Scale-In (5 wejść co 15%)", dict(n_entries=5, step=15.0)),
                        ("HTS Scale-In + Touch", dict(n_entries=5, step=15.0, use_touch=True))]:
        s, _ = run(A, dict(base, **extra))
        print(f"{name:38s} {fmt(s)}")


if __name__ == "__main__":
    main()
