# SELL_THROUGH — Deep Dive

*Companion to [`../pricing-algorithms-explained.md`](../pricing-algorithms-explained.md). This file
explains one advisor — **Sell-through** — end to end: every input it reads, every decision it makes,
and three real worked examples from a live GjirafaMall/KS run (run #26). No code, just the logic.*

---

## 1. What this advisor is for

Sell-through answers one question:

> **"At the speed this product is actually selling, will the stock we physically hold clear in a
> reasonable time — and is demand speeding up or slowing down?"**

From that it decides whether the current discount should be **pulled back**, **left alone**, or
**deepened** — or, if the item is about to sell out anyway on a healthy margin, **removed entirely**.

It is the consolidation of three older advisors (velocity-forecast + stockout-risk + momentum) into
one voice, so the same "how fast is it selling" signal is never counted three times in the blend.

**Default weight: 75** (high — it's a primary driver for anything that's selling).

---

## 2. The inputs it reads

| Input | Meaning | Why it matters here |
|---|---|---|
| **Local (KS) stock** | Units in *our own* warehouse | The only stock it will act on — see §3, gate 1 |
| **Weighted daily velocity** | Units/day, recent-weighted: 50% × last-7d rate + 30% × 14d + 20% × 30d | Recent sales count more than old sales |
| **Days-to-sellout (local)** | Local stock ÷ weighted daily velocity | The core "level" signal |
| **Current discount** | How far below the **anchor** (FinalPrice) today's price sits, as a fraction | The starting point it adjusts from |
| **Current margin** | Margin at the **current price** vs **PPTCV** (VAT reconciled) | Gate for the "remove discount" branch |
| **Band margin floor** | The band's minimum margin % | Defines "healthy margin" (floor + 5pp) |
| **7d / 90d velocity** | Short-run vs long-run sales rate | The trend (accelerating / decelerating) signal |
| **90d units** | Total units sold in 90 days | Must be ≥ 5 to trust the trend |
| **30d units** | Total units sold in 30 days | Drives the vote's confidence |
| **Anchor price** | `ProductPricing.FinalPrice` | All discounts and the proposed price are measured from here |

> **Supplier stock is deliberately ignored.** A supplier can add thousands of units overnight, so
> counting it would manufacture fake "overstock" and trigger needless markdowns. Sell-through only
> ever reasons about stock we hold locally.

---

## 3. The logic, step by step

### Step 0 — Two silence gates (when it says nothing at all)

1. **No local stock** (`KS ≤ 0`) → **silent.** Nothing of ours to clear; supplier-only stock is the
   guardrail's job, not this advisor's.
2. **Zero velocity** (no recent sales → days-to-sellout undefined) → **silent.** A locally-stocked
   item that isn't selling at all is *dead stock* — that's the DEAD_STOCK advisor's lane, not this one.

If neither gate fires, it always votes.

### Step 1 — Project days-to-sellout (local)

`days = local stock ÷ weighted daily velocity`. This single number drives everything below.

### Step 2 — The "remove the discount" branch (top priority)

If **both**:
- the item sells out within the **stockout horizon** (default **14 days**), **and**
- its **current margin** (PPTCV vs current price) is **healthy** = at least `band floor + 5pp`,

then discounting it just burns margin on something that will sell out regardless. It votes for
**full price** (`max(anchor, current price)` — never a markdown), reason **`SELL_THROUGH_REMOVE`**.
Confidence rises the closer the sellout is: `0.6 + 0.3 × (1 − days/14)`, capped at 0.9.

### Step 3 — Otherwise, the "level" curve (days-to-sellout → discount adjustment)

Starting from the **current** discount, it adjusts by how many days of local stock remain:

| Days of local stock | Action | Discount change |
|---|---|---|
| ≤ 21 (≈3 weeks) | Fast — shave the discount | **−5pp** |
| ≤ 45 (≈6 weeks) | On pace — hold | **0** |
| ≤ 90 (1.5–3 months) | Slow — slightly deeper | **+3pp** |
| ≤ 180 (3–6 months) | Very slow — deeper | **+6pp** |
| > 180 (6+ months) | Overstocked — markdown pressure | **+10pp** |

### Step 4 — The trend modifier (only with a real baseline: ≥ 5 units sold *before* the last 7 days)

It compares the 7-day velocity to the 90-day velocity (`accel = V7 / V90`). It runs **only when there
were at least 5 units sold in days 8–90** (`Qty90 − Qty7 ≥ 5`) — a genuine baseline to compare against.
This matters because `V90 = Qty90/90` assumes the SKU was sellable for the whole window: a freshly-stocked
item whose sales are all in the last week (`Qty7 == Qty90`) would otherwise show `accel = 90/7 ≈ 12.9`
*mechanically* and be falsely flagged "accelerating." Without a baseline, only the level curve applies.

- **Accelerating** (`accel ≥ 1.5`) → demand is picking up → temper the discount **−3pp** (don't give
  away margin on something gaining momentum).
- **Decelerating** (`accel ≤ 0.5`) → demand is fading → lean **+3pp** deeper.
- In between → no trend nudge.

### Step 5 — Confidence

`confidence = 0.3 + (30-day units ÷ 50)`, capped at 0.9. More recent sales history → a louder vote.
(The remove-branch uses its own confidence from Step 2.)

### Step 6 — The vote it casts

- **Suggested price** = `anchor × (1 − final target discount)`.
- **Reason code**: `SELL_THROUGH_FAST` if the target ended up *below* the current discount,
  `SELL_THROUGH_SLOW` if *above*, `SELL_THROUGH_HOLD` if unchanged (or `SELL_THROUGH_REMOVE` from Step 2).

---

## 4. From a vote to a price (how to read the examples)

Sell-through only ever produces **one vote**. That vote is then:

1. **Blended** with every other advisor's vote — a weighted average where each vote's pull =
   `band weight × its confidence` (its "effective weight").
2. **Clamped** by the guardrails — never below the band's margin floor (except locally-held dead
   stock), never above the anchor.
