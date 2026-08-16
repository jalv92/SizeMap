# SizeMap verdict — sm-raw-ES-20260813-part16.smr

Generated 2026-08-16 11:35 by `verdict/`, replaying recorded tape through the shipped engine files.

| | |
|---|---|
| tape | sm-raw-ES-20260813-part16.smr  (ES, tick 0.25) |
| market time | 2026-08-13 10:51:35 -> 11:50:34  (59.0 min) |
| records | 1,213,774  (1,085,599 depth, 128,117 trades, 0 feed resets, 0 epoch breaks) |
| engine | 12,872 ticks at 4 Hz, 2.5 s of CPU (0.195 ms/tick) |
| config | K_mult 1.5, MinAbsSize 40, TradeRetention 1500 s |
| resting size | p50 72, p85 84, p99 123, max 577  (12,864 sampled levels) |
| fidelity check | 469 classified episodes where this tool's captured Traded/Cancelled equal the engine's own, 0 mismatches |


## 1. Is the detector's output even plausible?

| | |
|---|---|
| walls detected | 50 distinct promotions (33 left memory) |
| wall lifetime | p50 570.5s, p90 1275.4s, max 1304.4s  (n 50) |
| wall peak size | p50 127.0 lots, p90 243.3 lots, max 577.0 lots  (n 50) |
| episodes | 1008 resolved |

**Census** — sampled 12,872 times at 4 Hz, through the RENDERER's own predicate:
`InWindow` -> **solid** groove; else `Confidence >= 0.25` -> **hollow** rule; else **undrawn**. The third bucket is
the one Phase 2 had no name for: tracked, counted as "remembered", and never on the chart. Target band from
visual-spec §5: **2-6 solid, 10-25 hollow**.

| census | mean | p50 | p90 | max | in target band |
|---|---|---|---|---|---|
| SOLID (drawn, in window) | 3.15 | 2 | 7 | 10 | 54.5% |
| HOLLOW (drawn, remembered) | 0.90 | 0 | 3 | 9 | 0.0% |
| UNDRAWN (tracked, invisible) | 5.39 | 5 | 10 | 13 | n/a |
| SOLID, ±25 ticks of mid | 3.15 | 2 | 7 | 10 | 54.5% |
| HOLLOW, ±25 ticks of mid | 0.90 | 0 | 3 | 9 | 0.0% |

Draw caps (§4: 12 solid, 24 hollow) bit in 0.0% / 0.0% of samples.

**Outcome split** (what the glyph layer would paint):

| outcome | n | share |
|---|---|---|
| ABSORBED | 41 | 4.1% |
| PULLED | 0 | 0.0% |
| CONSUMED | 428 | 42.5% |
| UNCLASSIFIED | 539 | 53.5% |

**Why a state does or does not fire.** `Consumed` is tested FIRST and wins on `Crossed` alone;
`Pulled` needs the level to empty while the quote has NOT crossed it.

| condition at resolution | n | share |
|---|---|---|
| the level emptied (`displayed == 0`) | 428 | 42.5% |
| the quote crossed the level (`Crossed`) | 428 | 42.5% |
| emptied **and** crossed -> `Consumed` preempts `Pulled` | 428 | 100.0% of emptied |
| emptied with the quote >= 2 ticks away | 2 | 0.5% of emptied |
| cancelled > traded (the `Pulled` size test) | 248 | 24.6% |

> **`PULLED` never fired once in 1008 episodes.** A state that cannot be reached is not a calibration problem, it is dead code with a glyph in the spec.

> Outcome split is spread across buckets (53.5% in the largest, UNCLASSIFIED), so the
> rest of this report is measuring something with variance in it.


## 2. Does EpisodeClassifier beat the null model?

```
if (size hit 0 && |quote - level| >= 2 ticks) return Pulled;
else if (tradedAt >= drop)                    return Absorbed;
else                                          return Consumed;
```

Rows = `EpisodeClassifier` (175 lines, 4 thresholds). Columns = the null (3 lines, 1 threshold).

