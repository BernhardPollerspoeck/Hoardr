using System.Net;
using Hoardr.Core.Auth;
using Hoardr.Tests.Support;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;

namespace Hoardr.Tests;

public class LoginTests(RegistryAppFactory factory) : IClassFixture<RegistryAppFactory>
{
    private HttpClient NoRedirect() =>
        factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });

    private static FormUrlEncodedContent Form(string user, string pass, string returnUrl = "/app")
        => new([
            new("username", user),
            new("password", pass),
            new("returnUrl", returnUrl),
        ]);

    [Fact]
    public async Task Master_Login_Sets_Cookie_And_Reaches_App_And_Admin()
    {
        var client = NoRedirect();

        var login = await client.PostAsync("/login", Form("master", RegistryAppFactory.MasterToken));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/app", login.Headers.Location!.ToString());
        Assert.Contains(login.Headers, h => h.Key == "Set-Cookie");

        // cookie now carried by the client
        var app = await client.GetAsync("/app");
        Assert.Equal(HttpStatusCode.OK, app.StatusCode);
        Assert.Contains("Repositories", await app.Content.ReadAsStringAsync());

        var admin = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.OK, admin.StatusCode);
        var adminHtml = await admin.Content.ReadAsStringAsync();
        Assert.Contains("ntfy", adminHtml);
        Assert.Contains("Tag retention", adminHtml);
    }

    [Fact]
    public async Task Account_Login_Reaches_App_But_Admin_Is_Denied()
    {
        var accounts = factory.Services.GetRequiredService<AccountService>();
        var name = "viewer-" + Guid.NewGuid().ToString("N")[..8];
        var account = accounts.CreateAccount(name, "pw")!;
        accounts.SetPermission(account.Id, "some/repo", canPull: true, canPush: false);

        var client = NoRedirect();
        var login = await client.PostAsync("/login", Form(name, "pw"));
        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);

        var app = await client.GetAsync("/app");
        Assert.Equal(HttpStatusCode.OK, app.StatusCode);

        // authenticated but not master → access denied → bounced back to /app
        var admin = await client.GetAsync("/admin");
        Assert.Equal(HttpStatusCode.Redirect, admin.StatusCode);
        Assert.Contains("/app", admin.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Wrong_Credentials_Bounce_Back_With_Error_And_No_Cookie()
    {
        var client = NoRedirect();

        var login = await client.PostAsync("/login", Form("master", "not-the-token"));

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.StartsWith("/login", login.Headers.Location!.ToString());
        Assert.Contains("error=1", login.Headers.Location!.ToString());
        Assert.DoesNotContain(login.Headers, h => h.Key == "Set-Cookie");
    }

    [Fact]
    public async Task Logout_Clears_The_Session()
    {
        var client = NoRedirect();
        await client.PostAsync("/login", Form("master", RegistryAppFactory.MasterToken));

        var logout = await client.PostAsync("/logout", content: null);
        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);
        Assert.Equal("/", logout.Headers.Location!.ToString());

        var app = await client.GetAsync("/app");
        Assert.Equal(HttpStatusCode.Redirect, app.StatusCode);
        Assert.Contains("/login", app.Headers.Location!.ToString());
    }
}
