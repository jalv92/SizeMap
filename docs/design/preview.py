#!/usr/bin/env python3
"""
SizeMap visual preview — renders the visual spec (ramp BEACON, direction INSTRUMENT,
price track = THE TOUCH TRACE) with synthetic-but-plausible depth data, so the design
is judged on real output before a line of C# exists.

Same pixel logic the C# Rasterizer will use: a log-normalised LUT index per
(tick, 250 ms column) written into a flat buffer and blitted once. Text here uses a
PIL font; the indicator uses a 5x7 1-bit bitmap font written into the same buffer.

NT8's candles are gone: SizeMapNullStyle renders nothing, so the price on this chart
is the touch trace — best bid and best ask, stepped, achromatic, with an ink casing.
"""
import math, random
from PIL import Image, ImageDraw, ImageFont

# ---------------------------------------------------------------- palette
BG        = (0x23, 0x24, 0x24)
STOPS_HEX = ["#232424", "#103772", "#1F4F94", "#446B9E", "#6187AE",
             "#A79F73", "#C9B97A", "#EFD472", "#FFF49D"]
TRADED    = (0xF1, 0x4B, 0xE9)   # rose  — "someone paid for it"
NOTTRADED = (0x97, 0xA1, 0xAC)   # grey  — "the seller walked"
INK       = (0x0E, 0x10, 0x13)   # grooves, casings, pre-REC ground
TEXT      = (0xE6, 0xEA, 0xEE)   # trace core, numbers
PLATE     = (0x16, 0x19, 0x1C)
ENVELOPE  = (0x3A, 0x3F, 0x44)


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
    """Log law anchored on robust percentiles of the whole book."""
    if size <= 0:
        return 0
    u = math.log(1 + size / s0) / math.log(1 + scap / s0)
    return int(round(255 * min(1.0, u)))