| classifier \ null | ABSORBED | PULLED | CONSUMED | row n | agrees |
|---|---|---|---|---|---|
| **ABSORBED** | 41 | 0 | 0 | 41 | 100.0% |
| **PULLED** | 0 | 0 | 0 | 0 | n/a |
| **CONSUMED** | 169 | 2 | 257 | 428 | 60.0% |
| **UNCLASSIFIED** | 374 | 0 | 165 | 539 | n/a |

Overall agreement, over the **469** episodes the classifier actually labelled: **63.5%**.
Over all 1008 resolved episodes (counting the classifier's refusals as disagreement): **29.6%**.

**Two caveats on the null, both in its favour and both stated rather than fixed.**

1. `traded >= drop` is trivially TRUE when nothing dropped: **382 of 1008** (37.9%) episodes resolved with `drop == 0`, and the null calls every one of them ABSORBED.
   On the **626** episodes where the wall actually lost size, agreement over the classifier's own labels is **60.5%** (262/433).
2. Neither model emits PULLED on this tape: the null needs the level to empty with the quote >= 2 ticks away,
   and episodes are only ever opened on walls AT the inside (`D_approach = 1`), where that distance is 1 tick.

> **Verdict:** the null model reproduces only **63.5%** of the labels, so the classifier is genuinely doing something different. Whether that difference is *better* is question 3, not this one.


## 3. Does the label carry forward information?

For every resolved episode: the mid at resolution, and the mid at +10 s / +30 s / +60 s of MARKET time.
`move` = signed ticks in the wall's through-direction, so positive always means *the wall gave way*.

1. **Per side, never pooled.** An ask wall giving way is price UP; a bid wall's is price DOWN. A pooled
   bucket is a blend of two opposite baselines, so the report's own instruction to read every move
   against the drift row cannot be followed for it. Phase 2's one significant result was ~31% bid/ask mix.
2. **`P(through)` is gone, at every horizon.** CONSUMED is *defined* as "the inside quote crossed the
   level", so it starts already through and every other bucket starts at 0%. Its huge z-scores measured
   that definition, not the future. `move` is a CHANGE from the mid at resolution and is the only
   column here that can carry forward information.
3. **Every mean-move test prints its 80%-power MDE.** A flat p from a test blind to a 3-tick effect is
   not evidence of no effect, and Phase 2 printed one such p with no marker.

**Drift control.** The same measurement with no episode in it: the mean forward move of the mid from
every one of the 12,872 census samples on this tape, in the ASK-wall sign convention (price up = +).
An ask wall's move IS this number when the label carries nothing; a bid wall's is its negative. The
windows overlap, so this row's own effective n is far below the sample count — it sets the CENTRE that
`excess` is measured from, and no test below depends on its precision (a same-side contrast cancels it).

| | +10s | +30s | +60s |
|---|---|---|---|
| unconditional mean move, ticks | -0.22 | -0.65 | -1.33 |

**Ask walls (resistance)** — `excess` = mean move minus this side's drift null (-0.22 / -0.65 / -1.33 ticks).

| outcome | n | walls | ep/wall | mean +10s | excess | mean +30s | excess | mean +60s | excess |
|---|---|---|---|---|---|---|---|---|---|
| ABSORBED | 8 | 7 | 1.1 | -0.88 (n8) | -0.65 | -1.62 (n8) | -0.97 | -2.38 (n8) | -1.05 |
| PULLED | 0 | - | - | - | - | - | - | - | - |
| CONSUMED | 91 | 14 | 6.5 | -0.18 (n89) | 0.04 | -0.15 (n88) | 0.50 | -0.95 (n88) | 0.37 |
| UNCLASSIFIED | 138 | 16 | 8.6 | -1.17 (n138) | -0.95 | -1.70 (n138) | -1.05 | -3.29 (n138) | -1.96 |

**Bid walls (support)** — `excess` = mean move minus this side's drift null (0.22 / 0.65 / 1.33 ticks).

| outcome | n | walls | ep/wall | mean +10s | excess | mean +30s | excess | mean +60s | excess |
|---|---|---|---|---|---|---|---|---|---|
| ABSORBED | 33 | 14 | 2.4 | -0.27 (n33) | -0.50 | -0.16 (n31) | -0.81 | -0.87 (n31) | -2.20 |
| PULLED | 0 | - | - | - | - | - | - | - | - |
| CONSUMED | 337 | 24 | 14.0 | 0.15 (n333) | -0.07 | -0.41 (n329) | -1.06 | -0.11 (n320) | -1.44 |
| UNCLASSIFIED | 401 | 25 | 16.0 | 0.02 (n397) | -0.21 | 0.35 (n392) | -0.30 | -0.16 (n380) | -1.49 |

**Significance, corrected.** The grid is pre-declared in code (4 contrasts x 2 sides x 3 horizons = 24 tests);
**18** have >= 2 episodes with a read on both sides and are testable. Correction is **Holm-Bonferroni**
over those 18 — step-down, exact family-wise error control, no independence assumption. The smallest raw p
must clear **0.00278**, the next 0.05/17, and so on; `holm p` is that comparison folded back onto the usual 0.05.

`MDE` is the smallest true difference these two n could detect at 80% power, alpha 0.05. A row whose
|diff| sits far under its MDE has not found nothing — it has not looked. `cluster` columns resample WALLS
with replacement (2 000 replicates, fixed seed): several episodes sit on one wall, so an i.i.d. p-value
counts each of them as fresh evidence and is optimistic. The cluster CI is the honest one.

| contrast | side | h | n / n | means | diff | MDE | t | raw p | holm p | walls | cluster 95% CI | cluster p |
|---|---|---|---|---|---|---|---|---|---|---|---|---|
| CONSUMED vs UNCLASSIFIED | ask | +10s | 89 / 138 | -0.18 vs -1.17 | 0.99 | 1.25 | 2.23 | 0.0260 | 0.4166 | 17 | [-0.24, 2.23] | 0.1090 |
| CONSUMED vs UNCLASSIFIED | ask | +30s | 88 / 138 | -0.15 vs -1.70 | 1.55 | 1.85 | 2.35 | 0.0186 | 0.3155 | 17 | [-0.67, 3.02] | 0.1760 |
| CONSUMED vs UNCLASSIFIED | ask | +60s | 88 / 138 | -0.95 vs -3.29 | 2.34 | 2.55 | 2.57 | 0.0103 | 0.1849 | 17 | [0.08, 4.63] | 0.0440 |
| CONSUMED vs UNCLASSIFIED | bid | +10s | 333 / 397 | 0.15 vs 0.02 | 0.13 | 0.62 | 0.60 | 0.5491 | 1.0000 | 25 | [-0.22, 0.56] | 0.4880 |
| CONSUMED vs UNCLASSIFIED | bid | +30s | 329 / 392 | -0.41 vs 0.35 | -0.76 | 1.11 | -1.92 | 0.0550 | 0.8244 | 25 | [-1.76, 0.38] | 0.1800 |
| CONSUMED vs UNCLASSIFIED | bid | +60s | 320 / 380 | -0.11 vs -0.16 | 0.05 | 1.42 | 0.10 | 0.9179 | 1.0000 | 25 | [-1.09, 1.40] | 0.8970 |
| CONSUMED vs ABSORBED | ask | +10s | 89 / 8 | -0.18 vs -0.88 | 0.70 | 2.94 | 0.66 | 0.5081 | 1.0000 | 14 | [-0.87, 2.84] | 0.5010 |
| CONSUMED vs ABSORBED | ask | +30s | 88 / 8 | -0.15 vs -1.62 | 1.48 | 4.74 | 0.87 | 0.3827 | 1.0000 | 14 | [-0.91, 3.38] | 0.2230 |
| CONSUMED vs ABSORBED | ask | +60s | 88 / 8 | -0.95 vs -2.38 | 1.42 | 5.67 | 0.70 | 0.4833 | 1.0000 | 14 | [-2.76, 5.85] | 0.5320 |
| CONSUMED vs ABSORBED | bid | +10s | 333 / 33 | 0.15 vs -0.27 | 0.42 | 1.17 | 1.02 | 0.3096 | 1.0000 | 24 | [-0.46, 1.13] | 0.3570 |
| CONSUMED vs ABSORBED | bid | +30s | 329 / 31 | -0.41 vs -0.16 | -0.25 | 2.88 | -0.24 | 0.8099 | 1.0000 | 24 | [-1.80, 1.52] | 0.7770 |
| CONSUMED vs ABSORBED | bid | +60s | 320 / 31 | -0.11 vs -0.87 | 0.76 | 3.43 | 0.62 | 0.5346 | 1.0000 | 24 | [-1.33, 2.70] | 0.4610 |
| ABSORBED vs UNCLASSIFIED | ask | +10s | 8 / 138 | -0.88 vs -1.17 | 0.30 | 2.88 | 0.29 | 0.7711 | 1.0000 | 16 | [-1.96, 2.22] | 0.7610 |
| ABSORBED vs UNCLASSIFIED | ask | +30s | 8 / 138 | -1.62 vs -1.70 | 0.07 | 4.61 | 0.05 | 0.9640 | 1.0000 | 16 | [-3.00, 2.96] | 0.9560 |
| ABSORBED vs UNCLASSIFIED | ask | +60s | 8 / 138 | -2.38 vs -3.29 | 0.91 | 5.50 | 0.47 | 0.6415 | 1.0000 | 16 | [-2.72, 4.21] | 0.7240 |
| ABSORBED vs UNCLASSIFIED | bid | +10s | 33 / 397 | -0.27 vs 0.02 | -0.29 | 1.17 | -0.70 | 0.4844 | 1.0000 | 25 | [-1.09, 0.80] | 0.5640 |
| ABSORBED vs UNCLASSIFIED | bid | +30s | 31 / 392 | -0.16 vs 0.35 | -0.51 | 2.88 | -0.50 | 0.6197 | 1.0000 | 25 | [-1.95, 0.73] | 0.3980 |
| ABSORBED vs UNCLASSIFIED | bid | +60s | 31 / 380 | -0.87 vs -0.16 | -0.71 | 3.42 | -0.58 | 0.5619 | 1.0000 | 25 | [-2.69, 1.54] | 0.5410 |

Censoring: 43 of 1008 episodes have no +60 s read (the tape ended first). Clustering: 1008 episodes sit on **42** distinct walls (24.0 episodes per wall),
and they also cluster in TIME, which the wall bootstrap does not undo. Both push the same way: the
effective n is smaller than the printed n, so every raw p above is optimistic and every CI is too narrow.

> **Not one of the 18 contrasts survives Holm at 0.05.** On this tape the outcome label does not separate the forward move of the mid, on either side, at any of the three horizons.


## 4. Is the TRADED vs CANCELLED split trustworthy?

| | |
|---|---|
| TradeRetention | 1500 s (the indicator's value) |
| episode duration | p50 3.0s, p90 3.2s, max 3.4s  (n 1008) — bounded by `T_episode` 3 s |
| episodes older than retention | 0 of 1008 (0.0%) |
| WALL age at resolution | p50 378.4s, p90 1046.9s, max 1301.6s  (n 1008) |
| walls older than retention | 0 of 1008 (0.0%) |

The two ages answer two different questions. `EpisodeClassifier` sums trades from the episode's own
open time, which `T_episode` caps at 3 s, so its Traded is safe from pruning. The **ledger** is the
exposed one: `ConsumptionTracker.Read` sums from the wall's ARM time, and any wall older than the
retention window has had the trades that ate it deleted before they could be counted.

*(No second pass: re-run with `--retention2 <sec>` to measure the bias directly.)*

**`W_assoc` (250 ms trade-association window, declared in `RadarConfig` and read by nothing).**
Attribution sums over the whole episode instead, so any episode containing BOTH a trade and a
cancellation can mis-split the two. How often that can bite:

| episode contains | n | share |
|---|---|---|
| trades AND cancellation (imprecision possible) | 324 | 32.1% |
| trades only | 343 | 34.0% |
| cancellation only | 100 | 9.9% |
| neither (no size change) | 241 | 23.9% |

