namespace Eva.App.Tests;

using System.Text.Json.Nodes;

public sealed class AppServiceTests
{
    [Fact]
    public void ShortcutParser_HandlesGSettingsArray()
    {
        var paths = GnomeShortcut.ParsePaths(
            "['/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/custom0/', '/org/gnome/settings-daemon/plugins/media-keys/custom-keybindings/eva/']");
        Assert.Equal(2, paths.Count);
        Assert.EndsWith("/eva/", paths[1], StringComparison.Ordinal);
    }

    [Fact]
    public void TemporaryAudio_IsDeleted()
    {
        var path = Path.GetTempFileName();
        Assert.True(File.Exists(path));
        PipeWireRecorder.TryDelete(path);
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Settings_RoundTrip()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"eva-settings-test-{Guid.NewGuid():N}");
        try
        {
            var store = new EvaSettingsStore(directory);
            var expected = EvaSettings.Default with { EveClientId = "client", Muted = true };
            await store.SaveAsync(expected);
            Assert.Equal(expected, await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public void CodexRouter_OnlyPlacesAgentMessageDeltaInTranscript()
    {
        var agent = CodexNotificationRouter.Route(JsonNode.Parse(
            """{"method":"item/agentMessage/delta","params":{"delta":"Market data ready."}}""")!);
        var command = CodexNotificationRouter.Route(JsonNode.Parse(
            """{"method":"item/commandExecution/outputDelta","params":{"delta":"{\"ok\":true,\"data\":[1,2,3]}"}}""")!);
        var itemCompleted = CodexNotificationRouter.Route(JsonNode.Parse(
            """{"method":"item/completed","params":{}}""")!);
        var turnCompleted = CodexNotificationRouter.Route(JsonNode.Parse(
            """{"method":"turn/completed","params":{}}""")!);

        Assert.Equal(CodexNotificationKind.AgentText, agent.Kind);
        Assert.Equal("Market data ready.", agent.Text);
        Assert.Equal(CodexNotificationKind.Diagnostic, command.Kind);
        Assert.Equal(CodexNotificationKind.Diagnostic, itemCompleted.Kind);
        Assert.Equal(CodexNotificationKind.TurnCompleted, turnCompleted.Kind);
    }
}
