/* ============================================================================
   usp_GetDailyPricingDataset — daily input dataset for the GjirafaMall
   Dynamic Pricing Tool. Deploy into the SOURCE database server (the one
   hosting GjirafaMall + GjirafaTranslations). The pricing tool calls it over
   its READ-ONLY connection once per cycle.

   FIX vs the original ad-hoc query: @now is captured ONCE at the top and used
   consistently everywhere — including the discount-validity OUTER APPLY,
   which previously called GETUTCDATE() separately and could disagree with
   the sales windows at midnight boundaries.

   All sales windows are cumulative trailing windows ending at @now (UTC):
   90d ⊇ 60d ⊇ 30d ⊇ 14d ⊇ 7d.

   MULTI-LAYER: the store/country filters and the vendor / unpublished toggles are now PARAMETERS,
   mirroring SourceQueries.DailyDatasetInline. NOTE: this compiled procedure reads the GjirafaMall
   operational database by three-part name; it CANNOT switch to GjirafaEcommerce (Gjirafa50) via a
   parameter. Gjirafa50 layers must run in InlineQuery mode (SourceDataset:Mode=InlineQuery), where
   the {opDb} token is substituted. Keep the body in sync with SourceQueries.cs.
   ============================================================================ */
CREATE OR ALTER PROCEDURE dbo.usp_GetDailyPricingDataset
    @StoreId              int,
    @TranslationCountryId int,
    @WarehouseStoreId     int,
    @FilterVendors        bit = 1,
    @ExcludeUnpublished   bit = 1
