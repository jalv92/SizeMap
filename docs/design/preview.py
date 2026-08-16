#!/usr/bin/env python3
"""
SizeMap visual preview — renders the FINAL VISUAL SPEC v1 (ramp BEACON, direction
INSTRUMENT) with synthetic-but-plausible depth data, so the design can be judged by
eye before a single line of C# exists.

This is the same pixel logic the real Rasterizer will use: a log-normalised LUT index
per (tick, 250ms column), written into a flat buffer, blitted once. Text here uses a
PIL font; the real indicator uses a 5x7 1-bit bitmap font (same layout, crisper).
"""
import math, random
from PIL import Image, ImageDraw, ImageFont

# ---------------------------------------------------------------- palette (spec 2.1)
BG        = (0x23, 0x24, 0x24)
STOPS_HEX = ["#232424", "#103772", "#1F4F94", "#446B9E", "#6187AE",
             "#A79F73", "#C9B97A", "#EFD472", "#FFF49D"]
TRADED    = (0xF1, 0x4B, 0xE9)   # rose  — "someone paid for it"
NOTTRADED = (0x97, 0xA1, 0xAC)   # grey  — "the seller walked"
INK       = (0x0E, 0x10, 0x13)
TEXT      = (0xE6, 0xEA, 0xEE)
PLATE     = (0x16, 0x19, 0x1C)
ENVELOPE  = (0x3A, 0x3F, 0x44)
UP        = (0x33, 0xBB, 0x55)   # NT8-ish candle up
DOWN      = (0xE0, 0x3B, 0x3B)   # NT8-ish candle down


def hx(s):
    s = s.lstrip("#")
    return tuple(int(s[i:i + 2], 16) for i in (0, 2, 4))


def lerp(a, b, t):
    return tuple(int(round(a[i] + (b[i] - a[i]) * t)) for i in range(3))


def build_lut():
    stops = [hx(s) for s in STOPS_HEX]
    idxs = [0, 32, 64, 96, 128, 160, 192, 224, 255]
    lut = []
    for i in range(256):
        s = 0
        while s < len(idxs) - 2 and i >= idxs[s + 1]:
            s += 1
        span = idxs[s + 1] - idxs[s]
        t = (i - idxs[s]) / span if span else 0.0
        c = lerp(stops[s], stops[s + 1], min(1.0, t))
        if i < 24:                      # fade the foot into the background
            c = lerp(BG, c, i / 24.0)
        lut.append(c)
    lut[0] = BG
    return lut


LUT = build_lut()


def idx_of(size, s0, scap):
    """Log law anchored on robust percentiles of the whole book (spec 2.4)."""
    if size <= 0:
        return 0
    u = math.log(1 + size / s0) / math.log(1 + scap / s0)
    return int(round(255 * min(1.0, u)))


# ---------------------------------------------------------------- synthetic market
def make_session(seed, n_cols, n_ticks, depth_levels, walls, start_col=0):
    """Returns (price_by_col, book[col] = {tick: size}, wall_specs)."""
    rnd = random.Random(seed)
    mid = n_ticks // 2
    price = []
    p = float(mid)
    v = 0.0
    for c in range(n_cols):
        v = v * 0.90 + rnd.gauss(0, 1.0)
        p += v * 0.34
        p = max(10, min(n_ticks - 10, p))
        price.append(p)

    # Resting liquidity is PRICE-ANCHORED and persistent: a level keeps its size and
    # mean-reverts slowly. That autocorrelation is what makes a heatmap read as long
    # horizontal bands instead of confetti. Re-rolling per column is the classic
    # synthetic-data mistake and it makes a good design look broken.
    book = [dict() for _ in range(n_cols)]
    resting = {}                                   # tick -> current size, persistent
    for c in range(n_cols):
        if c < start_col:
            continue
        m = int(round(price[c]))
        window = {m + s * (lv + 1) for lv in range(depth_levels) for s in (-1, +1)}
        for t in list(resting):
            if t not in window:
                del resting[t]                     # scrolled out of the feed's vision
        for t in window:
            if not (0 <= t < n_ticks):
                continue
            lv = abs(t - m) - 1
            base = 34 * math.exp(-lv * 0.050)
            if t not in resting:
                resting[t] = max(1, int(rnd.lognormvariate(math.log(base), 0.70)))
            else:                                  # slow mean reversion + small jitter
                cur = resting[t]
                tgt = base * math.exp(rnd.gauss(0, 0.10))
                resting[t] = max(1, int(cur * 0.90 + tgt * 0.10 + rnd.gauss(0, 1.4)))
            book[c][t] = resting[t]
    # stamp the walls: a persistent large resting size at a fixed price
    for w in walls:
        for c in range(max(start_col, w["c0"]), min(n_cols, w["c1"])):
            if c < w["cdeath"]:
                frac = 1.0
            else:
                k = (c - w["cdeath"]) / max(1, w["c1"] - w["cdeath"])
                frac = max(0.0, 1.0 - k)
            sz = int(w["peak"] * (w["floor"] + (1 - w["floor"]) * frac))
            if sz > 0 and abs(w["tick"] - price[c]) < depth_levels + 2:
                book[c][w["tick"]] = sz
    return price, book, walls


