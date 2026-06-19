# How the Pricing Tool Decides a Price — In Plain Words

This document explains, without any code, the *logic* behind each of the 5 pricing
algorithms and how they come together into one proposed price. It's written for anyone
on the team — you don't need to be an engineer to follow it.

---

## The big picture

For every product (SKU), once per run, the tool does this:

1. **Gathers the facts** — current price, full/shelf price, cost, stock on hand, and how
   many units sold over the last 7 / 14 / 30 / 60 / 90 days.
2. **Asks 5 "advisors" (the algorithms)** what the price should be. Each one looks at the
   facts from its own angle and either **votes** for a price or **stays silent** (says
   "no opinion").
3. **Blends the votes** into one number — a *weighted average*, where louder, more-confident
   advisors pull the result more.
4. **Applies hard limits (guardrails)** — never above the anchor price (FinalPrice), and never below
   the margin floor (except locally-held dead stock, which may go down to 50% of cost).
5. **Rounds to a nice-looking price** (e.g. ends in .99) — but only if rounding doesn't
   break the guardrails.

The result is a **proposal**, not a live price change. A human reviews and approves it, and
prices only go live through the explicit **Push** step.

> **Important:** The tool only ever proposes **discounts** (prices at or below the **anchor /
> FinalPrice**). It never raises a price above the anchor. (The displayed "shelf"/OldPrice is
> kept for reference but no longer governs the ceiling or the discount math.)

---

## A few words you'll see repeated

| Term | Plain meaning |
|---|---|
| **Anchor price (FinalPrice)** | The true reference/list price (from `ProductPricing.FinalPrice`). All discounts **and** the price ceiling are measured from here. |
| **Shelf price (OldPrice)** | The platform's strike-through "old" price. Shown for reference only — it no longer drives any calculation. |
| **Current price** | What the product actually sells for today (may already be discounted). |
| **Cost (PPTCV)** | What the item costs us to buy. Used for margin. If it's missing, the SKU is skipped — we never guess it. |
| **Margin** | Profit as a share of the selling price: (price − cost) / price. PPTCV is the all-in **VAT-inclusive** cost, so it's compared directly to the (also VAT-inclusive) price — no VAT stripping. |
| **Velocity** | Sales speed — units sold per day. We look at it over several time windows. |
| **Days-to-sellout** | At today's sales speed, how many days until the stock runs out. |
| **No-sale streak** | How many days in a row the item has sold nothing. |
| **Discount %** | How far below the shelf price we are (e.g. 30% off). "pp" means percentage points. |

Recent sales count more than old sales: when the tool measures "sales speed," it weights the
last 7 days at 50%, the last 14 days at 30%, and the last 30 days at 20%.

---

## The 5 algorithms

