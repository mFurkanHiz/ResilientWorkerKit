using System.Text.RegularExpressions;

namespace ResilientWorkerKit;

/// <summary>
/// Masks credentials inside free-form text and identifies sensitive headers.
/// <para>
/// Used by the engine before persisting exception messages to the execution history, by the
/// HTTP package's safe logging handler, and available to applications for their own diagnostics.
/// It is a defense-in-depth net, not a guarantee: the reliable protection is not putting secrets
/// into exception messages, log arguments or dead-letter payloads in the first place
/// (see docs/security.md).
/// </para>
/// </summary>
public static partial class SensitiveDataMasker
{
    /// <summary>Replacement for masked values.</summary>
    public const string Mask = "***";

    private static readonly HashSet<string> SensitiveHeaders = new(StringComparer.OrdinalIgnoreCase)
    {
        "Authorization",
        "Proxy-Authorization",
        "Cookie",
        "Set-Cookie",
        "X-Api-Key",
        "Api-Key",
        "X-Auth-Token",
        "X-Amz-Security-Token",
    };

    [GeneratedRegex(@"(?i)\b(bearer|basic)\s+[A-Za-z0-9\-._~+/=]+", RegexOptions.CultureInvariant)]
    private static partial Regex AuthSchemeRegex();

    [GeneratedRegex(@"(?i)\b(api[_-]?key|access[_-]?token|refresh[_-]?token|client[_-]?secret|password|secret|token)\s*([=:])\s*[^\s&""',;]+", RegexOptions.CultureInvariant)]
    private static partial Regex KeyValueRegex();

    /// <summary>Returns whether the header must never be logged in clear text.</summary>
    public static bool IsSensitiveHeader(string headerName, IEnumerable<string>? additional = null)
    {
        if (SensitiveHeaders.Contains(headerName))
        {
            return true;
        }

        return additional is not null && additional.Contains(headerName, StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Masks bearer/basic credentials and key=value style secrets inside a string.</summary>
    public static string MaskSecrets(string input)
    {
        if (string.IsNullOrEmpty(input))
        {
            return input;
        }

        var masked = AuthSchemeRegex().Replace(input, m => $"{m.Groups[1].Value} {Mask}");
        masked = KeyValueRegex().Replace(masked, m => $"{m.Groups[1].Value}{m.Groups[2].Value}{Mask}");
        return masked;
    }
}
