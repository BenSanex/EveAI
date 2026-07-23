using System.Text.Json.Serialization;

namespace EveEsi.Core;

public sealed record ApiError(string Code, string Message, string? Character = null);

public sealed record ResponseMeta(
    DateTimeOffset RetrievedAt,
    IReadOnlyList<string> SourceUrls,
    bool Cached = false,
    string? NextCursor = null,
    IReadOnlyList<string>? Characters = null);

public sealed record CliEnvelope<T>(
    bool Ok,
    T? Data,
    ResponseMeta Meta,
    IReadOnlyList<ApiError> Errors)
{
    public static CliEnvelope<T> Success(
        T data,
        IEnumerable<string> sources,
        bool cached = false,
        string? nextCursor = null,
        IEnumerable<string>? characters = null) =>
        new(true, data, new(DateTimeOffset.UtcNow, [.. sources], cached, nextCursor,
            characters is null ? null : [.. characters]), []);

    public static CliEnvelope<T> Failure(string code, string message) =>
        new(false, default, new(DateTimeOffset.UtcNow, []), [new(code, SecretRedactor.Redact(message))]);
}

public sealed record EsiResult(
    string Json,
    Uri Source,
    DateTimeOffset RetrievedAt,
    bool Cached,
    int? Pages = null);

public sealed record CharacterToken(
    long CharacterId,
    string CharacterName,
    string RefreshToken,
    IReadOnlyList<string> Scopes,
    bool IsDefault = false);

public sealed record OAuthTokenResponse(
    [property: JsonPropertyName("access_token")] string AccessToken,
    [property: JsonPropertyName("expires_in")] int ExpiresIn,
    [property: JsonPropertyName("refresh_token")] string RefreshToken,
    [property: JsonPropertyName("token_type")] string TokenType);

public static class SecretRedactor
{
    private static readonly string[] Markers = ["access_token", "refresh_token", "authorization", "bearer "];

    public static string Redact(string input)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            return input;
        }

        var lowered = input.ToLowerInvariant();
        return Markers.Any(lowered.Contains) ? "Sensitive authentication details were redacted." : input;
    }
}
