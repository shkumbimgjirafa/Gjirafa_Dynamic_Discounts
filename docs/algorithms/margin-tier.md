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
| **Effective margin %** | Source `GrossMargin` if present, else the cost-derived current margin | The *only* signal it acts on — fat vs thin. |
| **Band margin floor** | The band's minimum margin % | Defines "thin" (floor + 5pp). |
| **Current discount** | How far below the **anchor** (FinalPrice) today's price sits, as a fraction | The starting point both branches adjust from. |
| **Anchor price** | `ProductPricing.FinalPrice` | The proposed price is measured from here. |

> Two fixed thresholds, not band-configurable:
> - **High-margin threshold: 40%** — at or above this, the item can absorb a deeper cut.
> - **Thin-margin buffer: 5pp** — within 5pp of the band floor counts as "thin."

---

## 3. The logic, step by step

### Step 0 — Silence gate

If there is **no margin signal** (`EffectiveMarginPct` is null — typically a null-cost SKU), it is
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

### Example A — `1406992mo` · Band 1 (€0–10) · high margin, lone voter (the pure nudge)

| Fact | Value |
|---|---|
| Cost / Anchor / Current | €0.70 / €1.50 / €1.50 (at full price) |
| Effective margin · high threshold | 53.4% · 40% |
| Current discount off anchor | 0% |

**Margin-tier's reasoning**
- Margin 53.4% ≥ 40% → **high-margin branch** → target = `0% + 3pp` = **3%**.
- Vote = `1.50 × (1 − 0.03)` = **€1.455**, confidence **0.4**, reason **`HIGH_MARGIN_ROOM`**.

**The rest of the chain**
- Lone voter → raw = **€1.455**.
- Margin floor = `0.70 ÷ 0.85` = €0.82 → inside [0.82, 1.50], no clamp.
- Rounding (band 1 `.99`, and below €5 so the finer 10-cent `.x9` grid): candidates 1.39 / 1.49 → **1.49**
  is closer.
- **Final = €1.49 (−0.7% vs €1.50).**

> **Read:** this is margin-tier *in isolation* — and it shows why it's a light touch. A fat 53% margin
> earns the item a 3pp nudge, but rounding swallows almost all of it: the price moves a single cent. On
> its own, margin-tier barely registers; its job is to lean on the *blend*.

---

### Example B — `7351999mo` · Band 1 (€0–10) · thin margin, conserve

| Fact | Value |
|---|---|
| Cost / Anchor / Current | €1.21 / €1.50 / €1.37 |
| Effective margin · band floor (+5pp) | 19.2% · 15% (→ thin ≤ 20%) |
| Current discount off anchor | 8.7% |

**Margin-tier's reasoning**
- Margin 19.2% is within 5pp of the 15% floor (≤ 20%) **and** there's an 8.7% discount to pull back →
  **thin-margin branch**.
- target = `8.7% ÷ 2` = **4.3%** → **halve the discount**.
- Vote = `1.50 × (1 − 0.043)` = **€1.435**, confidence **0.6**, reason **`THIN_MARGIN_CONSERVE`**.

**The rest of the chain**
- Lone voter → raw = **€1.435**.
- Margin floor = `1.21 ÷ 0.85` = **€1.43** → inside [1.43, 1.50].
- Rounding (band 1, `.x9` grid below €5): the down candidate 1.39 falls *below* the floor, so it's
  rejected → **1.49**.
- **Final = €1.49 (+8.8% vs €1.37).**

> **Read:** the protective face of margin-tier. A thin 19% margin with a live discount gets that discount
> halved, pulling the price **up** toward the anchor — and the floor blocks any rounding that would dip
> back under it. Here the conserve vote and the floor agree, so the price climbs to €1.49.

---

### Example C — `677213lt` · Band 2 (€10–50) · high margin, blended with a louder voice

| Fact | Value |
|---|---|
| Cost / Anchor / Current | €19.00 / €39.50 / €25.49 |
| Effective margin · band floor | 51.9% · 13% |
| Current discount off anchor | 35.5% |

**Margin-tier's reasoning**
- Margin 51.9% ≥ 40% → **high-margin branch** → target = `35.5% + 3pp` = **38.5%**.
- Vote = `39.50 × (1 − 0.385)` = **€24.30**, confidence **0.4** → effective weight = `0.4 × 40` = **16**.

**The rest of the chain**
- Other vote: **SELL_THROUGH_REMOVE** — local stock sells out in ≈11 days at a 51.9% margin, so it votes
  **full price €39.50**, confidence 0.66 → effective weight = `0.66 × 75` = **49.9**.
- **Blend** = `(49.9 × 39.50 + 16 × 24.30) / 65.9` = **€35.81**.
- Margin floor = `19.00 ÷ 0.87` = €21.84 → inside [21.84, 39.50], no clamp.
- Rounding (band 2 `.99`, €1 grid): candidates 34.99 / 35.99 → **35.99** is closer.
- **Final = €35.99 (+41.2% vs €25.49).**

> **Read:** the realistic case. Sell-through wants to *remove* the discount entirely (€39.50); margin-tier
> says "there's room, go a touch deeper" (€24.30). With only a third of sell-through's pull, margin-tier
> doesn't reverse the decision — it just tugs the blend down from €39.50 to €35.81. The price still rises
> sharply, but margin-tier shaved ~€3.50 off how far. That tempering is exactly its purpose.

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
- **"Effective margin" uses the source `GrossMargin` first**, falling back to the cost-derived current
  margin — the same signal sell-through uses for its "healthy margin" remove branch.
- **The margin floor still bounds it like everyone else.** Margin-tier gets **no** tunnel exception — only
  [dead stock](dead-stock.md) is allowed below the floor. A high-margin deepening that would cross the
  floor is clamped right back to it.
