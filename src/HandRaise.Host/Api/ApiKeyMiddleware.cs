using System.Net;
using System.Security.Cryptography;
using System.Text;
using HandRaise.Host.Configuration;

namespace HandRaise.Host.Api;

public sealed class ApiKeyMiddleware(RequestDelegate next, ApiOptions options)
{
    public async Task InvokeAsync(HttpContext context)
    {
        if (!context.Request.Path.StartsWithSegments("/api/v1"))
        {
            await next(context);
            return;
        }

        var isWrite = !HttpMethods.IsGet(context.Request.Method) && !HttpMethods.IsHead(context.Request.Method) &&
            !HttpMethods.IsOptions(context.Request.Method);
        var hasConfiguredKey = !string.IsNullOrWhiteSpace(options.ApiKey);
        var suppliedKey = context.Request.Headers[options.ApiKeyHeader].FirstOrDefault()
            ?? context.Request.Query["api_key"].FirstOrDefault()
            ?? context.Request.Query["apiKey"].FirstOrDefault()
            ?? context.Request.Query[options.ApiKeyHeader].FirstOrDefault();
        if (string.IsNullOrWhiteSpace(suppliedKey) && context.Request.QueryString.HasValue)
        {
            var parsed = Microsoft.AspNetCore.WebUtilities.QueryHelpers.ParseQuery(context.Request.QueryString.Value);
            suppliedKey = parsed.TryGetValue("api_key", out var k1) ? k1.FirstOrDefault()
                : parsed.TryGetValue("apiKey", out var k2) ? k2.FirstOrDefault()
                : parsed.TryGetValue(options.ApiKeyHeader, out var k3) ? k3.FirstOrDefault()
                : null;
        }
        var authorized = isWrite
            ? hasConfiguredKey && IsValidKey(suppliedKey, options.ApiKey!)
            : hasConfiguredKey
                ? IsValidKey(suppliedKey, options.ApiKey!)
                : context.Connection.RemoteIpAddress is { } address && IPAddress.IsLoopback(address);
        if (!authorized)
        {
            await Results.Problem(
                statusCode: StatusCodes.Status401Unauthorized,
                title: "No autorizado",
                detail: !hasConfiguredKey
                    ? isWrite ? "Configure una API key para realizar escrituras." : "Sin API key configurada, la API solo acepta conexiones localhost."
                    : "La API key es inválida.").ExecuteAsync(context);
            return;
        }
        await next(context);
    }

    private static bool IsValidKey(string? supplied, string expected)
    {
        if (supplied is null) return false;
        var left = Encoding.UTF8.GetBytes(supplied);
        var right = Encoding.UTF8.GetBytes(expected);
        return left.Length == right.Length && CryptographicOperations.FixedTimeEquals(left, right);
    }
}
