using System.Text.Json;
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

    private sealed class EmptyStore : ICharacterTokenStore
    {
        public Task<CharacterToken?> GetAsync(long characterId, CancellationToken cancellationToken = default) =>
            Task.FromResult<CharacterToken?>(null);
        public Task<IReadOnlyList<CharacterToken>> ListAsync(CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<CharacterToken>>([]);
        public Task RemoveAsync(long characterId, CancellationToken cancellationToken = default) => Task.CompletedTask;
        public Task StoreAsync(CharacterToken token, CancellationToken cancellationToken = default) => Task.CompletedTask;
    }
}