# ---------------------------------------------------------------- renderer
def render(path, title, seed, depth_levels, n_ticks, walls, remembered,
           s0, scap, rec_col, hud2, dense, start_col=0):
    W, H = 1600, 900
    MARGIN_R = 150                      # the "future margin": ledgers live here
    plot_w = W - MARGIN_R
    rowH = H // n_ticks
    H = rowH * n_ticks                  # snap so cells are whole pixels
    col_w = 3 if dense else 5
    n_cols = plot_w // col_w

    price, book, walls = make_session(seed, n_cols, n_ticks, depth_levels,
                                      walls, start_col)

    img = Image.new("RGB", (W, H), BG)
    px = img.load()
    d = ImageDraw.Draw(img)

    def y_of(tick):                     # tick 0 at the bottom
        return H - (tick + 1) * rowH

    def fill_cell(tick, col, rgb):
        x0, y0 = col * col_w, y_of(tick)
        for y in range(y0, min(H, y0 + rowH)):
            for x in range(x0, min(plot_w, x0 + col_w)):
                px[x, y] = rgb

    # --- layer 3: the heat field -------------------------------------------------
    for c in range(n_cols):
        for t, sz in book[c].items():
            i = idx_of(sz, s0, scap)
            if i:
                fill_cell(t, c, LUT[i])

    # --- layer 2: depth-vision envelope (what the feed could SEE, per column) -----
    for c in range(n_cols):
        if not book[c]:
            continue
        ts = list(book[c].keys())
        for t in (max(ts), min(ts)):
            y = y_of(t) + (0 if t == max(ts) else rowH - 1)
            for x in range(c * col_w, min(plot_w, c * col_w + col_w)):
                px[x, max(0, min(H - 1, y))] = ENVELOPE

    # --- layer 4: wall grooves (a confirmed wall is an OBJECT) --------------------
    for w in walls:
        c0 = max(start_col, w["c0"])
        c1 = min(n_cols, w["c1"])
        if c1 <= c0:
            continue
        for yy in (y_of(w["tick"]) - 1, y_of(w["tick"]) + rowH):
            if 0 <= yy < H:
                for x in range(c0 * col_w, min(plot_w, c1 * col_w)):
                    px[x, yy] = INK
        for y in range(max(0, y_of(w["tick"]) - 1), min(H, y_of(w["tick"]) + rowH + 1)):
            px[min(plot_w - 1, c0 * col_w), y] = INK          # left cap = birth
        if w["peak"] > scap:                                   # saturation mark
            xs = min(plot_w - 1, c1 * col_w - 4)
            for off in (0, 2):
                for y in range(y_of(w["tick"]), min(H, y_of(w["tick"]) + rowH)):
                    px[max(0, xs + off), y] = TEXT

    # --- layer 5: remembered walls -> HOLLOW, colour = size at last sight ---------
    # dash duty = confidence.  colour never decays, dash does.  two honest channels.
    for r in remembered:
        col = LUT[idx_of(r["size"], s0, scap)]
        on = max(2, 2 * int(round(4 * r["conf"])))
        yt, yb = y_of(r["tick"]) - 1, y_of(r["tick"]) + rowH
        for x in range(r["c0"] * col_w, min(plot_w, r["c1"] * col_w)):
            if (x // 1) % 8 < on:
                if 0 <= yt < H:
                    px[x, yt] = col
                if 0 <= yb < H:
                    px[x, yb] = col
        # side tick on the left cap: up = was ask, down = was bid
        xs = r["c0"] * col_w
        for k in range(3):
            yy = (yt - 1 - k) if r["side"] == "ask" else (yb + 1 + k)
            if 0 <= yy < H and xs < plot_w:
                px[xs, yy] = col

    # --- layer 8: outcome glyphs (9x9, ink-dilated) ------------------------------
    def glyph(cx, cy, kind):
        col = TRADED if kind in ("consumed", "absorbed") else NOTTRADED
        d.ellipse([cx - 6, cy - 6, cx + 6, cy + 6], fill=BG, outline=INK)
        if kind == "consumed":                                  # bought out
            d.ellipse([cx - 4, cy - 4, cx + 4, cy + 4], fill=col)
        elif kind == "absorbed":                                # held / refilled
            d.polygon([(cx, cy - 5), (cx + 5, cy + 4), (cx - 5, cy + 4)], fill=col)
        elif kind == "pulled":                                  # seller walked
            d.line([cx - 4, cy - 4, cx + 4, cy + 4], fill=col, width=2)
            d.line([cx - 4, cy + 4, cx + 4, cy - 4], fill=col, width=2)
        else:                                                   # refused to classify
            d.polygon([(cx, cy - 5), (cx + 5, cy), (cx, cy + 5), (cx - 5, cy)],
                      outline=col)

    for r in remembered:
        gx = min(plot_w - 10, r["c1"] * col_w + 10)
        glyph(gx, y_of(r["tick"]) + rowH // 2, r["kind"])

    # --- the candles: NT8 draws them, SizeMap is BEHIND them (SetZOrder(-1)) ------
    bar = 11 if not dense else 15
    for b0 in range(start_col, n_cols - bar, bar):
        seg = price[b0:b0 + bar]
        o, cl, hi, lo = seg[0], seg[-1], max(seg), min(seg)
        up = cl >= o
        col = UP if up else DOWN
        xc = int((b0 + bar / 2) * col_w)
        d.line([xc, y_of(hi), xc, y_of(lo) + rowH], fill=col, width=1)
        y1, y2 = y_of(max(o, cl)), y_of(min(o, cl)) + rowH
        d.rectangle([xc - bar * col_w // 5, y1, xc + bar * col_w // 5, y2],
                    fill=col if up else None, outline=col)

    # --- REC rule: nothing left of this is claimed -------------------------------
    if rec_col:
        for y in range(H):
            px[rec_col * col_w, y] = ENVELOPE

    # --- layer 9: the consumption ledgers, in the future margin ------------------
    try:
        f  = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 12)
        fs = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 11)
        fb = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf", 13)
    except Exception:
        f = fs = fb = ImageFont.load_default()

    for w in sorted(walls, key=lambda x: -x["peak"])[:3]:
        cur = w["peak"] * w["floor"]
        drop = w["peak"] - cur
        tbf = w["tbf"]
        LW = 96
        x0 = plot_w + 8
        y0 = y_of(w["tick"]) + rowH // 2 - 5
        hgt = max(9, rowH)
        wr = int(LW * cur / w["peak"])
        wt = int(LW * (drop / w["peak"]) * tbf)
        wc = LW - wr - wt
        d.rectangle([x0 - 1, y0 - 1, x0 + LW, y0 + hgt], outline=INK)
        d.rectangle([x0, y0, x0 + wr, y0 + hgt - 1],
                    fill=LUT[idx_of(cur, s0, scap)])
        d.rectangle([x0 + wr, y0, x0 + wr + wt, y0 + hgt - 1], fill=TRADED)
        d.rectangle([x0 + wr + wt, y0, x0 + LW, y0 + hgt - 1], fill=NOTTRADED)
        d.text((x0 + 2, y0 + hgt + 2), f"{w['peak']}", font=fs, fill=TEXT)

    # --- layer 11: HUD -----------------------------------------------------------
    d.text((8, 6), title, font=fb, fill=TEXT)
    d.text((8, 24), hud2, font=f, fill=NOTTRADED)

    # --- layer 12: legend --------------------------------------------------------
    lw, lh = 260, 104
    lx, ly = plot_w - lw - 14, H - lh - 14
    d.rectangle([lx, ly, lx + lw, ly + lh], fill=PLATE, outline=INK)
    for i in range(160):                       # ramp strip, sampled from the LIVE lut
        d.rectangle([lx + 10 + i, ly + 8, lx + 10 + i, ly + 18],
                    fill=LUT[int(i * 255 / 159)])
    d.text((lx + 176, ly + 7), "LOG", font=fs, fill=NOTTRADED)
    d.text((lx + 10, ly + 21), f"{s0}", font=fs, fill=NOTTRADED)
    d.text((lx + 78, ly + 21), f"{int(math.sqrt(s0*scap))}", font=fs, fill=NOTTRADED)
    d.text((lx + 148, ly + 21), f"{scap}", font=fs, fill=NOTTRADED)
    d.rectangle([lx + 10, ly + 40, lx + 34, ly + 48], fill=LUT[250])
    d.text((lx + 40, ly + 38), "NOW", font=fs, fill=NOTTRADED)
    for x in range(lx + 84, lx + 108):
        if (x % 8) < 8:
            d.point((x, ly + 40), fill=LUT[210]); d.point((x, ly + 48), fill=LUT[210])
    d.text((lx + 114, ly + 38), "MEM", font=fs, fill=NOTTRADED)
    for x in range(lx + 158, lx + 182):
        if (x % 8) < 2:
            d.point((x, ly + 40), fill=LUT[210]); d.point((x, ly + 48), fill=LUT[210])
    d.text((lx + 188, ly + 38), "FAINT", font=fs, fill=NOTTRADED)
    for i, k in enumerate(("consumed", "absorbed", "pulled", "unclassified")):
        glyph(lx + 18 + i * 34, ly + 62, k)
    d.text((lx + 10, ly + 72), "BOUGHT  HELD  PULLED  N-C", font=fs, fill=NOTTRADED)
    d.rectangle([lx + 10, ly + 88, lx + 45, ly + 95], fill=LUT[180])
    d.rectangle([lx + 45, ly + 88, lx + 78, ly + 95], fill=TRADED)
    d.rectangle([lx + 78, ly + 90 - 2, lx + 90, ly + 95], fill=NOTTRADED)
    d.text((lx + 96, ly + 86), "LEFT BOUGHT CANCEL", font=fs, fill=NOTTRADED)

    img.save(path)
    print("wrote", path, img.size)


# ================================================================== scene A: 10 levels
render(
    "/tmp/sizemap_A.png",
    "SIZEMAP   NQ 09-26    DEPTH 10L  OBS 10    COL 4S   ROW 1T",
    seed=7, depth_levels=10, n_ticks=100, s0=8, scap=320, rec_col=18, dense=False,
    start_col=18,
    hud2="LOG  S0 8  CAP 320     WALLS 2L 4R 9D     1.3MS  4.0FPS",
    walls=[
        dict(tick=52, c0=40, c1=200, cdeath=140, peak=620, floor=0.30, tbf=0.87),
        dict(tick=27, c0=95, c1=250, cdeath=210, peak=280, floor=0.55, tbf=0.12),
    ],
    remembered=[
        dict(tick=63, c0=30, c1=210, size=430, conf=0.95, side="ask", kind="consumed"),
        dict(tick=44, c0=55, c1=225, size=190, conf=0.55, side="bid", kind="pulled"),
        dict(tick=15, c0=70, c1=235, size=310, conf=0.30, side="bid", kind="unclassified"),
        dict(tick=69, c0=110, c1=245, size=150, conf=0.75, side="ask", kind="absorbed"),
    ],
)

# ================================================================== scene B: 40 levels
render(
    "/tmp/sizemap_B.png",
    "SIZEMAP   ES 09-26    DEPTH 40L  OBS 40    COL 15S  ROW 1T",
    seed=21, depth_levels=40, n_ticks=100, s0=34, scap=480, rec_col=0, dense=True,
    hud2="LOG  S0 34  CAP 480    WALLS 4L 19R 61D    2.9MS  4.0FPS",
    walls=[
        dict(tick=58, c0=20, c1=330, cdeath=250, peak=1240, floor=0.28, tbf=0.83),
        dict(tick=34, c0=90, c1=400, cdeath=300, peak=740, floor=0.62, tbf=0.20),
        dict(tick=20, c0=150, c1=430, cdeath=360, peak=980, floor=0.45, tbf=0.66),
    ],
    remembered=[
        dict(tick=66, c0=30, c1=380, size=520, conf=0.95, side="ask", kind="consumed"),
        dict(tick=47, c0=60, c1=400, size=300, conf=0.62, side="ask", kind="absorbed"),
        dict(tick=11, c0=90, c1=415, size=410, conf=0.30, side="bid", kind="unclassified"),
        dict(tick=5,  c0=120, c1=430, size=260, conf=0.90, side="bid", kind="pulled"),
    ],
)
