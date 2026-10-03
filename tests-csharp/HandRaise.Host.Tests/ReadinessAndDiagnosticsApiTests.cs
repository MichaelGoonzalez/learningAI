using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using HandRaise.Application.Storage;
using HandRaise.Host.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HandRaise.Host.Tests;

public sealed class ReadinessAndDiagnosticsApiTests
{
    [Fact]
    public async Task ReadinessReturnsValidReport()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/health/readiness");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<NodeReadinessReport>();
        Assert.NotNull(report);
        Assert.Equal("node-test", report!.NodeId);
        Assert.Equal("site-test", report.SiteId);
        Assert.NotEmpty(report.Checks);
    }

    [Fact]
    public async Task DiagnosticsReturnsConsolidatedSystemState()
    {
        await using var app = CreateApp();
        await app.StartAsync();
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", "secret");

        var response = await client.GetAsync("/api/v1/system/diagnostics");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var report = await response.Content.ReadFromJsonAsync<NodeDiagnosticsReport>();
        Assert.NotNull(report);
        Assert.Equal("node-test", report!.NodeId);
        Assert.Equal("site-test", report.SiteId);
        Assert.NotEmpty(report.Version);
        Assert.True(report.WorkingSetBytes > 0);
    }

    private static WebApplication CreateApp() =>
        HostApplication.Build([], startCameras: false, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Environment.EnvironmentName = "Production";
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["api:apiKey"] = "secret",
                ["api:apiKeyHeader"] = "X-Api-Key",
                ["nodeId"] = "node-test",
                ["siteId"] = "site-test",
                ["capacity:targetFramesPerSecond"] = "5",
                ["storage:snapshotDirectory"] = Path.GetTempPath()
            });
        });
}

