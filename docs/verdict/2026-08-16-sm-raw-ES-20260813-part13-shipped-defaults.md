# SizeMap verdict — sm-raw-ES-20260813-part13.smr

Generated 2026-08-16 10:26 by `verdict/`, replaying recorded tape through the shipped engine files.

| | |
|---|---|
| tape | sm-raw-ES-20260813-part13.smr  (ES, tick 0.25) |
| market time | 2026-08-13 09:30:00 -> 10:51:39  (81.7 min) |
| records | 1,846,741  (1,639,587 depth, 207,073 trades, 0 feed resets, 0 epoch breaks) |
| engine | 70,163 ticks at 20 Hz, 17.4 s of CPU (0.247 ms/tick) |
| config | K_mult 4.0, MinAbsSize 40, TradeRetention 30 s |
| resting size | p50 70, p85 90, p99 167, max 645  (16,920 sampled levels) |
| fidelity check | 84 classified episodes where this tool's captured Traded/Cancelled equal the engine's own, 0 mismatches |


## 1. Is the detector's output even plausible?

| | |
|---|---|
| walls detected | 9 distinct promotions (9 left memory) |
| wall lifetime | p50 502.0s, p90 759.4s, max 766.0s  (n 9) |
| wall peak size | p50 524.0 lots, p90 600.2 lots, max 649.0 lots  (n 9) |
| episodes | 134 resolved |

**Census** — sampled 16,920 times at 4 Hz. `L` = in the depth window,
`R` = remembered (tracked, outside it). Target band from visual-spec §5: **2-6 live, 10-25 remembered**.

| census | mean | p50 | p90 | max | in target band |
|---|---|---|---|---|---|
| L, all tracked | 0.26 | 0 | 1 | 2 | 4.1% |
| R, all tracked | 0.70 | 0 | 2 | 3 | 0.0% |
| L, ±25 ticks of mid | 0.26 | 0 | 1 | 2 | 4.1% |
| R, ±25 ticks of mid | 0.64 | 0 | 2 | 3 | 0.0% |

**Outcome split** (what the glyph layer would paint):

| outcome | n | share |
|---|---|---|
| ABSORBED | 8 | 6.0% |
| PULLED | 0 | 0.0% |
| CONSUMED | 76 | 56.7% |
| UNCLASSIFIED | 50 | 37.3% |

**Why a state does or does not fire.** `Consumed` is tested FIRST and wins on `Crossed` alone;
`Pulled` needs the level to empty while the quote has NOT crossed it.

| condition at resolution | n | share |
|---|---|---|
| the level emptied (`displayed == 0`) | 76 | 56.7% |
| the quote crossed the level (`Crossed`) | 76 | 56.7% |
| emptied **and** crossed -> `Consumed` preempts `Pulled` | 76 | 100.0% of emptied |
| emptied with the quote >= 2 ticks away | 0 | 0.0% of emptied |
| cancelled > traded (the `Pulled` size test) | 15 | 11.2% |

> **`PULLED` never fired once in 134 episodes.** A state that cannot be reached is not a calibration problem, it is dead code with a glyph in the spec.

> Outcome split is spread across buckets (56.7% in the largest, CONSUMED), so the
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
| **ABSORBED** | 8 | 0 | 0 | 8 | 100.0% |
| **PULLED** | 0 | 0 | 0 | 0 | n/a |
| **CONSUMED** | 34 | 0 | 42 | 76 | 55.3% |
| **UNCLASSIFIED** | 43 | 0 | 7 | 50 | n/a |