3. **Rounded** to a psychological price, but only if rounding stays inside the guardrails.

So the final price is rarely *only* sell-through — it's sell-through's voice, combined and bounded.
The examples below show the whole chain honestly.

---

## 5. Three worked examples (real, from run #26 · GjirafaMall/KS)

### Example A — `654004lt` · Band 2 (€10–50) · a fast, accelerating seller

| Fact | Value |
|---|---|
| Cost / Anchor / Current | €13.50 / €25.50 / €18.99 |
| Source margin · band floor | 46.0% · 13% |
| Units sold 7d / 30d / 90d | 3 / 10 / 14 |
| Local stock · supplier | 10 · 24 |
| Current discount off anchor | 25.5% |

**Sell-through's reasoning**
- 10 local units ÷ ~0.37/day ≈ **27 days** to clear.
- 27 > 14 → not the remove branch. 27 ≤ 45 → **"on pace," hold** (target = current 25.5%).
- Trend: 90d units = 14 (≥5); `V7/V90 = (3/7)/(14/90) ≈ 2.8` → **accelerating** → temper **−3pp** → target **22.5%**.
- Vote = `25.50 × (1 − 0.225)` = **€19.76**. Target < current → reason **`SELL_THROUGH_FAST`**.
- Confidence = `0.3 + 10/50` = **0.50** → effective weight = `0.50 × 75` = **37.5**.

**The rest of the chain**
- Other vote: MARGIN_TIER (high margin, 46% ≥ 40%) → +3pp deeper → €18.23, eff. weight 16.
- **Blend** = `(37.5×19.76 + 16×18.23) / 53.5` = **€19.30**.
- Margin floor = `13.50 ÷ 0.87 × 1.18` = €18.31. €19.30 sits inside [18.31, 25.50] → no clamp.
- Rounding (.99, €1 grid since ≥€5): candidates 18.99 / 19.99 → **18.99** is closer.
- **Final = €18.99 = today's price → no change.**

> **Read:** a healthy-margin product selling fast and accelerating. Sell-through says "you could ease
> the discount back," margin-tier says "go a touch deeper," they net to ~€19.30, and `.99` rounding
> lands exactly on today's €18.99 — so the engine holds it steady.

---

### Example B — `pMG081120` · Band 3 (€50–100) · about to sell out, thin cost-margin

| Fact | Value |
|---|---|
| Cost / Anchor / Current | €84.77 / €112.62 / €112.62 (already at full price) |
| Source margin · band floor | 24.7% · 12% |
| Units sold 7d / 30d / 90d | 1 / 4 / 11 |
| Local stock · supplier | 1 · 10 |
| Current discount off anchor | 0% |

**Sell-through's reasoning**
- 1 local unit ÷ ~0.14/day ≈ **7 days** to clear → within the 14-day horizon.
- Effective (source) margin 24.7% ≥ floor 12% + 5% = 17% → **healthy** → **remove branch**.
- Vote = `max(anchor 112.62, current 112.62)` = **€112.62** (full price; never a markdown). Reason **`SELL_THROUGH_REMOVE`**.
- Confidence ≈ `0.6 + 0.3 × (1 − 7/14)` ≈ **0.75** → effective weight ≈ **56**.

