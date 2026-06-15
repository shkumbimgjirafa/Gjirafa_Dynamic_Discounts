# GjirafaMall AI Dynamic Pricing Tool — v1

In-house automated dynamic pricing for GjirafaMall (Kosovo store, selected vendors).
Recalculates **proposed** prices on a configurable schedule (default daily 03:00 UTC) from
margin, demand, inventory and historical sales — profit-first: hold sales volume, lift margin 25%+.

**The engine never writes to live platform prices.** It writes proposals to its own database.
Pushing approved prices to the platform is a separate, explicit, Manager-triggered step.

## Solution layout

| Project | Purpose |
|---|---|
| `PricingTool.Core` | Domain: the 10 pricing algorithms, weighted scoring, guardrails, VAT math, psychological rounding, demo data generator |
| `PricingTool.Data` | EF Core (migrations, `PricingTool` schema), source dataset readers, run orchestrator, CSV push integration, audit |
| `PricingTool.Engine` | Background worker — scheduled recalculation (reads schedule from DB, admin-editable live) |
| `PricingTool.Web` | ASP.NET Core admin UI + impact dashboard (Identity auth, Analyst/Manager roles) |
| `PricingTool.Tests` | xUnit suite (92 tests): every algorithm incl. 0-vs-NULL handling, guardrails, VAT reconciliation, rounding-never-violates-guardrails, weighted scoring, orchestrator policies |

## Quick start (demo mode — no source database needed)

Demo mode is **on by default** (`PricingEngine:UseDemoData = true`). It fabricates a realistic
catalog (~360 SKUs across all bands: fast movers, dead stock, discount-insensitive, missing-cost…)
and backfills 35 days of snapshot history on first boot so the dashboard has trends.

Requirements: .NET 8 SDK, SQL Server (LocalDB is fine — default connection strings use it).

```bash
dotnet run --project PricingTool.Web
```

