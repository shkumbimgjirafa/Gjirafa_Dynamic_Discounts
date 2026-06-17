namespace PricingTool.Data.Services;

/// <summary>
/// The daily dataset query, runnable verbatim when the stored procedure cannot be deployed.
/// @now is captured once and used consistently everywhere, including the discount filter.
///
/// MULTI-LAYER: the operational database is a {opDb} token (GjirafaMall / GjirafaEcommerce) that
/// <see cref="SqlSourceDataReader"/> substitutes per layer; the cross-database GjirafaTranslations
/// reference is shared and stays fixed. The store/country filters and the vendor / unpublished
/// toggles are bound as command parameters (@StoreId, @TranslationCountryId, @WarehouseStoreId,
/// @FilterVendors, @ExcludeUnpublished).
///
/// SET-BASED REWRITE: the latest tier price and the best active discount are now resolved in
/// bulk via ROW_NUMBER() into #tier / #disc, instead of per-product correlated OUTER APPLYs.
/// The correlated version did not complete on the full catalog (~680k SKUs); this version
/// returns the whole set in ~20s. Schema: adds AnchorPrice (= ProductPricing.FinalPrice, with a
/// shelf fallback) + AnchorFromShelf; drops the now-unused per-window avg-discount-% and the
/// Net14/30/60/90 windows. Keep in sync with scripts/usp_GetDailyPricingDataset.sql.
/// </summary>
public static class SourceQueries
{
    public const string StoredProcedureName = "dbo.usp_GetDailyPricingDataset";

    /// <summary>The token in <see cref="DailyDatasetInline"/> replaced with the layer's operational DB name.</summary>
    public const string OperationalDbToken = "{opDb}";

