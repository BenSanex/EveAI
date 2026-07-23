using System.Diagnostics;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace EveEsi.Core;

public sealed class EveAuthorizationFlow(HttpClient http, string clientId, Uri callbackUri)
{
    public static readonly Uri AuthorizationEndpoint = new("https://login.eveonline.com/v2/oauth/authorize");

    public async Task<CharacterToken> LinkAsync(
        Func<Uri, CancellationToken, Task> openBrowser,
        CancellationToken cancellationToken = default)
    {
        if (!callbackUri.IsLoopback || callbackUri.Scheme != Uri.UriSchemeHttp)
        {
            throw new InvalidOperationException("EVE SSO callback must be an HTTP loopback URI.");
        }
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new InvalidOperationException("EVE SSO client ID is required.");
        }

        var pkce = Pkce.Create();
        var authorization = BuildAuthorizationUri(pkce);
        using var listener = new HttpListener();
        listener.Prefixes.Add(callbackUri.AbsoluteUri);
        listener.Start();
        await openBrowser(authorization, cancellationToken).ConfigureAwait(false);

        var context = await listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
        var query = context.Request.QueryString;
        var state = query["state"] ?? "";
        var code = query["code"];
        var oauthError = query["error"];
        await Respond(context.Response, oauthError is null && code is not null, cancellationToken).ConfigureAwait(false);
        if (!Pkce.FixedTimeStateEquals(pkce.State, state))
        {
            throw new InvalidDataException("OAuth state did not match.");
        }
        if (oauthError is not null || string.IsNullOrWhiteSpace(code))
        {
            throw new InvalidOperationException($"EVE SSO authorization failed: {oauthError ?? "missing code"}");
        }

        var tokens = await ExchangeCode(code, pkce.Verifier, cancellationToken).ConfigureAwait(false);
        var identity = await new EveJwtValidator(http).ValidateAsync(
            tokens.AccessToken, clientId, EveScopes.ReadOnly, DateTimeOffset.UtcNow, cancellationToken)
            .ConfigureAwait(false);
        return new(identity.CharacterId, identity.CharacterName, tokens.RefreshToken, identity.Scopes);
    }

    private Uri BuildAuthorizationUri(PkceChallenge pkce)
    {
        var values = new Dictionary<string, string>
        {
            ["response_type"] = "code",
            ["redirect_uri"] = callbackUri.AbsoluteUri,
            ["client_id"] = clientId,
            ["scope"] = string.Join(' ', EveScopes.ReadOnly),
            ["code_challenge"] = pkce.Challenge,
            ["code_challenge_method"] = "S256",
            ["state"] = pkce.State
        };
        return new UriBuilder(AuthorizationEndpoint)
        {
            Query = string.Join('&', values.Select(static pair =>
                $"{Uri.EscapeDataString(pair.Key)}={Uri.EscapeDataString(pair.Value)}"))
        }.Uri;
    }

    private async Task<OAuthTokenResponse> ExchangeCode(
        string code,
        string verifier,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, EveOAuthClient.TokenEndpoint)
        {
            Content = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["grant_type"] = "authorization_code",
                ["code"] = code,
                ["client_id"] = clientId,
                ["code_verifier"] = verifier,
                ["redirect_uri"] = callbackUri.AbsoluteUri
            })
        };
        using var response = await http.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"EVE SSO code exchange failed ({(int)response.StatusCode}).",
                null,
                response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<OAuthTokenResponse>(cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidDataException("EVE SSO token response was empty.");
    }

    private static async Task Respond(HttpListenerResponse response, bool success, CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(success
            ? "<html><body><h1>Character linked</h1><p>You may return to Eva.</p></body></html>"
            : "<html><body><h1>Authorization failed</h1><p>You may return to Eva.</p></body></html>");
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body, cancellationToken).ConfigureAwait(false);
        response.Close();
    }
}

public sealed class EveJwtValidator(HttpClient http)
{
    public static readonly Uri JwksEndpoint = new("https://login.eveonline.com/oauth/jwks");

    public async Task<EveIdentity> ValidateAsync(
        string jwt,
        string expectedAudience,
        IEnumerable<string> requiredScopes,
        DateTimeOffset now,
        CancellationToken cancellationToken = default)
    {
        var parts = jwt.Split('.');
        if (parts.Length != 3)
        {
            throw new InvalidDataException("Malformed JWT.");
        }
        using var header = JsonDocument.Parse(Decode(parts[0]));
        if (header.RootElement.GetProperty("alg").GetString() != "RS256")
        {
            throw new InvalidDataException("Unexpected JWT signing algorithm.");
        }
        var kid = header.RootElement.GetProperty("kid").GetString()
            ?? throw new InvalidDataException("JWT signing key ID is missing.");
        using var response = await http.GetAsync(JwksEndpoint, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var keys = await response.Content.ReadFromJsonAsync<JsonObject>(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidDataException("EVE JWKS was empty.");
        var key = keys["keys"]?.AsArray().FirstOrDefault(item => item?["kid"]?.GetValue<string>() == kid)
            ?? throw new InvalidDataException("JWT signing key was not found.");
        VerifyRs256(parts[0] + "." + parts[1], parts[2], key);
        return EveJwtClaims.ValidatePayload(jwt, expectedAudience, requiredScopes, now);
    }

    public static void VerifyRs256(string signingInput, string encodedSignature, JsonNode jwk)
    {
        if (jwk["kty"]?.GetValue<string>() != "RSA" || jwk["alg"]?.GetValue<string>() is { } alg && alg != "RS256")
        {
            throw new InvalidDataException("Unsupported JWT signing key.");
        }
        using var rsa = RSA.Create();
        rsa.ImportParameters(new RSAParameters
        {
            Modulus = Decode(jwk["n"]?.GetValue<string>() ?? throw new InvalidDataException("JWK modulus is missing.")),
            Exponent = Decode(jwk["e"]?.GetValue<string>() ?? throw new InvalidDataException("JWK exponent is missing."))
        });
        var valid = rsa.VerifyData(
            Encoding.ASCII.GetBytes(signingInput),
            Decode(encodedSignature),
            HashAlgorithmName.SHA256,
            RSASignaturePadding.Pkcs1);
        if (!valid)
        {
            throw new CryptographicException("JWT signature is invalid.");
        }
    }

    private static byte[] Decode(string encoded)
    {
        var padded = encoded.Replace('-', '+').Replace('_', '/');
        padded += new string('=', (4 - padded.Length % 4) % 4);
        return Convert.FromBase64String(padded);
    }
}

public static class BrowserLauncher
{
    public static Task OpenAsync(Uri uri, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var info = new ProcessStartInfo("xdg-open") { UseShellExecute = false };
        info.ArgumentList.Add(uri.AbsoluteUri);
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not open the browser.");
        return Task.CompletedTask;
    }
}
