using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace EveEsi.Core;

public sealed record PkceChallenge(string Verifier, string Challenge, string State);

public static class Pkce
{
    public static PkceChallenge Create()
    {
        var verifier = Base64Url(RandomNumberGenerator.GetBytes(64));
        var challenge = Base64Url(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)));
        return new(verifier, challenge, Base64Url(RandomNumberGenerator.GetBytes(32)));
    }

    public static bool FixedTimeStateEquals(string expected, string actual) =>
        CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(expected),
            Encoding.UTF8.GetBytes(actual));

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
}

public sealed record EveIdentity(long CharacterId, string CharacterName, IReadOnlyList<string> Scopes);

public static class EveJwtClaims
{
    private static readonly HashSet<string> Issuers = new(StringComparer.Ordinal)
    {
        "https://login.eveonline.com",
        "https://login.eveonline.com/",
        "login.eveonline.com"
    };

    public static EveIdentity ValidatePayload(
        string jwt,
        string expectedAudience,
        IEnumerable<string> requiredScopes,
        DateTimeOffset now)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidDataException("Malformed JWT.");
        }

        using var document = JsonDocument.Parse(Decode(parts[1]));
        var root = document.RootElement;
        var issuer = root.GetProperty("iss").GetString();
        if (issuer is null || !Issuers.Contains(issuer))
        {
            throw new InvalidDataException("Unexpected JWT issuer.");
        }
        if (!AudienceMatches(root.GetProperty("aud"), expectedAudience))
        {
            throw new InvalidDataException("Unexpected JWT audience.");
        }
        if (root.GetProperty("exp").GetInt64() <= now.ToUnixTimeSeconds())
        {
            throw new InvalidDataException("JWT has expired.");
        }

        var subject = root.GetProperty("sub").GetString() ?? "";
        const string prefix = "CHARACTER:EVE:";
        if (!subject.StartsWith(prefix, StringComparison.Ordinal) ||
            !long.TryParse(subject[prefix.Length..], out var characterId))
        {
            throw new InvalidDataException("JWT character identity is invalid.");
        }

        var name = root.TryGetProperty("name", out var nameElement)
            ? nameElement.GetString()
            : root.TryGetProperty("http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name", out var legacyName)
                ? legacyName.GetString()
                : null;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException("JWT character name is missing.");
        }

        var scopes = ReadScopes(root);
        var missing = requiredScopes.Except(scopes, StringComparer.Ordinal).ToArray();
        if (missing.Length > 0)
        {
            throw new InvalidDataException($"Missing required scopes: {string.Join(", ", missing)}");
        }
        return new(characterId, name, scopes);
    }

    private static bool AudienceMatches(JsonElement audience, string expected)
    {
        if (audience.ValueKind != JsonValueKind.Array)
        {
            return false;
        }
        var values = audience.EnumerateArray()
            .Select(static item => item.GetString())
            .Where(static item => item is not null)
            .ToHashSet(StringComparer.Ordinal);
        return values.Contains(expected) && values.Contains("EVE Online");
    }

    private static IReadOnlyList<string> ReadScopes(JsonElement root)
    {
        if (!root.TryGetProperty("scp", out var scope))
        {
            return [];
        }
        return scope.ValueKind == JsonValueKind.Array
            ? scope.EnumerateArray().Select(static item => item.GetString()!).Where(static value => value is not null).ToArray()
            : (scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries);
    }

    private static byte[] Decode(string encoded)
    {
        var padded = encoded.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

public static class EveScopes
{
    public static readonly IReadOnlyList<string> ReadOnly =
    [
        "esi-location.read_location.v1",
        "esi-location.read_ship_type.v1",
        "esi-location.read_online.v1",
        "esi-skills.read_skills.v1",
        "esi-skills.read_skillqueue.v1",
        "esi-wallet.read_character_wallet.v1",
        "esi-assets.read_assets.v1",
        "esi-markets.read_character_orders.v1",
        "esi-industry.read_character_jobs.v1",
        "esi-contracts.read_character_contracts.v1"
    ];
}
