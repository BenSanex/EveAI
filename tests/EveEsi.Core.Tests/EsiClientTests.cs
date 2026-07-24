using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.Json;
using EveEsi.Core;

namespace EveEsi.Core.Tests;

public sealed class EsiClientTests
{
    [Fact]
    public async Task GetAsync_IsGetOnly_SendsCompatibilityDate_AndCaches()
    {
        var calls = 0;
        var handler = new StubHandler(request =>
        {
            calls++;
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal(EsiClient.CompatibilityDate, request.Headers.GetValues("X-Compatibility-Date").Single());
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"system_id\":30000142}"),
                Headers = { CacheControl = new() { MaxAge = TimeSpan.FromMinutes(1) } }
            };
        });
        var client = new EsiClient(new HttpClient(handler), new Uri("http://127.0.0.1:12345/"));

        var first = await client.GetAsync("latest/characters/1/location/");
        var second = await client.GetAsync("latest/characters/1/location/");

        Assert.False(first.Cached);
        Assert.True(second.Cached);
        Assert.Equal(1, calls);
    }

    [Theory]
    [InlineData("v1/markets/prices/")]
    [InlineData("https://evil.example/latest/markets/prices/")]
    public async Task GetAsync_RejectsUnreviewedPaths(string path)
    {
        var client = new EsiClient(new HttpClient(new StubHandler(_ => new(HttpStatusCode.OK))));
        await Assert.ThrowsAsync<ArgumentException>(() => client.GetAsync(path));
    }

    [Fact]
    public void EnsureReadOnly_RejectsEveryWriteVerb()
    {
        foreach (var method in new[] { HttpMethod.Post, HttpMethod.Put, HttpMethod.Patch, HttpMethod.Delete })
        {
            Assert.Throws<InvalidOperationException>(() => EsiClient.EnsureReadOnly(method));
        }
        EsiClient.EnsureReadOnly(HttpMethod.Get);
    }

    [Fact]
    public void Pkce_IsRandom_AndStateComparisonWorks()
    {
        var first = Pkce.Create();
        var second = Pkce.Create();
        Assert.NotEqual(first.Verifier, second.Verifier);
        Assert.NotEqual(first.State, second.State);
        Assert.True(Pkce.FixedTimeStateEquals(first.State, first.State));
        Assert.False(Pkce.FixedTimeStateEquals(first.State, second.State));
    }

    [Fact]
    public void VerifyRs256_ValidatesSignature()
    {
        using var rsa = RSA.Create(2048);
        var parameters = rsa.ExportParameters(false);
        const string input = "header.payload";
        var signature = rsa.SignData(
            Encoding.ASCII.GetBytes(input), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1);
        var jwk = new JsonObject
        {
            ["kty"] = "RSA",
            ["alg"] = "RS256",
            ["n"] = Base64Url(parameters.Modulus!),
            ["e"] = Base64Url(parameters.Exponent!)
        };

        EveJwtValidator.VerifyRs256(input, Base64Url(signature), jwk);
        Assert.Throws<CryptographicException>(() =>
            EveJwtValidator.VerifyRs256(input + "x", Base64Url(signature), jwk));
    }

    [Fact]
    public void Redactor_NeverReturnsTokenBearingErrors()
    {
        Assert.Equal(
            "Sensitive authentication details were redacted.",
            SecretRedactor.Redact("server returned refresh_token=super-secret"));
    }

    [Fact]
    public void JwtClaims_AcceptsDocumentedIssuerAndBothAudiences()
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("{}"));
        var payload = Base64Url(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
        {
            iss = "https://login.eveonline.com/",
            aud = new[] { "EVE Online", "client-id" },
            exp = DateTimeOffset.UtcNow.AddMinutes(10).ToUnixTimeSeconds(),
            sub = "CHARACTER:EVE:123",
            name = "Test Pilot",
            scp = new[] { "esi-location.read_location.v1" }
        })));

        var identity = EveJwtClaims.ValidatePayload(
            $"{header}.{payload}.signature",
            "client-id",
            ["esi-location.read_location.v1"],
            DateTimeOffset.UtcNow);

        Assert.Equal(123, identity.CharacterId);
        Assert.Equal("Test Pilot", identity.CharacterName);
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private sealed class StubHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(respond(request));
    }
}
