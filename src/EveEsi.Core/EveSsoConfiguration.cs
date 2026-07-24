using System.Text.Json;

namespace EveEsi.Core;

public sealed record EveSsoConfiguration(string ClientId, string CallbackUri)
{
    public static EveSsoConfiguration Default { get; } =
        new("", "http://127.0.0.1:41793/callback/");
}

public sealed class EveSsoConfigurationStore
{
    private readonly string _root;
    private readonly string _path;

    public EveSsoConfigurationStore(string? dataDirectory = null)
    {
        _root = dataDirectory ?? EvaDataDirectory.Get();
        Directory.CreateDirectory(_root);
        _path = Path.Combine(_root, "eve-sso.json");
    }

    public async Task<EveSsoConfiguration> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (File.Exists(_path))
        {
            await using var stream = File.OpenRead(_path);
            var stored = await JsonSerializer.DeserializeAsync<EveSsoConfiguration>(
                stream,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            if (stored is not null)
            {
                return stored;
            }
        }

        var legacy = await LoadLegacySettingsAsync(cancellationToken).ConfigureAwait(false);
        if (legacy is not null)
        {
            await SaveAsync(legacy, cancellationToken).ConfigureAwait(false);
            return legacy;
        }

        var environmentClientId = Environment.GetEnvironmentVariable("EVA_EVE_CLIENT_ID") ?? "";
        return EveSsoConfiguration.Default with { ClientId = environmentClientId };
    }

    public async Task SaveAsync(
        EveSsoConfiguration configuration,
        CancellationToken cancellationToken = default)
    {
        Validate(configuration);
        var temporary = _path + ".new";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(
                stream,
                configuration,
                cancellationToken: cancellationToken).ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, _path, true);
    }

    public static void Validate(EveSsoConfiguration configuration)
    {
        if (!Uri.TryCreate(configuration.CallbackUri, UriKind.Absolute, out var callback) ||
            callback.Scheme != Uri.UriSchemeHttp ||
            !callback.IsLoopback)
        {
            throw new InvalidDataException("EVE SSO callback must be an HTTP loopback URI.");
        }
    }

    public static void ValidateForAuthorization(EveSsoConfiguration configuration)
    {
        Validate(configuration);
        if (string.IsNullOrWhiteSpace(configuration.ClientId))
        {
            throw new InvalidDataException("EVE SSO client ID is required.");
        }
    }

    private async Task<EveSsoConfiguration?> LoadLegacySettingsAsync(CancellationToken cancellationToken)
    {
        var legacyPath = Path.Combine(_root, "settings.json");
        if (!File.Exists(legacyPath))
        {
            return null;
        }
        await using var stream = File.OpenRead(legacyPath);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        var clientId = Property(document.RootElement, "EveClientId")?.GetString();
        var callback = Property(document.RootElement, "CallbackUri")?.GetString()
            ?? EveSsoConfiguration.Default.CallbackUri;
        var candidate = new EveSsoConfiguration(clientId ?? "", callback);
        if (string.IsNullOrWhiteSpace(candidate.ClientId))
        {
            return null;
        }
        ValidateForAuthorization(candidate);
        return candidate;
    }

    private static JsonElement? Property(JsonElement element, string name)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return property.Value;
            }
        }
        return null;
    }
}

public static class EvaDataDirectory
{
    public static string Get() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "eva");
}
