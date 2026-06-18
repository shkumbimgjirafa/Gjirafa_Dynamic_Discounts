# Design: Intent-Based Outcome Tracking

> Status: **Phases 1–3 implemented & reviewed** · Date: 2026-06-15 · Attribution method: **simple pre/post** (per-SKU, own before-vs-after)

> **Phase 1 — as built** (deltas from this design, after the adversarial review): `PreMarginPct`/`PreGrossProfitPerDay` are nullable (cost may be absent); the two margin-% columns are `decimal(18,4)` not `(9,4)` (a loss-making SKU's recomputed margin can be a large negative number); `ProposedPriceId` carries a **unique filtered** index (one outcome per proposal, DB-enforced); `EvaluateAsync` detaches half-built rows on failure so a shared-scope save can't flush them; it runs at the end of `PricingRunOrchestrator.ExecuteRunAsync` inside a try/catch that never fails the run. Migration `20260615090616_AddPriceChangeOutcome`; all tests green.

> **Phases 2 & 3 — as built**: the dashboard "Did the bet pay off?" section ([HomeController.cs](../PricingTool.Web/Controllers/HomeController.cs) + [Index.cshtml](../PricingTool.Web/Views/Home/Index.cshtml)) shows three intent tiles — margin captures judged on €/day gross profit, volume plays & clearance on units/day — with win-rates plus *Biggest wins* / *Worst backfires* tables linking to the SKU drilldown, and an empty-state until outcomes mature. Profit deltas read "—" (not fabricated) when a SKU lacks cost on one side. Demo: [DemoOutcomeSeeder](../PricingTool.Data/Services/DemoOutcomeSeeder.cs) (run-once, idempotent, **non-fatal to startup**) runs a pricing run, marks an intent-balanced slice Approved→Pushed dated 9–22 days back, then grades them against the 35-day backfill — illustrative only. Also corrected: the outcome records the **live** price before/after (`CurrentPrice` → `ProposedPriceValue`), since `ChangePct` is measured against the live price, not shelf. 122 unit/integration tests green; the app must restart to apply the migration + seed the demo.

## 1. Problem

The Impact Dashboard today judges every SKU by one blended yardstick — margin lift + volume vs. the earliest-week baseline, averaged across all ~360 SKUs ([HomeController.cs:25-57](../PricingTool.Web/Controllers/HomeController.cs#L25)). But a price **increase** and a price **decrease** are different bets with different definitions of success. Blending them hides both outcomes: a successful margin grab and a failed clearance markdown cancel out to "flat" — which is exactly what the top cards read today.

We want to **measure each price change against the metric its own intent was chasing.**

## 2. Principle

A price move is a bet. Judge it by its thesis, not by a one-size metric. The common referee for all of them is **gross profit = margin € × units**.

| Intent | Direction | The bet | "Win" condition | Risk to watch |
|---|---|---|---|---|
| **Margin capture** | price ↑ | "We can charge more without losing the sales we have" | gross profit/day holds or rises | volume craters → profit drops despite higher margin % |
| **Volume stimulation** | price ↓ (healthy item) | "A lower price moves enough extra units to more than pay for thinner margin" | units/day up **and** gross profit/day holds or rises | discounted but units flat = margin donated for nothing |
| **Clearance** | price ↓ (dead/aging stock) | "Turn stuck inventory into cash before it ages further" | units move / sell-through up (margin is secondary, ≥ floor) | still not selling |

Clearance is called out deliberately: judging a dead-stock markdown on margin would always look like a loss, when *selling the unit at all* was the goal.

## 3. Cohort assignment

Cohort is decided by the **realized applied change**, not by reason code alone (several algorithms are bidirectional). Reason codes refine intent within a direction.

```
direction = sign(ProposedPrice.ChangePct)      // > 0 price up, < 0 price down
intent =
    ChangePct > 0                                            -> MarginCapture
    ChangePct < 0 AND ReasonCodes ∩ {DEAD_STOCK_MARKDOWN,
                                     STOCK_AGING}             -> Clearance
    ChangePct < 0 (otherwise)                                -> VolumeStimulation
```

Reason-code names live in [ReasonCodeText.cs](../PricingTool.Web/Services/ReasonCodeText.cs).

## 4. Measurement (simple pre/post)

### Anchor
`ProposedPrice.PushedUtc` ([ProposedPrice.cs:48](../PricingTool.Data/Entities/ProposedPrice.cs#L48)) — the day the price actually went live. Call its date **D0**. Only proposals with `Status = Pushed` and a non-null `PushedUtc` are ever evaluated. (Proposals that were never pushed have no real-world effect to measure.)

### Windows — no recomputation needed
`DailySnapshot` already stores trailing aggregates per SKU per day ([DailySnapshot.cs:25-39](../PricingTool.Data/Entities/DailySnapshot.cs#L25)). Because those windows are *trailing*, we get a clean before/after with two snapshot lookups:

- **Pre** = the snapshot on **D0**. Its `Qty7`/`Net7` describe the 7 days *leading up to* the change (all at the old price).
- **Post** = the snapshot on **D0 + W** (default `W = 7`, configurable to 14). Its `Qty7`/`Net7` describe the 7 days *after* the change took effect.

An outcome stays `Pending` until a snapshot at/after `D0 + W` exists.

### Metrics (`Net7` is VAT-exclusive revenue; `Pptcv` is the all-in **VAT-inclusive** cost — don't mix them: either compare margin as `(price − Pptcv)/price` on gross figures, or gross up `Net7` by the layer VAT before subtracting `Pptcv`)

```
unitsPerDay        = Qty7 / 7
grossProfitPerDay  = (Net7 - Pptcv * Qty7) / 7
marginPct          = (Net7 - Pptcv * Qty7) / Net7        // only when Net7 > 0
avgSellPrice       = Net7 / Qty7                          // realized, only when Qty7 > 0
```

Computed for both Pre and Post; the outcome stores both sides plus the deltas.

### Verdict rules (configurable neutral band `ε`, default 3%)

| Intent | Win | Backfire | Neutral |
|---|---|---|---|
| **MarginCapture** | `ΔgrossProfit/day ≥ +ε` | `ΔgrossProfit/day ≤ −ε` (volume loss outweighed the margin gain) | within ±ε |
| **VolumeStimulation** | `Δunits/day ≥ +ε` **and** `ΔgrossProfit/day ≥ −ε` | `Δunits/day < +ε` (discount didn't move units → margin donated) | units up but profit down ≥ ε |
| **Clearance** | `Δunits/day ≥ +ε` (stock moving) | `Δunits/day ≤ 0` (still stuck) | marginal movement |

Edge cases: `Qty7 = 0` on the pre side → margin % undefined; fall back to gross-profit-per-day (= 0) and units. For clearance with zero pre-sales, *any* post-sales is a Win.

> **Honest caveat (accepted with simple pre/post):** these deltas are **correlation, not proven causation** — seasonality, category trends, and regression-to-the-mean are not separated out. The schema (§5) is built so we can later add a category-relative baseline column or a holdout-group flag **without a breaking migration**, if we decide we want causal rigor.

## 5. Schema — new entity `PriceChangeOutcome`

New table in `PricingTool` schema; additive, duplicates nothing existing.

```csharp
public enum ChangeDirection { Up = 0, Down = 1 }
public enum ChangeIntent    { MarginCapture = 0, VolumeStimulation = 1, Clearance = 2 }
public enum OutcomeVerdict  { Pending = 0, Win = 1, Neutral = 2, Backfire = 3 }

public class PriceChangeOutcome
{
    public long Id { get; set; }

    public long ProposedPriceId { get; set; }       // FK -> ProposedPrice
    public ProposedPrice ProposedPrice { get; set; } = null!;
    public string Sku { get; set; } = "";
    public long SourceRunId { get; set; }            // run that produced the change
    public int? PriceBandId { get; set; }

    public DateTime AppliedUtc { get; set; }         // = ProposedPrice.PushedUtc
    public ChangeDirection Direction { get; set; }
    public ChangeIntent Intent { get; set; }
    public decimal OldPrice { get; set; }
    public decimal NewPrice { get; set; }

    public int WindowDays { get; set; }              // 7 by default

    // Pre / Post run-rates (null until matured)
    public decimal PreUnitsPerDay { get; set; }
    public decimal? PostUnitsPerDay { get; set; }
    public decimal? PreMarginPct { get; set; }
    public decimal? PostMarginPct { get; set; }
    public decimal PreGrossProfitPerDay { get; set; }
    public decimal? PostGrossProfitPerDay { get; set; }

    public OutcomeVerdict Verdict { get; set; } = OutcomeVerdict.Pending;
    public string? Note { get; set; }                // e.g. "discount didn't lift units"
    public DateTime? MeasuredUtc { get; set; }
    public long? MeasuredOnRunId { get; set; }

    // Reserved for the causal upgrade path (unused in v1 simple pre/post):
    // public decimal? PeerUnitsDeltaPct { get; set; }   // category-relative baseline
    // public bool IsHoldout { get; set; }
}
```

EF Core migration + `DbSet<PriceChangeOutcome>` on `PricingToolDbContext`. Index on `(Verdict)` and `(AppliedUtc)`.

## 6. Computation — `OutcomeEvaluationService`

Piggyback on the daily run; no new scheduler. Invoke once near the **start** of `ExecuteRunAsync` (right after `SaveSnapshotAsync`, so today's snapshot is available as a possible "post") in [PricingRunOrchestrator.cs:82](../PricingTool.Data/Services/PricingRunOrchestrator.cs#L82).

Per evaluation pass:
1. Find pushed proposals (`Status = Pushed`, `PushedUtc != null`) that **(a)** have no matured `PriceChangeOutcome` yet and **(b)** whose `PushedUtc.Date + WindowDays <= today`.
2. For each: assign cohort (§3), load Pre snapshot (D0) and Post snapshot (≥ D0+W) for that SKU, compute metrics (§4), assign verdict (§4), upsert the `PriceChangeOutcome` row, set `MeasuredUtc`/`MeasuredOnRunId`.
3. Create `Pending` outcome rows for freshly-pushed proposals so they're visibly "in flight" before maturing.

Wrap in the run's transaction/save batching; log a one-line summary to `AuditLogEntries` like the run does.

## 7. Dashboard — "Did the bet pay off?"

New section below the existing attribution tables on the Impact Dashboard.

- **Add to `DashboardViewModel`** ([ViewModels.cs:15](../PricingTool.Web/Models/ViewModels.cs#L15)): an `OutcomeSummary` per intent (count, avg Δ units/day, avg Δ gross profit/day, win/neutral/backfire counts) plus a short "biggest wins / worst backfires" list.
- **Query in `HomeController.Index`**: group matured `PriceChangeOutcome` rows by `Intent`.
- **Three tiles** (Margin captures / Volume plays / Clearance), each showing the metric that matters for *that* intent, plus a Win-rate bar. A small table lists top wins and worst backfires with a link to the SKU drill-down.
- Empty-state copy when no outcomes have matured yet (expected in demo until pushes exist — see §8).

## 8. Demo mode

In demo mode nothing is pushed today, so the section would be empty. Two options:

- **(Recommended, lightweight)** Extend the demo history backfill to mark a deterministic slice of historical changed proposals as `Approved` → `Pushed` with `PushedUtc` on their run date. The demo generator's existing deterministic velocity drift then produces post-change snapshots, so the tiles populate with a realistic mix of Win/Neutral/Backfire. *Illustrative only — demo data isn't causally responsive to price, consistent with the simple-pre/post caveat.*
- **(Stretch)** Teach the demo generator a price-response model so pushed discounts visibly lift units in later snapshots — more convincing demo, more work. Defer.

## 9. Phasing

1. **Schema + service + backfill of pending rows** (entity, migration, `OutcomeEvaluationService`, orchestrator hook).
2. **Dashboard section** (viewmodel, controller query, view tiles).
3. **Demo seeding** (§8 recommended option) so the section is non-empty in demo.
4. **(Later / separate)** Feedback loop: VolumeStimulation outcomes graded "discount didn't move units" feed a candidate list the discount-effectiveness algorithm can consume — closing the loop from measurement back into the engine.
5. **(Later / optional)** Causal upgrade: populate the reserved category-relative / holdout columns.

## 10. Open decisions

- **Window length `W`:** 7 days (fast read, noisier) vs 14 (steadier, slower feedback). Default 7, make it a `ToolSettings` value.
- **Neutral band `ε`:** start at 3% — tune after first real data.
- **Re-pricing churn:** if a SKU is re-priced again *before* its window matures, the outcome is ambiguous. Proposal: cancel the in-flight outcome (mark `Note = "superseded"`) and anchor a fresh one on the new `PushedUtc`.
- **Demo realism:** lightweight illustrative seeding (§8 option 1) vs modeled price-response (option 2).
