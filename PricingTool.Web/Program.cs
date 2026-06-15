using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using PricingTool.Core.Options;
using PricingTool.Data;
using PricingTool.Data.Services;
using PricingTool.Web.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddPricingTool(builder.Configuration);

builder.Services
    .AddDefaultIdentity<IdentityUser>(options =>
    {
        options.SignIn.RequireConfirmedAccount = false;
        options.Password.RequiredLength = 8;
    })
    .AddRoles<IdentityRole>()
    .AddEntityFrameworkStores<PricingToolDbContext>();

builder.Services.AddControllersWithViews();
builder.Services.AddRazorPages();
builder.Services.AddSingleton<RunLauncher>();

// Everything requires sign-in by default; Identity's own pages opt out via [AllowAnonymous].
builder.Services.AddAuthorizationBuilder()
    .SetFallbackPolicy(new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build());

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}");
app.MapRazorPages();

// Migrate + seed on startup (bands, schedule defaults, roles, initial admin, demo history).
using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<PricingToolDbContext>();
    await db.Database.MigrateAsync();

    var options = scope.ServiceProvider.GetRequiredService<IOptions<PricingEngineOptions>>().Value;
    await DbSeeder.SeedCoreAsync(db, options);

    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();
    await DbSeeder.SeedIdentityAsync(scope.ServiceProvider, app.Configuration, logger);

    if (options.UseDemoData)
        await scope.ServiceProvider.GetRequiredService<DemoHistoryBackfill>().EnsureBackfilledAsync();
}

app.Run();
