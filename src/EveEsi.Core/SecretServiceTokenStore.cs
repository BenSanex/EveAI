using System.Diagnostics;
using System.Text.Json;

namespace EveEsi.Core;

public interface ICharacterTokenStore
{
    Task<IReadOnlyList<CharacterToken>> ListAsync(CancellationToken cancellationToken = default);
    Task<CharacterToken?> GetAsync(long characterId, CancellationToken cancellationToken = default);
    Task StoreAsync(CharacterToken token, CancellationToken cancellationToken = default);
    Task RemoveAsync(long characterId, CancellationToken cancellationToken = default);
}

public sealed class SecretServiceTokenStore : ICharacterTokenStore
{
    private const string Service = "com.bensanex.eva.eve-sso";
    private readonly string _indexPath;

    public SecretServiceTokenStore(string? dataDirectory = null)
    {
        var root = dataDirectory ?? EvaDataDirectory.Get();
        Directory.CreateDirectory(root);
        _indexPath = Path.Combine(root, "characters.json");
    }

    public async Task<IReadOnlyList<CharacterToken>> ListAsync(CancellationToken cancellationToken = default)
    {
        var descriptors = await ReadIndex(cancellationToken).ConfigureAwait(false);
        var result = new List<CharacterToken>();
        foreach (var descriptor in descriptors)
        {
            var token = await GetAsync(descriptor.CharacterId, cancellationToken).ConfigureAwait(false);
            if (token is not null)
            {
                result.Add(token);
            }
        }
        return result;
    }

    public async Task<CharacterToken?> GetAsync(long characterId, CancellationToken cancellationToken = default)
    {
        var output = await RunSecretTool(
            ["lookup", "service", Service, "character-id", characterId.ToString()],
            null,
            allowNotFound: true,
            cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(output))
        {
            return null;
        }
        return JsonSerializer.Deserialize<CharacterToken>(output);
    }

    public async Task StoreAsync(CharacterToken token, CancellationToken cancellationToken = default)
    {
        var json = JsonSerializer.Serialize(token);
        await RunSecretTool(
            ["store", "--label", $"Eva EVE SSO — {token.CharacterName}", "service", Service,
             "character-id", token.CharacterId.ToString()],
            json,
            allowNotFound: false,
            cancellationToken).ConfigureAwait(false);

        var index = (await ReadIndex(cancellationToken).ConfigureAwait(false))
            .Where(item => item.CharacterId != token.CharacterId)
            .Append(new CharacterDescriptor(token.CharacterId, token.CharacterName, token.IsDefault))
            .OrderBy(static item => item.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        await WriteIndex(index, cancellationToken).ConfigureAwait(false);
    }

    public async Task RemoveAsync(long characterId, CancellationToken cancellationToken = default)
    {
        await RunSecretTool(
            ["clear", "service", Service, "character-id", characterId.ToString()],
            null,
            allowNotFound: true,
            cancellationToken).ConfigureAwait(false);
        var index = (await ReadIndex(cancellationToken).ConfigureAwait(false))
            .Where(item => item.CharacterId != characterId)
            .ToArray();
        await WriteIndex(index, cancellationToken).ConfigureAwait(false);
    }

    private async Task<CharacterDescriptor[]> ReadIndex(CancellationToken cancellationToken)
    {
        if (!File.Exists(_indexPath))
        {
            return [];
        }
        await using var stream = File.OpenRead(_indexPath);
        return await JsonSerializer.DeserializeAsync<CharacterDescriptor[]>(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false) ?? [];
    }

    private async Task WriteIndex(CharacterDescriptor[] index, CancellationToken cancellationToken)
    {
        var temporary = _indexPath + ".new";
        await using (var stream = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        {
            await JsonSerializer.SerializeAsync(stream, index, cancellationToken: cancellationToken)
                .ConfigureAwait(false);
            await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        File.Move(temporary, _indexPath, true);
    }

    private static async Task<string> RunSecretTool(
        IReadOnlyList<string> arguments,
        string? stdin,
        bool allowNotFound,
        CancellationToken cancellationToken)
    {
        var info = new ProcessStartInfo("secret-tool")
        {
            RedirectStandardInput = stdin is not null,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false
        };
        foreach (var argument in arguments)
        {
            info.ArgumentList.Add(argument);
        }
        using var process = Process.Start(info) ?? throw new InvalidOperationException("Could not start secret-tool.");
        if (stdin is not null)
        {
            await process.StandardInput.WriteAsync(stdin.AsMemory(), cancellationToken).ConfigureAwait(false);
            process.StandardInput.Close();
        }
        var output = await process.StandardOutput.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        if (process.ExitCode != 0 && !allowNotFound)
        {
            throw new InvalidOperationException(SecretRedactor.Redact(error));
        }
        return process.ExitCode == 0 ? output.Trim() : "";
    }

    private sealed record CharacterDescriptor(long CharacterId, string CharacterName, bool IsDefault);
}
