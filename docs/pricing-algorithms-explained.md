# How the Pricing Tool Decides a Price — In Plain Words

This document explains, without any code, the *logic* behind each of the 10 pricing
algorithms and how they come together into one proposed price. It's written for anyone
on the team — you don't need to be an engineer to follow it.

---

## The big picture

For every product (SKU), once per run, the tool does this:

1. **Gathers the facts** — current price, full/shelf price, cost, stock on hand, and how
   many units sold over the last 7 / 14 / 30 / 60 / 90 days.
2. **Asks 10 "advisors" (the algorithms)** what the price should be. Each one looks at the
   facts from its own angle and either **votes** for a price or **stays silent** (says
   "no opinion").
3. **Blends the votes** into one number — a *weighted average*, where louder, more-confident
   advisors pull the result more.
4. **Applies hard limits (guardrails)** — never lose too much margin, never discount more
   than allowed, never price above the shelf price.
5. **Rounds to a nice-looking price** (e.g. ends in .99) — but only if rounding doesn't
   break the guardrails.

The result is a **proposal**, not a live price change. A human reviews and approves it, and
prices only go live through the explicit **Push** step.

> **Important:** The tool only ever proposes **discounts** (prices at or below the full
> shelf price). It never raises a price above the shelf price.

---

## A few words you'll see repeated

| Term | Plain meaning |
|---|---|
| **Shelf price (OldPrice)** | The full, "list" price — the starting point. All discounts are measured down from here. |
| **Current price** | What the product actually sells for today (may already be discounted). |
| **Cost (PPTCV)** | What the item costs us to buy. Used for margin. If it's missing, the SKU is skipped — we never guess it. |
| **Margin** | How much of the selling price is profit after cost (and after VAT is stripped out). |
| **Velocity** | Sales speed — units sold per day. We look at it over several time windows. |
| **Days-to-sellout** | At today's sales speed, how many days until the stock runs out. |
| **No-sale streak** | How many days in a row the item has sold nothing. |
| **Discount %** | How far below the shelf price we are (e.g. 30% off). "pp" means percentage points. |

Recent sales count more than old sales: when the tool measures "sales speed," it weights the
last 7 days at 50%, the last 14 days at 30%, and the last 30 days at 20%.

---

## The 10 algorithms

Each one is an independent advisor. It only speaks when its specific situation applies;
otherwise it abstains. The **default weight** (0–100) is how much its vote counts before
confidence is factored in — it can be tuned per price band, or turned off entirely.

---

### 1. Sales velocity + inventory forecast — *default weight 70*
**The question it asks:** "At the current selling speed, will this stock clear in a
reasonable time?"

It projects how many days until sellout, then nudges the discount accordingly:

- **Sells out within ~3 weeks** → *shave* the discount (we're selling fast; no need to give
  margin away).
- **On pace (3–6 weeks)** → hold steady.
- **Slow (1.5–3 months of stock)** → discount a little deeper (+3pp).
- **Very slow (3–6 months)** → deeper still (+6pp).
- **Over 6 months of stock** → real markdown pressure (+10pp).

**How sure it is:** more sales history = more confidence in the forecast.
**Silent when:** there's no stock, or nothing is selling at all (that's dead-stock territory).

---

### 2. New-product protection — *default weight 90*
**The idea:** Don't discount brand-new launches. If a product launched within the last 90
days, it votes for **full price (0% off)** with high confidence.

**Note:** Today's data doesn't include a launch date, so this advisor currently stays silent.
It will switch on once launch dates are available.

---

### 3. Warehouse-stock aging markdown — *default weight 50*
**The situation:** The item has stock, sold *something* in the last 90 days (so it's not
totally dead), but hasn't sold anything this week and has been quiet for at least a week.

**What it does:** Deepen the discount by **2pp for every week of silence**, up to +12pp.
The longer the dry spell, the more confident the vote.

