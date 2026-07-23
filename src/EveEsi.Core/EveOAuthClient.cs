using System.Net.Http.Json;

namespace EveEsi.Core;

public sealed class EveOAuthClient(HttpClient http, string clientId)
{
    public static readonly Uri TokenEndpoint = new("https://login.eveonline.com/v2/oauth/token");

    public async Task<OAuthTokenResponse> RefreshAsync(
        string refreshToken,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("EVE SSO client ID is not configured.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "refresh_token",
                ["refresh_token"] = refreshToken,
                ["client_id"] = clientId
            })
        };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            var reason = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new HttpRequestException(
                $"EVE SSO token refresh failed ({(int)response.StatusCode}): {SecretRedactor.Redact(reason)}",
                null,
                response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("EVE SSO returned an empty token response.");
    }
}
