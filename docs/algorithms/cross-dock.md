# CROSS_DOCK — Deep Dive

*Companion to [`../pricing-algorithms-explained.md`](../pricing-algorithms-explained.md), and a sibling
of [`sell-through.md`](sell-through.md), [`dead-stock.md`](dead-stock.md), [`margin-tier.md`](margin-tier.md)
and [`elasticity.html`](elasticity.html). This file explains one advisor — **Cross-dock
(supplier-fulfilled) markdown** — end to end: every input it reads, every decision it makes, and worked
examples. The examples here are **illustrative** (round numbers chosen to show the arithmetic), not from a
specific run.*

---

## 1. What this advisor is for

Cross-dock answers the question nobody else in the engine will:

> **"We don't hold this in our own warehouse — we sell it by ordering from a supplier once a customer
> buys — so what price maximizes sales × profit when there's no on-hand stock signal and often too little
> price history to fit elasticity?"**

These are the **supplier-fulfilled** SKUs: `KsStock == 0`, `SupplierStock > 0`. A customer orders, we order
from the supplier, and we resell. The unit is never ours to hold. Before this lane existed they fell
through every other advisor:

- **Sell-through** abstains — it gates on locally-held stock (`KsStock > 0`) and needs an on-hand
  days-to-sellout it can't compute here.
- **Dead-stock** abstains — same local-stock gate; it clears inventory *we* hold.
- **Elasticity** often can't fit — supplier SKUs are frequently long-tail / sporadic and never accumulate
  the dense weekly price history the fit requires.

So a selling supplier SKU used to just sit at its anchor price forever, with no advisor speaking for it.
Cross-dock is that voice.

**The economics are genuinely different from dead-stock.** There is **no holding cost, no aging risk, no
stockout risk** on our side — the supplier carries all of it. So a markdown here is never about clearing
inventory; it's pure demand discovery. And crucially, because nothing is sunk, a cross-dock markdown is
bounded by the **normal margin floor** — never the dead-stock below-floor tunnel. A sale below the floor
would just lose money on every fulfilled order; there's no sunk capital to recover by going lower.

**Default weight: 40** (low). When elasticity (the stronger signal) also fires, the weighted blend defers
to it; when elasticity is absent — the common case for supplier SKUs — cross-dock carries the vote.

---

## 2. The inputs it reads

| Input | Meaning | Why it matters here |
|---|---|---|
| **Local (KS) stock** | Units in *our own* warehouse | The gate: must be **0**. Anything we hold is sell-through / dead-stock's lane. |
| **Supplier stock** | Units held only by suppliers | Must be **> 0** — there has to be something we can actually sell. |
| **90-day units (`Qty90`)** | Units sold in 90 days | The **branch selector**: `> 0` → selling (hold); `== 0` → not selling (tunnel). *Not* an eligibility gate — non-selling SKUs are exactly what Branch B is for. |
| **7d / 90d velocity** | Units/day, recent vs baseline | Branch A reads the trend to decide hold vs. defend. |
| **Zero-sale streak (`ZeroSaleStreakDays`)** | Consecutive snapshots with no movement | Branch B's depth dial — drives how far the markdown has walked. |
| **Current price** | Today's selling price | The markdown's **starting point** and its monotonic ceiling — the lane never raises it (one exception: §3, the floor lift). |
| **Anchor price** | `ProductPricing.FinalPrice` | The reference the discount schedule is measured from. |
| **Unit cost (PPTCV)** | All-in, VAT-inclusive landed cost (purchase + transport + customs + VAT) | Defines the **margin floor** = `cost ÷ (1 − floor%)` — the hard lower bound for the whole lane. Missing cost → it abstains. |
| **MarkAsNew window** | Platform "new product" flag | If set, the engine holds the price anyway; cross-dock abstains so it never tunnels a just-listed SKU. |

> Transport is **in** PPTCV (the "T"), so the margin floor reflects the true landed cross-dock cost — a
> floor-bounded markdown can't quietly push a fulfilled order into a loss.

---

## 3. The logic, step by step

### Step 0 — The gate (when it says nothing at all)

It votes **only** when **all** of these hold:

1. **No local stock** (`KsStock == 0`) — we hold none ourselves.
2. **Positive supplier stock** (`SupplierStock > 0`) — there's something to sell.
3. **Not platform-new** (`IsNewProduct == false`) — a just-listed SKU is held by the engine anyway.
4. **Known cost** (`PPTCV` present) — without it there's no margin floor to bound the markdown, so it
   abstains rather than vote unbounded.

