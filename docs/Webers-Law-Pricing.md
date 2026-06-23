# Weber's Law and Price Perception — A Mathematical Treatment

> Context: written while deciding how to round Gjirafa50 prices. This version formalises the
> perceptual model and then derives the `Gj50Charm` rounding grid and selection policy as precise
> mathematical objects. Every definition here corresponds 1:1 to a method in
> `PricingTool.Core/Services/RoundingService.cs`; cross-references are given inline.

## 1. The perceptual model

### 1.1 Weber's Law

For a stimulus of intensity $I > 0$, let $\Delta I(I)$ be the *just-noticeable difference* (JND):
the smallest change a subject can reliably detect. **Weber's Law** (Ernst Weber, 1830s) asserts that
the JND is a constant *fraction* of the stimulus:

$$\frac{\Delta I(I)}{I} = k, \qquad k > 0 \text{ constant (the **Weber fraction**).}$$

Equivalently $\Delta I(I) = k\,I$ — the absolute threshold scales linearly with the stimulus. The
detectability of a change $\delta$ at intensity $I$ therefore depends only on the **ratio**
$\delta / I$, never on $\delta$ alone.

### 1.2 The Weber–Fechner law (derivation)

Fechner's extension models *perceived* magnitude $p(I)$ by assuming each JND contributes a constant
increment $dp$ of perceived intensity. Treating the JND as an infinitesimal,

$$dp = c\,\frac{dI}{I} \quad\Longrightarrow\quad p(I) = c \int_{I_0}^{I} \frac{dx}{x} = c\,\ln\!\frac{I}{I_0},$$

where $I_0$ is the perceptual threshold ($p(I_0)=0$). Perception is **logarithmic** in the stimulus.
Two consequences used below:

- **Ratio invariance.** $p(\lambda I) - p(I) = c\ln\lambda$ is independent of $I$: scaling every price
  by $\lambda$ shifts perceived magnitude by a constant. A rounding rule that is "perceptually
  uniform" must therefore act on **ratios**, i.e. be scale-equivariant.
- **Compression.** $p'(I) = c/I$ is decreasing, so a fixed absolute step $\delta$ is felt as
  $p(I+\delta)-p(I) \approx c\,\delta/I$ — shrinking as $I$ grows.

### 1.3 Application to price

Identify $I$ with price. A price change $\delta$ is "noticeable" iff $\delta / I \gtrsim k$:

| Base price $I$ | Change $\delta$ | $\delta/I$ | Detectable? |
|---------------:|----------------:|-----------:|-------------|
| €50    | €10 | 20%   | strongly |
| €200   | €10 | 5%    | yes |
| €1,000 | €10 | 1%    | marginal |
| €3,000 | €10 | 0.33% | no |

This is also why left-digit ("charm") pricing has magnitude-dependent strength: the perceptual gap
between an $X.99$ ending and the next round number $X+1$ is $\approx c/(X{+}1)$, large for small $X$
and negligible for large $X$ (€4,999 vs €5,000 is a $0.02\%$ gap).

## 2. Design objective

We want a rounding map $R:\mathbb{R}_{>0}\to\mathbb{R}_{>0}$ that (i) lands on a "clean" charm ending,
(ii) moves the price by a bounded **fraction** at every magnitude (so by §1.2 the perceptual cost is
uniform), and (iii) never leaves the engine's guardrail interval $[L,U]$ (margin floor $L$, anchor/
OldPrice cap $U$). Formally, fixing a Weber fraction $k$ (default $k=0.02$;
`DefaultRelativePrecision`), we require

$$\frac{|R(p) - p|}{p} \le k \quad\text{whenever feasible}, \qquad R(p)\in[L,U].$$

A **fixed-absolute** grid (e.g. "round to nearest €10") fails (ii): the relative move $10/p$ is $20\%$
at €50 and $0.3\%$ at €3,000. A **scale-equivariant** grid — step proportional to $p$ — satisfies it
by construction. That is the entire justification for the grid below.

## 3. The charm grid

### 3.1 Snap primitives

