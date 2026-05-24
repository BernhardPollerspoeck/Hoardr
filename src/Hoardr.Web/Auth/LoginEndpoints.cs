using System.Security.Claims;
using Hoardr.Core.Auth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

namespace Hoardr.Web.Auth;

/// <summary>Form-based cookie login/logout for the UI (Docker clients still use Basic on /v2).</summary>
public static class LoginEndpoints
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string MasterRole = "master";

    public static void MapAuthEndpoints(this WebApplication app)
    {
        app.MapPost("/login", async (
            HttpContext ctx, Authenticator auth,
            [FromForm] string? username, [FromForm] string? password, [FromForm] string? returnUrl) =>
        {
            var identity = auth.VerifyCredentials(username ?? "", password ?? "");
            if (identity is null)
                return Results.Redirect($"/login?error=1&returnUrl={Uri.EscapeDataString(Safe(returnUrl))}");

            var claims = new List<Claim>
            {
                new(ClaimTypes.Name, identity.Name),
                new("account_id", identity.AccountId.ToString()),
            };
            if (identity.IsMaster)
                claims.Add(new Claim(ClaimTypes.Role, MasterRole));

            var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, Scheme));
            await ctx.SignInAsync(Scheme, principal, new AuthenticationProperties { IsPersistent = true });
            return Results.Redirect(Safe(returnUrl));
        }).DisableAntiforgery();

        app.MapPost("/logout", async (HttpContext ctx) =>
        {
            await ctx.SignOutAsync(Scheme);
            return Results.Redirect("/");
        }).DisableAntiforgery();
    }

    // Only allow local redirects; default to the dashboard.
    private static string Safe(string? url)
        => !string.IsNullOrEmpty(url) && url.StartsWith('/') && !url.StartsWith("//") ? url : "/app";
}
