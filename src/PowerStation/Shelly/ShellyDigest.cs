// SPDX-License-Identifier: GPL-2.0-or-later
using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace KQ4WLR.PowerStation.Shelly;

/// <summary>
/// RFC 7616 SHA-256 digest authentication as implemented by Shelly Gen2+
/// firmware. The username is always <c>admin</c> and the realm is the device
/// id. PowerStation stores only HA1 = SHA256(admin:realm:password), never the
/// password itself.
/// </summary>
internal static class ShellyDigest
{
    public const string Username = "admin";

    public static string Sha256Hex(string value) =>
        Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));

    public static string ComputeHa1(string realm, string password) =>
        Sha256Hex($"{Username}:{realm}:{password}");

    public static string ComputeResponse(
        string ha1, string nonce, string nc, string cnonce, string method, string uri)
    {
        var ha2 = Sha256Hex($"{method}:{uri}");
        return Sha256Hex($"{ha1}:{nonce}:{nc}:{cnonce}:auth:{ha2}");
    }

    /// <summary>Parses a <c>WWW-Authenticate: Digest ...</c> header value.</summary>
    public static DigestChallenge? ParseChallenge(string? headerValue)
    {
        if (string.IsNullOrWhiteSpace(headerValue)) return null;
        var text = headerValue.Trim();
        if (!text.StartsWith("Digest", StringComparison.OrdinalIgnoreCase)) return null;
        text = text[6..];

        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var i = 0;
        while (i < text.Length)
        {
            while (i < text.Length && (text[i] == ',' || char.IsWhiteSpace(text[i]))) i++;
            var keyStart = i;
            while (i < text.Length && text[i] != '=' && text[i] != ',') i++;
            var key = text[keyStart..i].Trim();
            if (i >= text.Length || text[i] != '=')
            {
                continue;
            }
            i++; // '='
            string value;
            if (i < text.Length && text[i] == '"')
            {
                i++;
                var sb = new StringBuilder();
                while (i < text.Length && text[i] != '"')
                {
                    if (text[i] == '\\' && i + 1 < text.Length) i++;
                    sb.Append(text[i]);
                    i++;
                }
                i++; // closing quote
                value = sb.ToString();
            }
            else
            {
                var valueStart = i;
                while (i < text.Length && text[i] != ',') i++;
                value = text[valueStart..i].Trim();
            }
            if (key.Length > 0) values[key] = value;
        }

        if (!values.TryGetValue("realm", out var realm) || !values.TryGetValue("nonce", out var nonce))
            return null;
        values.TryGetValue("algorithm", out var algorithm);
        values.TryGetValue("stale", out var stale);
        return new DigestChallenge(
            realm,
            nonce,
            string.IsNullOrEmpty(algorithm) ? "SHA-256" : algorithm,
            string.Equals(stale, "true", StringComparison.OrdinalIgnoreCase));
    }

    public static string BuildAuthorizationHeader(
        string ha1, DigestChallenge challenge, int nonceCount, string method, string uri)
    {
        var nc = nonceCount.ToString("x8", CultureInfo.InvariantCulture);
        var cnonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(8));
        var response = ComputeResponse(ha1, challenge.Nonce, nc, cnonce, method, uri);
        return $"Digest username=\"{Username}\", realm=\"{challenge.Realm}\", nonce=\"{challenge.Nonce}\", " +
               $"uri=\"{uri}\", algorithm=SHA-256, response=\"{response}\", qop=auth, nc={nc}, cnonce=\"{cnonce}\"";
    }
}

internal sealed record DigestChallenge(string Realm, string Nonce, string Algorithm, bool Stale);