For a grid spacing $s>0$ and offset $o\in[0,s)$, define the largest/smallest lattice points of the
arithmetic progression $\{ns + o : n\in\mathbb{Z}\}$ bracketing $p$ (`SnapDown` / `SnapUp`):

$$\operatorname{down}_s^o(p) = \left\lfloor \tfrac{p-o}{s} \right\rfloor s + o, \qquad
  \operatorname{up}_s^o(p) = \left\lceil \tfrac{p-o}{s} \right\rceil s + o.$$

These satisfy $\operatorname{down}_s^o(p) \le p \le \operatorname{up}_s^o(p)$ with both ends in the
lattice $o + s\mathbb{Z}$, and $\operatorname{up} - \operatorname{down} \in \{0, s\}$.

### 3.2 Weber-scaled step selection

Let $S = \{1,5,10,25,50,100,250,500,1000\}$ be the admissible "nice" spacings (`CharmSteps`).
$2$ and $2.5$ are deliberately excluded: they generate off-charm endings under the offset below.
Define the **budget** $b(p) = k\,|p|$ and the step (`Gj50CharmStep`)

$$s(p) = \max\{\, \sigma \in S : \sigma \le b(p)\,\}, \qquad s(p) = 1 \text{ if the set is empty.}$$

So $s(p)$ is the coarsest admissible spacing within the Weber budget, floored at $1$. Pair it with the
offset

$$o(p) = s(p) - \tfrac12,$$

which places every lattice point exactly $0.50$ **below a multiple of the step** — the charm ending.
Examples: $s=1\Rightarrow$ endings $\dots.50$; $s=5\Rightarrow \dots4.50,\dots9.50$;
$s=100\Rightarrow \dots99.50$. The full down/up charm candidates (`Gj50CharmSnap`) are

$$d(p) = \operatorname{down}_{s(p)}^{o(p)}(p), \qquad u(p) = \operatorname{up}_{s(p)}^{o(p)}(p).$$

### 3.3 Relative-step bound

By definition of $s(p)$, for $p$ above the smallest admissible magnitude ($b(p)\ge 1$, i.e.
$p \ge 1/k \approx €50$ at $k=2\%$) we have $s(p) \le b(p) = k p$, hence the spacing — and therefore
any single-step move — is at most a $k$ fraction of price:

$$\frac{s(p)}{p} \le k.$$

Below that magnitude $s(p)=1$ is floored and $1/p > k$: one $0.50$ step exceeds the Weber budget. This
"sub-budget" regime is handled explicitly by the selection policy (§4, case C).

## 4. Selection policy (round-up biased)

The grid fixes *where* charm points lie; the **selection policy** $R(p)$ chooses between $d(p)$ and
$u(p)$. Standard conventions take the nearest. `Gj50Charm` instead **prefers the higher** point —
a higher price recaptures margin — subject to three guards. The whole procedure is
`Apply` → `SelectCharmUpBiased`.

### 4.1 Auxiliary magnitude functions

Leading place value (`LeadingMagnitude`):

$$m(p) = 10^{\lfloor \log_{10} |p| \rfloor} \qquad (m(47)=10,\ m(199)=100,\ m(1233)=1000).$$

**Salience modulus** $\mu(p) = m(p)/2$ — i.e. $5$ in the tens, $50$ in the hundreds, $500$ in the
thousands. A whole number is *salient* iff it is a multiple of $\mu(p)$. The two salience predicates
(each charm point is $0.50$ below a whole number $c+\tfrac12$):

$$
\text{JustBelow}(c,p) \iff (c + \tfrac12) \bmod \mu(p) = 0
\qquad(\text{`SitsJustBelowRoundNumber`}),
$$
$$
\text{JustAbove}(p) \iff (p - \tfrac12) \bmod \mu(p) = 0
\qquad(\text{`SitsJustAboveRoundNumber`}).
$$

Tying salience to $m(p)$ rather than to raw trailing-zero count is what keeps $R$ **monotone** across
the step transitions of $s(\cdot)$ (a trailing-zeros rule breaks monotonicity near $1250$).

