using Microsoft.AspNetCore.Authentication;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Services;
using PricingTool.Web.Services;

var builder = WebApplication.CreateBuilder(args);

// Load .env (gitignored) for local source-database credentials. When SOURCE_DB_HOST is
// filled in, this builds the SourceReadOnly connection string and turns off demo mode.
EnvFileLoader.ApplyDotEnv(builder);

builder.Services.AddPricingTool(builder.Configuration);
builder.Services.AddControllersWithViews(o => o.Filters.Add<LayerContextFilter>());
builder.Services.AddSingleton<RunLauncher>();

// Multi-layer: session holds the selected layer; CurrentLayerService resolves it per request.
builder.Services.AddHttpContextAccessor();
builder.Services.AddScoped<CurrentLayerService>();
builder.Services.AddSession(o =>
{
    o.Cookie.IsEssential = true;
    o.IdleTimeout = TimeSpan.FromDays(7);
});

// AUTHENTICATION INTENTIONALLY DISABLED until Gjirafa's Porta SSO is integrated.
// DevAuthHandler auto-signs every request in as a single "demo" user holding both the Analyst
// and Manager roles, so the app is fully usable with no login. The [Authorize(Roles = ...)]
// markers on the controllers are preserved and resume enforcing the moment this scheme is
// swapped for the real Porta scheme — no controller or view changes required.
builder.Services
    .AddAuthentication(DevAuthHandler.SchemeName)
    .AddScheme<AuthenticationSchemeOptions, DevAuthHandler>(DevAuthHandler.SchemeName, null);

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseSession();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");

// Migrate + seed on startup (bands, schedule defaults, demo history). No identity seeding —
// authentication is handled by the dev shim until Porta is wired in.
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
    var startupLogger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("Startup");
    await DbInitializer.MigrateWithRetryAsync(db, startupLogger);

    var options = scope.ServiceProvider.GetRequiredService<IOptions<PricingEngineOptions>>().Value;
    await DbSeeder.SeedCoreAsync(db, options);

    if (options.UseDemoData)
    {
        await scope.ServiceProvider.GetRequiredService<DemoHistoryBackfill>().EnsureBackfilledAsync();
        await scope.ServiceProvider.GetRequiredService<DemoOutcomeSeeder>().EnsureSeededAsync();
    }
}

app.Run();