# ---------------------------------------------------------------- synthetic market
def make_session(seed, n_cols, n_ticks, depth_levels, walls, start_col=0):
    """-> price, bid_top[], ask_bot[], book[col] = {tick: size}"""
    rnd = random.Random(seed)
    mid = n_ticks // 2
    price, p, v = [], float(mid), 0.0
    for c in range(n_cols):
        v = v * 0.90 + rnd.gauss(0, 1.0)
        p = max(10, min(n_ticks - 10, p + v * 0.34))
        price.append(p)

    # The touch. Spread is 1 tick most of the time and occasionally widens — which is
    # the whole reason to draw two lines instead of one.
    bid_top, ask_bot = [], []
    for c in range(n_cols):
        m = int(round(price[c]))
        spread = 1 if rnd.random() > 0.06 else rnd.choice([2, 2, 3, 4])
        bid_top.append(m)
        ask_bot.append(m + spread)

    # Resting liquidity is PRICE-ANCHORED and persistent: a level keeps its size and
    # mean-reverts slowly. That autocorrelation is what makes a heatmap read as long
    # horizontal bands instead of confetti.
    book = [dict() for _ in range(n_cols)]
    resting = {}
    for c in range(n_cols):
        if c < start_col:
            continue
        window = set()
        for lv in range(depth_levels):
            window.add(bid_top[c] - lv)
            window.add(ask_bot[c] + lv)
        for t in list(resting):
            if t not in window:
                del resting[t]                     # scrolled out of the feed's vision
        for t in window:
            if not (0 <= t < n_ticks):
                continue
            lv = min(abs(t - bid_top[c]), abs(t - ask_bot[c]))
            base = 34 * math.exp(-lv * 0.050)
            if t not in resting:
                resting[t] = max(1, int(rnd.lognormvariate(math.log(base), 0.70)))
            else:
                cur = resting[t]
                tgt = base * math.exp(rnd.gauss(0, 0.10))
                resting[t] = max(1, int(cur * 0.90 + tgt * 0.10 + rnd.gauss(0, 1.4)))
            book[c][t] = resting[t]

    for w in walls:                                # stamp the walls
        for c in range(max(start_col, w["c0"]), min(n_cols, w["c1"])):
            frac = 1.0 if c < w["cdeath"] else max(
                0.0, 1.0 - (c - w["cdeath"]) / max(1, w["c1"] - w["cdeath"]))
            sz = int(w["peak"] * (w["floor"] + (1 - w["floor"]) * frac))
            if sz > 0 and abs(w["tick"] - price[c]) < depth_levels + 2:
                book[c][w["tick"]] = sz
    return price, bid_top, ask_bot, book


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

    price, bid_top, ask_bot, book = make_session(
        seed, n_cols, n_ticks, depth_levels, walls, start_col)

    img = Image.new("RGB", (W, H), BG)
    px = img.load()
    d = ImageDraw.Draw(img)

    def rowtop(t):  return H - (t + 1) * rowH
    def rowbot(t):  return H - t * rowH - 1

    def put(x, y, rgb):
        if 0 <= x < plot_w and 0 <= y < H:
            px[x, y] = rgb

    # --- layer 1: ground. Pre-REC is INK, not background --------------------------
    # #232424 is simultaneously "chart background" and "ramp stop 0 = zero resting
    # size". Leaving the un-recorded region at bg would claim the book was empty.
    if rec_col:
        d.rectangle([0, 0, rec_col * col_w - 1, H], fill=INK)

    # --- layer 2: the heat field --------------------------------------------------
    for c in range(n_cols):
        for t, sz in book[c].items():
            i = idx_of(sz, s0, scap)
            if not i:
                continue
            for y in range(rowtop(t), rowtop(t) + rowH):
                for x in range(c * col_w, min(plot_w, c * col_w + col_w)):
                    put(x, y, LUT[i])

    # --- layer 3: depth-vision envelope -------------------------------------------
    for c in range(n_cols):
        if not book[c]:
            continue
        ts = list(book[c].keys())
        for t, edge in ((max(ts), 0), (min(ts), rowH - 1)):
            for x in range(c * col_w, min(plot_w, c * col_w + col_w)):
                put(x, rowtop(t) + edge, ENVELOPE)

    # --- layer 4: wall grooves ----------------------------------------------------
    for w in walls:
        c0, c1 = max(start_col, w["c0"]), min(n_cols, w["c1"])
        if c1 <= c0:
            continue
        for yy in (rowtop(w["tick"]) - 1, rowtop(w["tick"]) + rowH):
            for x in range(c0 * col_w, min(plot_w, c1 * col_w)):
                put(x, yy, INK)
        for y in range(rowtop(w["tick"]) - 1, rowtop(w["tick"]) + rowH + 1):
            put(c0 * col_w, y, INK)                       # left cap = birth
        if w["peak"] > scap:                              # saturation mark
            for off in (0, 2):
                for y in range(rowtop(w["tick"]), rowtop(w["tick"]) + rowH):
                    put(c1 * col_w - 4 + off, y, TEXT)

    # --- layer 5: remembered walls -> HOLLOW --------------------------------------
    # colour = size at last sight and never decays; dash duty = confidence.
    for r in remembered:
        col = LUT[idx_of(r["size"], s0, scap)]
        on = max(2, 2 * int(round(4 * r["conf"])))
        yt, yb = rowtop(r["tick"]) - 1, rowtop(r["tick"]) + rowH
        for x in range(r["c0"] * col_w, min(plot_w, r["c1"] * col_w)):
            if x % 8 < on:
                put(x, yt, col); put(x, yb, col)
        xs = r["c0"] * col_w                              # side tick: up = was ask
        for k in range(3):
            put(xs, (yt - 1 - k) if r["side"] == "ask" else (yb + 1 + k), col)

    # --- layer 6: THE TOUCH TRACE -------------------------------------------------
    # Two stepped lines, best bid and best ask. 1 px near-white core with a 1 px ink
    # casing on each side: the core fails only on backgrounds lighter than L 0.835,
    # the casing only on those darker than L 0.272 — disjoint, so no substrate on the
    # chart can defeat the pair. Worst composite over the whole LUT is dL 0.382.
    #
    # The ask core sits ONE PIXEL ABOVE its quoted row, so its lower casing lands on
    # the row's own top edge — which, when the ask is parked on a confirmed wall, is
    # already that wall's ink groove. Same colour, no-op write: the trace costs the
    # wall it is eating exactly zero heat pixels.
    def trace(c, side):
        # A step function is a TREAD plus a RISER, not a filled column. The tread runs
        # the full column at the quote in force; the riser is 1 px wide at the column's
        # left edge, joining the previous quote to this one. Filling the column height
        # would paint prices that were never quoted during that column.
        t_now = ask_bot[c] if side == "ask" else bid_top[c]
        t_prev = (ask_bot[c - 1] if side == "ask" else bid_top[c - 1]) if c else t_now

        def core_y(t):
            return rowtop(t) - 1 if side == "ask" else rowbot(t) + 1

        y_now, y_prev = core_y(t_now), core_y(t_prev)
        x0 = c * col_w
        live = c >= (rec_col or 0)

        if live:
            for x in range(x0, min(plot_w, x0 + col_w)):  # tread + its casing
                put(x, y_now - 1, INK); put(x, y_now + 1, INK)
            for y in range(min(y_now, y_prev), max(y_now, y_prev) + 1):
                put(x0 - 1, y, INK); put(x0 + 1, y, INK)  # riser casing, left + right
            for y in range(min(y_now, y_prev), max(y_now, y_prev) + 1):
                put(x0, y, TEXT)                          # riser core
            for x in range(x0, min(plot_w, x0 + col_w)):
                put(x, y_now, TEXT)                       # tread core last, wins
        else:                                             # reconstructed: dashed, bare
            for y in range(min(y_now, y_prev), max(y_now, y_prev) + 1):
                if (y // 4) % 2 == 0:
                    put(x0, y, TEXT)
            for x in range(x0, min(plot_w, x0 + col_w)):
                if (x // 4) % 2 == 0:
                    put(x, y_now, TEXT)

    for c in range(n_cols):
        trace(c, "ask")
        trace(c, "bid")

    # --- layer 7: outcome glyphs (9x9, ink-dilated) -------------------------------
    def glyph(cx, cy, kind):
        col = TRADED if kind in ("consumed", "absorbed") else NOTTRADED
        d.ellipse([cx - 6, cy - 6, cx + 6, cy + 6], fill=BG, outline=INK)
        if kind == "consumed":
            d.ellipse([cx - 4, cy - 4, cx + 4, cy + 4], fill=col)
        elif kind == "absorbed":
            d.polygon([(cx, cy - 5), (cx + 5, cy + 4), (cx - 5, cy + 4)], fill=col)
        elif kind == "pulled":
            d.line([cx - 4, cy - 4, cx + 4, cy + 4], fill=col, width=2)
            d.line([cx - 4, cy + 4, cx + 4, cy - 4], fill=col, width=2)
        else:
            d.polygon([(cx, cy - 5), (cx + 5, cy), (cx, cy + 5), (cx - 5, cy)],
                      outline=col)

    for r in remembered:
        glyph(min(plot_w - 10, r["c1"] * col_w + 10),
              rowtop(r["tick"]) + rowH // 2, r["kind"])

    # --- layer 8: the consumption ledgers, in the future margin -------------------
    try:
        f  = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 12)
        fs = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono.ttf", 11)
        fb = ImageFont.truetype("/usr/share/fonts/truetype/dejavu/DejaVuSansMono-Bold.ttf", 13)
    except Exception:
        f = fs = fb = ImageFont.load_default()

    for w in sorted(walls, key=lambda x: -x["peak"])[:3]:
        cur = w["peak"] * w["floor"]
        drop = w["peak"] - cur
        LW, x0 = 96, plot_w + 8
        y0 = rowtop(w["tick"]) + rowH // 2 - 5
        hgt = max(9, rowH)
        wr = int(LW * cur / w["peak"])
        wt = int(LW * (drop / w["peak"]) * w["tbf"])
        d.rectangle([x0 - 1, y0 - 1, x0 + LW, y0 + hgt], outline=INK)
        d.rectangle([x0, y0, x0 + wr, y0 + hgt - 1], fill=LUT[idx_of(cur, s0, scap)])
        d.rectangle([x0 + wr, y0, x0 + wr + wt, y0 + hgt - 1], fill=TRADED)
        d.rectangle([x0 + wr + wt, y0, x0 + LW, y0 + hgt - 1], fill=NOTTRADED)
        d.text((x0 + 2, y0 + hgt + 2), f"{w['peak']}", font=fs, fill=TEXT)

    # --- layer 9: REC rule --------------------------------------------------------
    if rec_col:
        for y in range(H):
            put(rec_col * col_w, y, ENVELOPE)
        d.text((rec_col * col_w + 6, 52), "REC", font=fs, fill=ENVELOPE)

    # --- layer 10: HUD ------------------------------------------------------------
    d.text((8, 6), title, font=fb, fill=TEXT)
    d.text((8, 24), hud2, font=f, fill=NOTTRADED)

    # --- layer 11: legend ---------------------------------------------------------
    lw, lh = 260, 120
    lx, ly = plot_w - lw - 14, H - lh - 14
    d.rectangle([lx, ly, lx + lw, ly + lh], fill=PLATE, outline=INK)
    for i in range(160):
        d.rectangle([lx + 10 + i, ly + 8, lx + 10 + i, ly + 18],
                    fill=LUT[int(i * 255 / 159)])
    d.text((lx + 176, ly + 7), "LOG", font=fs, fill=NOTTRADED)
    d.text((lx + 10, ly + 21), f"{s0}", font=fs, fill=NOTTRADED)
    d.text((lx + 78, ly + 21), f"{int(math.sqrt(s0 * scap))}", font=fs, fill=NOTTRADED)
    d.text((lx + 148, ly + 21), f"{scap}", font=fs, fill=NOTTRADED)
    d.rectangle([lx + 10, ly + 40, lx + 34, ly + 48], fill=LUT[250])
    d.text((lx + 40, ly + 38), "NOW", font=fs, fill=NOTTRADED)
    for x in range(lx + 84, lx + 108):
        d.point((x, ly + 40), fill=LUT[210]); d.point((x, ly + 48), fill=LUT[210])
    d.text((lx + 114, ly + 38), "MEM", font=fs, fill=NOTTRADED)
    for x in range(lx + 158, lx + 182):
        if x % 8 < 2:
            d.point((x, ly + 40), fill=LUT[210]); d.point((x, ly + 48), fill=LUT[210])
    d.text((lx + 188, ly + 38), "FAINT", font=fs, fill=NOTTRADED)
    for i, k in enumerate(("consumed", "absorbed", "pulled", "unclassified")):
        glyph(lx + 18 + i * 34, ly + 62, k)
    d.text((lx + 10, ly + 72), "BOUGHT  HELD  PULLED  N-C", font=fs, fill=NOTTRADED)
    for x in range(lx + 10, lx + 46):                     # touch key
        d.point((x, ly + 87), fill=INK); d.point((x, ly + 88), fill=TEXT)
        d.point((x, ly + 89), fill=INK)
    d.text((lx + 52, ly + 82), "TOUCH", font=fs, fill=NOTTRADED)
    for x in range(lx + 100, lx + 136):
        if (x // 4) % 2 == 0:
            d.point((x, ly + 88), fill=TEXT)
    d.text((lx + 142, ly + 82), "REPLAY", font=fs, fill=NOTTRADED)
    d.rectangle([lx + 10, ly + 100, lx + 45, ly + 107], fill=LUT[180])
    d.rectangle([lx + 45, ly + 100, lx + 78, ly + 107], fill=TRADED)
    d.rectangle([lx + 78, ly + 100, lx + 90, ly + 107], fill=NOTTRADED)
    d.text((lx + 96, ly + 98), "LEFT BOUGHT CANCEL", font=fs, fill=NOTTRADED)

    img.save(path)
    print("wrote", path, img.size)


# ================================================================== scene A: 10 levels
render(
    "/tmp/sizemap_A.png",
    "SIZEMAP   NQ 09-26    DEPTH 10L  OBS 10    COL 4S  ROW 1T   TR ON Q:REAL   SPRD",
    seed=7, depth_levels=10, n_ticks=100, s0=8, scap=320, rec_col=52, dense=False,
    start_col=52,
    hud2="LOG  S0 8  CAP 320     WALLS 2L 4R 9D     PX/T 9     1.3MS  4.0FPS",
    walls=[
        dict(tick=68, c0=70, c1=200, cdeath=150, peak=620, floor=0.30, tbf=0.87),
        dict(tick=36, c0=110, c1=250, cdeath=215, peak=280, floor=0.55, tbf=0.12),
    ],
    remembered=[
        dict(tick=82, c0=62, c1=210, size=430, conf=0.95, side="ask", kind="consumed"),
        dict(tick=58, c0=80, c1=225, size=190, conf=0.55, side="bid", kind="pulled"),
        dict(tick=20, c0=95, c1=235, size=310, conf=0.30, side="bid", kind="unclassified"),
        dict(tick=90, c0=130, c1=245, size=150, conf=0.75, side="ask", kind="absorbed"),
    ],
)

# ================================================================== scene B: 40 levels
render(
    "/tmp/sizemap_B.png",
    "SIZEMAP   ES 09-26    DEPTH 40L  OBS 40    COL 15S  ROW 1T   TR ON Q:REAL   EXT",
    seed=21, depth_levels=40, n_ticks=100, s0=34, scap=480, rec_col=0, dense=True,
    hud2="LOG  S0 34  CAP 480    WALLS 4L 19R 61D   PX/T 9     2.9MS  4.0FPS",
    walls=[
        dict(tick=76, c0=20, c1=330, cdeath=250, peak=1240, floor=0.28, tbf=0.83),
        dict(tick=45, c0=90, c1=400, cdeath=300, peak=740, floor=0.62, tbf=0.20),
        dict(tick=26, c0=150, c1=430, cdeath=360, peak=980, floor=0.45, tbf=0.66),
    ],
    remembered=[
        dict(tick=88, c0=30, c1=380, size=520, conf=0.95, side="ask", kind="consumed"),
        dict(tick=62, c0=60, c1=400, size=300, conf=0.62, side="ask", kind="absorbed"),
        dict(tick=14, c0=90, c1=415, size=410, conf=0.30, side="bid", kind="unclassified"),
        dict(tick=6,  c0=120, c1=430, size=260, conf=0.90, side="bid", kind="pulled"),
    ],
)
