using System;
using System.Collections.Generic;
using Microsoft.Extensions.Logging;

namespace HandRaise.Host.Api;

public static class CorsOriginValidator
{
    public const string ProductionOrigin = "https://vision-control-module.ai.studio";
    public const string ProductionHost = "vision-control-module.ai.studio";
    public const string Localhost3000Origin = "http://localhost:3000";
    public const string LocalIp3000Origin = "http://127.0.0.1:3000";

    public static bool IsAllowedOrigin(
        string? origin,
        IEnumerable<string>? additionalAllowedOrigins = null,
        ILogger? logger = null)
    {
        if (string.IsNullOrWhiteSpace(origin))
        {
            return false;
        }

        var normalized = origin.Trim().TrimEnd('/');

        if (string.Equals(normalized, ProductionOrigin, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, Localhost3000Origin, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(normalized, LocalIp3000Origin, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (additionalAllowedOrigins != null)
        {
            foreach (var allowed in additionalAllowedOrigins)
            {
                if (string.IsNullOrWhiteSpace(allowed)) continue;
                var allowedNormalized = allowed.Trim().TrimEnd('/');
                if (string.Equals(normalized, allowedNormalized, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        if (!Uri.TryCreate(normalized, UriKind.Absolute, out var uri))
        {
            logger?.LogWarning("CORS origin rejected (invalid absolute URI): {Origin}", normalized);
            return false;
        }

        // Strict scheme check: HTTPS only
        if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase))
        {
            logger?.LogWarning("CORS origin rejected (non-HTTPS): {Origin}", normalized);
            return false;
        }

        // Must not contain userinfo
        if (!string.IsNullOrEmpty(uri.UserInfo))
        {
            logger?.LogWarning("CORS origin rejected (contains userinfo): {Origin}", normalized);
            return false;
        }

        // Must not contain non-default port if present in standard origin format (or must be 443)
        if (!uri.IsDefaultPort && uri.Port != 443)
        {
            logger?.LogWarning("CORS origin rejected (non-standard port): {Origin}", normalized);
            return false;
        }

        var host = uri.Host;

        // Check if host is production
        if (string.Equals(host, ProductionHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        // Validate AI Studio Preview: ais-dev-<id>.<region>.run.app or ais-pre-<id>.<region>.run.app
        if (IsValidAiStudioPreviewHost(host))
        {
            return true;
        }

        logger?.LogWarning("CORS origin rejected: {Origin}", normalized);
        return false;
    }

    private static bool IsValidAiStudioPreviewHost(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;

        var labels = host.Split('.');
        // Structure must be:
        // 4 labels: [0]=ais-(dev|pre)-<id>, [1]=<region>, [2]="run", [3]="app"
        // or 3 labels: [0]=ais-(dev|pre)-<id>, [1]="run", [2]="app"
        if (labels.Length != 3 && labels.Length != 4)
        {
            return false;
        }

        if (!string.Equals(labels[^2], "run", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(labels[^1], "app", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var firstLabel = labels[0];
        string idPart;
        if (firstLabel.StartsWith("ais-dev-", StringComparison.OrdinalIgnoreCase))
        {
            idPart = firstLabel["ais-dev-".Length..];
        }
        else if (firstLabel.StartsWith("ais-pre-", StringComparison.OrdinalIgnoreCase))
        {
            idPart = firstLabel["ais-pre-".Length..];
        }
        else
        {
            return false;
        }

        if (string.IsNullOrEmpty(idPart) || !IsValidHostLabel(idPart))
        {
            return false;
        }

        if (labels.Length == 4)
        {
            var regionLabel = labels[1];
            if (string.IsNullOrEmpty(regionLabel) || !IsValidHostLabel(regionLabel))
            {
                return false;
            }
        }

        return true;
    }

    private static bool IsValidHostLabel(string label)
    {
        if (string.IsNullOrEmpty(label)) return false;
        if (label.StartsWith('-') || label.EndsWith('-')) return false;

        foreach (var c in label)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c != '-')
            {
                return false;
            }
        }

        return true;
    }
}
