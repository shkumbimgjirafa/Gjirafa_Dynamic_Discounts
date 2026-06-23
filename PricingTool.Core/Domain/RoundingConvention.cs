namespace PricingTool.Core.Domain;

public enum RoundingConvention
{
    /// <summary>No psychological rounding; price is only normalized to 2 decimals.</summary>
    None = 0,

    /// <summary>Prices end in .99 (e.g. 7.99, 49.99). Typical for low bands.</summary>
    EndsIn99 = 1,

    /// <summary>Prices end in .95 (e.g. 7.95).</summary>
    EndsIn95 = 2,

    /// <summary>Whole euro amounts (e.g. 120).</summary>
    WholeEuro = 3,

    /// <summary>
    /// Multiples of 5 EUR; round-hundred results step down to X95 (e.g. 1000 -> 995, 1100 -> 1095).
    /// Intended for the EUR 1,000+ bands.
    /// </summary>
    Charm995 = 4,

    /// <summary>
    /// Whole-currency prices whose last two digits are 99 (e.g. 6199, 9999). For currencies with no
    /// minor unit / large nominal values (MKD, ALL) where the .99/.95 EUR conventions don't apply.
    /// </summary>
    EndsIn99Hundreds = 5,

    /// <summary>
    /// Prices end in .50 (e.g. 47.50, 149.50) — Gjirafa50's signature ending. The grid is whole
    /// currency + 0.50 (step 1, offset .50). Selection and guardrail safety are handled by the
    /// standard <see cref="RoundingService.Apply"/> nearest-candidate logic, so the snapped price
    /// stays on the engine's profit-optimal target and never breaches the margin floor / OldPrice cap.
    /// </summary>
    EndsIn50 = 6,

    /// <summary>
    /// Gjirafa50 charm rounding — the platform version of the old Power BI "GJ50 KS FP Final" logic.
    /// Two independent ideas combined:
    ///
    /// <para><b>Grid (Weber's Law):</b> the charm granularity scales with price magnitude so the
    /// snap is always a roughly constant fraction (~<c>relativePrecision</c>, default 2%) of the
    /// price — no hard cliffs. The step is the coarsest "nice" value (1, 5, 10, 25, 50, 100, 250,
    /// 500, 1000) that fits the budget, and every charm point is <c>(multiple of step) − 0.50</c>:
    /// step 1 → …50, step 5 → …4.50/…9.50, step 10 → …9.50, step 100 → …99.50, etc. Every ending is
    /// "just below a round number". See <c>docs/Webers-Law-Pricing.md</c>.</para>
    ///
    /// <para><b>Selection (round-up biased):</b> unlike the other conventions (nearest), this one
    /// prefers the <i>higher</i> charm point to claw back margin — but only when it makes sense:
    /// (1) it stays at/below the anchor / OldPrice cap (enforced by the bounds check),
    /// (2) it does not land just above a salient round number (no 199.80 → 200.50 "1xx → 2xx" jump), and
    /// (3) the up-move stays within the Weber tolerance. The result <b>always ends in .50</b>: when no
    /// charm point fits the budget (cheap items, where one .50 step exceeds the tolerance) it snaps to
    /// the nearest .50 instead of clawing up. Only a pinned/narrow guardrail band can yield a non-.50
    /// hold.</para>
    /// </summary>
    Gj50Charm = 7,

    /// <summary>
    /// GjirafaMall charm rounding — Power BI's <b>…49 / …99</b> endings, selected the platform way:
    /// <list type="number">
    /// <item><b>Round-ten euro</b> (integer part a multiple of 10 — 50.xx, 120.xx, 100.xx, 1000.xx):
    /// pull <b>down</b> across the ten to the …9.99 just below it (50.30 → 49.99, 120.88 → 119.99,
    /// 100.00 → 99.99) — never a …0.99. (Falls back to the nearest …49/…99 when the pull exceeds the
    /// Weber budget, e.g. cheap 10.40 → 10.49.)</item>
    /// <item>Otherwise <b>round up to this euro's …99</b> when the Weber budget (k·price) reaches it
    /// (45.40 → 45.99), clawing margin; if it can't afford the jump, the nearest …49/…99.</item>
    /// </list>
    /// Clamped to the guardrail bounds (margin floor / OldPrice cap).
    ///
    /// <para>Unlike the raw PBI column — which forces the …99 at any distance (123.76 → 124.99) — the
    /// up-jump here is bounded by the Weber tolerance, so 123.76 → 123.99.</para>
    ///
    /// <para><b>Low-price exception:</b> at/below <c>lowPriceThreshold</c> (€5) it snaps to the
    /// <b>nearest 10 cents</b> (0.10 grid) — the GjirafaMall low-price rule.</para>
    /// </summary>
    GjmCharm = 8,
}
