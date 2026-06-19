# DEAD_STOCK — Deep Dive

*Companion to [`../pricing-algorithms-explained.md`](../pricing-algorithms-explained.md), and a sibling
of [`sell-through.md`](sell-through.md), [`margin-tier.md`](margin-tier.md) and
[`elasticity.html`](elasticity.html). This file explains one advisor — **Dead-stock progressive
markdown** — end to end: every input it reads, every decision it makes, and three real worked examples
from a live GjirafaMall/KS run (run #29). No code, just the logic.*

---

## 1. What this advisor is for

Dead-stock answers one blunt question:

> **"This thing we physically hold hasn't sold a single unit in 90 days — how hard do we have to cut
> the price to get it moving, and how low are we allowed to go?"**

It is the engine's clearance lane. Where sell-through reasons about things that *are* selling (just
faster or slower), dead-stock only ever speaks up for stock that is **completely frozen** — zero sales
across the whole 90-day window — and it only ever does one thing: **mark the price down, progressively,
the longer the item sits unsold.**

It is also the **one** advisor the engine trusts to go *below the margin floor*. Everything else is
clamped at the floor; dead-stock alone gets a relief valve — the "tunnel" — because inventory you're
holding and can't sell is a sunk cost, and clearing it at a thin (or even negative) margin beats holding
it forever. (That relief is enforced by the guardrail, not the algorithm — see §4.)