AS
BEGIN
    SET NOCOUNT ON;

    DROP TABLE IF EXISTS #vendors, #stock, #sales, #pricing;

    DECLARE @now  datetime = GETUTCDATE();   -- single timestamp for the whole dataset
    DECLARE @d7   datetime = DATEADD(DAY, -7,  @now);
    DECLARE @d14  datetime = DATEADD(DAY, -14, @now);
    DECLARE @d30  datetime = DATEADD(DAY, -30, @now);
    DECLARE @d60  datetime = DATEADD(DAY, -60, @now);
    DECLARE @d90  datetime = DATEADD(DAY, -90, @now);
    DECLARE @storeIdStr varchar(16) = CAST(@StoreId AS varchar(16));

    -- 0) Target vendors (GjirafaMall vendor set when @FilterVendors = 1; all vendors otherwise)
    SELECT v.Id INTO #vendors
    FROM GjirafaMall.dbo.Vendor v
    WHERE v.Deleted = 0
      AND v.Active = 1
      AND (@FilterVendors = 0 OR (v.Name LIKE '%Gjiraf%' OR v.Name IN ('Dino Toys', 'Mysu', 'Apple')));
    CREATE CLUSTERED INDEX cx ON #vendors (Id);

    -- 1) Stock per product, vendor- and stock-filtered; optional unpublished-store exclusion
    SELECT
        p.Id  AS ProductId,
        p.Sku,
        SUM(CASE WHEN w.IsLocalToStoreIds = @WarehouseStoreId THEN pwi.StockQuantity ELSE 0 END) AS LocalWarehouseStock,
        SUM(CASE WHEN w.IsLocalToStores  = 0 THEN pwi.StockQuantity ELSE 0 END) AS Supplier_WarehouseStock
    INTO #stock
    FROM GjirafaMall.dbo.Product p
    INNER JOIN #vendors v ON v.Id = p.VendorId
    INNER JOIN GjirafaMall.dbo.ProductWarehouseInventory pwi ON pwi.ProductId = p.Id
    INNER JOIN GjirafaMall.dbo.Warehouse w ON w.Id = pwi.WarehouseId
    WHERE ISNULL(p.IsOutlet, 0) = 0   -- outlets are priced by a separate system; never include them
      AND (@ExcludeUnpublished = 0
           OR p.UnpublishedStoreids IS NULL
           OR p.UnpublishedStoreids NOT LIKE '%' + @storeIdStr + '%')
    GROUP BY p.Id, p.Sku
    HAVING SUM(CASE WHEN w.IsLocalToStoreIds = @WarehouseStoreId THEN pwi.StockQuantity ELSE 0 END)
         + SUM(CASE WHEN w.IsLocalToStores  = 0 THEN pwi.StockQuantity ELSE 0 END) <> 0;
    CREATE CLUSTERED INDEX cx ON #stock (ProductId);

    -- 2) Sales, cumulative trailing windows to 90 days (store @StoreId only)
    SELECT
        oi.ProductId,
        SUM(CASE WHEN o.CreatedOnUtc >= @d7  THEN oi.Quantity     ELSE 0 END) AS [7d_qty],
        SUM(CASE WHEN o.CreatedOnUtc >= @d7  THEN oi.PriceExclTax ELSE 0 END) AS [7d_net],
        SUM(CASE WHEN o.CreatedOnUtc >= @d14 THEN oi.Quantity     ELSE 0 END) AS [14d_qty],
        SUM(CASE WHEN o.CreatedOnUtc >= @d14 THEN oi.PriceExclTax ELSE 0 END) AS [14d_net],
        SUM(CASE WHEN o.CreatedOnUtc >= @d30 THEN oi.Quantity     ELSE 0 END) AS [30d_qty],
        SUM(CASE WHEN o.CreatedOnUtc >= @d30 THEN oi.PriceExclTax ELSE 0 END) AS [30d_net],
        SUM(CASE WHEN o.CreatedOnUtc >= @d60 THEN oi.Quantity     ELSE 0 END) AS [60d_qty],
        SUM(oi.Quantity)     AS [90d_qty]
    INTO #sales
    FROM GjirafaMall.dbo.OrderItem oi
    INNER JOIN GjirafaMall.dbo.[Order] o ON o.Id = oi.OrderId
    INNER JOIN #stock st ON st.ProductId = oi.ProductId
    WHERE o.OrderStatusId IN (20, 30)
      AND o.StoreId = @StoreId
      AND o.CreatedOnUtc >= @d90
    GROUP BY oi.ProductId;
    CREATE CLUSTERED INDEX cx ON #sales (ProductId);

    -- 2b) Pricing (cost + margin) from GjirafaTranslations (shared), country @TranslationCountryId
    SELECT pp.ProductCode AS Sku, pp.PPTCV, pp.GrossMargin, pp.FinalPrice
    INTO #pricing
    FROM GjirafaTranslations.dbo.ProductPricing pp
    INNER JOIN #stock st ON st.Sku = pp.ProductCode
    WHERE pp.CountryId = @TranslationCountryId;
    CREATE CLUSTERED INDEX cx ON #pricing (Sku);

    -- 3) Final dataset, one row per in-scope SKU
    SELECT
        st.Sku,
        px.OldPrice,
        ISNULL(NULLIF(pr.FinalPrice, 0), px.OldPrice) AS AnchorPrice,
        CASE WHEN NULLIF(pr.FinalPrice, 0) IS NULL THEN 1 ELSE 0 END AS AnchorFromShelf,
        px.CurrentPrice,
        (px.OldPrice - px.CurrentPrice) / NULLIF(px.OldPrice, 0) AS CurrentDiscountPct,
        pr.PPTCV,
        pr.GrossMargin,
        st.LocalWarehouseStock,
        st.Supplier_WarehouseStock,
        ISNULL(s.[7d_qty], 0)  AS [7d_qty],  ISNULL(s.[7d_net], 0)  AS [7d_net],
        ISNULL(s.[14d_qty], 0) AS [14d_qty],
        ISNULL(s.[30d_qty], 0) AS [30d_qty],
        ISNULL(s.[60d_qty], 0) AS [60d_qty],
        ISNULL(s.[90d_qty], 0) AS [90d_qty]
    FROM #stock st
    OUTER APPLY (
        SELECT TOP 1 tp.Price, tp.OldPrice
        FROM GjirafaMall.dbo.TierPrice tp
        WHERE tp.ProductId = st.ProductId AND tp.StoreId = @StoreId
        ORDER BY tp.Id DESC
    ) tp
    OUTER APPLY (
        SELECT TOP 1
            IIF(d.UsePercentage = 1,
                ISNULL(NULLIF(tp.OldPrice, 0), tp.Price) * (1 - d.DiscountPercentage / 100.0),
                d.DiscountAmount) AS DiscountedPrice
        FROM GjirafaMall.dbo.Discount_AppliedToProducts dap
        INNER JOIN GjirafaMall.dbo.Discount d ON d.Id = dap.Discount_Id
        WHERE dap.Product_Id = st.ProductId
          AND d.StoreId = @StoreId
          AND d.RequiresCouponCode = 0
          AND @now BETWEEN ISNULL(d.StartDateUtc, '1900') AND ISNULL(d.EndDateUtc, '2999')  -- uses the SAME @now
        ORDER BY IIF(d.UsePercentage = 1,
                     ISNULL(NULLIF(tp.OldPrice, 0), tp.Price) * (1 - d.DiscountPercentage / 100.0),
                     d.DiscountAmount) ASC  -- lowest resulting price wins
    ) dx
    CROSS APPLY (
        SELECT
            ISNULL(NULLIF(tp.OldPrice, 0), tp.Price) AS OldPrice,
            ISNULL(dx.DiscountedPrice, tp.Price)     AS CurrentPrice
    ) px
    LEFT JOIN #pricing pr ON pr.Sku = st.Sku
    LEFT JOIN #sales   s  ON s.ProductId = st.ProductId;

    DROP TABLE #vendors, #stock, #sales, #pricing;
END
GO

/* Grant execute to the tool's read-only login, e.g.:
   GRANT EXECUTE ON dbo.usp_GetDailyPricingDataset TO [pricingtool_ro];
*/
