using System.Text.Json;
using System.Text.RegularExpressions;

namespace TestAgent.Infrastructure;

public static partial class SensitiveDataRedactor
{
    private static readonly string[] SensitiveNames =
    ["content", "oldtext", "newtext", "apikey", "api_key", "authorization", "token", "password", "secret", "credential", "key"];

    public static string Arguments(string json, int max = 1_200)
    {
        try
        {
            using var document = JsonDocument.Parse(json); var value = RedactElement(document.RootElement, null);
            return Limit(JsonSerializer.Serialize(value), max);
        }
        catch { return "<invalid-json-redacted>"; }
    }

    public static string Text(string? value, int max = 1_200)
    {
        if (string.IsNullOrEmpty(value)) return value ?? "";
        var result = BearerRegex().Replace(value, "$1<redacted>");
        result = KeyValueRegex().Replace(result, "$1<redacted>");
        return Limit(result, max);
    }

    public static bool ContainsLikelySecret(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        return BearerRegex().IsMatch(value) || KeyValueRegex().IsMatch(value) ||
               PrivateKeyRegex().IsMatch(value) || CommonTokenRegex().IsMatch(value);
    }

    private static object? RedactElement(JsonElement value, string? propertyName)
    {
        if (propertyName?.Equals("url", StringComparison.OrdinalIgnoreCase) == true && value.ValueKind == JsonValueKind.String)
            return SanitizeUrl(value.GetString());
        if (propertyName is not null && IsSensitive(propertyName))
            return $"<redacted:{RawLength(value)} chars>";
        return value.ValueKind switch
        {
            JsonValueKind.Object => value.EnumerateObject().ToDictionary(x => x.Name, x => RedactElement(x.Value, x.Name), StringComparer.OrdinalIgnoreCase),
            JsonValueKind.Array => value.EnumerateArray().Select(x => RedactElement(x, propertyName)).ToArray(),
            JsonValueKind.String => value.GetString(), JsonValueKind.Number => value.GetRawText(),
            JsonValueKind.True => true, JsonValueKind.False => false, _ => null
        };
    }
    private static bool IsSensitive(string name) => SensitiveNames.Any(x => name.Equals(x, StringComparison.OrdinalIgnoreCase) || name.EndsWith("_" + x, StringComparison.OrdinalIgnoreCase));
    private static string SanitizeUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return "<invalid-url-redacted>";
        return uri.GetLeftPart(UriPartial.Authority) + (uri.AbsolutePath == "/" ? "/" : "/<redacted-path>") +
               (string.IsNullOrEmpty(uri.Query) ? "" : "?<redacted>");
    }
    private static int RawLength(JsonElement value) => value.ValueKind == JsonValueKind.String ? (value.GetString() ?? "").Length : value.GetRawText().Length;
    private static string Limit(string value, int max) => value.Length <= max ? value : value[..max] + "…";

    [GeneratedRegex(@"(?i)(Bearer\s+)[A-Za-z0-9._~+/=-]{8,}")]
    private static partial Regex BearerRegex();
    [GeneratedRegex("(?i)((?:api[_-]?key|authorization|token|password|secret|credential)\\s*[:=]\\s*)[^\\s,;\\\"']+")]
    private static partial Regex KeyValueRegex();
    [GeneratedRegex("-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----", RegexOptions.IgnoreCase)]
    private static partial Regex PrivateKeyRegex();
    [GeneratedRegex(@"(?<![A-Za-z0-9])(?:sk-[A-Za-z0-9_-]{16,}|gh[opusr]_[A-Za-z0-9]{20,})(?![A-Za-z0-9])", RegexOptions.IgnoreCase)]
    private static partial Regex CommonTokenRegex();
}
