using System.Text.Json;
using System.Net;
using EveEsi.Core;

namespace EveEsi.Cli.Tests;

public sealed class CliTests
{
    [Fact]
    public void Parser_RequiresBoundedLimit()
    {
        var valid = Arguments.Parse(["assets", "search", "--type", "34", "--limit", "200"]);
        Assert.Equal(200, valid.RequiredLimit());

        var tooLarge = Arguments.Parse(["assets", "search", "--type", "34", "--limit", "201"]);
        Assert.Throws<CliUsageException>(() => tooLarge.RequiredLimit());
    }

    [Fact]
    public async Task HelpJson_UsesStableEnvelope()
    {
        var output = new StringWriter();
        var exit = await EveCli.RunAsync(["help", "--json"], stdout: output, stderr: new StringWriter());
        using var json = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exit);
        Assert.True(json.RootElement.GetProperty("ok").GetBoolean());
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("data").ValueKind);
        Assert.Equal(JsonValueKind.Object, json.RootElement.GetProperty("meta").ValueKind);
        Assert.Equal(JsonValueKind.Array, json.RootElement.GetProperty("errors").ValueKind);
    }

    [Fact]
    public async Task UnknownCommand_DoesNotExposeSecrets()
    {
        var output = new StringWriter();
        var exit = await EveCli.RunAsync(
            ["raw", "post", "--refresh_token", "secret", "--json"],
            stdout: output,
            stderr: new StringWriter(),
            tokenStore: new EmptyStore());

        Assert.Equal(2, exit);
        Assert.DoesNotContain("secret", output.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task MarketAvailability_ReturnsCompactResolvedSummary()
    {
        var handler = new StubHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("""
                [
                  {"order_id":1,"is_buy_order":false,"location_id":60011866,"system_id":30002659,"price":125.5,"volume_remain":40},
                  {"order_id":2,"is_buy_order":false,"location_id":60011866,"system_id":30002659,"price":125.5,"volume_remain":10},
                  {"order_id":3,"is_buy_order":false,"location_id":60000000,"system_id":30002659,"price":100.0,"volume_remain":99}
                ]
                """)
        });
        var output = new StringWriter();
        var exit = await EveCli.RunAsync(
            ["market", "availability", "--item", "Core Probe I", "--location", "Dodixie IX - Moon 20", "--json"],
            new HttpClient(handler),
            new EmptyStore(),
            output,
            new StringWriter(),
            entityCatalog: new StubCatalog());
        using var json = JsonDocument.Parse(output.ToString());

        Assert.Equal(0, exit);
        var data = json.RootElement.GetProperty("data");
        Assert.True(data.GetProperty("available").GetBoolean());
        Assert.Equal(2, data.GetProperty("sell").GetProperty("orderCount").GetInt32());
        Assert.Equal(50, data.GetProperty("sell").GetProperty("totalQuantity").GetInt64());
        Assert.Equal(125.5m, data.GetProperty("sell").GetProperty("bestPrice").GetDecimal());
        Assert.DoesNotContain("order_id", output.ToString(), StringComparison.Ordinal);
    }

    private sealed class EmptyStore : ICharacterTokenStore
    {
        public Task<CharacterToken?> GetAsync(long characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterToken?>(null);
        public Task<IReadOnlyList<CharacterToken>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CharacterToken>>([]);
        public Task RemoveAsync(long characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StoreAsync(CharacterToken token, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }

    private sealed class StubHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) => Task.FromResult(response);
    }

    private sealed class StubCatalog : IEveEntityCatalog
    {
        private static readonly EveEntity Item = new(30013, "Core Probe I", EveEntityKind.Type);
        private static readonly EveEntity Station = new(
            60011866, "Dodixie IX - Moon 20 - Federation Navy Assembly Plant",
            EveEntityKind.Station, 30002659, null, 30002659);
        private static readonly EveEntity System = new(
            30002659, "Dodixie", EveEntityKind.SolarSystem, 20000468, 10000032);

        public bool IsAvailable => true;
        public Task<SdeMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<SdeMetadata?>(null);
        public Task<EveEntity?> FindByIdAsync(
            long id, EveEntityKind? kind = null, CancellationToken cancellationToken = default) =>
            Task.FromResult<EveEntity?>(id switch
            {
                30013 => Item,
                60011866 => Station,
                30002659 => System,
                _ => null
            });
        public Task<IReadOnlyList<EveEntity>> SearchAsync(
            string query,
            EveEntityKind? kind = null,
            int limit = 10,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<EveEntity>>(
                kind == EveEntityKind.Type ? [Item] :
                kind == EveEntityKind.Station ? [Station] : []);
    }
}
