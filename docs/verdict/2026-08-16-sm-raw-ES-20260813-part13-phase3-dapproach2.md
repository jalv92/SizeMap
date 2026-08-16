# SizeMap verdict — sm-raw-ES-20260813-part13.smr

Generated 2026-08-16 11:35 by `verdict/`, replaying recorded tape through the shipped engine files.

| | |
|---|---|
| tape | sm-raw-ES-20260813-part13.smr  (ES, tick 0.25) |
| market time | 2026-08-13 09:30:00 -> 10:51:39  (81.7 min) |
| records | 1,846,741  (1,639,587 depth, 207,073 trades, 0 feed resets, 0 epoch breaks) |
| engine | 18,107 ticks at 4 Hz, 4.1 s of CPU (0.227 ms/tick) |
| config | K_mult 1.5, MinAbsSize 40, TradeRetention 1500 s |
| resting size | p50 70, p85 90, p99 167, max 646  (18,104 sampled levels) |
| fidelity check | 899 classified episodes where this tool's captured Traded/Cancelled equal the engine's own, 0 mismatches |


## 1. Is the detector's output even plausible?

| | |
|---|---|
| walls detected | 129 distinct promotions (125 left memory) |
| wall lifetime | p50 487.1s, p90 861.1s, max 1419.8s  (n 129) |
| wall peak size | p50 122.0 lots, p90 206.4 lots, max 649.0 lots  (n 129) |
| episodes | 2067 resolved |

**Census** — sampled 18,107 times at 4 Hz, through the RENDERER's own predicate:
`InWindow` -> **solid** groove; else `Confidence >= 0.25` -> **hollow** rule; else **undrawn**. The third bucket is
the one Phase 2 had no name for: tracked, counted as "remembered", and never on the chart. Target band from
visual-spec §5: **2-6 solid, 10-25 hollow**.

| census | mean | p50 | p90 | max | in target band |
|---|---|---|---|---|---|
| SOLID (drawn, in window) | 4.18 | 4 | 8 | 10 | 62.5% |
| HOLLOW (drawn, remembered) | 1.08 | 1 | 3 | 7 | 0.0% |
| UNDRAWN (tracked, invisible) | 9.71 | 9 | 18 | 25 | n/a |
| SOLID, ±25 ticks of mid | 4.18 | 4 | 8 | 10 | 62.5% |
| HOLLOW, ±25 ticks of mid | 1.07 | 1 | 3 | 7 | 0.0% |

Draw caps (§4: 12 solid, 24 hollow) bit in 0.0% / 0.0% of samples.

**Outcome split** (what the glyph layer would paint):

| outcome | n | share |
|---|---|---|
| ABSORBED | 105 | 5.1% |
| PULLED | 0 | 0.0% |
| CONSUMED | 794 | 38.4% |
| UNCLASSIFIED | 1168 | 56.5% |

**Why a state does or does not fire.** `Consumed` is tested FIRST and wins on `Crossed` alone;
`Pulled` needs the level to empty while the quote has NOT crossed it.

| condition at resolution | n | share |
|---|---|---|
| the level emptied (`displayed == 0`) | 794 | 38.4% |
| the quote crossed the level (`Crossed`) | 794 | 38.4% |
| emptied **and** crossed -> `Consumed` preempts `Pulled` | 794 | 100.0% of emptied |
| emptied with the quote >= 2 ticks away | 1 | 0.1% of emptied |
| cancelled > traded (the `Pulled` size test) | 420 | 20.3% |

> **`PULLED` never fired once in 2067 episodes.** A state that cannot be reached is not a calibration problem, it is dead code with a glyph in the spec.

> Outcome split is spread across buckets (56.5% in the largest, UNCLASSIFIED), so the
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
| **ABSORBED** | 105 | 0 | 0 | 105 | 100.0% |
| **PULLED** | 0 | 0 | 0 | 0 | n/a |
| **CONSUMED** | 337 | 1 | 456 | 794 | 57.4% |
| **UNCLASSIFIED** | 856 | 0 | 312 | 1168 | n/a |