1. Open the printed URL — the app loads straight to the dashboard. **Authentication is disabled**
   (interim dev shim) until Gjirafa's Porta SSO is connected, so there is no login and every page
   is accessible. See [Authentication](#authentication) below.
2. **Schedule → Run now** executes a full pricing cycle (pull → snapshot → propose).
3. **Proposals** — review, filter, approve (changes >±20% require explicit confirmation), then
   **Push** writes the approved-prices CSV to `exports/` (the v1 integration point).
4. **Dashboard** — margin/revenue/volume trends vs baseline, algorithm/band attribution, health flags.

The Engine worker (`dotnet run --project PricingTool.Engine`) is only needed for *scheduled*
runs; on-demand runs work from the Web app alone. Both can run side by side — a DB-level guard
prevents overlapping runs.

CLI escape hatch: `dotnet run --project PricingTool.Engine -- --run-now` executes one full
pricing run immediately (applying migrations/seeding first) and exits — handy for ops, cron,
and smoke tests.

## Production setup

### 1. Databases & connection strings

Two connection strings (in `appsettings.json` or environment variables
`ConnectionStrings__PricingToolDb` / `ConnectionStrings__SourceReadOnly`):

- **`PricingToolDb`** — the tool's own database (read/write). All tables live in the
  `PricingTool` schema. Migrations apply automatically on startup
  (or manually: `dotnet ef database update --project PricingTool.Data`).
- **`SourceReadOnly`** — the live platform server (GjirafaMall + GjirafaTranslations).
  Use a SQL login with **read-only / execute-only** rights; add `ApplicationIntent=ReadOnly`
  where applicable. The tool only ever reads from it.

No secrets in code — use environment variables or a secret store for real credentials.

### 2. Deploy the dataset stored procedure

Run [`scripts/usp_GetDailyPricingDataset.sql`](scripts/usp_GetDailyPricingDataset.sql) on the
source server and grant `EXECUTE` to the read-only login. This script contains the **required
fix**: `@now` is captured once and used consistently throughout (the original ad-hoc query
called `GETUTCDATE()` separately inside the discount `OUTER APPLY`).

If a stored procedure cannot be created, set `SourceDataset:Mode = "InlineQuery"` and the tool
runs the same corrected query verbatim.

### 3. Turn off demo mode

```json
"PricingEngine": { "UseDemoData": false }
```

### 4. Run

- `PricingTool.Web` — admin UI/dashboard (also serves on-demand runs).
- `PricingTool.Engine` — host as a Windows service / container for the scheduled daily run.

## Configuration reference (`PricingEngine` section)

| Key | Default | Meaning |
|---|---|---|
| `VatRatePct` | `18.0` | Kosovo standard VAT — **confirm with the team**. Shelf prices are VAT-inclusive; costs (PPTCV) and net revenue are VAT-exclusive. All margin math reconciles through this single value (`VatMath`). |
| `StockoutRiskDays` | `14` | Algorithm 4 horizon: projected sellout within N days + healthy margin → vote discount off |
| `NewProductProtectionDays` | `90` | Algorithm 2 window (inactive until a launch-date source exists — open decision) |
| `ChangeConfirmationThresholdPct` | `20.0` | Proposals beyond ±this % require explicit confirmation at approval (enforced server-side) |
| `UseDemoData` | `true` | Replace the SQL source reader with the demo generator |
| `DefaultRunTimeUtc` / `DefaultCadenceHours` | `03:00` / `24` | Seeds the schedule on first boot; afterwards the schedule is edited in the UI (stored in `ToolSettings`) |
| `PushExportDirectory` | `exports` | Where the v1 CSV push integration writes approved prices |

Per-band knobs (margin floor, discount ceiling, rounding convention + toggle, per-algorithm
enable/weight 0–100) are **data**, edited on the Price Bands page. Per-SKU rounding opt-outs
live in `SkuOverrides`.

## How a pricing run works

1. Pull the daily dataset over the read-only connection (one row per in-scope SKU).
2. Snapshot the full pull into `DailySnapshots` (history for the dashboard + aging signals;
   a same-day re-pull replaces that day's snapshot, and proposals keep their own copy of inputs).
3. Per SKU: skip & flag if cost is NULL (**never treated as zero**), price missing, or no band
   matches (**bands key off PPTCV/cost**, not the selling price); otherwise run every
   band-enabled algorithm → weighted average of votes
   (band weight × vote confidence) → **guardrail clamp** (margin floor with VAT reconciliation,
   discount ceiling, OldPrice cap) → **psychological rounding** that never violates the
   guardrails.
4. Write `ProposedPrices` + every `AlgorithmVotes` row, wrapped in a `PricingRuns` record
   (status, SKU/error counts) — failures and partial runs stay visible.
5. Humans take it from there: review → approve → push (CSV export via `IPricePushService`).

### The 10 algorithms

`VELOCITY_FORECAST`, `NEW_PRODUCT`, `STOCK_AGING`, `STOCKOUT_RISK`, `ELASTICITY`,
`MARGIN_TIER`, `DEAD_STOCK`, `DISCOUNT_EFFECTIVENESS`, `MOMENTUM`, `SUPPLIER_LOCAL` —
each an `IPricingAlgorithm` in `PricingTool.Core/Algorithms`, individually toggleable and
weighted per band. Algorithms return `null` when they have no opinion; if nothing votes,
the price stays unchanged.

Aging ("consecutive snapshot days of no movement") is derived from the tool's own snapshot
history: consecutive daily snapshots with zero trailing-7d sales.

## Authentication

**Authentication is intentionally disabled in this build** — it will be provided by Gjirafa's
**Porta** SSO later. In the meantime a dev shim ([`DevAuthHandler`](PricingTool.Web/Services/DevAuthHandler.cs))
auto-signs every request in as a single `demo` user holding both roles, so the app opens with no
login and nothing is gated. There are no ASP.NET Identity tables or accounts.

**Wiring in Porta later:** replace the `AddAuthentication(...).AddScheme<…DevAuthHandler>`
registration in [`Program.cs`](PricingTool.Web/Program.cs) with Porta's scheme (e.g. OpenID
Connect) and map its claims to the `Analyst`/`Manager` roles below. The `[Authorize(Roles = …)]`
markers already on the controllers then resume enforcing with no other code changes.

### Roles (enforced once Porta is connected)

- **Analyst** — view everything, trigger on-demand runs (simulations; runs only produce proposals).
- **Manager** — everything + edit bands/schedule, approve/reject, push.

## Tests

```bash
dotnet test
```

## Open decisions (v1 defaults implemented)

1. **Objective function nuance** — strict total-profit max vs margin tie-break: deferred to
   configuration via per-band algorithm weights.
2. **Product launch/creation date source** — dataset has none; the snapshot column is nullable
   and Algorithm 2 stays silent until it's populated.
3. **Final price band boundaries** — seeded €0–10 / 10–50 / 50–100 / 100–250 / 250–500 /
   500–750 / 750–1,000 / 1,000+ are **placeholders**; bands 2–7 must be confirmed before go-live
   (editable in the UI).
4. **Competitor pricing** — phase 2 (no competitor data in the dataset). Seasonality likewise
   phase 2 (needs >90d of accumulated snapshots — which this tool is now collecting).
5. **Smoothing limits** beyond the ±20% confirmation threshold — not implemented in v1.
6. **Push mechanism into NopCommerce** — `IPricePushService` is the integration point; v1 ships
   `CsvPricePushService` (file export). The platform team replaces the DI registration in
   `PricingTool.Data/DependencyInjection.cs` when the real write-back mechanism is decided.

## Safety rules recap (non-negotiable, implemented)

1. Engine writes proposals only; the push step is explicit, human, Manager-gated.
2. Source data via a dedicated read-only connection; tool tables in their own DB/schema.
3. Every pull snapshotted (`DailySnapshots`).
4. Every config change and every push audited (who/what/when/old/new) — Audit Log page.
5. Every run wrapped in a `PricingRuns` record with status and error counts.
