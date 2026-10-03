using System.Net;
using HandRaise.Application.Events;
using HandRaise.Application.Storage;
using HandRaise.Host;
using HandRaise.Host.Api;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace HandRaise.Host.Tests;

public sealed class CorsAndPreflightApiTests
{
    public const string RealAiStudioPreviewOrigin = "https://ais-dev-qpm5r6zpk5xw6maf6w73q5-828775920475.us-east1.run.app";
    public const string ProductionOrigin = "https://vision-control-module.ai.studio";
    public const string AiStudioPreOrigin = "https://ais-pre-test123.us-east1.run.app";
    public const string MaliciousRunAppOrigin = "https://malicious.run.app";
    public const string FakeEvilDomainOrigin = "https://ais-dev-fake.evil.com";
    public const string FakeSubdomainEvilOrigin = "https://ais-dev-fake.us-east1.run.app.evil.com";
    public const string Localhost3000Origin = "http://localhost:3000";
    public const string LocalIp3000Origin = "http://127.0.0.1:3000";
    public const string Localhost3001Disallowed = "http://localhost:3001";
    public const string LocalIp5173Disallowed = "http://127.0.0.1:5173";
    public const string InsecureHttpOrigin = "http://ais-dev-test.us-east1.run.app";
    public const string PrefixSpoofOrigin = "https://evil-ais-dev-test.us-east1.run.app";
    public const string ConfiguredCustomOrigin = "http://localhost:4000";
    public const string ValidApiKey = "4f9b8c7e1a2d3e4f5a6b7c8d9e0f1a2b";