    public const string DailyDatasetInline = @"
DROP TABLE IF EXISTS #vendors, #stock, #sales, #pricing, #tier, #disc;
DECLARE @now  datetime = GETUTCDATE();
DECLARE @d7   datetime = DATEADD(DAY, -7,  @now);
DECLARE @d14  datetime = DATEADD(DAY, -14, @now);
DECLARE @d30  datetime = DATEADD(DAY, -30, @now);
DECLARE @d60  datetime = DATEADD(DAY, -60, @now);
DECLARE @d90  datetime = DATEADD(DAY, -90, @now);
DECLARE @storeIdStr varchar(16) = CAST(@StoreId AS varchar(16));

SELECT v.Id INTO #vendors
FROM {opDb}.dbo.Vendor v
WHERE v.Deleted = 0
  AND v.Active = 1
  AND (@FilterVendors = 0 OR (v.Name LIKE '%Gjiraf%' OR v.Name IN ('Dino Toys', 'Mysu', 'Apple')));
CREATE CLUSTERED INDEX cx ON #vendors (Id);

SELECT
    p.Id  AS ProductId,
    p.Sku,
    SUM(CASE WHEN w.IsLocalToStoreIds = @WarehouseStoreId THEN pwi.StockQuantity ELSE 0 END) AS LocalWarehouseStock,
    SUM(CASE WHEN w.IsLocalToStores  = 0 THEN pwi.StockQuantity ELSE 0 END) AS Supplier_WarehouseStock,
    MAX(CASE WHEN p.MarkAsNew = 1
             AND (p.MarkAsNewStartDateTimeUtc IS NULL OR @now >= p.MarkAsNewStartDateTimeUtc)
             AND (p.MarkAsNewEndDateTimeUtc   IS NULL OR @now <= p.MarkAsNewEndDateTimeUtc)
        THEN 1 ELSE 0 END) AS IsNewProduct
INTO #stock
FROM {opDb}.dbo.Product p
INNER JOIN #vendors v ON v.Id = p.VendorId
INNER JOIN {opDb}.dbo.ProductWarehouseInventory pwi ON pwi.ProductId = p.Id
INNER JOIN {opDb}.dbo.Warehouse w ON w.Id = pwi.WarehouseId
WHERE ISNULL(p.IsOutlet, 0) = 0   -- outlets are priced by a separate system; never include them
  AND (@ExcludeUnpublished = 0
       OR p.UnpublishedStoreids IS NULL
       OR p.UnpublishedStoreids NOT LIKE '%' + @storeIdStr + '%')
GROUP BY p.Id, p.Sku
HAVING SUM(CASE WHEN w.IsLocalToStoreIds = @WarehouseStoreId THEN pwi.StockQuantity ELSE 0 END)
     + SUM(CASE WHEN w.IsLocalToStores  = 0 THEN pwi.StockQuantity ELSE 0 END) <> 0;
CREATE CLUSTERED INDEX cx ON #stock (ProductId);

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
FROM {opDb}.dbo.OrderItem oi
INNER JOIN {opDb}.dbo.[Order] o ON o.Id = oi.OrderId
INNER JOIN #stock st ON st.ProductId = oi.ProductId
WHERE o.OrderStatusId IN (20, 30)
  AND o.StoreId = @StoreId
  AND o.CreatedOnUtc >= @d90
GROUP BY oi.ProductId;
CREATE CLUSTERED INDEX cx ON #sales (ProductId);

SELECT pp.ProductCode AS Sku, pp.PPTCV, pp.GrossMargin, pp.FinalPrice
INTO #pricing
FROM GjirafaTranslations.dbo.ProductPricing pp
INNER JOIN #stock st ON st.Sku = pp.ProductCode
WHERE pp.CountryId = @TranslationCountryId;
CREATE CLUSTERED INDEX cx ON #pricing (Sku);

-- Latest tier price per product (StoreId = @StoreId), resolved in bulk (replaces correlated OUTER APPLY).
SELECT t.ProductId, t.Price, t.OldPrice
INTO #tier
FROM (
    SELECT tp.ProductId, tp.Price, tp.OldPrice,
           ROW_NUMBER() OVER (PARTITION BY tp.ProductId ORDER BY tp.Id DESC) AS rn
    FROM {opDb}.dbo.TierPrice tp
    INNER JOIN #stock st ON st.ProductId = tp.ProductId
    WHERE tp.StoreId = @StoreId
) t
WHERE t.rn = 1;
CREATE CLUSTERED INDEX cx ON #tier (ProductId);

-- Best (lowest) active discounted price per product, resolved in bulk (replaces correlated OUTER APPLY).
-- Depends on #tier because percentage discounts apply to the tier old/current price.
SELECT x.ProductId, x.DiscountedPrice
INTO #disc
FROM (
    SELECT dap.Product_Id AS ProductId,
           IIF(d.UsePercentage = 1,
               ISNULL(NULLIF(t.OldPrice, 0), t.Price) * (1 - d.DiscountPercentage / 100.0),
               d.DiscountAmount) AS DiscountedPrice,
           ROW_NUMBER() OVER (
               PARTITION BY dap.Product_Id
               ORDER BY IIF(d.UsePercentage = 1,
                            ISNULL(NULLIF(t.OldPrice, 0), t.Price) * (1 - d.DiscountPercentage / 100.0),
                            d.DiscountAmount) ASC) AS rn
    FROM {opDb}.dbo.Discount_AppliedToProducts dap
    INNER JOIN {opDb}.dbo.Discount d ON d.Id = dap.Discount_Id
    INNER JOIN #tier t ON t.ProductId = dap.Product_Id
    WHERE d.StoreId = @StoreId
      AND d.RequiresCouponCode = 0
      AND @now BETWEEN ISNULL(d.StartDateUtc, '1900') AND ISNULL(d.EndDateUtc, '2999')
) x
WHERE x.rn = 1;
CREATE CLUSTERED INDEX cx ON #disc (ProductId);

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
    st.IsNewProduct,
    ISNULL(s.[7d_qty], 0)  AS [7d_qty],  ISNULL(s.[7d_net], 0)  AS [7d_net],
    ISNULL(s.[14d_qty], 0) AS [14d_qty],
    ISNULL(s.[30d_qty], 0) AS [30d_qty],
    ISNULL(s.[60d_qty], 0) AS [60d_qty],
    ISNULL(s.[90d_qty], 0) AS [90d_qty]
FROM #stock st
LEFT JOIN #tier t  ON t.ProductId  = st.ProductId
LEFT JOIN #disc dx ON dx.ProductId = st.ProductId
CROSS APPLY (
    SELECT
        ISNULL(NULLIF(t.OldPrice, 0), t.Price) AS OldPrice,
        ISNULL(dx.DiscountedPrice, t.Price)    AS CurrentPrice
) px
LEFT JOIN #pricing pr ON pr.Sku = st.Sku
LEFT JOIN #sales   s  ON s.ProductId = st.ProductId;

DROP TABLE #vendors, #stock, #sales, #pricing, #tier, #disc;";
}
