# SizeMap verdict — sm-raw-ES-20260814.smr

Generated 2026-08-16 11:35 by `verdict/`, replaying recorded tape through the shipped engine files.

| | |
|---|---|
| tape | sm-raw-ES-20260814.smr  (ES, tick 0.25) |
| market time | 2026-08-14 09:30:00 -> 14:52:21  (322.4 min) |
| records | 4,049,914  (3,624,949 depth, 424,643 trades, 0 feed resets, 0 epoch breaks) |
| engine | 65,727 ticks at 4 Hz, 8.6 s of CPU (0.131 ms/tick) |
| config | K_mult 1.5, MinAbsSize 40, TradeRetention 1500 s |
| resting size | p50 86, p85 103, p99 179, max 767  (65,731 sampled levels) |
| fidelity check | 1895 classified episodes where this tool's captured Traded/Cancelled equal the engine's own, 0 mismatches |


## 1. Is the detector's output even plausible?

| | |
|---|---|
| walls detected | 177 distinct promotions (173 left memory) |
| wall lifetime | p50 681.5s, p90 1751.2s, max 3393.2s  (n 177) |
| wall peak size | p50 143.0 lots, p90 241.0 lots, max 769.0 lots  (n 177) |
| episodes | 5266 resolved |

**Census** — sampled 65,727 times at 4 Hz, through the RENDERER's own predicate:
`InWindow` -> **solid** groove; else `Confidence >= 0.25` -> **hollow** rule; else **undrawn**. The third bucket is
the one Phase 2 had no name for: tracked, counted as "remembered", and never on the chart. Target band from
visual-spec §5: **2-6 solid, 10-25 hollow**.

| census | mean | p50 | p90 | max | in target band |
|---|---|---|---|---|---|
| SOLID (drawn, in window) | 3.39 | 3 | 6 | 11 | 67.5% |
| HOLLOW (drawn, remembered) | 0.80 | 1 | 2 | 8 | 0.0% |
| UNDRAWN (tracked, invisible) | 4.57 | 4 | 9 | 23 | n/a |
| SOLID, ±25 ticks of mid | 3.39 | 3 | 6 | 11 | 67.5% |
| HOLLOW, ±25 ticks of mid | 0.80 | 1 | 2 | 8 | 0.0% |

Draw caps (§4: 12 solid, 24 hollow) bit in 0.0% / 0.0% of samples.

**Outcome split** (what the glyph layer would paint):

| outcome | n | share |
|---|---|---|
| ABSORBED | 175 | 3.3% |
| PULLED | 1 | 0.0% |
| CONSUMED | 1719 | 32.6% |
| UNCLASSIFIED | 3371 | 64.0% |

**Why a state does or does not fire.** `Consumed` is tested FIRST and wins on `Crossed` alone;
`Pulled` needs the level to empty while the quote has NOT crossed it.

| condition at resolution | n | share |
|---|---|---|
| the level emptied (`displayed == 0`) | 1720 | 32.7% |
| the quote crossed the level (`Crossed`) | 1719 | 32.6% |
| emptied **and** crossed -> `Consumed` preempts `Pulled` | 1719 | 99.9% of emptied |
| emptied with the quote >= 2 ticks away | 8 | 0.5% of emptied |
| cancelled > traded (the `Pulled` size test) | 1345 | 25.5% |


> Outcome split is spread across buckets (64.0% in the largest, UNCLASSIFIED), so the
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
| **ABSORBED** | 175 | 0 | 0 | 175 | 100.0% |
| **PULLED** | 0 | 1 | 0 | 1 | 100.0% |
| **CONSUMED** | 632 | 7 | 1080 | 1719 | 62.8% |
| **UNCLASSIFIED** | 2303 | 0 | 1068 | 3371 | n/a |

