namespace PricingTool.Data.Services;

/// <summary>
/// The daily dataset query, runnable verbatim when the stored procedure cannot be deployed.
/// This is the CORRECTED version: @now is captured once and used consistently everywhere,
/// including the discount OUTER APPLY (the original called GETUTCDATE() there separately).
/// Keep in sync with scripts/usp_GetDailyPricingDataset.sql.
/// </summary>
public static class SourceQueries
{
    public const string StoredProcedureName = "dbo.usp_GetDailyPricingDataset";

    public const string DailyDatasetInline = @"
DROP TABLE IF EXISTS #vendors, #stock, #sales, #pricing;
DECLARE @now  datetime = GETUTCDATE();
DECLARE @d7   datetime = DATEADD(DAY, -7,  @now);
DECLARE @d14  datetime = DATEADD(DAY, -14, @now);
DECLARE @d30  datetime = DATEADD(DAY, -30, @now);
DECLARE @d60  datetime = DATEADD(DAY, -60, @now);
DECLARE @d90  datetime = DATEADD(DAY, -90, @now);

SELECT v.Id INTO #vendors
FROM GjirafaMall.dbo.Vendor v
WHERE v.Deleted = 0
  AND v.Active = 1
  AND (v.Name LIKE '%Gjiraf%' OR v.Name IN ('Dino Toys', 'Mysu', 'Apple'));
CREATE CLUSTERED INDEX cx ON #vendors (Id);

SELECT
    p.Id  AS ProductId,
    p.Sku,
    SUM(CASE WHEN w.IsLocalToStoreIds = 2 THEN pwi.StockQuantity ELSE 0 END) AS KS_WarehouseStock,
    SUM(CASE WHEN w.IsLocalToStores  = 0 THEN pwi.StockQuantity ELSE 0 END) AS Supplier_WarehouseStock
INTO #stock
FROM GjirafaMall.dbo.Product p
INNER JOIN #vendors v ON v.Id = p.VendorId
INNER JOIN GjirafaMall.dbo.ProductWarehouseInventory pwi ON pwi.ProductId = p.Id
INNER JOIN GjirafaMall.dbo.Warehouse w ON w.Id = pwi.WarehouseId
GROUP BY p.Id, p.Sku
HAVING SUM(CASE WHEN w.IsLocalToStoreIds = 2 THEN pwi.StockQuantity ELSE 0 END)
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
    SUM(CASE WHEN o.CreatedOnUtc >= @d60 THEN oi.PriceExclTax ELSE 0 END) AS [60d_net],
    SUM(oi.Quantity)     AS [90d_qty],
    SUM(oi.PriceExclTax) AS [90d_net],
    SUM(CASE WHEN o.CreatedOnUtc >= @d7 THEN oi.DiscountAmountExclTax ELSE 0 END)
      / NULLIF(SUM(CASE WHEN o.CreatedOnUtc >= @d7 THEN oi.PriceExclTax + oi.DiscountAmountExclTax ELSE 0 END), 0) AS [7d_avg_discount_pct],
    SUM(CASE WHEN o.CreatedOnUtc >= @d14 THEN oi.DiscountAmountExclTax ELSE 0 END)
      / NULLIF(SUM(CASE WHEN o.CreatedOnUtc >= @d14 THEN oi.PriceExclTax + oi.DiscountAmountExclTax ELSE 0 END), 0) AS [14d_avg_discount_pct],
    SUM(CASE WHEN o.CreatedOnUtc >= @d30 THEN oi.DiscountAmountExclTax ELSE 0 END)
      / NULLIF(SUM(CASE WHEN o.CreatedOnUtc >= @d30 THEN oi.PriceExclTax + oi.DiscountAmountExclTax ELSE 0 END), 0) AS [30d_avg_discount_pct],
    SUM(CASE WHEN o.CreatedOnUtc >= @d60 THEN oi.DiscountAmountExclTax ELSE 0 END)
      / NULLIF(SUM(CASE WHEN o.CreatedOnUtc >= @d60 THEN oi.PriceExclTax + oi.DiscountAmountExclTax ELSE 0 END), 0) AS [60d_avg_discount_pct],
    SUM(oi.DiscountAmountExclTax)
      / NULLIF(SUM(oi.PriceExclTax + oi.DiscountAmountExclTax), 0) AS [90d_avg_discount_pct]
INTO #sales
FROM GjirafaMall.dbo.OrderItem oi
INNER JOIN GjirafaMall.dbo.[Order] o ON o.Id = oi.OrderId
INNER JOIN #stock st ON st.ProductId = oi.ProductId
WHERE o.OrderStatusId IN (20, 30)
  AND o.StoreId = 2
  AND o.CreatedOnUtc >= @d90
GROUP BY oi.ProductId;
CREATE CLUSTERED INDEX cx ON #sales (ProductId);

SELECT pp.ProductCode AS Sku, pp.PPTCV, pp.GrossMargin
INTO #pricing
FROM GjirafaTranslations.dbo.ProductPricing pp
INNER JOIN #stock st ON st.Sku = pp.ProductCode
WHERE pp.CountryId = 1;
CREATE CLUSTERED INDEX cx ON #pricing (Sku);

SELECT
    st.Sku,
    px.OldPrice,
    px.CurrentPrice,
    (px.OldPrice - px.CurrentPrice) / NULLIF(px.OldPrice, 0) AS CurrentDiscountPct,
    pr.PPTCV,
    pr.GrossMargin,
    st.KS_WarehouseStock,
    st.Supplier_WarehouseStock,
    ISNULL(s.[7d_qty], 0)  AS [7d_qty],  ISNULL(s.[7d_net], 0)  AS [7d_net],  s.[7d_avg_discount_pct],
    ISNULL(s.[14d_qty], 0) AS [14d_qty], ISNULL(s.[14d_net], 0) AS [14d_net], s.[14d_avg_discount_pct],
    ISNULL(s.[30d_qty], 0) AS [30d_qty], ISNULL(s.[30d_net], 0) AS [30d_net], s.[30d_avg_discount_pct],
    ISNULL(s.[60d_qty], 0) AS [60d_qty], ISNULL(s.[60d_net], 0) AS [60d_net], s.[60d_avg_discount_pct],
    ISNULL(s.[90d_qty], 0) AS [90d_qty], ISNULL(s.[90d_net], 0) AS [90d_net], s.[90d_avg_discount_pct]
FROM #stock st
OUTER APPLY (
    SELECT TOP 1 tp.Price, tp.OldPrice
    FROM GjirafaMall.dbo.TierPrice tp
    WHERE tp.ProductId = st.ProductId AND tp.StoreId = 2
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
      AND d.StoreId = 2
      AND d.RequiresCouponCode = 0
      AND @now BETWEEN ISNULL(d.StartDateUtc, '1900') AND ISNULL(d.EndDateUtc, '2999')
    ORDER BY IIF(d.UsePercentage = 1,
                 ISNULL(NULLIF(tp.OldPrice, 0), tp.Price) * (1 - d.DiscountPercentage / 100.0),
                 d.DiscountAmount) ASC
) dx
CROSS APPLY (
    SELECT
        ISNULL(NULLIF(tp.OldPrice, 0), tp.Price) AS OldPrice,
        ISNULL(dx.DiscountedPrice, tp.Price)     AS CurrentPrice
) px
LEFT JOIN #pricing pr ON pr.Sku = st.Sku
LEFT JOIN #sales   s  ON s.ProductId = st.ProductId;

DROP TABLE #vendors, #stock, #sales, #pricing;";
}