Overall agreement, over the **899** episodes the classifier actually labelled: **62.4%**.
Over all 2067 resolved episodes (counting the classifier's refusals as disagreement): **27.1%**.

**Two caveats on the null, both in its favour and both stated rather than fixed.**

1. `traded >= drop` is trivially TRUE when nothing dropped: **816 of 2067** (39.5%) episodes resolved with `drop == 0`, and the null calls every one of them ABSORBED.
   On the **1251** episodes where the wall actually lost size, agreement over the classifier's own labels is **58.4%** (474/812).
2. Neither model emits PULLED on this tape: the null needs the level to empty with the quote >= 2 ticks away,
   and episodes are only ever opened on walls AT the inside (`D_approach = 1`), where that distance is 1 tick.

> **Verdict:** the null model reproduces only **62.4%** of the labels, so the classifier is genuinely doing something different. Whether that difference is *better* is question 3, not this one.


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
every one of the 18,106 census samples on this tape, in the ASK-wall sign convention (price up = +).
An ask wall's move IS this number when the label carries nothing; a bid wall's is its negative. The
windows overlap, so this row's own effective n is far below the sample count — it sets the CENTRE that
`excess` is measured from, and no test below depends on its precision (a same-side contrast cancels it).

| | +10s | +30s | +60s |
|---|---|---|---|
| unconditional mean move, ticks | 0.28 | 0.86 | 1.73 |

**Ask walls (resistance)** — `excess` = mean move minus this side's drift null (0.28 / 0.86 / 1.73 ticks).

| outcome | n | walls | ep/wall | mean +10s | excess | mean +30s | excess | mean +60s | excess |
|---|---|---|---|---|---|---|---|---|---|
| ABSORBED | 68 | 33 | 2.1 | 0.43 (n68) | 0.16 | 1.82 (n68) | 0.95 | 2.29 (n68) | 0.56 |
| PULLED | 0 | - | - | - | - | - | - | - | - |
| CONSUMED | 569 | 72 | 7.9 | 0.36 (n569) | 0.08 | 1.48 (n566) | 0.62 | 2.74 (n566) | 1.01 |
| UNCLASSIFIED | 713 | 62 | 11.5 | 0.41 (n713) | 0.14 | 1.09 (n712) | 0.23 | 1.90 (n710) | 0.17 |

**Bid walls (support)** — `excess` = mean move minus this side's drift null (-0.28 / -0.86 / -1.73 ticks).

| outcome | n | walls | ep/wall | mean +10s | excess | mean +30s | excess | mean +60s | excess |
|---|---|---|---|---|---|---|---|---|---|
| ABSORBED | 37 | 17 | 2.2 | -0.81 (n37) | -0.53 | -2.11 (n37) | -1.24 | -3.12 (n37) | -1.39 |
| PULLED | 0 | - | - | - | - | - | - | - | - |
| CONSUMED | 225 | 32 | 7.0 | -0.48 (n225) | -0.20 | -1.10 (n225) | -0.24 | -1.07 (n225) | 0.66 |
| UNCLASSIFIED | 455 | 43 | 10.6 | -0.37 (n455) | -0.09 | -0.98 (n455) | -0.11 | -1.45 (n452) | 0.28 |

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
| CONSUMED vs UNCLASSIFIED | ask | +10s | 569 / 713 | 0.36 vs 0.41 | -0.05 | 0.41 | -0.36 | 0.7181 | 1.0000 | 73 | [-0.43, 0.38] | 0.7970 |
| CONSUMED vs UNCLASSIFIED | ask | +30s | 566 / 712 | 1.48 vs 1.09 | 0.39 | 0.76 | 1.43 | 0.1536 | 1.0000 | 73 | [-0.45, 1.34] | 0.3960 |
| CONSUMED vs UNCLASSIFIED | ask | +60s | 566 / 710 | 2.74 vs 1.90 | 0.84 | 1.13 | 2.08 | 0.0374 | 0.6732 | 73 | [-0.38, 2.20] | 0.1930 |
| CONSUMED vs UNCLASSIFIED | bid | +10s | 225 / 455 | -0.48 vs -0.37 | -0.11 | 0.61 | -0.50 | 0.6186 | 1.0000 | 44 | [-0.56, 0.47] | 0.6740 |
| CONSUMED vs UNCLASSIFIED | bid | +30s | 225 / 455 | -1.10 vs -0.98 | -0.12 | 1.17 | -0.29 | 0.7702 | 1.0000 | 44 | [-1.42, 1.22] | 0.8730 |
| CONSUMED vs UNCLASSIFIED | bid | +60s | 225 / 452 | -1.07 vs -1.45 | 0.38 | 1.85 | 0.57 | 0.5654 | 1.0000 | 44 | [-1.59, 2.61] | 0.7040 |
| CONSUMED vs ABSORBED | ask | +10s | 569 / 68 | 0.36 vs 0.43 | -0.07 | 0.77 | -0.27 | 0.7885 | 1.0000 | 72 | [-0.56, 0.51] | 0.8130 |
| CONSUMED vs ABSORBED | ask | +30s | 566 / 68 | 1.48 vs 1.82 | -0.34 | 1.67 | -0.56 | 0.5738 | 1.0000 | 72 | [-1.50, 0.83] | 0.5830 |
| CONSUMED vs ABSORBED | ask | +60s | 566 / 68 | 2.74 vs 2.29 | 0.45 | 2.75 | 0.46 | 0.6477 | 1.0000 | 72 | [-1.36, 2.24] | 0.5980 |
| CONSUMED vs ABSORBED | bid | +10s | 225 / 37 | -0.48 vs -0.81 | 0.34 | 1.13 | 0.83 | 0.4072 | 1.0000 | 33 | [-0.37, 1.17] | 0.3620 |
| CONSUMED vs ABSORBED | bid | +30s | 225 / 37 | -1.10 vs -2.11 | 1.01 | 2.48 | 1.14 | 0.2557 | 1.0000 | 33 | [-0.39, 2.66] | 0.1580 |
| CONSUMED vs ABSORBED | bid | +60s | 225 / 37 | -1.07 vs -3.12 | 2.05 | 3.56 | 1.62 | 0.1063 | 1.0000 | 33 | [-0.37, 4.94] | 0.1070 |
| ABSORBED vs UNCLASSIFIED | ask | +10s | 68 / 713 | 0.43 vs 0.41 | 0.02 | 0.74 | 0.08 | 0.9373 | 1.0000 | 63 | [-0.53, 0.55] | 0.9700 |
| ABSORBED vs UNCLASSIFIED | ask | +30s | 68 / 712 | 1.82 vs 1.09 | 0.72 | 1.64 | 1.24 | 0.2156 | 1.0000 | 63 | [-0.27, 1.88] | 0.1600 |
| ABSORBED vs UNCLASSIFIED | ask | +60s | 68 / 710 | 2.29 vs 1.90 | 0.39 | 2.71 | 0.40 | 0.6868 | 1.0000 | 63 | [-1.25, 2.28] | 0.6630 |
| ABSORBED vs UNCLASSIFIED | bid | +10s | 37 / 455 | -0.81 vs -0.37 | -0.44 | 1.08 | -1.15 | 0.2482 | 1.0000 | 44 | [-1.40, 0.45] | 0.2920 |
| ABSORBED vs UNCLASSIFIED | bid | +30s | 37 / 455 | -2.11 vs -0.98 | -1.13 | 2.40 | -1.32 | 0.1875 | 1.0000 | 44 | [-2.63, 0.41] | 0.1310 |
| ABSORBED vs UNCLASSIFIED | bid | +60s | 37 / 452 | -3.12 vs -1.45 | -1.67 | 3.41 | -1.37 | 0.1694 | 1.0000 | 44 | [-4.17, 0.37] | 0.0990 |

Censoring: 9 of 2067 episodes have no +60 s read (the tape ended first). Clustering: 2067 episodes sit on **117** distinct walls (17.7 episodes per wall),
and they also cluster in TIME, which the wall bootstrap does not undo. Both push the same way: the
effective n is smaller than the printed n, so every raw p above is optimistic and every CI is too narrow.

> **Not one of the 18 contrasts survives Holm at 0.05.** On this tape the outcome label does not separate the forward move of the mid, on either side, at any of the three horizons.


## 4. Is the TRADED vs CANCELLED split trustworthy?

| | |
|---|---|
| TradeRetention | 1500 s (the indicator's value) |
| episode duration | p50 3.1s, p90 3.2s, max 3.4s  (n 2067) — bounded by `T_episode` 3 s |
| episodes older than retention | 0 of 2067 (0.0%) |
| WALL age at resolution | p50 190.3s, p90 522.5s, max 1046.8s  (n 2067) |
| walls older than retention | 0 of 2067 (0.0%) |

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
| trades AND cancellation (imprecision possible) | 586 | 28.4% |
| trades only | 851 | 41.2% |
| cancellation only | 182 | 8.8% |
| neither (no size change) | 448 | 21.7% |