    [Theory]
    [InlineData(RealAiStudioPreviewOrigin, true)]
    [InlineData(ProductionOrigin, true)]
    [InlineData(Localhost3000Origin, true)]
    [InlineData(LocalIp3000Origin, true)]
    [InlineData(AiStudioPreOrigin, true)]
    [InlineData("https://ais-dev-test.run.app", true)]
    [InlineData("https://ais-pre-abc.run.app", true)]
    [InlineData(Localhost3001Disallowed, false)]
    [InlineData(LocalIp5173Disallowed, false)]
    [InlineData(MaliciousRunAppOrigin, false)]
    [InlineData(FakeEvilDomainOrigin, false)]
    [InlineData(FakeSubdomainEvilOrigin, false)]
    [InlineData(InsecureHttpOrigin, false)]
    [InlineData(PrefixSpoofOrigin, false)]
    [InlineData("https://example.invalid", false)]
    [InlineData("https://user:pass@ais-dev-qpm5r6zpk5xw6maf6w73q5-828775920475.us-east1.run.app", false)]
    [InlineData("https://ais-dev-.us-east1.run.app", false)]
    [InlineData(null, false)]
    [InlineData("", false)]
    public void CorsOriginValidatorValidatesOriginsCorrectly(string? origin, bool expected)
    {
        var result = CorsOriginValidator.IsAllowedOrigin(origin);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void CorsOriginValidatorAcceptsCustomConfiguredOrigins()
    {
        var configured = new[] { ConfiguredCustomOrigin, "https://custom-portal.company.com" };
        Assert.True(CorsOriginValidator.IsAllowedOrigin(ConfiguredCustomOrigin, configured));
        Assert.True(CorsOriginValidator.IsAllowedOrigin("https://custom-portal.company.com", configured));
        Assert.False(CorsOriginValidator.IsAllowedOrigin("https://not-in-list.com", configured));
    }

    [Fact]
    public async Task OptionsPreflightFromExactAiStudioPreviewOriginSucceedsWithCorsHeaders()
    {
        await using var app = CreateApp(ValidApiKey);
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/health");
        request.Headers.Add("Origin", RealAiStudioPreviewOrigin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-api-key");

        var response = await client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.OK,
            $"Expected 204 or 200 for preflight from {RealAiStudioPreviewOrigin}, got {response.StatusCode}");

        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(RealAiStudioPreviewOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());

        Assert.True(response.Headers.Contains("Access-Control-Allow-Methods"));
        var allowedMethods = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Methods"));
        Assert.Contains("GET", allowedMethods);

        Assert.True(response.Headers.Contains("Access-Control-Allow-Headers"));
        var allowedHeaders = string.Join(",", response.Headers.GetValues("Access-Control-Allow-Headers"));
        Assert.Contains("x-api-key", allowedHeaders, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("content-type", allowedHeaders, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task GetHealthWithExactAiStudioPreviewOriginAndValidApiKeyReturns200WithCorsHeaders()
    {
        await using var app = CreateApp(ValidApiKey);
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add("Origin", RealAiStudioPreviewOrigin);
        request.Headers.Add("X-Api-Key", ValidApiKey);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(RealAiStudioPreviewOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    [Theory]
    [InlineData(ProductionOrigin)]
    [InlineData(Localhost3000Origin)]
    [InlineData(LocalIp3000Origin)]
    [InlineData(AiStudioPreOrigin)]
    public async Task OptionsPreflightFromOtherAllowedOriginsReturnsCorsHeaders(string origin)
    {
        await using var app = CreateApp(ValidApiKey);
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/health");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-api-key");

        var response = await client.SendAsync(request);

        Assert.True(response.StatusCode == HttpStatusCode.NoContent || response.StatusCode == HttpStatusCode.OK);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    [Theory]
    [InlineData(Localhost3001Disallowed)]
    [InlineData(LocalIp5173Disallowed)]
    [InlineData(MaliciousRunAppOrigin)]
    [InlineData(FakeEvilDomainOrigin)]
    [InlineData(FakeSubdomainEvilOrigin)]
    [InlineData(InsecureHttpOrigin)]
    [InlineData(PrefixSpoofOrigin)]
    public async Task OptionsPreflightFromDisallowedOriginsDoesNotReturnAllowOrigin(string origin)
    {
        await using var app = CreateApp(ValidApiKey);
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Options, "/api/v1/health");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("Access-Control-Request-Method", "GET");
        request.Headers.Add("Access-Control-Request-Headers", "content-type,x-api-key");

        var response = await client.SendAsync(request);

        Assert.False(response.Headers.Contains("Access-Control-Allow-Origin"),
            $"Disallowed origin {origin} should not receive Access-Control-Allow-Origin header.");
    }

    [Fact]
    public async Task GetHealthWithoutApiKeyReturns401WithCorsHeadersForPreviewOrigin()
    {
        await using var app = CreateApp(ValidApiKey);
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add("Origin", RealAiStudioPreviewOrigin);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(RealAiStudioPreviewOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    [Fact]
    public async Task GetHealthWithInvalidApiKeyReturns401WithCorsHeadersForPreviewOrigin()
    {
        await using var app = CreateApp(ValidApiKey);
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add("Origin", RealAiStudioPreviewOrigin);
        request.Headers.Add("X-Api-Key", "invalid-key-12345");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(RealAiStudioPreviewOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    [Fact]
    public async Task GetHealthWithProductionOriginAndValidApiKeyReturns200WithCorsHeaders()
    {
        await using var app = CreateApp(ValidApiKey);
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add("Origin", ProductionOrigin);
        request.Headers.Add("X-Api-Key", ValidApiKey);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(ProductionOrigin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    [Theory]
    [InlineData(Localhost3000Origin)]
    [InlineData(LocalIp3000Origin)]
    public async Task GetHealthWithLocal3000OriginAndValidApiKeyReturns200WithCorsHeaders(string origin)
    {
        await using var app = CreateApp(ValidApiKey);
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Api-Key", ValidApiKey);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    [Theory]
    [InlineData(Localhost3000Origin)]
    [InlineData(LocalIp3000Origin)]
    public async Task GetHealthWithLocal3000OriginWithoutApiKeyReturns401WithCorsHeaders(string origin)
    {
        await using var app = CreateApp(ValidApiKey);
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add("Origin", origin);

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    [Theory]
    [InlineData(Localhost3000Origin)]
    [InlineData(LocalIp3000Origin)]
    public async Task GetHealthWithLocal3000OriginInvalidApiKeyReturns401WithCorsHeaders(string origin)
    {
        await using var app = CreateApp(ValidApiKey);
        await app.StartAsync();
        var client = app.GetTestClient();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health");
        request.Headers.Add("Origin", origin);
        request.Headers.Add("X-Api-Key", "bad-key");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.True(response.Headers.Contains("Access-Control-Allow-Origin"));
        Assert.Equal(origin, response.Headers.GetValues("Access-Control-Allow-Origin").FirstOrDefault());
    }

    private static WebApplication CreateApp(string apiKey, string[]? allowedOrigins = null) =>
        HostApplication.Build([], startCameras: false, builder =>
        {
            builder.WebHost.UseTestServer();
            builder.Environment.EnvironmentName = "Production";
            var inMemory = new Dictionary<string, string?>
            {
                ["api:apiKey"] = apiKey,
                ["api:apiKeyHeader"] = "X-Api-Key",
                ["nodeId"] = "node-test",
                ["siteId"] = "site-test",
                ["capacity:targetFramesPerSecond"] = "5",
                ["storage:snapshotDirectory"] = Path.GetTempPath()
            };
            if (allowedOrigins != null)
            {
                for (var i = 0; i < allowedOrigins.Length; i++)
                {
                    inMemory[$"api:allowedOrigins:{i}"] = allowedOrigins[i];
                }
            }
            builder.Configuration.AddInMemoryCollection(inMemory);
            builder.Services.AddSingleton<IHandEventRepository>(new FakeEventRepository());
        });

    private sealed class FakeEventRepository : IHandEventRepository
    {
        public Task<IReadOnlyList<HandEvent>> QueryAsync(EventQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<HandEvent>>([]);
        public Task<HandEvent?> GetByIdAsync(string id, CancellationToken cancellationToken = default) =>
            Task.FromResult<HandEvent?>(null);
        public Task<IReadOnlyList<EventStatistic>> GetStatisticsAsync(EventQuery query, CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EventStatistic>>([]);
        public Task SaveBatchAsync(IReadOnlyList<HandEvent> events, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task<IReadOnlyList<StoredSnapshot>> GetSnapshotsAsync(CancellationToken cancellationToken = default) => Task.FromResult<IReadOnlyList<StoredSnapshot>>([]);
        public Task DeleteEventsBeforeAsync(DateTimeOffset cutoff, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task ClearSnapshotPathAsync(string eventId, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