This is the "gentle nudge for a slowing item" — distinct from full dead stock (#7).

---

### 4. Stockout-risk protection — *default weight 80*
**The insight:** If something is about to sell out *and* it's already making healthy margin,
discounting it just burns profit for no reason.

**What it does:** If projected to sell out within ~14 days **and** margin is comfortably above
the band's floor, it votes to **remove the discount** (go to full price). The sooner the
sellout, the stronger the conviction.

---

### 5. Price elasticity (heuristic) — *default weight 50*
**The question:** "When we discounted more, did customers actually buy more?"

It compares recent discounting (last 30 days) against the longer baseline (90 days), and
checks whether sales sped up:

- **We discounted deeper but sales barely moved** → demand here is *insensitive* to price →
  **pull the discount back** to the baseline level (we were giving money away).
- **Sales jumped clearly under the deeper discount** → demand *responds* to price →
  **protect the current (discounted) price**.

**Silent when:** recent and baseline discounting are about the same (nothing to compare), or
there's not enough sales data.

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

**What it does:** Start at **10% off** and add **5pp every two weeks** it stays unsold,
deepening the discount as far down as the margin floor allows (there's no discount ceiling
to stop it). It only ever marks *down* — it will never shrink a discount that's already in
place.

This is the strongest "get it moving" advisor for stuck inventory.

**Silent when:** the dead stock sits *only* in supplier warehouses (none held locally). We
don't discount stock we don't hold that isn't selling — see the supplier-stock guardrail
below.

---

### 8. Discount-effectiveness correction — *default weight 65*
**The watchdog:** "We're running a real discount — is it doing anything?"

If the item is currently at least 10% off, but its recent sales pace (last 14 days) is flat
versus the 90-day baseline, the discount isn't earning its keep → **halve it**. Stop giving
margin away for sales we'd have made anyway.

(Truly dead items are left to #7; this one is about *active but wasted* discounts.)

---

### 9. Velocity-trend momentum — *default weight 45*
**The idea:** Is demand speeding up or slowing down right now?

- **Accelerating** (last week's pace is ≥1.5× the 90-day pace) → demand is coming anyway →
  **trim the discount by a third**.
- **Decelerating** (last week is ≤0.5× the 90-day pace) → losing steam → **add a modest
  3pp discount** to hold volume.
- **Steady** → no opinion.

**Silent when:** there's too little sales history to trust the trend (fewer than 5 units in
90 days).

---

### 10. Supplier-vs-local stock positioning — *default weight 10 (low)*
**The angle:** *Where* the stock sits affects how fast we can fulfill it.

- **Mostly local stock (≥80%)** and **selling well** (≥10 in 30 days) → lean toward a
  **fuller price** (cut the discount to three-quarters).

This is a gentle tie-breaker, which is why its default weight is low. It only ever leans
*toward fuller price* now — it never discounts supplier-only stock, because we don't give
margin away on inventory that sits only in supplier warehouses and isn't selling.

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

After the averaging, three non-negotiable limits are applied:

1. **Margin floor** — the price can't drop below the level that still earns the band's
   minimum margin on cost. This is the *only* limit on how deep a discount can go.
2. **Shelf-price cap** — the price can't go above the full shelf price. The tool proposes
   discounts, not increases.
3. **No markdown on supplier-only dead stock** — if every unit sits in a supplier warehouse
   (none in our own) **and** nothing has sold in 90 days, the price is never marked *below*
   today's price. We don't give margin away on inventory we don't hold that isn't selling.
   (Raising the price back toward full is still allowed — only a net markdown is blocked.)
   This is enforced centrally, so it applies no matter which advisor proposed the discount.

> **Note:** There is **no discount ceiling**. Discounts may go as deep as the margin floor
> allows — the floor is the sole brake on how low a price can land.

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

Rounding is only applied if the rounded price **still respects the guardrails**. If rounding
would push the price out of bounds, it's skipped and the exact guarded price is kept.

---

## Where price bands fit in

Every SKU is sorted into a **price band** based on its **cost (PPTCV)** — not its selling
price. Each band carries its own:

- margin floor (the guardrail above),
- rounding style,
- and the on/off switch + weight for each of the 10 algorithms.

So bands are the control panel: they let the team tune how aggressively each strategy applies
to cheap vs. expensive products, without changing any algorithm itself.

---

## One-line summary of each algorithm

| # | Algorithm | Pushes price… | When |
|---|---|---|---|
| 1 | Sales velocity + inventory forecast | down if slow, up if fast | there's stock and some sales |
| 2 | New-product protection | up (full price) | freshly launched (≤90 days) |
| 3 | Warehouse-stock aging markdown | down | has stock, quiet ≥1 week, not fully dead |
| 4 | Stockout-risk protection | up (full price) | will sell out soon & margin is healthy |
| 5 | Price elasticity | back to baseline / hold | recent deeper discount, measured response |
| 6 | Margin-tier prioritization | down if fat margin, up if thin | margin is high or near the floor |
| 7 | Dead-stock progressive markdown | down (progressively) | zero sales in 90 days, still in **local** stock |
| 8 | Discount-effectiveness correction | up (halve discount) | discounting but sales are flat |
| 9 | Velocity-trend momentum | up if accelerating, down if slowing | enough sales to read a trend |
| 10 | Supplier-vs-local stock positioning | up (toward fuller price) | mostly-local stock selling well |

*All numeric thresholds above are the current defaults and can be tuned. Discounts are always
measured against the full shelf price; margins are always computed after VAT is removed.*
