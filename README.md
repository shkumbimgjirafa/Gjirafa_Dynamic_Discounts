# Gjirafa AI Dynamic Pricing Tool — v1

In-house automated dynamic pricing for Gjirafa's e-commerce stores. Recalculates **proposed**
prices on a configurable schedule (default daily 03:00 UTC) from margin, demand, inventory and
historical sales — profit-first: hold sales volume, lift margin 25%+.

**The engine never writes to live platform prices.** It writes proposals to its own database.
Pushing approved prices to the platform is a separate, explicit, Manager-triggered step.

## Layers (multi-store)

The tool is **multi-layer**: a *layer* is a Brand × Country combination, and everything — the
source pull, snapshots, proposals, price bands, schedule and dashboard — is scoped to the
selected layer. Six layers ship seeded and active:

| Brand | Country | Store DB | StoreId | TranslationCountryId | Warehouse | Currency | Vendor filter |
|---|---|---|---|---|---|---|---|
| GjirafaMall | KS | `GjirafaMall` | 2 | 1 | 2 | EUR | Gjirafa/Dino Toys/Mysu/Apple |
| GjirafaMall | MK | `GjirafaMall` | 1 | 3 | 1 | MKD | (same set) |
| GjirafaMall | AL | `GjirafaMall` | 3 | 2 | 3 | ALL | (same set) |
| Gjirafa50 | KS | `GjirafaEcommerce` | 2 | 1 | 2 | EUR | **all vendors** |
| Gjirafa50 | MK | `GjirafaEcommerce` | 1 | 3 | 1 | MKD | **all vendors** |
| Gjirafa50 | AL | `GjirafaEcommerce` | 3 | 2 | 3 | ALL | **all vendors** |

- The source query reads the operational data (vendors, products, stock, orders, tier prices,
  discounts) from the layer's **operational database** (`GjirafaMall` ↔ `GjirafaEcommerce`,
  substituted into the query per layer) and cost/margin from the **shared** `GjirafaTranslations`
  database keyed by `TranslationCountryId`.
- Each layer carries its own price bands, per-algorithm weights and schedule. Switch the active
  layer from the brand → country dropdown in the nav; it's remembered in your session.
- Runs are **per-layer but serialized globally** (one run at a time across all layers — the
  bulk-write path is not concurrency-safe). The scheduler fires each active layer on its own slot.
- **Excluded from every layer:** outlet products (`Product.IsOutlet = 1`, priced by a separate
  system) and products not published in the layer's store (`UnpublishedStoreids`).

The `Layer` rows (IDs, currency, toggles, schedule) are seeded by `DbSeeder` and the `AddLayers`
migration; activating/deactivating or retuning a layer is a data edit (no redeploy).

## Solution layout

| Project | Purpose |
|---|---|
| `PricingTool.Core` | Domain: the 5 pricing algorithms, weighted scoring, guardrails (incl. new-product hold), VAT math, psychological rounding, demo data generator |
| `PricingTool.Data` | EF Core (migrations, `PricingTool` schema), per-layer source dataset readers, run orchestrator, bulk writer, CSV push integration, audit |
| `PricingTool.Engine` | Background worker — scheduled recalculation, looping active layers (schedule read per layer from DB, admin-editable live) |
| `PricingTool.Web` | ASP.NET Core admin UI + impact dashboard, layer switcher (dev-shim auth, Analyst/Manager roles) |
| `PricingTool.Tests` | xUnit suite (182 tests): every algorithm incl. 0-vs-NULL handling, guardrails, gross-margin & VAT math, profit/margin KPIs, rounding-never-violates-guardrails (all conventions), weighted scoring, orchestrator policies |

## Quick start (demo mode — no source database needed)

Demo mode is **on by default** (`PricingEngine:UseDemoData = true`). It fabricates a realistic
catalog (~360 SKUs across all bands: fast movers, dead stock, discount-insensitive, missing-cost…)
**for every active layer** and backfills 35 days of snapshot history on first boot so each layer's
dashboard has trends.

Requirements: .NET 8 SDK, SQL Server (LocalDB is fine — default connection strings use it).

```bash
dotnet run --project PricingTool.Web
```

