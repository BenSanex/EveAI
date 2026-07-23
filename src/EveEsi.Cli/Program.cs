using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using EveEsi.Core;

return await EveCli.RunAsync(args);

public static class EveCli
{
    private const int MaxLimit = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true
    };

    public static async Task<int> RunAsync(
        string[] args,
        HttpClient? httpClient = null,
        ICharacterTokenStore? tokenStore = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default)
    {
        stdout ??= Console.Out;
        stderr ??= Console.Error;
        var parsed = Arguments.Parse(args);
        if (parsed.Command.Count == 0 || parsed.Command.SequenceEqual(["help"], StringComparer.OrdinalIgnoreCase))
        {
            await WriteHelp(stdout, parsed.Json).ConfigureAwait(false);
            return 0;
        }

        try
        {
            httpClient ??= new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            tokenStore ??= new SecretServiceTokenStore();
            var esi = new EsiClient(httpClient);
            var operation = string.Join(' ', parsed.Command).ToLowerInvariant();
            var (data, results, characters) = operation switch
            {
                "characters list" => await ListCharacters(tokenStore, cancellationToken).ConfigureAwait(false),
                "universe type" => await PublicGet(esi, $"latest/universe/types/{parsed.RequiredLong("id")}/", cancellationToken).ConfigureAwait(false),
                "universe system" => await PublicGet(esi, $"latest/universe/systems/{parsed.RequiredLong("id")}/", cancellationToken).ConfigureAwait(false),
                "universe station" => await PublicGet(esi, $"latest/universe/stations/{parsed.RequiredLong("id")}/", cancellationToken).ConfigureAwait(false),
                "universe route" => await PublicGet(esi, $"latest/route/{parsed.RequiredLong("from")}/{parsed.RequiredLong("to")}/", cancellationToken).ConfigureAwait(false),
                "market prices" => await MarketPrices(esi, parsed, cancellationToken).ConfigureAwait(false),
                "market orders" => await MarketOrders(esi, parsed, cancellationToken).ConfigureAwait(false),
                "market history" => await MarketHistory(esi, parsed, cancellationToken).ConfigureAwait(false),
                "character summary" => await WithCharacters(parsed, tokenStore, httpClient, CharacterSummary, cancellationToken).ConfigureAwait(false),
                "character location" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterGet(c, e, t, "location", ct), cancellationToken).ConfigureAwait(false),
                "character ship" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterGet(c, e, t, "ship", ct), cancellationToken).ConfigureAwait(false),
                "character skills" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterGet(c, e, t, "skills", ct), cancellationToken).ConfigureAwait(false),
                "character skill-queue" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterGet(c, e, t, "skillqueue", ct), cancellationToken).ConfigureAwait(false),
                "wallet summary" => await WithCharacters(parsed, tokenStore, httpClient, WalletSummary, cancellationToken).ConfigureAwait(false),
                "wallet journal" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterGet(c, e, t, "wallet/journal", ct), cancellationToken).ConfigureAwait(false),
                "wallet transactions" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterGet(c, e, t, "wallet/transactions", ct), cancellationToken).ConfigureAwait(false),
                "assets search" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => AssetSearch(c, e, t, parsed, ct), cancellationToken).ConfigureAwait(false),
                "orders list" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterGet(c, e, t, "orders", ct), cancellationToken).ConfigureAwait(false),
                "industry jobs" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterGet(c, e, t, "industry/jobs", ct), cancellationToken).ConfigureAwait(false),
                "contracts list" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterGet(c, e, t, "contracts", ct), cancellationToken).ConfigureAwait(false),
                "universe search" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => UniverseSearch(c, e, t, parsed, ct), cancellationToken).ConfigureAwait(false),
                _ => throw new CliUsageException($"Unknown command '{operation}'. Run 'eve-esi help'.")
            };

            var sources = results.Select(static item => item.Source.AbsoluteUri).Distinct(StringComparer.Ordinal).ToArray();
            var envelope = new CliEnvelope<JsonNode>(
                true,
                data,
                new(DateTimeOffset.UtcNow, sources, results.All(static item => item.Cached), null, characters),
                []);
            await Write(stdout, envelope, parsed.Json).ConfigureAwait(false);
            return 0;
        }
        catch (CliUsageException exception)
        {
            await WriteError(stdout, stderr, parsed.Json, "usage", exception.Message).ConfigureAwait(false);
            return 2;
        }
        catch (OperationCanceledException)
        {
            await WriteError(stdout, stderr, parsed.Json, "cancelled", "The operation was cancelled.").ConfigureAwait(false);
            return 130;
        }
        catch (Exception exception)
        {
            await WriteError(stdout, stderr, parsed.Json, "request_failed", SecretRedactor.Redact(exception.Message))
                .ConfigureAwait(false);
            return 1;
        }
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> ListCharacters(
        ICharacterTokenStore store,
        CancellationToken cancellationToken)
    {
        var characters = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var safe = characters.Select(static item => new
        {
            item.CharacterId,
            item.CharacterName,
            item.Scopes,
            item.IsDefault
        });
        return (JsonSerializer.SerializeToNode(safe, JsonOptions)!, [], characters.Select(static item => item.CharacterName).ToArray());
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> PublicGet(
        EsiClient esi,
        string path,
        CancellationToken cancellationToken)
    {
        var result = await esi.GetAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (JsonNode.Parse(result.Json)!, [result], []);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> MarketPrices(
        EsiClient esi,
        Arguments arguments,
        CancellationToken cancellationToken)
    {
        var type = arguments.RequiredLong("type");
        var result = await esi.GetAsync("latest/markets/prices/", cancellationToken: cancellationToken).ConfigureAwait(false);
        var array = JsonNode.Parse(result.Json)!.AsArray();
        var match = array.FirstOrDefault(item => item?["type_id"]?.GetValue<long>() == type);
        return (match?.DeepClone() ?? new JsonObject(), [result], []);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> MarketOrders(
        EsiClient esi,
        Arguments arguments,
        CancellationToken cancellationToken)
    {
        var region = arguments.RequiredLong("region");
        var type = arguments.RequiredLong("type");
        var limit = arguments.RequiredLimit();
        var result = await esi.GetAsync(
            $"latest/markets/{region}/orders/?order_type=all&type_id={type}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        return (Take(result.Json, limit), [result], []);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> MarketHistory(
        EsiClient esi,
        Arguments arguments,
        CancellationToken cancellationToken)
    {
        var region = arguments.RequiredLong("region");
        var type = arguments.RequiredLong("type");
        var limit = arguments.RequiredLimit();
        var result = await esi.GetAsync(
            $"latest/markets/{region}/history/?type_id={type}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var parsed = JsonNode.Parse(result.Json)!.AsArray();
        return (new JsonArray(parsed.TakeLast(limit).Select(static item => item?.DeepClone()).ToArray()), [result], []);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> WithCharacters(
        Arguments arguments,
        ICharacterTokenStore store,
        HttpClient http,
        Func<CharacterToken, EsiClient, string, CancellationToken, Task<(JsonNode Data, IReadOnlyList<EsiResult> Results)>> action,
        CancellationToken cancellationToken)
    {
        var all = await store.ListAsync(cancellationToken).ConfigureAwait(false);
        var selected = arguments.Has("all")
            ? all
            : [ResolveCharacter(all, arguments.Required("character"))];
        if (selected.Count == 0)
        {
            throw new CliUsageException("No linked characters were found.");
        }

        var output = new JsonObject();
        var results = new List<EsiResult>();
        var failures = new JsonArray();
        var oauth = new EveOAuthClient(http, Environment.GetEnvironmentVariable("EVA_EVE_CLIENT_ID") ?? "");
        foreach (var character in selected)
        {
            try
            {
                var refreshed = await oauth.RefreshAsync(character.RefreshToken, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(refreshed.RefreshToken, character.RefreshToken, StringComparison.Ordinal))
                {
                    await store.StoreAsync(character with { RefreshToken = refreshed.RefreshToken }, cancellationToken)
                        .ConfigureAwait(false);
                }
                var value = await action(character, new EsiClient(http), refreshed.AccessToken, cancellationToken)
                    .ConfigureAwait(false);
                output[character.CharacterName] = value.Data;
                results.AddRange(value.Results);
            }
            catch (Exception exception) when (selected.Count > 1)
            {
                failures.Add(new JsonObject
                {
                    ["character"] = character.CharacterName,
                    ["error"] = SecretRedactor.Redact(exception.Message)
                });
            }
        }
        if (failures.Count > 0)
        {
            output["_partialErrors"] = failures;
        }
        return (output, results, selected.Select(static item => item.CharacterName).ToArray());
    }

    private static CharacterToken ResolveCharacter(IReadOnlyList<CharacterToken> characters, string selector)
    {
        if (long.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out var id))
        {
            return characters.SingleOrDefault(item => item.CharacterId == id)
                ?? throw new CliUsageException($"Character ID {id} is not linked.");
        }
        var matches = characters.Where(item =>
            string.Equals(item.CharacterName, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.Length == 1
            ? matches[0]
            : throw new CliUsageException($"Character '{selector}' was not found uniquely; use an exact name or ID.");
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> CharacterGet(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        string suffix,
        CancellationToken cancellationToken)
    {
        var result = await esi.GetAsync(
            $"latest/characters/{character.CharacterId}/{suffix}/", accessToken, cancellationToken).ConfigureAwait(false);
        return (JsonNode.Parse(result.Json)!, [result]);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> CharacterSummary(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var paths = new[] { "location", "ship", "online", "skills", "skillqueue" };
        var output = new JsonObject();
        var results = new List<EsiResult>();
        foreach (var path in paths)
        {
            var value = await CharacterGet(character, esi, accessToken, path, cancellationToken).ConfigureAwait(false);
            output[path] = value.Item1;
            results.AddRange(value.Item2);
        }
        return (output, results);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> WalletSummary(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var balance = await CharacterGet(character, esi, accessToken, "wallet", cancellationToken).ConfigureAwait(false);
        var journal = await CharacterGet(character, esi, accessToken, "wallet/journal", cancellationToken).ConfigureAwait(false);
        var transactions = await CharacterGet(character, esi, accessToken, "wallet/transactions", cancellationToken).ConfigureAwait(false);
        return (new JsonObject
        {
            ["balance"] = balance.Item1,
            ["recentJournal"] = Take(journal.Item1.ToJsonString(), 10),
            ["recentTransactions"] = Take(transactions.Item1.ToJsonString(), 10)
        }, [.. balance.Item2, .. journal.Item2, .. transactions.Item2]);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> AssetSearch(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        Arguments arguments,
        CancellationToken cancellationToken)
    {
        var type = arguments.RequiredLong("type");
        var limit = arguments.RequiredLimit();
        var result = await esi.GetPagesAsync(
            $"latest/characters/{character.CharacterId}/assets/", 20, accessToken, cancellationToken).ConfigureAwait(false);
        var matches = JsonNode.Parse(result.Json)!.AsArray()
            .Where(item => item?["type_id"]?.GetValue<long>() == type)
            .Take(limit)
            .Select(static item => item?.DeepClone())
            .ToArray();
        return (new JsonArray(matches), [result]);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> UniverseSearch(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        Arguments arguments,
        CancellationToken cancellationToken)
    {
        var category = Uri.EscapeDataString(arguments.Required("category"));
        var query = Uri.EscapeDataString(arguments.Required("query"));
        var strict = arguments.Has("strict") ? "true" : "false";
        var result = await esi.GetAsync(
            $"latest/characters/{character.CharacterId}/search/?categories={category}&search={query}&strict={strict}",
            accessToken,
            cancellationToken).ConfigureAwait(false);
        return (JsonNode.Parse(result.Json)!, [result]);
    }

    private static JsonArray Take(string json, int limit) =>
        new(JsonNode.Parse(json)!.AsArray().Take(limit).Select(static item => item?.DeepClone()).ToArray());

    private static async Task WriteHelp(TextWriter writer, bool json)
    {
        var catalogue = new[]
        {
            "characters list",
            "character summary|location|ship|skills|skill-queue --character <id|name> [--all]",
            "wallet summary|journal|transactions --character <id|name> [--all]",
            "assets search --character <id|name> [--all] --type <id> --limit <1..200>",
            "orders list --character <id|name> [--all]",
            "industry jobs --character <id|name> [--all]",
            "contracts list --character <id|name> [--all]",
            "universe search --character <id|name> --category <category> --query <text>",
            "universe type|system|station --id <id>",
            "universe route --from <system-id> --to <system-id>",
            "market prices --type <id>",
            "market orders|history --region <id> --type <id> --limit <1..200>"
        };
        if (json)
        {
            await Write(writer, CliEnvelope<string[]>.Success(catalogue, []), true).ConfigureAwait(false);
        }
        else
        {
            await writer.WriteLineAsync("eve-esi — bounded read-only EVE ESI client\n\n" + string.Join('\n', catalogue.Select(static item => $"  {item}")) + "\n\nAdd --json for the stable machine-readable envelope.")
                .ConfigureAwait(false);
        }
    }

    private static async Task Write<T>(TextWriter writer, CliEnvelope<T> envelope, bool json)
    {
        if (json)
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions)).ConfigureAwait(false);
            return;
        }
        await writer.WriteLineAsync(envelope.Data is null
            ? "(no data)"
            : JsonSerializer.Serialize(envelope.Data, JsonOptions)).ConfigureAwait(false);
        if (envelope.Meta.SourceUrls.Count > 0)
        {
            await writer.WriteLineAsync($"\nSources:\n{string.Join('\n', envelope.Meta.SourceUrls)}").ConfigureAwait(false);
        }
    }

    private static async Task WriteError(TextWriter stdout, TextWriter stderr, bool json, string code, string message)
    {
        if (json)
        {
            await Write(stdout, CliEnvelope<JsonNode>.Failure(code, message), true).ConfigureAwait(false);
        }
        else
        {
            await stderr.WriteLineAsync($"error: {message}").ConfigureAwait(false);
        }
    }
}

public sealed class Arguments
{
    private const int MaxLimit = 200;
    private readonly Dictionary<string, string?> _options;
    public IReadOnlyList<string> Command { get; }
    public bool Json => Has("json");

    private Arguments(IReadOnlyList<string> command, Dictionary<string, string?> options)
    {
        Command = command;
        _options = options;
    }

    public static Arguments Parse(IReadOnlyList<string> args)
    {
        var command = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < args.Count; index++)
        {
            var token = args[index];
            if (!token.StartsWith("--", StringComparison.Ordinal))
            {
                command.Add(token);
                continue;
            }
            var key = token[2..];
            if (key.Length == 0 || options.ContainsKey(key))
            {
                throw new CliUsageException($"Invalid or duplicate option '{token}'.");
            }
            var value = index + 1 < args.Count && !args[index + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[++index]
                : null;
            options[key] = value;
        }
        if (command.Count > 2)
        {
            throw new CliUsageException("Commands have exactly two words; values must follow named options.");
        }
        return new(command, options);
    }

    public bool Has(string name) => _options.ContainsKey(name);

    public string Required(string name) =>
        _options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value)
            ? value
            : throw new CliUsageException($"--{name} is required.");

    public long RequiredLong(string name) =>
        long.TryParse(Required(name), NumberStyles.None, CultureInfo.InvariantCulture, out var value) && value > 0
            ? value
            : throw new CliUsageException($"--{name} must be a positive integer.");

    public int RequiredLimit()
    {
        var value = RequiredLong("limit");
        return value <= MaxLimit
            ? (int)value
            : throw new CliUsageException($"--limit must be at most {MaxLimit}.");
    }
}

public sealed class CliUsageException(string message) : Exception(message);