Overall agreement, over the **1895** episodes the classifier actually labelled: **66.3%**.
Over all 5266 resolved episodes (counting the classifier's refusals as disagreement): **23.9%**.

**Two caveats on the null, both in its favour and both stated rather than fixed.**

1. `traded >= drop` is trivially TRUE when nothing dropped: **2242 of 5266** (42.6%) episodes resolved with `drop == 0`, and the null calls every one of them ABSORBED.
   On the **3024** episodes where the wall actually lost size, agreement over the classifier's own labels is **63.4%** (1106/1745).
2. Neither model emits PULLED on this tape: the null needs the level to empty with the quote >= 2 ticks away,
   and episodes are only ever opened on walls AT the inside (`D_approach = 1`), where that distance is 1 tick.

> **Verdict:** the null model reproduces only **66.3%** of the labels, so the classifier is genuinely doing something different. Whether that difference is *better* is question 3, not this one.


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
every one of the 65,726 census samples on this tape, in the ASK-wall sign convention (price up = +).
An ask wall's move IS this number when the label carries nothing; a bid wall's is its negative. The
windows overlap, so this row's own effective n is far below the sample count — it sets the CENTRE that
`excess` is measured from, and no test below depends on its precision (a same-side contrast cancels it).

| | +10s | +30s | +60s |
|---|---|---|---|
| unconditional mean move, ticks | -0.04 | -0.13 | -0.23 |

**Ask walls (resistance)** — `excess` = mean move minus this side's drift null (-0.04 / -0.13 / -0.23 ticks).

| outcome | n | walls | ep/wall | mean +10s | excess | mean +30s | excess | mean +60s | excess |
|---|---|---|---|---|---|---|---|---|---|
| ABSORBED | 82 | 37 | 2.2 | 0.03 (n82) | 0.07 | 0.48 (n82) | 0.60 | 0.34 (n82) | 0.56 |
| PULLED | 0 | - | - | - | - | - | - | - | - |
| CONSUMED | 729 | 57 | 12.8 | -0.15 (n729) | -0.11 | -0.47 (n726) | -0.34 | -0.88 (n726) | -0.65 |
| UNCLASSIFIED | 1548 | 64 | 24.2 | -0.00 (n1548) | 0.04 | -0.00 (n1546) | 0.12 | -0.24 (n1544) | -0.01 |

**Bid walls (support)** — `excess` = mean move minus this side's drift null (0.04 / 0.13 / 0.23 ticks).

| outcome | n | walls | ep/wall | mean +10s | excess | mean +30s | excess | mean +60s | excess |
|---|---|---|---|---|---|---|---|---|---|
| ABSORBED | 93 | 41 | 2.3 | -0.08 (n93) | -0.12 | 0.26 (n93) | 0.13 | 0.85 (n93) | 0.62 |
| PULLED | 1 | 1 | 1.0 | -3.00 (n1) | -3.04 | 0.00 (n1) | -0.13 | -7.00 (n1) | -7.23 |
| CONSUMED | 990 | 75 | 13.2 | -0.25 (n990) | -0.29 | -0.20 (n990) | -0.33 | -0.03 (n990) | -0.25 |
| UNCLASSIFIED | 1823 | 80 | 22.8 | 0.11 (n1823) | 0.07 | 0.25 (n1823) | 0.13 | 0.38 (n1823) | 0.15 |

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
| CONSUMED vs UNCLASSIFIED | ask | +10s | 729 / 1548 | -0.15 vs -0.00 | -0.15 | 0.27 | -1.51 | 0.1304 | 1.0000 | 64 | [-0.40, 0.10] | 0.2520 |
| CONSUMED vs UNCLASSIFIED | ask | +30s | 726 / 1546 | -0.47 vs -0.00 | -0.46 | 0.47 | -2.76 | 0.0057 | 0.0917 | 64 | [-0.98, 0.05] | 0.0730 |
| CONSUMED vs UNCLASSIFIED | ask | +60s | 726 / 1544 | -0.88 vs -0.24 | -0.64 | 0.70 | -2.59 | 0.0097 | 0.1455 | 64 | [-1.52, 0.18] | 0.1430 |
| CONSUMED vs UNCLASSIFIED | bid | +10s | 990 / 1823 | -0.25 vs 0.11 | -0.36 | 0.27 | -3.70 | 0.0002 | 0.0038 ** | 80 | [-0.59, -0.15] | 0.0010 |
| CONSUMED vs UNCLASSIFIED | bid | +30s | 990 / 1823 | -0.20 vs 0.25 | -0.45 | 0.44 | -2.88 | 0.0040 | 0.0677 | 80 | [-0.86, -0.08] | 0.0140 |
| CONSUMED vs UNCLASSIFIED | bid | +60s | 990 / 1823 | -0.03 vs 0.38 | -0.41 | 0.61 | -1.86 | 0.0634 | 0.7953 | 80 | [-1.18, 0.30] | 0.2660 |
| CONSUMED vs ABSORBED | ask | +10s | 729 / 82 | -0.15 vs 0.03 | -0.18 | 0.89 | -0.57 | 0.5687 | 1.0000 | 57 | [-0.88, 0.49] | 0.6010 |
| CONSUMED vs ABSORBED | ask | +30s | 726 / 82 | -0.47 vs 0.48 | -0.94 | 1.41 | -1.87 | 0.0612 | 0.7953 | 57 | [-1.97, 0.00] | 0.0520 |
| CONSUMED vs ABSORBED | ask | +60s | 726 / 82 | -0.88 vs 0.34 | -1.22 | 1.75 | -1.94 | 0.0524 | 0.7342 | 57 | [-2.68, 0.16] | 0.0860 |
| CONSUMED vs ABSORBED | bid | +10s | 990 / 93 | -0.25 vs -0.08 | -0.17 | 0.91 | -0.52 | 0.6065 | 1.0000 | 75 | [-0.77, 0.43] | 0.5760 |
| CONSUMED vs ABSORBED | bid | +30s | 990 / 93 | -0.20 vs 0.26 | -0.46 | 1.29 | -1.00 | 0.3171 | 1.0000 | 75 | [-1.33, 0.36] | 0.2600 |
| CONSUMED vs ABSORBED | bid | +60s | 990 / 93 | -0.03 vs 0.85 | -0.88 | 1.62 | -1.51 | 0.1299 | 1.0000 | 75 | [-2.01, 0.26] | 0.1420 |
| ABSORBED vs UNCLASSIFIED | ask | +10s | 82 / 1548 | 0.03 vs -0.00 | 0.03 | 0.87 | 0.10 | 0.9170 | 1.0000 | 64 | [-0.67, 0.73] | 0.9820 |
| ABSORBED vs UNCLASSIFIED | ask | +30s | 82 / 1546 | 0.48 vs -0.00 | 0.48 | 1.38 | 0.98 | 0.3293 | 1.0000 | 64 | [-0.62, 1.46] | 0.3770 |
| ABSORBED vs UNCLASSIFIED | ask | +60s | 82 / 1544 | 0.34 vs -0.24 | 0.57 | 1.69 | 0.95 | 0.3441 | 1.0000 | 64 | [-0.65, 1.68] | 0.3680 |
| ABSORBED vs UNCLASSIFIED | bid | +10s | 93 / 1823 | -0.08 vs 0.11 | -0.19 | 0.89 | -0.60 | 0.5480 | 1.0000 | 80 | [-0.84, 0.42] | 0.5500 |
| ABSORBED vs UNCLASSIFIED | bid | +30s | 93 / 1823 | 0.26 vs 0.25 | 0.01 | 1.25 | 0.02 | 0.9874 | 1.0000 | 80 | [-0.94, 0.89] | 0.9720 |
| ABSORBED vs UNCLASSIFIED | bid | +60s | 93 / 1823 | 0.85 vs 0.38 | 0.47 | 1.57 | 0.84 | 0.4014 | 1.0000 | 80 | [-0.69, 1.50] | 0.4060 |

Censoring: 7 of 5266 episodes have no +60 s read (the tape ended first). Clustering: 5266 episodes sit on **144** distinct walls (36.6 episodes per wall),
and they also cluster in TIME, which the wall bootstrap does not undo. Both push the same way: the
effective n is smaller than the printed n, so every raw p above is optimistic and every CI is too narrow.

> **1 of 18 contrasts survive BOTH Holm and the wall bootstrap.** Read the sign, the side and the horizon: a result that flips sign between sides or tapes is a session, not a signal.


## 4. Is the TRADED vs CANCELLED split trustworthy?

| | |
|---|---|
| TradeRetention | 1500 s (the indicator's value) |
| episode duration | p50 3.1s, p90 3.2s, max 3.7s  (n 5266) — bounded by `T_episode` 3 s |
| episodes older than retention | 0 of 5266 (0.0%) |
| WALL age at resolution | p50 438.6s, p90 1548.1s, max 2990.2s  (n 5266) |
| walls older than retention | 571 of 5266 (10.8%) |

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
| trades AND cancellation (imprecision possible) | 1488 | 28.3% |
| trades only | 1685 | 32.0% |
| cancellation only | 666 | 12.6% |
| neither (no size change) | 1427 | 27.1% |

