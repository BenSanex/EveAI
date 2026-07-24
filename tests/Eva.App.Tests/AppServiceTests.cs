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
    public async Task Settings_MigratesMissingModelToFastDefault()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"eva-settings-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "settings.json"),
                """
                {
                  "EveClientId":"",
                  "CallbackUri":"http://127.0.0.1:41793/callback/",
                  "WhisperModelDirectory":"models/whisper",
                  "PiperModelPath":"models/piper.onnx",
                  "CodexThreadId":null,
                  "PromptRevision":"ship-computer-v2",
                  "Muted":false
                }
                """);

            var loaded = await new EvaSettingsStore(directory).LoadAsync();

            Assert.Equal("gpt-5.6-luna", loaded.CodexModel);
            Assert.Equal("low", loaded.CodexReasoningEffort);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task FasterWhisperWorker_ReturnsTextAndDeletesRecording()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"eva-speech-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(directory);
        var recording = Path.Combine(directory, "recording.wav");
        var model = Path.Combine(directory, "model");
        var worker = Path.Combine(directory, "worker.sh");
        Directory.CreateDirectory(model);
        await File.WriteAllBytesAsync(recording, [1, 2, 3, 4]);
        await File.WriteAllTextAsync(
            worker,
            """
            while IFS= read -r request; do
              printf '%s\n' '{"id":1,"ok":true,"text":"Set destination to Arnon.","provider":"cuda","elapsedMs":42}'
            done
            """);
        try
        {
            await using var transcriber = new FasterWhisperTranscriber("/bin/sh", worker);
            var text = await transcriber.TranscribeAndDeleteAsync(recording, model);

            Assert.Equal("Set destination to Arnon.", text);
            Assert.Equal("cuda", transcriber.LastProvider);
            Assert.Equal(42, transcriber.LastElapsedMilliseconds);
            Assert.False(File.Exists(recording));
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

    [Theory]
    [InlineData("thread not found")]
    [InlineData("{\"code\":\"thread_not_found\"}")]
    public void ThreadRecovery_RecognizesMissingThreadErrors(string message)
    {
        Assert.True(MainWindow.IsThreadNotFound(new InvalidOperationException(message)));
        Assert.False(MainWindow.IsThreadNotFound(new InvalidOperationException("authentication failed")));
    }
}
