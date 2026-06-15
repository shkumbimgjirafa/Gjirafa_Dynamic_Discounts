using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Options;
using PricingTool.Data.Services;

namespace PricingTool.Web.Services;

/// <summary>
/// INTERIM no-authentication shim. Real authentication is intentionally disabled until
/// Gjirafa's Porta SSO is integrated. This handler auto-authenticates every request as a single
/// "demo" user holding both the Analyst and Manager roles, so the whole app is usable with no
/// login screen and nothing is gated.
///
/// To switch to real auth later: replace the AddScheme&lt;...DevAuthHandler&gt; registration in
/// Program.cs with the Porta scheme (e.g. OpenID Connect) and map Porta's role claims to
/// "Analyst"/"Manager". The [Authorize(Roles = ...)] markers already on the controllers then
/// begin enforcing again with no other code changes.
/// </summary>
public class DevAuthHandler : AuthenticationHandler<AuthenticationSchemeOptions>
{
    public const string SchemeName = "DevNoAuth";

    public DevAuthHandler(
        IOptionsMonitor<AuthenticationSchemeOptions> options,
        ILoggerFactory logger,
        UrlEncoder encoder)
        : base(options, logger, encoder) { }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        var claims = new[]
        {
            new Claim(ClaimTypes.Name, "demo"),
            new Claim(ClaimTypes.Role, PricingRoles.Analyst),
            new Claim(ClaimTypes.Role, PricingRoles.Manager),
        };
        var identity = new ClaimsIdentity(claims, SchemeName);
        var ticket = new AuthenticationTicket(new ClaimsPrincipal(identity), SchemeName);
        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
