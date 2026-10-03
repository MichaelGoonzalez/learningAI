using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using HandRaise.Application.Notifications;
using HandRaise.Domain.Analytics;
using HandRaise.Domain.Notifications;

namespace HandRaise.Infrastructure.Windows.Notifications;

public sealed class HttpWebhookNotificationSender : INotificationSender
{
    private readonly HttpClient _httpClient;
    private readonly bool _allowInsecureHttp;
    private readonly bool _enableSsrfCheck;

    public NotificationDestinationType SupportedType => NotificationDestinationType.Webhook;

    public HttpWebhookNotificationSender(
        HttpClient? httpClient = null,
        bool allowInsecureHttp = true,
        bool enableSsrfCheck = false)
    {
        _httpClient = httpClient ?? new HttpClient();
        _allowInsecureHttp = allowInsecureHttp;
        _enableSsrfCheck = enableSsrfCheck;
    }

    public async Task<NotificationSenderResult> SendAsync(
        NotificationDestination destination,
        WebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(payload);

        if (destination.Configuration == null ||
            !destination.Configuration.TryGetValue("url", out var urlObj) ||
            urlObj is null ||
            string.IsNullOrWhiteSpace(urlObj.ToString()))
        {
            return new NotificationSenderResult(false, null, "URL de webhook no configurada.");
        }

        var urlStr = urlObj.ToString()!;
        if (!Uri.TryCreate(urlStr, UriKind.Absolute, out var uri))
        {
            return new NotificationSenderResult(false, null, $"URL de webhook inválida: '{urlStr}'");
        }

        if (!_allowInsecureHttp && uri.Scheme != Uri.UriSchemeHttps)
        {
            return new NotificationSenderResult(false, null, "SSRF / Política de seguridad: sólo se permiten webhooks HTTPS.");
        }

        if (_enableSsrfCheck && IsProhibitedHost(uri))
        {
            return new NotificationSenderResult(false, null, "SSRF / Destino prohibido por política de seguridad.");
        }

        var methodStr = "POST";
        if (destination.Configuration.TryGetValue("method", out var methodObj) && methodObj is not null)
        {
            methodStr = methodObj.ToString()!.ToUpperInvariant();
        }

        var httpMethod = methodStr switch
        {
            "PUT" => HttpMethod.Put,
            "PATCH" => HttpMethod.Patch,
            _ => HttpMethod.Post
        };

        var timeoutMs = 5000;
        if (destination.Configuration.TryGetValue("timeout_ms", out var timeoutObj) && timeoutObj is not null)
        {
            if (int.TryParse(timeoutObj.ToString(), out var parsedTimeout) && parsedTimeout > 0)
            {
                timeoutMs = parsedTimeout;
            }
        }

        using var request = new HttpRequestMessage(httpMethod, uri);
        var jsonContent = JsonSerializer.Serialize(payload, AnalyticJsonDefaults.Options);
        request.Content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

        if (destination.Configuration.TryGetValue("secret_token", out var secretObj) && secretObj is not null && !string.IsNullOrWhiteSpace(secretObj.ToString()))
        {
            var secret = secretObj.ToString()!;
            if (secret != "********")
            {
                request.Headers.TryAddWithoutValidation("X-Webhook-Token", secret);
            }
        }

        if (destination.Configuration.TryGetValue("headers", out var headersObj) && headersObj is not null)
        {
            if (headersObj is IReadOnlyDictionary<string, object?> headersDict)
            {
                foreach (var (k, v) in headersDict)
                {
                    if (v is not null && v.ToString() != "********")
                    {
                        request.Headers.TryAddWithoutValidation(k, v.ToString());
                    }
                }
            }
            else if (headersObj is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in je.EnumerateObject())
                {
                    var val = prop.Value.ToString();
                    if (val != "********")
                    {
                        request.Headers.TryAddWithoutValidation(prop.Name, val);
                    }
                }
            }
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(TimeSpan.FromMilliseconds(timeoutMs));

        try
        {
            var response = await _httpClient.SendAsync(request, cts.Token);
            var statusCode = (int)response.StatusCode;

            if (response.IsSuccessStatusCode)
            {
                return new NotificationSenderResult(true, statusCode, null, null);
            }

            string responseBody;
            try
            {
                responseBody = await response.Content.ReadAsStringAsync(cts.Token);
                if (responseBody.Length > 300) responseBody = responseBody[..300] + "...";
            }
            catch
            {
                responseBody = "";
            }

            return new NotificationSenderResult(
                Success: false,
                StatusCode: statusCode,
                Error: $"HTTP {statusCode} {response.ReasonPhrase}: {responseBody}");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return new NotificationSenderResult(false, null, $"Webhook timeout tras {timeoutMs}ms");
        }
        catch (Exception ex)
        {
            return new NotificationSenderResult(false, null, $"Error enviando webhook: {ex.Message}");
        }
    }

    private static bool IsProhibitedHost(Uri uri)
    {
        var host = uri.Host.ToLowerInvariant();
        if (host == "169.254.169.254" || host == "metadata.google.internal" || host == "instance-data")
        {
            return true;
        }

        if (IPAddress.TryParse(host, out var ip))
        {
            if (ip.IsIPv6LinkLocal || ip.IsIPv6Multicast) return true;
            var bytes = ip.GetAddressBytes();
            if (bytes.Length == 4)
            {
                // 169.254.0.0/16 link-local
                if (bytes[0] == 169 && bytes[1] == 254) return true;
                // 224.0.0.0/4 multicast
                if (bytes[0] >= 224 && bytes[0] <= 239) return true;
            }
        }

        return false;
    }
}