Overall agreement, over the **84** episodes the classifier actually labelled: **59.5%**.
Over all 134 resolved episodes (counting the classifier's refusals as disagreement): **37.3%**.

**Two caveats on the null, both in its favour and both stated rather than fixed.**

1. `traded >= drop` is trivially TRUE when nothing dropped: **40 of 134** (29.9%) episodes resolved with `drop == 0`, and the null calls every one of them ABSORBED.
   On the **94** episodes where the wall actually lost size, agreement over the classifier's own labels is **55.8%** (43/77).
2. Neither model emits PULLED on this tape: the null needs the level to empty with the quote >= 2 ticks away,
   and episodes are only ever opened on walls AT the inside (`D_approach = 1`), where that distance is 1 tick.

> **Verdict:** the null model reproduces only **59.5%** of the labels, so the classifier is genuinely doing something different. Whether that difference is *better* is question 3, not this one.


## 3. Does the label carry forward information?

For every resolved episode: the mid at resolution, and the mid at +10 s / +30 s / +60 s of MARKET time.
`through` = the mid is on the far side of the wall price (above an ask wall, below a bid wall).
`move` = signed ticks in the wall's through-direction, so positive always means *the wall gave way*.

**Both sides**

| outcome | n | through at t0 | P(through) +10s | P(through) +30s | P(through) +60s | mean move +10s | mean move +30s | mean move +60s | median +10s | median +30s | median +60s |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ABSORBED | 8 | 0.0% | 37.5% (n8) | 12.5% (n8) | 12.5% (n8) | 0.00 | -1.75 | -4.62 | -0.50 | -2.50 | -6.00 |
| PULLED | 0 | - | - | - | - | - | - | - | - | - | - |
| CONSUMED | 76 | 90.8% | 69.7% (n76) | 63.2% (n76) | 63.2% (n76) | 0.81 | 2.07 | 3.62 | 1.00 | 1.00 | 1.00 |
| UNCLASSIFIED | 50 | 0.0% | 32.0% (n50) | 34.0% (n50) | 40.0% (n50) | -0.23 | -0.29 | -0.33 | 0.00 | 0.00 | 0.00 |

**Bid walls (support)**

| outcome | n | through at t0 | P(through) +10s | P(through) +30s | P(through) +60s | mean move +10s | mean move +30s | mean move +60s | median +10s | median +30s | median +60s |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ABSORBED | 5 | 0.0% | 20.0% (n5) | 0.0% (n5) | 0.0% (n5) | -0.60 | -4.00 | -6.80 | -1.00 | -5.00 | -7.00 |
| PULLED | 0 | - | - | - | - | - | - | - | - | - | - |
| CONSUMED | 15 | 100.0% | 33.3% (n15) | 33.3% (n15) | 40.0% (n15) | -0.47 | -1.93 | 1.33 | -1.00 | -3.00 | -2.00 |
| UNCLASSIFIED | 25 | 0.0% | 20.0% (n25) | 12.0% (n25) | 24.0% (n25) | -0.68 | -2.40 | -3.44 | -1.00 | -2.00 | -2.00 |

**Ask walls (resistance)**

| outcome | n | through at t0 | P(through) +10s | P(through) +30s | P(through) +60s | mean move +10s | mean move +30s | mean move +60s | median +10s | median +30s | median +60s |
|---|---|---|---|---|---|---|---|---|---|---|---|
| ABSORBED | 3 | 0.0% | 66.7% (n3) | 33.3% (n3) | 33.3% (n3) | 1.00 | 2.00 | -1.00 | 1.00 | 1.00 | -1.00 |
| PULLED | 0 | - | - | - | - | - | - | - | - | - | - |
| CONSUMED | 61 | 88.5% | 78.7% (n61) | 70.5% (n61) | 68.9% (n61) | 1.12 | 3.06 | 4.19 | 1.00 | 2.00 | 1.00 |
| UNCLASSIFIED | 25 | 0.0% | 44.0% (n25) | 56.0% (n25) | 56.0% (n25) | 0.22 | 1.82 | 2.78 | 1.00 | 2.00 | 4.00 |

**Drift control — read every `move` above against this row.** The same measurement with no episode in it:
the mean forward move of the mid from every one of the 16,919 census samples on this tape.
An ask wall's move IS this number when the label carries nothing; a bid wall's is its negative.

| | +10s | +30s | +60s |
|---|---|---|---|
| unconditional mean move, ticks (price up = +) | 0.27 | 0.85 | 1.70 |

**Significance** — CONSUMED against each of the others, at all three horizons. `move` is Welch t with a
normal-tail p; `P(through)` is a two-proportion z, with the smallest difference these n could detect at 80% power.

| comparison | horizon | n / n | mean move | t | p | P(through) | z | p | 80%-power MDE |
|---|---|---|---|---|---|---|---|---|---|
| CONSUMED vs ABSORBED | +10s | 76 / 8 | 0.81 vs 0.00 | 1.31 | 0.1899 | 69.7% vs 37.5% | 1.84 | 0.0658 | 49.1 pts |
| CONSUMED vs ABSORBED | +30s | 76 / 8 | 2.07 vs -1.75 | 1.90 | 0.0572 | 63.2% vs 12.5% | 2.76 | 0.0057 | 51.3 pts |
| CONSUMED vs ABSORBED | +60s | 76 / 8 | 3.62 vs -4.62 | 5.28 | <0.0001 | 63.2% vs 12.5% | 2.76 | 0.0057 | 51.3 pts |
| CONSUMED vs PULLED | +10s | 76 / 0 | 0.81 vs - | - | - | 69.7% vs n/a | - | - | - pts |
| CONSUMED vs PULLED | +30s | 76 / 0 | 2.07 vs - | - | - | 63.2% vs n/a | - | - | - pts |
| CONSUMED vs PULLED | +60s | 76 / 0 | 3.62 vs - | - | - | 63.2% vs n/a | - | - | - pts |
| CONSUMED vs UNCLASSIFIED | +10s | 76 / 50 | 0.81 vs -0.23 | 2.28 | 0.0228 | 69.7% vs 32.0% | 4.16 | <0.0001 | 25.4 pts |
| CONSUMED vs UNCLASSIFIED | +30s | 76 / 50 | 2.07 vs -0.29 | 2.39 | 0.0168 | 63.2% vs 34.0% | 3.20 | 0.0014 | 25.5 pts |
| CONSUMED vs UNCLASSIFIED | +60s | 76 / 50 | 3.62 vs -0.33 | 2.90 | 0.0038 | 63.2% vs 40.0% | 2.55 | 0.0107 | 25.4 pts |

> **Read the `move` columns, not the `P(through)` ones.** CONSUMED is DEFINED as "the quote crossed the level",
> so it starts already through and the others start at 0% by construction. Only the move from the mid AT
> resolution is a fair forward test, and it is the one that has to be significant for the label to be worth painting.

Censoring: 0 of 134 episodes have no +60 s read (the tape ended first).
> **Underpowered.** The smallest outcome bucket holds 8 episodes. Percentages computed on that are noise with a decimal point; the p-values above are reported for completeness, not for belief. Episodes are also NOT independent — they cluster in time and several can sit on the same wall — so even a large n overstates the effective sample.


## 4. Is the TRADED vs CANCELLED split trustworthy?

| | |
|---|---|
| TradeRetention | 30 s (the indicator's value) |
| episode duration | p50 1.8s, p90 3.1s, max 3.3s  (n 134) — bounded by `T_episode` 3 s |
| episodes older than retention | 0 of 134 (0.0%) |
| WALL age at resolution | p50 228.6s, p90 335.3s, max 457.7s  (n 134) |
| walls older than retention | 119 of 134 (88.8%) |

The two ages answer two different questions. `EpisodeClassifier` sums trades from the episode's own
open time, which `T_episode` caps at 3 s, so its Traded is safe from pruning. The **ledger** is the
exposed one: `ConsumptionTracker.Read` sums from the wall's ARM time, and any wall older than the
retention window has had the trades that ate it deleted before they could be counted.

**Re-run at TradeRetention 1500 s** (longest wall lifetime on this tape: 766 s).

| | |
|---|---|
| episodes matched 1:1 | 134 of 134 (episode boundaries must not move; they did not) |
| outcome flips | 0 — retention does not reach `EpisodeClassifier`, as predicted above |
| mean TradeBackedFraction | 0.463 -> 0.880  (mean shift 0.417) |
| median TradeBackedFraction | 0.296 -> 1.000 |
| TBF moved by > 0.05 | 92 of 134 (68.7%) |
| **ledger glyph changes** | 74 of 134 (55.2%) cross a §4 TBF boundary (0.15 / 0.85) and would be painted as a DIFFERENT death |

**`W_assoc` (250 ms trade-association window, declared in `RadarConfig` and read by nothing).**
Attribution sums over the whole episode instead, so any episode containing BOTH a trade and a
cancellation can mis-split the two. How often that can bite:

| episode contains | n | share |
|---|---|---|
| trades AND cancellation (imprecision possible) | 47 | 35.1% |
| trades only | 76 | 56.7% |
| cancellation only | 2 | 1.5% |
| neither (no size change) | 9 | 6.7% |

