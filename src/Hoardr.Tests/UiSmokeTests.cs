using System.Net;
using Hoardr.Tests.Support;

namespace Hoardr.Tests;

public class UiSmokeTests(RegistryAppFactory factory) : IClassFixture<RegistryAppFactory>
{
    [Theory]
    [InlineData("/")]
    [InlineData("/faq")]
    [InlineData("/setup")]
    [InlineData("/login")]
    public async Task Public_Pages_Render_Anonymously(string path)
    {
        var resp = await factory.CreateClient().GetAsync(path);
        Assert.Equal(HttpStatusCode.OK, resp.StatusCode);
    }

    [Fact]
    public async Task Landing_Sells_And_Links_GitHub()
    {
        var html = await factory.CreateClient().GetStringAsync("/");

        Assert.Contains("Hoardr", html);
        Assert.Contains("enjoy running", html);                      // the hero promise
        Assert.Contains("github.com/BernhardPollerspoeck/Hoardr", html); // GitHub everywhere
        Assert.Contains("Star on GitHub", html);
    }

    [Fact]
    public async Task Protected_Dashboard_Redirects_Anonymous_To_Login()
    {
        var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false,
        });

        var resp = await client.GetAsync("/app");

        Assert.Equal(HttpStatusCode.Redirect, resp.StatusCode);
        Assert.Contains("/login", resp.Headers.Location!.ToString());
    }
}
