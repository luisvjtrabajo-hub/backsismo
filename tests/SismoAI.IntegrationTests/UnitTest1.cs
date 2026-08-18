using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using SismoAI.Infrastructure;

namespace SismoAI.IntegrationTests;

public sealed class ApiSmokeTests : IClassFixture<SismoAiFactory>
{
    private readonly HttpClient _client;

    public ApiSmokeTests(SismoAiFactory factory)
    {
        _client = factory.CreateClient();
    }

    [Fact]
    public async Task Health_ReturnsOk()
    {
        var response = await _client.GetAsync("/health");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task CountryDailyFeatures_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/dashboard/features/country-daily?countryCode=PE&days=90");

        response.EnsureSuccessStatusCode();
    }

    [Fact]
    public async Task Dashboard_ReturnsOk()
    {
        var response = await _client.GetAsync("/api/dashboard");

        response.EnsureSuccessStatusCode();
    }
}

public sealed class SismoAiFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(Microsoft.AspNetCore.Hosting.IWebHostBuilder builder)
    {
        builder.ConfigureAppConfiguration((_, config) =>
        {
            config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ingestion:Enabled"] = "false",
                ["ConnectionStrings:PostgreSql"] = string.Empty,
                ["ConnectionStrings:Sqlite"] = string.Empty
            });
        });

        builder.ConfigureServices(services =>
        {
            services.RemoveAll(typeof(DbContextOptions<SismoDbContext>));
            services.RemoveAll(typeof(SismoDbContext));
            services.AddDbContext<SismoDbContext>(options => options.UseInMemoryDatabase("sismoai-tests"));
        });
    }
}