**The rest of the chain**
- Other vote: ELASTICITY (demand provably elastic) → profit-max price **€144.77**, eff. weight 60.
- **Blend** = `(60×144.77 + 56×112.62) / 116` = **€129.23**.
- Margin floor = `84.77 ÷ 0.88 × 1.18` = **€113.67** — which is **above** the anchor €112.62. Even full
  price misses the 12% floor, so the guardrail **holds the floor (€113.67)** and raises two flags:
  `MARGIN_FLOOR_ABOVE_ANCHOR` (needs a human) + `MARGIN_FLOOR_CLAMPED`. No `.99` value fits the
  collapsed bounds, so rounding is skipped.
- **Final = €113.67 (+0.9% vs current).**

> **Read:** sell-through is *right* — "it's about to sell out at a healthy margin, stop discounting."
> Elasticity wants even more. But the **cost-based** margin floor sits just above the anchor, so the
> guardrail overrides everyone and parks it at the floor, flagged for review. Note the tension: the
> *source* margin (24.7%) and the *cost-implied* margin (~11% at the anchor) disagree — a data check,
> not a pricing decision.

---

### Example C — `5941329mo` · Band 5 (€250–500, whole-euro rounding) · slow, decelerating

| Fact | Value |
|---|---|
| Cost / Anchor / Current | €325.33 / €599.50 / €429.00 |
| Source margin · band floor | 45.7% · 10% |
| Units sold 7d / 30d / 90d | 0 / 2 / 20 |
| Local stock · supplier | 2 · 0 |
| Current discount off anchor | 28.4% |

**Sell-through's reasoning**
- 2 local units ÷ ~0.013/day ≈ **150 days** to clear.
- 150 > 14 → not remove. 150 falls in the ≤180 band → **"very slow," +6pp** → target 34.4%.
- Trend: 90d units = 20 (≥5); `V7/V90 = 0 / (20/90) = 0` ≤ 0.5 → **decelerating** → **+3pp** → target **37.4%**.
- Vote = `599.50 × (1 − 0.374)` = **€375.05**. Target > current → reason **`SELL_THROUGH_SLOW`**.
- Confidence = `0.3 + 2/50` = **0.34** → effective weight = **25.5**.

**The rest of the chain**
- Other vote: MARGIN_TIER (high margin) → +3pp → €411.02, eff. weight 16.
- **Blend** = `(25.5×375.05 + 16×411.02) / 41.5` = **€388.91**.
- Margin floor = `325.33 ÷ 0.90 × 1.18` = **€426.57**. The blend (€388.91) is **below** the floor → clamp
  **up** to €426.57 (`MARGIN_FLOOR_CLAMPED`).
- Rounding (band 5 = **whole euro**): 426 is below the floor, so → **€427**.
- **Final = €427.00 (−0.5% vs current €429).**

> **Read:** a genuinely slow, fading mover. Sell-through pushes a real markdown (toward ~€375) to get
> it moving and margin-tier tempers it — but the 10% **margin floor (€426.57)** stops the markdown well
> short, and whole-euro rounding lands €427. The lesson: sell-through's deepening is always bounded by
> the margin floor (for normal, non-dead stock).

---

## 6. Gotchas & things to remember

- **It's one voice, not the verdict.** In all three examples the final price came from the *blend* and
  the *guardrails*, not sell-through alone. A high default weight (75) means it usually leads, but
  margin floors and the anchor cap always have the last word.
- **"Healthy margin" (remove branch) uses the *source* margin; the margin *floor* uses *cost*.** When
  those two disagree (Example B), the cost-based floor wins and the SKU is flagged for a human.
- **Supplier stock never counts** — only locally-held stock drives days-to-sellout.
- **The trend modifier needs a baseline** (≥ 5 units sold *before* the last 7 days, `Qty90 − Qty7 ≥ 5`);
  otherwise — including a freshly-stocked one-week burst — only the level curve applies, so a recent
  burst can't be misread as "accelerating."
- **Below-floor selling prices can be frozen by a different rule.** A locally-stocked item whose
  *current* price is already under its margin floor is held in place by the dead-stock "tunnel" freeze
  even while it's selling — so sell-through's vote there is overridden (you'll see
  `DEAD_STOCK_TUNNEL_HELD` rather than a sell-through outcome).
