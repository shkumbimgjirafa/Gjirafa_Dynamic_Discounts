using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace PricingTool.Data;

/// <summary>
/// Lets `dotnet ef migrations add` run against this project without booting the web app.
/// The connection string here is never used at runtime — runtime config comes from appsettings.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<PricingToolDbContext>
{
    public PricingToolDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<PricingToolDbContext>()
            .UseSqlServer("Server=(localdb)\\mssqllocaldb;Database=PricingTool;Trusted_Connection=True;")
            .Options;
        return new PricingToolDbContext(options);
    }
}
