namespace PricingTool.Core.Domain;

/// <summary>
/// The per-layer parameters that scope the source dataset pull. Carries everything the source
/// query needs to fetch one layer's catalog: which operational database to read, the store/country
/// filter ids, the local-warehouse split id, and the per-brand query toggles.
/// </summary>
public record LayerSourceContext
{
    /// <summary>NOP operational database name substituted into the query (GjirafaMall / GjirafaEcommerce).</summary>
    public required string OperationalDatabase { get; init; }

    /// <summary>Store id used to filter orders, tier prices and discounts.</summary>
    public required int StoreId { get; init; }

    /// <summary>CountryId used to filter product pricing in GjirafaTranslations.</summary>
    public required int TranslationCountryId { get; init; }

    /// <summary>Warehouse store id for the IsLocalToStoreIds local-stock split.</summary>
    public required int WarehouseStoreId { get; init; }

    /// <summary>True = restrict to the GjirafaMall vendor set; false = all vendors (Gjirafa50).</summary>
    public bool FilterVendors { get; init; } = true;

    /// <summary>True = exclude products not published in this store (UnpublishedStoreids NOT LIKE '%StoreId%').</summary>
    public bool ExcludeUnpublished { get; init; } = true;
}
