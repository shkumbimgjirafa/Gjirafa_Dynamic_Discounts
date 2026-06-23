# GJ50 KS — `.50` Rounding in the Pricing Platform

**Scope:** Gjirafa50, Kosovo (KS) layer. How the platform should produce Gjirafa50's signature
**`.50`** price ending, and why the old Power BI logic should **not** be ported.

---

## 1. Where rounding lives in the platform

`PriceCalculator.Decide` runs this pipeline per SKU:

```
algorithms → weighted scoring (incl. fitted elasticity) → guardrail clamp → psychological rounding
```

**Profit is already maximized upstream**, in scoring + elasticity. By the time we reach rounding,
`clamp.Price` is the engine's profit-optimal target. `RoundingService.Apply` then:

- generates a **down** and an **up** candidate for the band's `RoundingConvention`,
- **rejects** any candidate outside the guardrail bounds (lower = margin floor, upper = OldPrice cap),
  so *rounding can never undo a clamp*,
- picks the candidate **closest to the scored price** (tie → lower).

## 2. Why the old Power BI logic must not be ported

The PBI `GJ50 KS FP Final` column did crude price-setting *and* charm rounding in one step, with no
elasticity:

- It forced the integer digit to 4 or 9 and pulled `104.5→99.5`, `204.5→199.5`.
- Measured live (325,364 KS SKUs): **119,302 (37%)** were snapped *below* their own `.50`-rounded
  price, giving away **≈406k** of margin — e.g. an intended **106.31** sold at **99.50**.

In the platform that behaviour is **wrong twice over**:
1. Forcing the price down to a charm point ignores the margin floor / OldPrice guardrails.
2. The engine already found the profit-optimal price via elasticity. Pushing it up *or* down to a
   charm digit moves it **off** that optimum — that's lost profit, not gained.

So the rounding layer's only job for KS is: **snap to the nearest `.50`, staying inside guardrails.**
Nothing more. (An earlier "always round up to claw back margin" idea is also wrong here for the same
reason — it overshoots the elasticity-optimal point.)

## 3. The change (implemented)

Added a first-class convention — no new math, it reuses the existing `SnapDown/SnapUp(step, offset)`
helper on a whole-currency + `0.50` grid:

- `RoundingConvention.EndsIn50 = 6` (`Domain/RoundingConvention.cs`)
- `RoundingService.RoundDown/RoundUp`: `EndsIn50 => Snap{Down,Up}(price, 1m, 0.50m)`
- Tests in `RoundingTests.cs` (down/up/nearest-pick + added to the guardrail-safety property test)

Behaviour: `47.31 → 47.50`, `47.80 → 47.50`, `100.00 → 99.50` (tie → lower; **no** forced "snap to
99" give-away). Always ends in `.50`; never breaches the margin floor or OldPrice cap because the
existing bounds check owns that.

## 4. Remaining step — assign `EndsIn50` to the KS layer (needs sign-off)

KS bands are currently seeded with `EndsIn99 / WholeEuro / Charm995` (`DbSeeder.BandSeeds`, with the
`nonEur → EndsIn99Hundreds` override). To switch KS to the `.50` signature, set `EndsIn50` on the KS
layer's bands. This is a **live pricing-output change**, so it needs:

- a decision on whether **all** KS bands use `.50`, or only some price ranges;
- a band-seed update + EF migration (or a config change via the Bands admin screen);
- confirmation that the `nonEur` override path doesn't apply to KS (KS = EUR, so it shouldn't).

## 5. Open data issue (separate from rounding)

Live KS data has **7,414 SKUs where the discounted price exceeds the list/OldPrice**. In the platform
the OldPrice cap hides this, but it points to an upstream discount/cost mismatch worth its own ticket.

## 6. Future — elasticity is already the profit lever

No extra "profit-aware rounding" is needed: the profit optimization is the scoring + fitted-elasticity
stage, not rounding. If charm endings (e.g. `…9.50`) ever prove to lift conversion enough to be worth
a deliberate deviation, that belongs in scoring as a signal — not as a margin-bleeding rounding rule.
