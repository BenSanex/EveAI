using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;

namespace EveEsi.Core;

public sealed class EsiClient
{
    public const string CompatibilityDate = "2025-05-20";
    public static readonly Uri ProductionOrigin = new("https://esi.evetech.net/");

    private readonly HttpClient _http;
    private readonly Uri _origin;
    private readonly ConcurrentDictionary<string, CacheEntry> _cache = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;

    public EsiClient(HttpClient http, Uri? origin = null, TimeProvider? timeProvider = null)
    {
        _http = http;
        _origin = origin ?? ProductionOrigin;
        _time = timeProvider ?? TimeProvider.System;

        if (_origin.Scheme != Uri.UriSchemeHttps && !_origin.IsLoopback)
        {
            throw new ArgumentException("ESI origin must use HTTPS.", nameof(origin));
        }
    }

    public async Task<EsiResult> GetAsync(
        string relativePath,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        if (!relativePath.StartsWith("latest/", StringComparison.Ordinal))
        {
            throw new ArgumentException("Only reviewed latest/ ESI routes are allowed.", nameof(relativePath));
        }

        var source = new Uri(_origin, relativePath);
        var key = source.AbsoluteUri;
        _cache.TryGetValue(key, out var cached);
        if (cached is not null && cached.ExpiresAt > _time.GetUtcNow())
        {
            return new(cached.Json, source, cached.RetrievedAt, true);
        }

        for (var attempt = 0; attempt < 3; attempt++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, source);
            request.Headers.Add("X-Compatibility-Date", CompatibilityDate);
            request.Headers.UserAgent.ParseAdd("Eva/0.1 (+https://github.com/BenSanex/EveAI)");
            if (!string.IsNullOrWhiteSpace(accessToken))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
            }
            if (cached?.ETag is not null)
            {
                request.Headers.IfNoneMatch.Add(new EntityTagHeaderValue(cached.ETag));
            }

            using var response = await _http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);

            if (response.StatusCode == HttpStatusCode.NotModified && cached is not null)
            {
                var refreshed = cached with { ExpiresAt = GetExpiry(response, _time.GetUtcNow()) };
                _cache[key] = refreshed;
                return new(cached.Json, source, cached.RetrievedAt, true);
            }

            if (response.StatusCode is HttpStatusCode.TooManyRequests or HttpStatusCode.ServiceUnavailable)
            {
                if (attempt == 2)
                {
                    await ThrowEsiError(response, cancellationToken).ConfigureAwait(false);
                }
                await Task.Delay(GetRetryDelay(response, attempt), _time, cancellationToken).ConfigureAwait(false);
                continue;
            }

            if (!response.IsSuccessStatusCode)
            {
                await ThrowEsiError(response, cancellationToken).ConfigureAwait(false);
            }

            var json = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            using var _ = JsonDocument.Parse(json);
            var retrievedAt = _time.GetUtcNow();
            var entry = new CacheEntry(
                json,
                response.Headers.ETag?.Tag,
                GetExpiry(response, retrievedAt),
                retrievedAt);
            _cache[key] = entry;
            return new(json, source, retrievedAt, false);
        }

        throw new InvalidOperationException("ESI retry loop ended unexpectedly.");
    }

    public async Task<EsiResult> GetPagesAsync(
        string relativePath,
        int maxPages,
        string? accessToken = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxPages, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(maxPages, 20);

        var arrays = new List<JsonElement>();
        Uri? firstSource = null;
        var allCached = true;
        for (var page = 1; page <= maxPages; page++)
        {
            var separator = relativePath.Contains('?', StringComparison.Ordinal) ? '&' : '?';
            var result = await GetAsync($"{relativePath}{separator}page={page}", accessToken, cancellationToken)
                .ConfigureAwait(false);
            firstSource ??= result.Source;
            allCached &= result.Cached;
            using var document = JsonDocument.Parse(result.Json);
            if (document.RootElement.ValueKind != JsonValueKind.Array)
            {
                throw new InvalidDataException("Paginated ESI response was not an array.");
            }
            var pageItems = document.RootElement.EnumerateArray().Select(static item => item.Clone()).ToArray();
            arrays.AddRange(pageItems);
            if (pageItems.Length == 0)
            {
                break;
            }
        }

        return new(JsonSerializer.Serialize(arrays), firstSource!, _time.GetUtcNow(), allCached, maxPages);
    }

    public static void EnsureReadOnly(HttpMethod method)
    {
        if (method != HttpMethod.Get)
        {
            throw new InvalidOperationException("Eva permits only read-only ESI GET requests.");
        }
    }

    private static DateTimeOffset GetExpiry(HttpResponseMessage response, DateTimeOffset now)
    {
        if (response.Headers.CacheControl?.MaxAge is { } maxAge)
        {
            return now.Add(maxAge);
        }
        if (response.Content.Headers.Expires is { } expires)
        {
            return expires;
        }
        return now.AddSeconds(30);
    }

    private static TimeSpan GetRetryDelay(HttpResponseMessage response, int attempt)
    {
        if (response.Headers.RetryAfter?.Delta is { } delta)
        {
            return delta > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : delta;
        }
        if (response.Headers.RetryAfter?.Date is { } date)
        {
            var remaining = date - DateTimeOffset.UtcNow;
            return remaining > TimeSpan.Zero && remaining < TimeSpan.FromSeconds(30)
                ? remaining
                : TimeSpan.FromSeconds(1);
        }
        return TimeSpan.FromMilliseconds(250 * Math.Pow(2, attempt));
    }

    private static async Task ThrowEsiError(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        var message = body.Length > 500 ? body[..500] : body;
        throw new HttpRequestException(
            string.Create(CultureInfo.InvariantCulture,
                $"ESI returned {(int)response.StatusCode} {response.ReasonPhrase}: {SecretRedactor.Redact(message)}"),
            null,
            response.StatusCode);
    }

    private sealed record CacheEntry(
        string Json,
        string? ETag,
        DateTimeOffset ExpiresAt,
        DateTimeOffset RetrievedAt);
}
