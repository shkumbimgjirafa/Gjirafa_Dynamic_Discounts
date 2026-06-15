using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using PricingTool.Core.Abstractions;
using PricingTool.Core.Algorithms;
using PricingTool.Core.Domain;
using PricingTool.Core.Options;
using PricingTool.Core.Services;
using PricingTool.Data.Services;

namespace PricingTool.Data;

public static class DependencyInjection
{
    /// <summary>
    /// Registers the whole pricing stack (DbContext, Core pipeline, all 10 algorithms,
    /// readers, orchestrator, push integration). Shared by Web and Engine.
    /// </summary>
    public static IServiceCollection AddPricingTool(this IServiceCollection services, IConfiguration config)
    {
        services.Configure<PricingEngineOptions>(config.GetSection(PricingEngineOptions.SectionName));

        services.AddDbContext<PricingToolDbContext>(o =>
            o.UseSqlServer(
                config.GetConnectionString("PricingToolDb")
                    ?? throw new InvalidOperationException("Missing connection string 'PricingToolDb'."),
                sql => sql.EnableRetryOnFailure()));

        // Core pipeline — stateless, safe as singletons.
        services.AddSingleton<WeightedScoringService>();
        services.AddSingleton<GuardrailService>();
        services.AddSingleton<RoundingService>();
        services.AddSingleton<PriceCalculator>();

        // All 10 v1 algorithms. Per-band enable/disable + weights live in BandAlgorithmSettings.
        services.AddSingleton<IPricingAlgorithm, SalesVelocityForecastAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, NewProductProtectionAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, WarehouseStockAgingAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, StockoutRiskAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, PriceElasticityHeuristicAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, MarginTierAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, DeadStockMarkdownAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, DiscountEffectivenessAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, VelocityMomentumAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, SupplierVsLocalStockAlgorithm>();

        var useDemoData = config.GetSection(PricingEngineOptions.SectionName).GetValue<bool>("UseDemoData");
        if (useDemoData)
            services.AddScoped<ISourceDataReader, DemoSourceDataReader>();
        else
            services.AddScoped<ISourceDataReader, SqlSourceDataReader>();

        services.AddScoped<IBulkWriteService, BulkWriteService>();
        services.AddScoped<SnapshotService>();
        services.AddScoped<BandConfigProvider>();
        services.AddScoped<AuditService>();
        services.AddScoped<OutcomeEvaluationService>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<PricingRunOrchestrator>();
        services.AddScoped<DemoHistoryBackfill>();
        services.AddScoped<DemoOutcomeSeeder>();
        services.AddScoped<IPricePushService, CsvPricePushService>();

        return services;
    }
}