> Consolidated from the original 10: the velocity family (velocity-forecast + stockout-risk +
> momentum) is now one **Sell-through** advisor; warehouse-stock-aging, supplier-vs-local and
> discount-effectiveness were retired; new-product protection is no longer a voting algorithm — it's
> now a hard guardrail (see below). **Cross-dock (#11)** was later added for supplier-fulfilled SKUs.
> Section numbers below are kept for reference.

Each one is an independent advisor. It only speaks when its specific situation applies;
otherwise it abstains. The **default weight** (0–100) is how much its vote counts before
confidence is factored in — it can be tuned per price band, or turned off entirely.

---

### 1. Sell-through (velocity + inventory) — *default weight 75*
**The question it asks:** "At the current selling speed, will this stock clear in a
reasonable time — and is demand speeding up or slowing down?"

This is the consolidated velocity/inventory advisor (it merges the former velocity-forecast,
stockout-risk and momentum algorithms, so the same sales-speed signal isn't counted three
times in the blend). Days-of-stock is measured on **our own (KS) warehouse stock only** — supplier
stock isn't ours to clear and is volatile (a supplier can add thousands of units overnight), so it
never drives a markdown. It projects days-to-sellout of local stock and reads the velocity trend:

- **About to sell out (~≤2 weeks) on a healthy margin** → **remove the discount** (no point
  burning margin on something that will sell out anyway). Never a markdown.
- **Fast (≤3 weeks)** → *shave* the discount (−5pp). **On pace (≤45 days)** → hold.
  **Slow (1.5–3 months)** → deepen +3pp. **Very slow (3–6 months)** → deepen +6pp.
  **Over 6 months of stock** → markdown pressure (+10pp).
- **Trend modifier:** accelerating demand tempers the discount shallower; decelerating deepens it.

**How sure it is:** more sales history = more confidence.
**Silent when:** we hold no local (KS) stock (supplier-only stock is the guardrail's job), or nothing
is selling at all (that's dead-stock territory).

---

### 2. ~~New-product protection~~ — *now a guardrail, not an algorithm*
New-product protection moved from an (out-votable) algorithm to a **hard engine rule**: while a
product is inside the platform's **MarkAsNew** window (`MarkAsNew = 1` and the current date is within
its start/end dates), the engine holds the **current price exactly as-is — no discount, no change** —
overriding every algorithm and guardrail. See the guardrails section.

---

### 3. ~~Warehouse-stock aging markdown~~ — *merged into #1*
The "slowing but not dead" case is now handled by the Sell-through advisor (#1). (In practice
it never fired and overlapped #1.)

---

### 4. ~~Stockout-risk protection~~ — *merged into #1*
"Remove the discount when it's about to sell out on a healthy margin" is now the fast extreme
of the Sell-through advisor (#1).

---

### 5. Price elasticity (fitted) — *default weight 80*
**The question:** "Does demand here actually respond to price?"

A per-SKU elasticity is **fitted weekly** from years of transaction history (a log-log
regression of units sold against the realized price). The fit records both the coefficient **and
its standard error**, and only SKUs with enough price variation and a trustworthy fit are stored.

- **Confidently elastic** → vote the **profit-maximizing price** `P* = cost · E/(E+1)` (PPTCV is the
  all-in VAT-inclusive cost, so P* is already a selling price), **capped at the anchor** and clamped by
  the guardrails. "Confidently" means the standard error puts the whole one-sided 95% interval below
  −1 (`E + 1.645·SE ≤ −1`). A noisy near-unit estimate like `−1.18 ± 0.6` is **not** trusted, because
  the markup `E/(E+1)` explodes as E nears −1 (−1.18 → ×6.6) — so we only act when we're sure the SKU
  is genuinely elastic. More-elastic SKUs move toward a price nearer cost (grow volume); the anchor
  cap bounds the rest.
- **Not confidently elastic, inelastic, or no trustworthy fit** → **stay silent** — left to the
  margin-tier (#6) advisor and the margin-floor guardrail.

> **Deep dive (with the math and graphs):** [`algorithms/elasticity.html`](algorithms/elasticity.html) —
> the demand model, the weekly log-log regression, the profit-max derivation `P* = cost·E/(E+1)`, the
> markup blow-up near E=−1, and the confidence gate, all with rendered equations and live charts.

---

### 6. Margin-tier prioritization — *default weight 40*
**The idea:** How much room does the margin give us to play with?

- **High margin (≥40%)** → it can absorb a slightly deeper cut profitably → +3pp discount.
- **Thin margin (within 5pp of the band's floor)** and already discounted → play it safe →
  **halve the discount**.
- **In between** → no opinion.

---

### 7. Dead-stock progressive markdown — *default weight 75*
**The situation:** Zero sales in the last 90 days, but we still have stock **in our own
warehouse** sitting there — true dead stock that's ours to clear.

**What it does:** Start at **10% off** and add **5pp every two weeks** it stays unsold. This
is the **one** advisor allowed to push a price **below the margin floor** — for stock we
physically hold that simply won't move, clearing it at a loss beats holding it forever, so
the markdown may run all the way down to **50% of cost** (a negative margin). It only ever
marks *down* — it will never shrink a discount that's already in place.

**If it finally sells:** the price is **held** at the level that moved it — the SKU stays in
the markdown "tunnel" and is never raised back up just because a sale came in. (If it goes
quiet again, the markdown resumes deepening.)

This is the strongest "get it moving" advisor for stuck inventory.

**Silent when:** the dead stock sits *only* in supplier warehouses (none held locally) — that's the
**Cross-dock advisor's** lane now (#11), not dead-stock's — **or**
when the stock is **freshly arrived**: if the oldest unit we hold has been in the warehouse less than
**30 days** (from the warehouse check-in log), a no-sales SKU is treated as a just-landed pre-order /
restock that hasn't had a chance to sell, not as dead stock. (This is separate from the new-product
hold, which only covers *platform-new* products — the age gate also catches restocks of existing SKUs.)

---

### 8. ~~Discount-effectiveness correction~~ — *retired*
Removed: a crude heuristic (raw velocity vs *today's* shelf discount) that ignored the discount
actually in effect during the window and over-fired on near-dead items. "Is this discount
working?" is now answered by the fitted **Price elasticity (#5)** signal, with the margin floor
as the backstop.

---

### 9. ~~Velocity-trend momentum~~ — *merged into #1*
The "is demand accelerating or decelerating" signal is now the trend modifier inside the
Sell-through advisor (#1).

---

### 10. ~~Supplier-vs-local stock positioning~~ — *retired*
Marginal in practice and overlapped #1. (Its concern — what to do with supplier-fulfilled stock — is now
handled properly by the **Cross-dock** advisor, #11.)

---

### 11. Cross-dock (supplier-fulfilled) markdown — *default weight 40*
**The situation:** We don't hold this in our own warehouse (`KsStock = 0`) but can sell it from supplier
stock (`SupplierStock > 0`) — a customer orders, we order from the supplier, we resell. Sell-through and
dead-stock both abstain on these (they need locally-held stock), and elasticity often can't fit (too little
price history), so without this lane a selling supplier SKU never gets a price and just sits at its anchor.

**What it does** — a sell-through / dead-stock **hybrid**, with the branch chosen by sales status:
- **Selling (`Qty90 > 0`)** → **hold** the working price (an active vote at today's price, so a co-voting
  margin-tier can't claw the working discount back up). It deepens one small step only when demand is
  *decaying* **and** there's comfortable margin room; thin margin → just hold. It never raises.
- **Not selling (`Qty90 == 0`)** → a **soft progressive markdown** from the current price toward the band
  margin floor as the no-sale streak grows (opens at **5%**, **+5pp every ~3 weeks** — gentler than
  dead-stock). It starts at today's price and only deepens; it never raises.
- **Priced below the floor** → lift it up to the floor and stop (the lane's only upward move).

**Key difference from dead-stock:** cross-dock is bounded by the **normal margin floor — never** the
below-floor (50%-of-cost) tunnel. Nothing is sunk (we never buy the unit), so a sale below the floor would
just lose money on every fulfilled order. There's no holding cost or aging risk pushing us lower.

**Low weight (40)** so it defers to elasticity (80) when both fire, but carries the vote when elasticity is
absent — the common case for supplier SKUs. Monotonic and sticky: when a marked-down SKU finally sells, it
flips to the "hold" branch and keeps the price that moved it.

> **Deep dive:** [`algorithms/cross-dock.md`](algorithms/cross-dock.md) — every input, every branch, and
> worked examples.

**Silent when:** we hold any local stock (that's sell-through / dead-stock's lane), there's no supplier
stock to sell, the SKU is platform-new, or its cost is unknown.

---

## How the votes become one price

Each advisor's vote carries two things: a **suggested price** and a **confidence** (0 to 1).

```
vote's pull  =  the band's weight for that algorithm  ×  the vote's confidence
final price  =  weighted average of all suggested prices, by their pull
```

- A confident vote from a heavily-weighted algorithm moves the result a lot.
- A tentative vote, or one from a low-weighted algorithm, barely nudges it.
- Algorithms that stayed silent simply don't participate.
- **If nobody votes, the price doesn't change.**

Because weights are set **per price band**, the same advisor can matter a lot for one tier of
products and be switched off for another.

---

## The guardrails (hard limits)

After the averaging, these non-negotiable limits are applied:

1. **Margin floor** — for normally-selling products the price can't drop below the level that
   still earns the band's minimum margin on cost. The **one exception** is locally-held dead
   stock (no sales in 90 days): there the dead-stock markdown is allowed *through* the floor,
   down to **50% of cost**, to clear inventory we're stuck with (see advisor #7). And once such
   a below-floor price starts selling, it's held there — never raised back.
2. **Anchor-price cap** — the price can't go above the anchor (FinalPrice). The tool proposes
   discounts off the true reference, not increases above it. (The display "shelf"/OldPrice no
   longer governs this.) If FinalPrice is missing or zero, the anchor falls back to the shelf
   price and the proposal is flagged (*"No FinalPrice — anchored to the shelf price"*), since the
   cap may then rest on an inflated reference.
3. *(Removed)* **Supplier-only stock is no longer frozen.** There used to be a guardrail that blocked any
   markdown on supplier-only stock that wasn't selling. That freeze has been **removed**: supplier-fulfilled
   SKUs are now priced deliberately by the **Cross-dock advisor** (#11), bounded by the normal margin floor
   above — a markdown may run down to the floor, but never below it (there's no sunk cost to recover, so the
   dead-stock tunnel doesn't apply).
4. **New-product hold** — while a product is in the platform's **MarkAsNew** window, its price is
   held exactly as-is (no discount, no change), overriding every algorithm. New launches are never
   touched until their new-product window ends.

> **Note:** There is **no discount ceiling**. For most products the margin floor is the sole
> brake on how low a price can land; for locally-held dead stock that brake is lowered to 50%
> of cost (a deliberate loss to clear stock we're stuck with).

**Special case:** if even the full shelf price doesn't meet the margin floor (a mis-priced
item), the tool holds the margin floor and **flags it for a human** — that situation needs a
person, not an automatic discount.

When a guardrail kicks in, the proposal is tagged (e.g. *"Raised to margin floor"*) so
reviewers can see exactly what happened.

---

## The finishing touch: rounding

Finally, the price is snapped to a tidy, psychological value — depending on the band's
setting this might be `.99` or `.95` endings, a whole number, "995-style" steps, or (for
currencies with no minor unit like MKD and ALL) whole-currency `…99` endings.

For cheap items the `.99` grid is too coarse — `0.99` vs `1.99` is a huge relative swing that
distorts margin — so **under €5 it tightens to a 10-cent `.x9` grid** (…0.99, 1.09, 1.19, 1.29),
keeping the charm look while the rounding step stays small relative to the price (a €1.21 input
lands on `1.19`). Ties go to the lower price; the €5 threshold is configurable.

Rounding is only applied if the rounded price **still respects the guardrails**. If rounding
would push the price out of bounds, it's skipped and the exact guarded price is kept.

---

## Where price bands fit in

Every SKU is sorted into a **price band** based on its **cost (PPTCV)** — not its selling
price. Each band carries its own:

- margin floor (the guardrail above),
- rounding style,
- and the on/off switch + weight for each algorithm.

So bands are the control panel: they let the team tune how aggressively each strategy applies
to cheap vs. expensive products, without changing any algorithm itself.

---

## One-line summary of each algorithm

| Algorithm | Pushes price… | When |
|---|---|---|
| Sell-through (velocity + inventory) | up if fast / selling out, down if slow | there's stock and some sales |
| Dead-stock progressive markdown | down (progressively) | zero sales in 90 days, still in **local** stock |
| Price elasticity (fitted) | to profit-max price `cost·E/(E+1)` | demand is provably elastic (weekly-fitted) |
| Margin-tier prioritization | down if fat margin, up if thin | margin is high or near the floor |
| Cross-dock (supplier-fulfilled) markdown | hold if selling, down (to the margin floor) if not | **no local stock**, supplier stock only |

*All numeric thresholds above are the current defaults and can be tuned. Discounts are always
measured against the full shelf price; margin = (price − PPTCV) / price, where PPTCV is the all-in
VAT-inclusive cost (purchase + transport + customs + VAT) — the same definition as the source GrossMargin.*
