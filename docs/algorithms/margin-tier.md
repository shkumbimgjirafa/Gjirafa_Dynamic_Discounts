# MARGIN_TIER — Deep Dive

*Companion to [`../pricing-algorithms-explained.md`](../pricing-algorithms-explained.md), and a sibling
of [`sell-through.md`](sell-through.md), [`dead-stock.md`](dead-stock.md) and
[`elasticity.html`](elasticity.html). This file explains one
advisor — **Margin-tier prioritization** — end to end: every input it reads, every decision it makes,
and three real worked examples from a live GjirafaMall/KS run (run #29). No code, just the logic.*

---

## 1. What this advisor is for

Margin-tier answers a question about *headroom*, not about sales:

> **"Given how fat or thin this product's margin is, can we afford to be a bit more aggressive with the
> discount — or should we protect what little margin is left?"**

It is the engine's **risk-budget voice**. It doesn't know or care how fast the item sells; it only looks
at the margin and nudges the discount **slightly deeper** when there's plenty of margin to give away, or
**pulls it back** when the margin is dangerously thin. On mid-range margins it has no opinion at all.

It is deliberately a **light touch**: a small ±3pp nudge with a modest weight, designed to *temper* the
louder advisors (sell-through, elasticity), not to drive the price on its own.

**Default weight: 40** (low — it's a secondary, tempering voice, not a primary driver).

---

## 2. The inputs it reads

| Input | Meaning | Why it matters here |
|---|---|---|
| **Current margin %** | Margin at the **current price** vs **PPTCV** (VAT-reconciled) | The *only* signal it acts on — fat vs thin. |
| **Band margin floor** | The band's minimum margin % | Defines "thin" (floor + 5pp). |
| **Current discount** | How far below the **anchor** (FinalPrice) today's price sits, as a fraction | The starting point both branches adjust from. |
| **Anchor price** | `ProductPricing.FinalPrice` | The proposed price is measured from here. |

> Two fixed thresholds, not band-configurable:
> - **High-margin threshold: 40%** — at or above this, the item can absorb a deeper cut.
> - **Thin-margin buffer: 5pp** — within 5pp of the band floor counts as "thin."

---

## 3. The logic, step by step

### Step 0 — Silence gate

If there is **no margin signal** (`CurrentMarginPct` is null — typically a null-cost SKU), it is
**silent**. Otherwise it always lands in exactly one of three branches.

### Step 1 — The high-margin branch (fat → go a touch deeper)

If **margin ≥ 40%**: the item earns plenty per sale, so a deeper discount is still profitable.

- `target = current discount + 0.03` (**+3pp**).
- Confidence **0.4** (a gentle suggestion), reason **`HIGH_MARGIN_ROOM`**.

### Step 2 — The thin-margin branch (thin → conserve)

If **margin ≤ band floor + 5pp** *and* there is a discount to pull back (`current discount > 0`): the
margin is close to (or already under) the floor, so discounting it further is dangerous.

- `target = current discount ÷ 2` (**halve the discount** — pull the price back up toward the anchor).
- Confidence **0.6** (a louder, more protective vote), reason **`THIN_MARGIN_CONSERVE`**.

### Step 3 — The mid-margin no-op

If the margin is comfortably between the two thresholds (above floor + 5pp but below 40%): **no opinion**
→ silent. It only speaks at the extremes.

### Step 4 — The vote it casts

- **Suggested price** = `anchor × (1 − target)`.
- **Reason / confidence** per the branch above (`HIGH_MARGIN_ROOM` @ 0.4, or `THIN_MARGIN_CONSERVE` @ 0.6).

---

## 4. From a vote to a price (how to read the examples)

Margin-tier produces **one vote**. That vote is then:

1. **Blended** with every other advisor's vote — a weighted average where each vote's pull =
   `band weight × its confidence`. With weight 40 and confidence 0.4, a high-margin vote's effective
   weight is only **16** — so against sell-through (75) or elasticity (80) it's usually a minor
   counterweight, not the decider.
2. **Clamped** by the guardrails — never below the band's margin floor, never above the anchor.
3. **Rounded** to a psychological price, but only if rounding stays inside the guardrails.

Because the nudge is small (±3pp, or halving a small discount), `.99`/whole-euro rounding frequently
**absorbs most of it** — so margin-tier's visible effect on the final price is often a fraction of a
percent unless another advisor is moving the price in the same direction.

---

## 5. Three worked examples (real, from run #29 · GjirafaMall/KS)

> **Basis note (post-change):** these SKUs come from run #29, which ran on the *legacy* margin signal
> (source `GrossMargin`, ≈ the list/anchor margin). The margins below have been **recomputed on the
> current basis** — PPTCV vs the **current** price. For SKUs already discounted, the two differ sharply,
> and **Example C's branch flips as a result** — that flip is the whole point of the change.

### Example A — `1406992mo` · Band 1 (€0–10) · high margin, lone voter (the pure nudge)

| Fact | Value |
|---|---|
| Cost (PPTCV) / Anchor / Current | €0.70 / €1.50 / €1.50 (at full price) |
| Current margin · high threshold | 53.3% · 40% |
| Current discount off anchor | 0% |

*(At full price, current price = anchor, so the current and list margins coincide here.)*

**Margin-tier's reasoning**
- Margin 53.3% ≥ 40% → **high-margin branch** → target = `0% + 3pp` = **3%**.
- Vote = `1.50 × (1 − 0.03)` = **€1.455**, confidence **0.4**, reason **`HIGH_MARGIN_ROOM`**.

**The rest of the chain**
- Lone voter → raw = **€1.455**.
- Margin floor = `0.70 ÷ 0.85` = €0.82 → inside [0.82, 1.50], no clamp.
- Rounding (band 1 `.99`, and below €5 so the finer 10-cent `.x9` grid): candidates 1.39 / 1.49 → **1.49**
  is closer.
- **Final = €1.49 (−0.7% vs €1.50).**

> **Read:** this is margin-tier *in isolation* — and it shows why it's a light touch. A fat 53.3% margin
> earns the item a 3pp nudge, but rounding swallows almost all of it: the price moves a single cent. On
> its own, margin-tier barely registers; its job is to lean on the *blend*.

---

### Example B — `7351999mo` · Band 1 (€0–10) · thin margin, conserve

| Fact | Value |
|---|---|
| Cost (PPTCV) / Anchor / Current | €1.21 / €1.50 / €1.37 |
| Current margin · band floor (+5pp) | 11.7% · 15% (→ thin ≤ 20%) |
| Current discount off anchor | 8.7% |

*(The legacy list margin was 19.2% — `(1.50 − 1.21)/1.50`. At the **current** €1.37 it's `(1.37 − 1.21)/1.37` = 11.7%, already under the floor.)*

**Margin-tier's reasoning**
- Margin 11.7% is already below the 15% floor (well inside the ≤ 20% thin threshold) **and** there's an
  8.7% discount to pull back → **thin-margin branch**.
- target = `8.7% ÷ 2` = **4.3%** → **halve the discount**.
- Vote = `1.50 × (1 − 0.043)` = **€1.435**, confidence **0.6**, reason **`THIN_MARGIN_CONSERVE`**.

**The rest of the chain**
- Lone voter → raw = **€1.435**.
- Margin floor = `1.21 ÷ 0.85` = **€1.43** → inside [1.43, 1.50].
- Rounding (band 1, `.x9` grid below €5): the down candidate 1.39 falls *below* the floor, so it's
  rejected → **1.49**.
- **Final = €1.49 (+8.8% vs €1.37).**

> **Read:** the protective face of margin-tier — and a case where the basis change *sharpens* the signal.
> At the current price the margin is only ~11.7%, already **under** the floor, so conserving is plainly
> right; the discount is halved, pulling the price **up** toward the anchor, and the floor blocks any
> rounding that would dip back under it. (On the legacy list margin this looked like a 19% item merely
> "near" the floor — the current basis shows it's actually below it.)

---

### Example C — `677213lt` · Band 2 (€10–50) · already deeply discounted → now mid-tier (silent)

| Fact | Value |
|---|---|
| Cost (PPTCV) / Anchor / Current | €19.00 / €39.50 / €25.49 |
| Current margin · high threshold · band floor (+5pp) | 25.5% · 40% · 13% (→ thin ≤ 18%) |
| Current discount off anchor | 35.5% |

*(The legacy list margin was 51.9% — `(39.50 − 19.00)/39.50`. But this item is already **35.5% off**, so
at the current €25.49 the margin is only `(25.49 − 19.00)/25.49` = **25.5%**.)*

**Margin-tier's reasoning (under the corrected basis)**
- Current margin 25.5% is **below** the 40% high threshold and **above** the thin threshold (18%) →
  **mid-tier → no opinion**. Margin-tier is **silent**.
- *(On the legacy list margin of 51.9% it would have fired `HIGH_MARGIN_ROOM` and voted ~€24.30 — i.e.
  "there's room, discount this already-35%-off item even deeper." The current basis correctly sees the
  margin has already been spent.)*

**The rest of the chain**
- Only vote: **SELL_THROUGH_REMOVE** — local stock sells out in ≈11 days, and 25.5% is still ≥ floor+5pp
  (18%), so it stays healthy and votes **full price €39.50**.
- Lone voter → raw = **€39.50** (the anchor; the guardrail ceiling).
- Margin floor = `19.00 ÷ 0.87` = €21.84 → inside [21.84, 39.50]. `.99` rounding can't go above the
  anchor, so it settles at **€38.99**.
- **Final ≈ €38.99 (+53% vs €25.49).**

> **Read:** the case the change was *made* for. The item is already 35.5% off, so its real (current)
> margin is a middling 25.5%, not the fat 51.9% the list margin advertises. On the legacy signal,
> margin-tier piled on — "high margin, cut deeper" — on an item that had already given up most of its
> margin. On the current basis it correctly shuts up, and sell-through (which sees the item will sell out
> regardless) carries the decision back toward full price. Same SKU, opposite contribution — because the
> margin signal now reflects what the item *actually* earns at today's price.

---

## 6. Gotchas & things to remember

- **It's a tempering voice, not a driver.** Default weight 40 and confidence 0.4 (high branch) give an
  effective weight of just 16 — against sell-through (75) or elasticity (80) it nudges the blend, it
  doesn't decide it (Example C). Its lone-voter effect is usually swallowed by rounding (Example A).
- **Two fixed thresholds.** High = **40%** margin; thin = within **5pp of the band floor**. Everything in
  between is a deliberate no-op.
- **The two branches are asymmetric.** High-margin = +3pp deeper at confidence 0.4 (gentle). Thin-margin =
  *halve* the discount at confidence 0.6 (louder and more protective — conserving margin matters more than
  chasing a bit extra).
- **Thin-margin only fires when there's a discount to pull back** (`current discount > 0`). A thin-margin
  item already at full price gets no vote.
- **The margin signal is the current margin** — computed from the **current price vs PPTCV** (VAT
  reconciled), *not* the source `GrossMargin` and *not* the anchor margin. So as the live price moves
  under a discount, the tier it lands in moves with it.
- **The margin floor still bounds it like everyone else.** Margin-tier gets **no** tunnel exception — only
  [dead stock](dead-stock.md) is allowed below the floor. A high-margin deepening that would cross the
  floor is clamped right back to it.