1. Open the printed URL — the app loads straight to the dashboard for the default layer
   (**GjirafaMall — Kosovo**). **Authentication is disabled** (interim dev shim) until Gjirafa's
   Porta SSO is connected, so there is no login and every page is accessible. See
   [Authentication](#authentication).
2. Use the **layer switcher** (top-right, brand → country) to scope the whole app to another layer.
3. **Schedule → Run now** executes a full pricing cycle for the current layer (pull → snapshot → propose).
4. **Proposals** — review, filter, approve (changes >±20% require explicit confirmation), then
   **Push** writes the approved-prices CSV to `exports/` (the v1 integration point).
5. **Dashboard** — margin/revenue/volume trends vs baseline, algorithm/band attribution, health flags.

The Engine worker (`dotnet run --project PricingTool.Engine`) is only needed for *scheduled*
runs; on-demand runs work from the Web app alone. Both can run side by side — a DB-level guard
serializes runs.

CLI escape hatch: `dotnet run --project PricingTool.Engine -- --run-now [--layer <code>]` executes
one full pricing run immediately (applying migrations/seeding first) and exits. With no `--layer`
it runs **all active layers** sequentially; `--layer` accepts `MK`, `GjirafaMall/MK`, or the
display name. Handy for ops, cron, and smoke tests.

## Production setup

### 1. Databases & connection strings

Two connection strings (in `appsettings.json` or environment variables
`ConnectionStrings__PricingToolDb` / `ConnectionStrings__SourceReadOnly`):

- **`PricingToolDb`** — the tool's own database (read/write). All tables live in the
  `PricingTool` schema. Migrations apply automatically on startup
  (or manually: `dotnet ef database update --project PricingTool.Data`).
- **`SourceReadOnly`** — the live platform **server** (hosting `GjirafaMall`, `GjirafaEcommerce`
  and the shared `GjirafaTranslations`). Use a SQL login with **read-only / execute-only** rights;
  add `ApplicationIntent=ReadOnly` where applicable. The operational database is selected per layer
  by substituting the catalog name into the query, so the connection's `InitialCatalog` is
  irrelevant as long as the login can read all three databases.

No secrets in code — use environment variables or a secret store for real credentials. (For local
dev, `EnvFileLoader` reads a gitignored `.env` with `SOURCE_DB_*` values and flips demo mode off.)

### 2. Deploy the dataset stored procedure (optional)

Run [`scripts/usp_GetDailyPricingDataset.sql`](scripts/usp_GetDailyPricingDataset.sql) on the
source server and grant `EXECUTE` to the read-only login. It takes the per-layer parameters
(`@StoreId`, `@TranslationCountryId`, `@WarehouseStoreId`, `@FilterVendors`, `@ExcludeUnpublished`).

**Important:** the compiled procedure reads the `GjirafaMall` operational database by name and
**cannot switch to `GjirafaEcommerce`**, so **Gjirafa50 layers require inline-query mode**. Set
`SourceDataset:Mode = "InlineQuery"` and the tool runs the same query verbatim, substituting the
operational database per layer (`{opDb}` token). Inline mode is the source of truth.

### 3. Turn off demo mode

```json
"PricingEngine": { "UseDemoData": false }
```

### 4. Run

- `PricingTool.Web` — admin UI/dashboard (also serves on-demand runs).
- `PricingTool.Engine` — host as a Windows service / container for the scheduled daily runs
  (iterates active layers).

## Configuration reference (`PricingEngine` section)

| Key | Default | Meaning |
|---|---|---|
| `VatRatePct` | `18.0` | Default/fallback VAT. The **effective rate is per layer** (`Layer.VatRatePct`: 18 for KS/MK, 20 for AL). Prices **and** PPTCV (the all-in landed cost: purchase + transport + customs + VAT) are VAT-**inclusive**, so margin = (price − PPTCV) / price with no VAT conversion. VAT is used only to gross up VAT-exclusive sales revenue (e.g. the average-selling-price column). |
| `StockoutRiskDays` | `14` | Algorithm 4 horizon: projected sellout within N days + healthy margin → vote discount off |
| `NewProductProtectionDays` | `90` | Unused — new-product protection now uses the platform `MarkAsNew` window, not this config. |
| `ChangeConfirmationThresholdPct` | `20.0` | Proposals beyond ±this % require explicit confirmation at approval (enforced server-side) |
| `UseDemoData` | `true` | Replace the SQL source reader with the demo generator |
| `SourceDataset:Mode` | `StoredProcedure` | `InlineQuery` runs the query verbatim — **required for Gjirafa50 layers** |
| `DefaultRunTimeUtc` / `DefaultCadenceHours` | `03:00` / `24` | Seed the schedule on first boot; afterwards each layer's schedule is edited in the UI (stored **per layer** on the `Layers` row) |
| `PushExportDirectory` | `exports` | Where the v1 CSV push integration writes approved prices |

Per-band knobs (margin floor, rounding convention + toggle, per-algorithm
enable/weight 0–100) are **data**, edited per layer on the Price Bands page. Per-SKU rounding
opt-outs live in `SkuOverrides` (scoped per layer).

## How a pricing run works

1. Pull the daily dataset for the layer over the read-only connection (one row per in-scope SKU),
   scoped by the layer's store/country IDs, vendor filter, and the outlet / unpublished exclusions.
   Prices come back in the layer's **native currency** (EUR/MKD/ALL) — no FX conversion.
2. Snapshot the full pull into `DailySnapshots` (history for the dashboard + aging signals;
   a same-day re-pull replaces that layer's snapshot for the day, and proposals keep their own copy
   of inputs).
3. Per SKU: skip & flag if cost is NULL (**never treated as zero**), price missing, or no band
   matches (**bands key off PPTCV/cost**, not the selling price); otherwise run every
   band-enabled algorithm → weighted average of votes (band weight × vote confidence) →
   **guardrail clamp** (margin floor on the all-in cost = PPTCV/(1−floor%) + anchor/FinalPrice cap, falling back to the
   shelf price + a flag when FinalPrice is missing; no discount ceiling; locally-held dead stock may pierce
   the floor down to 50% of cost, and a below-floor price that starts selling is held there) →
   **psychological rounding** that never violates the guardrails.
4. Write `ProposedPrices` + every `AlgorithmVotes` row, wrapped in a `PricingRuns` record
   (status, SKU/error counts) — failures and partial runs stay visible.
5. Humans take it from there: review → approve → push (CSV export via `IPricePushService`).

### Psychological rounding

Selected per band (per layer), and always clamped inside the band guardrails:

| Convention | Use |
|---|---|
| `.99` / `.95` endings, whole number, 995-style (€5 steps) | EUR layers |
| **…99 whole-currency** (e.g. 6149 → 6199, 9990 → 9999) | MKD / ALL layers — `.99`/`.95` are meaningless for currencies with no minor unit |

Under €5 the `.99` grid tightens to a 10-cent `.x9` grid (…0.99, 1.09, 1.19) so cheap items don't
swing between 0.99 and 1.99 and distort margin (threshold: `PricingEngine:LowPriceRoundingThreshold`).

### The 5 algorithms

`SELL_THROUGH`, `DEAD_STOCK`, `ELASTICITY`, `MARGIN_TIER`, `CROSS_DOCK` — each an `IPricingAlgorithm` in
`PricingTool.Core/Algorithms`, individually toggleable and weighted per band. Algorithms return
`null` when they have no opinion; if nothing votes, the price stays unchanged. (Consolidated from
the original 10: `SELL_THROUGH` merges velocity-forecast + stockout-risk + momentum; `STOCK_AGING`,
`SUPPLIER_LOCAL` and `DISCOUNT_EFFECTIVENESS` were retired; `NEW_PRODUCT` is no longer an algorithm —
new-product protection is now a hard engine rule from the platform MarkAsNew window. `CROSS_DOCK` was
later added for supplier-fulfilled SKUs, replacing the old supplier-only-no-markdown guardrail.)

Aging ("consecutive snapshot days of no movement") is derived from the tool's own snapshot
history: consecutive daily snapshots with zero trailing-7d sales. This counter measures **snapshot
rows**, which equal calendar days only at the **24h run cadence** the engine is tuned for; a slower
cadence (e.g. 72h) would make each row span multiple days and slow `DEAD_STOCK`'s 5pp-per-2-week
progression — so the streak would need converting to calendar days before changing cadence.

**Excluded at the source:** SKUs whose code ends in `yz` are local-supplier products priced manually
by a dedicated person; the daily query drops them so the engine never proposes for them.

`ELASTICITY` acts only on **confidently-elastic** SKUs: the weekly fit stores the slope **and its
standard error**, and the profit-max price `cost·E/(E+1)` is voted only when `E + 1.645·SE ≤ −1` (the
whole one-sided 95% interval is below −1). This silences noisy near-unit fits whose markup would
explode (e.g. −1.18 ± 0.6 → ×6.6); the vote is additionally **capped at the anchor**.

`DEAD_STOCK` is the only algorithm allowed below the margin floor: for locally-held stock with no
sales in 90 days, the markdown deepens 5pp every two weeks and may run down to **50% of cost**
(`PricingEngine:DeadStockFloorCostFraction`, a negative margin) to clear it. Enforced in
`GuardrailService` (flag `DEAD_STOCK_FLOOR_RELAXED`), so it's gated on the dead-stock context, not on
which algorithm voted. Once such a below-floor price starts selling again it's frozen at that level
(`DEAD_STOCK_TUNNEL_HELD`) — never raised back.

`CROSS_DOCK` (weight 40) is the lane for **supplier-fulfilled** SKUs — `KsStock == 0 && SupplierStock > 0`,
the ones the other advisors skip (sell-through/dead-stock need local stock; elasticity often can't fit). A
sell-through/dead-stock hybrid: it **holds** the working price when the SKU is selling (`Qty90 > 0`) and
runs a **soft progressive markdown toward the band margin floor** when it isn't (`Qty90 == 0`, opening at
5% and deepening 5pp every ~3 weeks). Unlike dead-stock it's bounded by the **normal** margin floor, never
the below-floor tunnel — nothing is sunk to recover. It's monotonic (never raises, except lifting an
underwater price up to the floor) and low-weighted so it defers to `ELASTICITY` when that fires. Adding it
**removed** the old supplier-only-no-markdown freeze: supplier stock is now priced, not frozen. See
[`docs/algorithms/cross-dock.md`](docs/algorithms/cross-dock.md).