### 4.2 Feasibility and tolerance

Guardrail feasibility of each candidate, and the Weber tolerance $\tau(p)=k p$:

$$
\text{downOk} \iff d \in [L,U] \wedge d > 0,\qquad
\text{upOk} \iff u \in [L,U],
$$
$$
\text{upIn} \iff \text{upOk} \wedge (u - p \le \tau),\qquad
\text{downIn} \iff \text{downOk} \wedge (p - d \le \tau).
$$

### 4.3 The decision function

**Pre-step (just-above pull).** If $d = u = p$ (the price already sits on a charm point) *and*
$\text{JustAbove}(p)$, shift the down candidate one step lower, $d \leftarrow d - s(p)$, and recompute
$\text{downOk}$. This lets e.g. $70.50 \mapsto 69.50$ and $100.50 \mapsto 99.50$ when the move fits the
tolerance.

Then:

$$
R(p) =
\begin{cases}
d & \text{(A) } \text{upIn} \wedge \text{downIn} \wedge \text{JustBelow}(d,p) \\[2pt]
u & \text{(A) } \text{upIn} \wedge \text{downIn} \wedge \neg\,\text{JustBelow}(d,p) \\[2pt]
u & \text{(B) } \text{upIn} \wedge \neg\,\text{downIn} \\[2pt]
d & \text{(B) } \text{downIn} \wedge \neg\,\text{upIn} \\[2pt]
\arg\min_{x\in\{d,u\}} |x-p| & \text{(C) both sub-tolerance, } d>0,\ \text{both feasible (tie}\to d) \\[2pt]
d \text{ or } u & \text{(C) sub-tolerance, only one feasible} \\[2pt]
\operatorname{clip}_{[L,U]}(p) & \text{(D) no feasible charm point — hold (skip flag set).}
\end{cases}
$$

Reading the cases:

- **(A) In-budget, both sides available.** Default to the higher point $u$ to claw margin, **unless**
  $d$ sits just below a salient round number — then landing on $u$ would be landing *just above* it,
  which is anti-charm, so take $d$. (Guard 2.)
- **(B) Only one side within tolerance.** Take it. Note $\text{upOk}$ already enforces $u \le U$, so
  guard 1 (anchor cap) is free; and rounding *up* can never fall below $L$, so the margin floor is
  always safe.
- **(C) Sub-budget regime** ($p \lesssim €25$, where $s=1$ and $1/p>k$ so neither side is within
  $\tau$). The round-up bias stands down; snap to the **nearest** charm point. The move is at most one
  $0.50$ step, and the result still ends in $.50$.
- **(D)** A pinned/degenerate band ($U \le L$, or no lattice point in $[L,U]$): hold $p$ clipped to
  the bounds; this is the only path that can yield a non-$.50$ result.

### 4.4 Guarantees

1. **Floor safety.** $R(p) \ge L$ in every case: cases A/B/C return a feasible candidate
   ($\ge L$ by `downOk`/`upOk`), and an up-move only increases price. *Rounding can never undo a
   guardrail clamp.*
2. **Tolerance.** In cases A/B, $|R(p)-p| \le \tau = kp$. In case C the move is $\le s(p)=1$ (one half-
   step). So the relative move is bounded by $\max(k,\,1/p)$ everywhere.
3. **Charm ending.** Outside the degenerate case (D), $R(p) \in o(p) + s(p)\mathbb{Z}$, i.e. it always
   ends in $.50$.
4. **Monotonicity.** $R$ is non-decreasing in $p$ (verified by a ~260k-point sweep test in
   `RoundingTests.cs`), which is why salience is defined via $m(p)$ rather than trailing zeros.

## 5. Worked examples (default $k = 2\%$)