If any fail, it's **silent**.

### Step 1 — The floor-protection lift (the one upward move)

If the SKU is already priced **at or below** the margin floor (`CurrentPrice ≤ floor`) — underwater or
mispriced — it votes the **floor** and stops. Reason code **`CROSS_DOCK_FLOOR`**. This is the only time the
lane ever proposes a price *above* today's.

### Step 2 — Pick the branch by sales status

- **Branch A — selling** (`Qty90 > 0`): see Step 3a.
- **Branch B — not selling** (`Qty90 == 0`): see Step 3b.

### Step 3a — Branch A: hold the working price (sticky)

When the SKU is selling, **the discount that produced the sale is load-bearing** — clawing it back would
likely kill the very demand it unlocked. So the default is **HOLD**: an *active* vote at the current price
(reason **`CROSS_DOCK_HOLD`**). Holding is deliberately a vote, not silence, so that a co-voting
margin-tier (which is stock-agnostic and would otherwise nudge the price up to "conserve margin") can't
claw the working discount back.

The **only** deviation is a defensive deepen, taken when **both**:
- demand is **decaying** — `Velocity7 ≤ 0.5 × Velocity90` (recent half-week running at half the 90-day
  baseline or less), **and**
- margin is **comfortably above the floor** (≥ floor + 5pp) — thin margin → just hold, don't erode it.

Then it deepens by **one step** (`CrossDockStepPct`, default 5pp) toward the floor (reason
**`CROSS_DOCK_DEFEND`**). It never raises.

### Step 3b — Branch B: soft progressive tunnel toward the floor

When the SKU isn't selling, walk the price down from today's level toward the margin floor as the
no-movement streak grows:

```
steps  = ZeroSaleStreakDays ÷ CrossDockStepIntervalDays      (default interval 21 snapshot days)
disc   = min(0.99, CrossDockStartDiscountPct%  +  CrossDockStepPct% × steps)   (default 5% + 5pp/step)
price  = max(floor, min(CurrentPrice, anchor × (1 − disc)))
```

| Zero-sale streak | Steps | Schedule discount off anchor |
|---|---|---|
| 0–20 snapshots | 0 | **5%** (the gentle opening cut) |
| 21–41 | 1 | **10%** |
| 42–62 | 2 | **15%** |
| … | … | +5pp every further ~3 weeks … |

This is **deliberately softer than dead-stock** (which opens at 10% and steps 5pp every 14 days), because a
cross-dock markdown is demand discovery on stock we don't hold, not loss-recovery on stock we own.

Two clamps do the real work:

- **`min(CurrentPrice, …)`** makes the markdown **start at the current price**. The schedule is
  anchor-relative, so until it overtakes any discount the SKU already carries, the price simply **holds at
  today's level**; once the schedule goes deeper, the price follows it down. (Because the schedule is a
  pure function of the streak, it never **compounds** run-over-run.)
- **`max(floor, …)`** caps the descent at the **margin floor** — never the dead-stock below-floor tunnel.

Reason code **`CROSS_DOCK_TUNNEL`**, confidence **0.8**.

### Step 4 — Monotonic & sticky

The lane never raises the price (except the Step-1 floor lift). And when a tunnelled SKU **finally sells**,
`Qty90` turns positive on the next run → it switches to Branch A → **holds the achieved price**. The
markdown's progress is kept, not clawed back. That branch switch *is* the self-correction: the lane finds a
price that moves units, then locks it.

---

## 4. From a vote to a price

Cross-dock produces **one vote**. Like every advisor it's **blended** (weighted average by band weight ×
confidence), **clamped** by the guardrails, then **rounded**.

Because its weight is low (40), the blend behaves exactly as intended:

- **Elasticity also fires** → elasticity (80) dominates the weighted average; cross-dock only tempers it.
  The fitted curve wins when it exists.
- **Elasticity is absent** (the usual case for supplier SKUs) → cross-dock carries the vote and actually
  moves the price; its solid confidence keeps it ahead of margin-tier's light nudge.

There is **no special guardrail** for cross-dock. The normal margin-floor clamp is its only lower bound,
and the anchor cap its upper. (The old "supplier-only, never marked down" freeze that used to pin these
SKUs at their current price has been **removed** — this lane replaces it; see
[`dead-stock.md`](dead-stock.md) §6 and the guardrails section of the overview.)

---

## 5. Worked examples (illustrative)