**Reporting.** Movers shows each SKU's average selling price per 7d/30d/90d window (gross, VAT incl.).
Both Movers and Proposals show profit &amp; margin KPI cards — now → proposed over 7d/30d/90d, using a
naive same-quantity baseline (assume the trailing window's units sell again at the current vs proposed
price; VAT-net, cost-known SKUs only). Proposals' cards track the active filters; Movers' summarise the
listed movers. The math lives in `KpiMath.FromSums` (shared by the in-memory Movers path and the
single-round-trip Proposals SQL aggregate).

## Full-catalog scale

A real run prices the **entire** catalog (~660k in-scope SKUs per GjirafaMall layer after the
outlet filter), so the write/read path is built for volume:

- Snapshots, proposals and votes are written via **`SqlBulkCopy`** (`BulkWriteService`), not EF
  row-by-row. A full live run completes in a few minutes.
- The same-date snapshot replace **batches** its delete (50k/iteration) and all run-path SQL runs
  on a 600s command timeout (the run's DbContext is scoped, so the web UI keeps fast-fail timeouts).
- The Proposals listing sorts by change magnitude via a DB-computed `AbsChangePct` column + index,
  so "top 500 by magnitude" is an index range scan, not a half-million-row live sort.
- Outcome evaluation batch-loads each pushed SKU's snapshot history once (no per-proposal N+1).

> Known follow-up: `GetZeroSaleStreaksAsync` still materializes snapshot history in memory — fine
> now, but it should move server-side before many months of daily full-catalog history accumulate.
> Full-catalog daily runs also grow `PricingToolDb` by ~1.4M+ rows/run (a retention policy is TBD).

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
3. **Price band boundaries** — seeded €0–10 … 1,000+ are **placeholders** (editable per layer);
   bands for MKD/ALL layers are seeded with the same numeric structure and should be **retuned in
   each currency** before go-live.
4. **Live-source verification for non-KS layers** — the MK/AL/Gjirafa50 store/country IDs are as
   provided; confirm them (and that `Product.IsOutlet` / `UnpublishedStoreids` exist in both
   operational DBs) with a real run per layer before relying on them.
5. **Competitor pricing / seasonality** — phase 2 (no competitor data; seasonality needs >90d of
   accumulated snapshots, which this tool is now collecting).
6. **Push mechanism into NopCommerce** — `IPricePushService` is the integration point; v1 ships
   `CsvPricePushService` (file export). The platform team replaces the DI registration in
   `PricingTool.Data/DependencyInjection.cs` when the real write-back mechanism is decided.

## Safety rules recap (non-negotiable, implemented)

1. Engine writes proposals only; the push step is explicit, human, Manager-gated.
2. Source data via a dedicated read-only connection; tool tables in their own DB/schema.
3. Every pull snapshotted (`DailySnapshots`), scoped per layer.
4. Every config change and every push audited (who/what/when/old/new, with layer) — Audit Log page.
5. Every run wrapped in a `PricingRuns` record with status and error counts.
