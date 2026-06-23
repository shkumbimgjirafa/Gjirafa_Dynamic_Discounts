# Rounding comparison — Power BI vs new `Gj50Charm`

How the new engine rounding (`RoundingConvention.Gj50Charm`, default 2% Weber precision)
compares to the old Power BI `GJ50 KS FP Final` calculated column, across price ranges.

> Both methods always produce a `.50` ending. Pure rounding only: wide guardrail bounds
> (no margin-floor / OldPrice cap), and the PBI `min(result, list price)` cap is omitted, so
> this shows each method's rounding *shape*. The same input price is fed to both.

## Below €10

| Input price | Power BI | New `Gj50Charm` | Difference |
|------------:|---------:|----------------:|:-----------|
| €4.99 | €4.50 | €4.50 | same |
| €7.20 | €7.50 | €7.50 | same |
| €9.50 | €9.50 | €9.50 | same |

*Both always end in .50. The new engine snaps cheap items to the nearest .50 (the round-up bias stands down when a .50 step exceeds the 2% budget).*

## €10 – €50

| Input price | Power BI | New `Gj50Charm` | Difference |
|------------:|---------:|----------------:|:-----------|
| €12.30 | €12.50 | €12.50 | same |
| €27.40 | €27.50 | €27.50 | same |
| €49.90 | €49.50 | €49.50 | same |

*Both end in .50 and match closely; the new engine snaps to nearest .50 under budget.*

## €50 – €300

| Input price | Power BI | New `Gj50Charm` | Difference |
|------------:|---------:|----------------:|:-----------|
| €89.90 | €89.50 | €89.50 | same |
| €100.00 | €99.50 | €99.50 | same |
| €249.99 | €249.50 | €249.50 | same |

*The 4.50/9.50 charm range. Round numbers stay just below (100 → 99.50), never just above.*

## €300 – €1,500

| Input price | Power BI | New `Gj50Charm` | Difference |
|------------:|---------:|----------------:|:-----------|
| €349.00 | €349.50 | €349.50 | same |
| €612.30 | €609.50 | €619.50 | +10.00 (higher) |
| €1,233.23 | €1,229.50 | €1,239.50 | +10.00 (higher) |

*PBI snaps to nearest 10; the new engine rounds up on a proportional grid.*

## €1,500 – €3,000

| Input price | Power BI | New `Gj50Charm` | Difference |
|------------:|---------:|----------------:|:-----------|
| €1,750.00 | €1,749.50 | €1,774.50 | +25.00 (higher) |
| €1,999.00 | €1,999.50 | €1,999.50 | same |
| €2,750.00 | €2,749.50 | €2,799.50 | +50.00 (higher) |

*PBI snaps to nearest 50; the new engine claws margin up within budget.*

## €3,000+

| Input price | Power BI | New `Gj50Charm` | Difference |
|------------:|---------:|----------------:|:-----------|
| €3,450.00 | €3,399.50 | €3,499.50 | +100.00 (higher) |
| €5,000.00 | €4,999.50 | €4,999.50 | same |
| €8,990.00 | €8,999.50 | €8,999.50 | same |

*PBI snaps to nearest 100 (can drop a lot); the new engine stays proportional.*