| Engine price $p$ | $s(p)$ | $d / u$ | $R(p)$ | Case | Why |
|-----------------:|-------:|---------|-------:|:----:|-----|
| €1,233.23 | 10 | 1229.50 / 1239.50 | **1239.50** | A | $\neg$JustBelow(1229.50); up, +0.51% |
| €3,450.00 | 50 | 3449.50 / 3499.50 | **3499.50** | A | $\neg$JustBelow(3449.50); up, $+1.4\% \le k$ |
| €100.00   | 1  | 99.50 / 100.50    | **99.50**   | A | JustBelow(99.50): $100\bmod 50=0$ → down |
| €199.80   | 1  | 199.50 / 200.50   | **199.50**  | A | JustBelow(199.50): $200\bmod 50=0$ → down ("1xx→2xx") |
| €1,000.00 | 10 | 999.50 / 1009.50  | **999.50**  | A | JustBelow(999.50): $1000\bmod 500=0$ → down |
| €70.50    | 1  | 69.50 / 70.50     | **69.50**   | pre+A | JustAbove(70.50): $70\bmod 5=0$ → pull down |
| €7.20     | 1  | 6.50 / 7.50       | **7.50**    | C | $1/7.2\!>\!k$ → nearest .50 |
| €20.00    | 1  | 19.50 / 20.50     | **19.50**   | C | sub-budget → nearest .50 (tie → down) |

Note $1250$ is **not** salient under $\mu$ ($1250 \bmod 500 \ne 0$), so it rounds up like its
neighbours instead of dropping below them — the monotonicity point.

## 6. Relation to the Power BI `GJ50 KS FP Final` column

The legacy PBI logic rounded in three hard tiers:

| Range        | Rule                       | Step $s$ | $s/p$ at lower edge |
|--------------|----------------------------|---------:|--------------------:|
| €300–1,500   | $\operatorname{round}(p/10)\cdot10-0.5$  | 10  | $10/300 \approx 3.3\%$ |
| €1,500–3,000 | $\operatorname{round}(p/50)\cdot50-0.5$  | 50  | $50/1500 \approx 3.3\%$ |
| €3,000+      | $\operatorname{round}(p/100)\cdot100-0.5$| 100 | $100/3000 \approx 3.3\%$ |

All three cliffs sit at $s/p \approx 3.3\%$ — the PBI author was already targeting a constant Weber
fraction, but discretised it into three steps. That introduces (a) boundary artifacts (two near-equal
items split across €1,500 get different $s$) and (b) oversized moves at the top of each tier
($s/p \to 3.3\%$ at the edge but $\to s/3p$, smaller, just inside). `Gj50Charm` replaces the three
cliffs with the smooth selection $s(p)=\max\{\sigma\in S:\sigma\le kp\}$ ($k$ tunable), adds the
salience/monotonicity guards, and runs inside the engine's $[L,U]$ guardrails — which the raw PBI
column did not, so it could (and did, on ~37% of KS SKUs) snap prices below the margin floor.

## 7. Caveats — where the model stops applying

1. **Left-digit effect is discrete, not Weberian.** Weber–Fechner is smooth/logarithmic; the $X.99$
   anchor is a discontinuity at round-number boundaries. The two compose here: Weber bounds *how far*
   to move ($\tau$), charm endings choose *where* to land.
2. **Prestige inversion.** For high-value, emotion-driven goods, round "prestige" endings can beat
   charm. Above some threshold dropping the $-0.50$ for a clean round number may convert better.
3. **$k$ is not universal.** The Weber fraction varies by category, customer, and context; $k=2\%$
   (`PricingEngineOptions.CharmRelativePrecision`) is a sensible catalog default and the one knob
   worth A/B-testing.

## Sources

- [Weber–Fechner law — Wikipedia](https://en.wikipedia.org/wiki/Weber%E2%80%93Fechner_law)
- [Just-noticeable difference — Wikipedia](https://en.wikipedia.org/wiki/Just-noticeable_difference)
- [Weber's Law in pricing — kimhatton.com](https://kimhatton.com/unlocking-the-pricing-puzzle-the-power-of-webers-law/)
- [Charm pricing — Price2Spy](https://www.price2spy.com/blog/charm-pricing/)
- [Psychological pricing — Wikipedia](https://en.wikipedia.org/wiki/Psychological_pricing)
</content>
</invoke>
