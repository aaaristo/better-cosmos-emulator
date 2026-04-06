using System.Security.Cryptography;
using System.Text;
using System.Web;

namespace Cosmos.Emulator.Core.Auth;

public class HmacSignatureValidator
{
    private readonly byte[] _masterKeyBytes;

    public HmacSignatureValidator(string masterKeyBase64)
    {
        _masterKeyBytes = Convert.FromBase64String(masterKeyBase64);
    }

    public bool Validate(string authorizationHeader, string verb, string resourceType, string resourceLink, string date)
    {
        var parsed = ParseAuthorizationHeader(authorizationHeader);
        if (parsed is null)
            return false;

        var (type, version, signature) = parsed.Value;

        if (!string.Equals(type, "master", StringComparison.OrdinalIgnoreCase))
            return false;

        if (!string.Equals(version, "1.0", StringComparison.OrdinalIgnoreCase))
            return false;

        // Try standard lowercase resource link first
        var expectedSignature = GenerateSignature(verb, resourceType, resourceLink, date);
        if (string.Equals(signature, expectedSignature, StringComparison.Ordinal))
            return true;

        // The SDK uses _rid-based paths where base64 is case-sensitive.
        // Try without lowercasing the resource link (for _rid-based auth).
        var ridSignature = GenerateSignaturePreserveCase(verb, resourceType, resourceLink, date);
        return string.Equals(signature, ridSignature, StringComparison.Ordinal);
    }

    public string GenerateSignature(string verb, string resourceType, string resourceLink, string date)
    {
        var payload = $"{verb.ToLowerInvariant()}\n{resourceType.ToLowerInvariant()}\n{resourceLink}\n{date.ToLowerInvariant()}\n\n";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(_masterKeyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToBase64String(hash);
    }

    public string GenerateSignaturePreserveCase(string verb, string resourceType, string resourceLink, string date)
    {
        // For _rid-based paths: verb and resourceType are lowercased, but resourceLink keeps original case
        var payload = $"{verb.ToLowerInvariant()}\n{resourceType.ToLowerInvariant()}\n{resourceLink}\n{date.ToLowerInvariant()}\n\n";
        var payloadBytes = Encoding.UTF8.GetBytes(payload);

        using var hmac = new HMACSHA256(_masterKeyBytes);
        var hash = hmac.ComputeHash(payloadBytes);
        return Convert.ToBase64String(hash);
    }

    public string GenerateAuthorizationHeader(string verb, string resourceType, string resourceLink, string date)
    {
        var signature = GenerateSignature(verb, resourceType, resourceLink, date);
        var authPayload = $"type=master&ver=1.0&sig={signature}";
        return HttpUtility.UrlEncode(authPayload);
    }

    private static (string type, string version, string signature)? ParseAuthorizationHeader(string header)
    {
        var decoded = HttpUtility.UrlDecode(header);
        if (string.IsNullOrEmpty(decoded))
            return null;

        string? type = null, version = null, signature = null;

        foreach (var part in decoded.Split('&'))
        {
            var kvp = part.Split('=', 2);
            if (kvp.Length != 2) continue;

            switch (kvp[0].ToLowerInvariant())
            {
                case "type":
                    type = kvp[1];
                    break;
                case "ver":
                    version = kvp[1];
                    break;
                case "sig":
                    signature = kvp[1];
                    break;
            }
        }

        if (type is null || version is null || signature is null)
            return null;

        return (type, version, signature);
    }
}
