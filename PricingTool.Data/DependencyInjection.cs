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

        // The 5 pricing algorithms. Per-band enable/disable + weights live in BandAlgorithmSettings.
        // (The velocity family — forecast/stockout/momentum — is now one SellThrough advisor; the old
        // discount-effectiveness heuristic was retired in favour of the fitted elasticity + margin floor.)
        services.AddSingleton<IPricingAlgorithm, SellThroughAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, NewProductProtectionAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, PriceElasticityHeuristicAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, MarginTierAlgorithm>();
        services.AddSingleton<IPricingAlgorithm, DeadStockMarkdownAlgorithm>();

        var useDemoData = config.GetSection(PricingEngineOptions.SectionName).GetValue<bool>("UseDemoData");
        if (useDemoData)
        {
            services.AddScoped<ISourceDataReader, DemoSourceDataReader>();
            services.AddScoped<IElasticitySourceReader, DemoElasticitySourceReader>();
        }
        else
        {
            services.AddScoped<ISourceDataReader, SqlSourceDataReader>();
            services.AddScoped<IElasticitySourceReader, SqlElasticitySourceReader>();
        }

        services.AddScoped<IBulkWriteService, BulkWriteService>();
        services.AddScoped<SnapshotService>();
        services.AddScoped<BandConfigProvider>();
        services.AddScoped<AuditService>();
        services.AddScoped<OutcomeEvaluationService>();
        services.AddScoped<ScheduleService>();
        services.AddScoped<PricingRunOrchestrator>();
        services.AddScoped<ElasticityFitService>();
        services.AddScoped<DemoHistoryBackfill>();
        services.AddScoped<DemoOutcomeSeeder>();
        services.AddScoped<IPricePushService, CsvPricePushService>();

        return services;
    }
}