> Throughout: **anchor €100**, **cost €45**, band floor **10%** → margin floor = `45 ÷ 0.90` = **€50**.
> Supplier-fulfilled (`KsStock = 0`, `SupplierStock > 0`).

### Example A — not selling, fresh streak: the gentle opening cut
- `Qty90 = 0`, current €100 (full price), streak 0 → step 0 → disc **5%**.
- price = `max(50, min(100, 100 × 0.95))` = **€95**. Reason **`CROSS_DOCK_TUNNEL`**.
- **Read:** a non-selling supplier SKU gets a soft 5% nudge to test for demand — risk-free, since we hold
  no stock and €95 is well above the €50 floor.

### Example B — not selling, long streak: clamped at the margin floor
- `Qty90 = 0`, current €100, streak 210 → step 10 → schedule disc 55% → `100 × 0.45` = €45.
- price = `max(50, min(100, 45))` = **€50** — the **margin floor**, not lower. Reason `CROSS_DOCK_TUNNEL`.
- **Read:** even after a long dry spell the markdown stops dead at the floor. Unlike dead-stock there's no
  tunnel below it — nothing is sunk to recover.

### Example C — not selling but already discounted: starts at the current price
- `Qty90 = 0`, current **€80** (already 20% off), streak 21 → step 1 → schedule disc 10%.
- `min(80, 100 × 0.90 = 90)` = **€80** — the schedule (10%) is shallower than the existing 20%, so it
  **holds at today's price**, never raising it. It only deepens once the streak's schedule passes 20%.

### Example D — priced below the floor: the one lift up
- current **€40**, which is below the €50 floor → vote **€50**, reason **`CROSS_DOCK_FLOOR`**.
- **Read:** the lane corrects an underwater price up to the floor and stops — the only upward move it makes.

### Example E — selling steadily: hold the working price
- `Qty90 = 30`, current €80, healthy 7-day velocity → **HOLD** at **€80**, reason **`CROSS_DOCK_HOLD`**.
- **Read:** it's selling at a 20% discount — that discount is what's working, so don't touch it.

### Example F — selling but decaying, with margin room: defend
- `Qty90 = 30` but `Qty7 = 0` (recent ≤ ½ baseline) and margin `(80−45)/80 = 43.75%` ≫ floor+5pp → deepen
  one step: 20% + 5pp = 25% → **€75**, reason **`CROSS_DOCK_DEFEND`**.
- **Read:** sales are drying up and there's plenty of margin headroom, so a small deeper cut tries to keep
  units moving — still floor-bounded, never a raise.

### Example G — selling but decaying, thin margin: hold
- Same decay but current **€52**, cost €45 → margin `13.5% < floor 10% + 5pp` → **HOLD** at €52, reason
  `CROSS_DOCK_HOLD`. Thin margin means there's nothing to give; don't erode it.

---

## 6. Gotchas & things to remember

- **It's the supplier-fulfilled lane, identified behaviourally, not by a flag.** Any SKU can be
  cross-docked; the signal is simply `KsStock == 0 && SupplierStock > 0`. There is no "is cross-dock"
  column.
- **`Qty90` is the branch selector, not a gate.** Non-selling supplier SKUs are *included* (Branch B), not
  excluded — discounting them is the lowest-risk way to discover demand, since nothing is held.
- **Bounded by the NORMAL margin floor, never the dead-stock tunnel.** Cross-dock can mark down to the
  floor and no further. The below-floor (50%-of-cost) tunnel stays dead-stock-only — that relief exists to
  recover sunk capital, and there is none here.
- **Monotonic and sticky.** The lane only ever lowers the price (the one exception is lifting an
  underwater price up to the floor). A working discount is held, not clawed back; the markdown a non-seller
  earned is kept when it starts selling.
- **The hold is an active vote, by design.** It votes the current price (not silence) specifically so a
  co-voting margin-tier can't pull a working discount back up.
- **Low weight is intentional.** It defers to elasticity when that fires and carries the vote when it
  doesn't — validate against vote data that it isn't fighting elasticity in a way that worsens prices.
- **Streak depth is in snapshot days, not calendar days** — it tracks calendar time only at the daily run
  cadence, same caveat as dead-stock.
- **No supplier-only freeze anymore.** The engine used to *block* any markdown on non-selling supplier
  stock (`SUPPLIER_ONLY_NO_MARKDOWN`); that guardrail was removed when this lane was added. Supplier-only
  stock is now priced here, bounded only by the margin floor.
