using System.Text.Json;

namespace Eva.App;

public sealed record EvaSettings(
    string EveClientId,
    string CallbackUri,
    string WhisperModelDirectory,
    string PiperModelPath,
    string CodexModel,
    string CodexReasoningEffort,
    string? CodexThreadId,
    string? PromptRevision,
    bool Muted)
{
    public static EvaSettings Default { get; } = new(
        "",
        "http://127.0.0.1:41793/callback/",
        Path.Combine(AppContext.BaseDirectory, "models", "whisper-small-en"),
        Path.Combine(AppContext.BaseDirectory, "models", "en_US-lessac-medium.onnx"),
        "gpt-5.6-luna",
        "low",
        null,
        "ship-computer-v2",
        false);
}

public sealed class EvaSettingsStore
{
    private readonly string _path;

    public EvaSettingsStore(string? root = null)
    {
        root ??= EveEsi.Core.EvaDataDirectory.Get();
        Directory.CreateDirectory(root);
        _path = Path.Combine(root, "settings.json");
    }

    public async Task<EvaSettings> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (!File.Exists(_path))
        {
            return EvaSettings.Default;
        }
        await using var stream = File.OpenRead(_path);
        var loaded = await JsonSerializer.DeserializeAsync<EvaSettings>(
            stream, cancellationToken: cancellationToken).ConfigureAwait(false);
        return loaded is null
            ? EvaSettings.Default
            : loaded with
            {
                CodexModel = string.IsNullOrWhiteSpace(loaded.CodexModel)
                    ? EvaSettings.Default.CodexModel
                    : loaded.CodexModel,
                CodexReasoningEffort = string.IsNullOrWhiteSpace(loaded.CodexReasoningEffort)
                    ? EvaSettings.Default.CodexReasoningEffort
                    : loaded.CodexReasoningEffort
            };
    }

    public async Task SaveAsync(EvaSettings settings, CancellationToken cancellationToken = default)
    {
        var temporary = _path + ".new";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, settings, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, _path, true);
    }
}