**Default weight: 75** (high — when it fires it should usually lead, because nothing else has an opinion
on stock that isn't selling).

---

## 2. The inputs it reads

| Input | Meaning | Why it matters here |
|---|---|---|
| **90-day units (`Qty90`)** | Total units sold in 90 days | The gate: it must be **exactly 0**. One sale and this advisor goes silent. |
| **Local (KS) stock** | Units in *our own* warehouse | The only stock it acts on — must be `> 0`. Supplier-only dead stock is left alone (see §6). |
| **Oldest-unit age (`OldestUnitAgeDays`)** | Days the *oldest unit currently on hand* has sat in our warehouse, from the WMS check-in log (`ProductCheckIns`) | The freshness gate: must be **≥ 30 days** (`DeadStockMinStockAgeDays`). A freshly-received pre-order/restock has no 90-day sales simply because it just arrived — it isn't "dead." Unknown age (no check-in row) is treated as old enough. |
| **Zero-sale streak (`ZeroSaleStreakDays`)** | Consecutive snapshots with no movement | Drives *how deep* the markdown goes — every two weeks of silence buys another step. |
| **Current discount** | How far below the **anchor** (FinalPrice) today's price sits, as a fraction | A ratchet floor: the vote never *shrinks* an existing discount. |
| **Anchor price** | `ProductPricing.FinalPrice` | All discounts and the proposed price are measured from here. |
| **Unit cost (PPTCV)** | All-in, VAT-inclusive landed cost | Not read by the algorithm itself, but it defines the **margin floor** and the **dead-stock tunnel floor** the guardrail enforces afterwards. |

> It reads **no** velocity, trend, or margin signal of its own. By definition there *is* no velocity
> (zero sales), so there's nothing to weigh — the only variables are "is it stuck?" (the gate) and "how
> long has it been stuck?" (the depth).

---

## 3. The logic, step by step

### Step 0 — The gate (when it says nothing at all)

It votes **only** when **all three** hold:

1. **Zero 90-day sales** (`Qty90 == 0`) — anything that has sold even once is sell-through's lane, not
   this one.
2. **Positive local stock** (`KsStock > 0`) — there must be units *we hold* to clear.
3. **Not freshly stocked** — the oldest on-hand unit must be at least **30 days** old
   (`OldestUnitAgeDays ≥ DeadStockMinStockAgeDays`). A pre-order or restock that only just landed has no
   90-day sales because it *hasn't had a chance to sell*, not because it's dead — marking it down on
   arrival would be wrong. Unknown age (no WMS check-in row) is treated as old enough, so any coverage
   gap falls back to the prior behaviour. This same gate also closes the tunnel for fresh stock (§4).

If any fail, it's **silent**. In particular, stock that sits *only* in a supplier warehouse and
isn't selling is deliberately ignored — it's not ours to give margin away on (see §6).

### Step 1 — Count the markdown steps

`steps = ZeroSaleStreakDays ÷ 14` (integer division). Each completed two-week stretch of no movement
earns one step.

> **Snapshot days, not calendar days.** `ZeroSaleStreakDays` counts *snapshot rows*, which equal calendar
> days only at the ~daily (24 h) run cadence. At a slower cadence each step spans proportionally more
> calendar time — "14" means "two weeks" only when the engine runs daily.

### Step 2 — The progressive markdown curve

`target discount = min(0.99, 0.10 + 0.05 × steps)`.

| Zero-sale streak | Steps | Target discount off anchor |
|---|---|---|
| 0–13 snapshots | 0 | **10%** (the opening cut) |
| 14–27 | 1 | **15%** |
| 28–41 | 2 | **20%** |
| 42–55 | 3 | **25%** |
| … | … | +5pp every further two weeks … |

There is **no discount ceiling** in the algorithm — it will keep deepening toward the 99% cap. The real
limit on how low the *price* can land is set later, by the guardrail (Step 4 / §4).

### Step 3 — The ratchet (never un-discount)

`target = max(target, current discount fraction)`. If the item already carries a discount deeper than the
curve suggests (e.g. it was marked down hard in a previous life), the vote keeps that deeper discount
rather than walking it *back up*. This advisor only ever marks **down**.

### Step 4 — The vote it casts

- **Suggested price** = `anchor × (1 − target)`.
- **Confidence**: a flat **0.8** — it's a high-conviction signal (the item is provably not selling).
- **Reason code**: always **`DEAD_STOCK_MARKDOWN`**, with text naming the local units stuck and the
  snapshot-day streak.

---

## 4. From a vote to a price — and the tunnel (how to read the examples)

Dead-stock produces **one vote**. As with every advisor it is then **blended**, **clamped** by the
guardrails, and **rounded**. But dead-stock has a unique relationship with the clamp — the **margin-floor
guardrail treats it specially**, and that's where most of the interesting behaviour lives:

1. **Normal floor.** For a normal SKU the lower bound is the margin-floor price `cost ÷ (1 − floor%)`
   (PPTCV is already VAT-inclusive, so there's no VAT step). A vote below it is clamped **up**.

2. **The dead-stock tunnel (the one exception).** A locally-held SKU with **no 90-day sales** is the
   *only* case allowed to pierce that floor. Its markdown may run **below** the margin floor — but no
   lower than the **dead-stock floor = 50% of cost** (`DeadStockFloorCostFraction`, a deliberately
   negative margin). Crossing the floor raises the `DEAD_STOCK_FLOOR_RELAXED` flag.

3. **The "held" pin.** Once a below-floor (tunnel) price *finally starts selling again*
   (`Qty90 > 0`), the engine **pins it exactly** at that price — it is never raised back toward the floor,
   because that price is what's finally moving units. Flag: `DEAD_STOCK_TUNNEL_HELD`. Note this happens
   *after* dead-stock has gone silent (the item is selling now), so the **algorithm casts no vote here at
   all** — it's purely a guardrail behaviour that belongs to the dead-stock lane.

4. **Anchor cap & rounding** apply as usual on top.

So the final price is dead-stock's voice *bounded by an unusually generous floor* — and sometimes it's not
a vote at all, but the guardrail honouring a tunnel price the lane created on an earlier run.

---

## 5. Three worked examples (real, from run #29 · GjirafaMall/KS)

> Streaks in this run are still young (~4 snapshot days), so every example is at **step 0 — the opening
> 10% cut**. The progressive deepening (§3) is the same arithmetic repeated as the streak grows.

### Example A — `kCON077` · Band 2 (€10–50) · the opening cut, comfortably above the floor

| Fact | Value |
|---|---|
| Cost / Anchor / Current | €18.84 / €26.07 / €26.07 (at full price) |
| Band floor | 13% |
| 90-day units · local stock | 0 · 1 |
| Zero-sale streak | 4 snapshot days (→ step 0) |

**Dead-stock's reasoning**
- Gate: 0 sales in 90 days **and** 1 unit held locally → it votes.
- Streak 4 → `steps = 0` → target = **10%**. Current discount is 0%, so the ratchet doesn't lift it.
- Vote = `26.07 × (1 − 0.10)` = **€23.46**, confidence **0.8**, reason **`DEAD_STOCK_MARKDOWN`**.

**The rest of the chain**
- Lone voter → raw weighted price = **€23.46**.
- Margin floor = `18.84 ÷ 0.87` = **€21.65**. €23.46 sits inside [21.65, 26.07] → **no clamp**, no tunnel.
- Rounding (band 2 = `.99`, €1 grid since ≥€5): candidates 22.99 / 23.99 → **22.99** is closer.
- **Final = €22.99 (−11.8% vs €26.07).** Margin there is still ~18% — well above the 13% floor.

> **Read:** the clean, common case. A full-price item that simply isn't moving gets its first 10% nudge,
> and because the cut still clears the margin floor, nothing special happens — it's just a discount.

---

### Example B — `5.6603.12Mvicsst` · Band 2 (€10–50) · the tunnel: the cut pierces the floor

| Fact | Value |
|---|---|
| Cost / Anchor / Current | €19.07 / €22.43 / €22.43 (at full price) |
| Band floor · dead-stock floor (50% of cost) | 13% (→ €21.92) · €9.54 |
| 90-day units · local stock | 0 · 1 |
| Zero-sale streak | 4 snapshot days (→ step 0) |

**Dead-stock's reasoning**
- Gate passes (0 sales, 1 local unit). Step 0 → target **10%**.
- Vote = `22.43 × 0.90` = **€20.19**, reason **`DEAD_STOCK_MARKDOWN`**.

**The rest of the chain**
- Lone voter → raw = **€20.19**.
- Margin floor = `19.07 ÷ 0.87` = **€21.92**. The vote €20.19 is **below** it — but this is locally-held
  dead stock, so the **tunnel opens**: the floor is relaxed (`DEAD_STOCK_FLOOR_RELAXED`) down to the
  dead-stock floor of `19.07 × 0.50` = **€9.54**. €20.19 is comfortably above €9.54, so it stands.
- Rounding (band 2 `.99`): candidates 19.99 / 20.99 → **19.99** is closer.
- **Final = €19.99 (−10.9% vs €22.43).** Margin there ≈ 4.6% — *below* the 13% floor, allowed only
  because it's dead stock we hold.

> **Read:** the same opening 10% cut as Example A, but here the cut lands under the margin floor. For any
> other SKU the guardrail would clamp it back up to €21.92; because it's frozen local stock, the engine
> lets the markdown through the tunnel to try to clear it.

---

### Example C — `kCQ660` · Band 2 (€10–50) · the tunnel "held": it started selling again

| Fact | Value |
|---|---|
| Cost / Anchor / Current | €26.24 / €35.87 / €29.99 |
| Band floor | 13% (→ €30.16) |
| 90-day units · local stock | **2** · 2 |

**What dead-stock does**
- **Nothing.** `Qty90 = 2 ≠ 0`, so the gate fails and the algorithm is **silent**. The item is selling
  again, so it's no longer "dead."

**Why the price still doesn't move (the guardrail's tunnel-held rule)**
- The only vote cast is sell-through's **`SELL_THROUGH_HOLD`** at €29.99 (≈36 days of local stock — "on
  pace, hold").
- But notice the current price €29.99 sits *below* the margin floor `26.24 ÷ 0.87` = **€30.16**. Normally
  the guardrail would clamp it **up** to the floor (≈ €30.99 after rounding) — a price increase.
- Instead, because this is locally-held stock that got under the floor via the tunnel and is **now moving
  units**, the engine **pins it exactly at €29.99** and flags `DEAD_STOCK_TUNNEL_HELD`. The below-floor
  price is finally working; raising it risks killing the sales it just won.
- **Final = €29.99 (0.0% — held).**

> **Read:** the payoff of the tunnel. An item was marked down below margin to clear it, it started
> selling at that price, and the engine now *protects* that price rather than clawing the margin back.
> The dead-stock lane isn't a vote here — it's a standing guardrail that remembers how the price got low.

---

## 6. Gotchas & things to remember

- **It only ever marks down, and only on stock we hold that's had a chance to sell.** Zero 90-day sales,
  `KsStock > 0`, **and** an oldest on-hand unit at least 30 days old are all required. No velocity,
  margin, or trend input — just "stuck?", "how long?", and "has it actually been sitting here?".
- **Freshly-stocked pre-orders/restocks are spared.** A SKU that just arrived has no 90-day sales because
  it hasn't had time to sell, not because it's dead — the WMS `OldestUnitAgeDays` gate (default 30 days)
  keeps it out of the markdown lane *and* the below-floor tunnel until it has genuinely aged. The
  `MarkAsNew` guardrail only covers *platform-new* products; this gate is what catches restocks of
  existing SKUs. Unknown age (no check-in row) falls back to the prior behaviour.
- **Supplier-only dead stock is left alone.** If every unit sits in a supplier warehouse (`KsStock == 0`),
  the algorithm abstains and the guardrail additionally blocks any markdown
  (`SUPPLIER_ONLY_NO_MARKDOWN`) — we don't give margin away on inventory we don't physically hold. See
  the stock-location rules shared with [`sell-through.md`](sell-through.md).
- **It's the only advisor allowed below the margin floor** — through the *tunnel*, down to 50% of cost.
  Every other algorithm (sell-through, elasticity, margin-tier) is hard-clamped at the floor.
- **The "held" pin is a guardrail, not a vote.** When you see `DEAD_STOCK_TUNNEL_HELD` the dead-stock
  *algorithm* said nothing (the item is selling); the guardrail is honouring a below-floor price an
  earlier run created. The winning reason code will be a sell-through one, not a dead-stock one.
- **Streak depth is in snapshot days, not calendar days** — it tracks calendar time only at the daily run
  cadence (§3, Step 1).
- **The ratchet never un-discounts.** An item already discounted deeper than the curve keeps that deeper
  discount; the vote can only equal or exceed the existing markdown.
