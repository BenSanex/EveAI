using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using EveEsi.Core;

return await EveCli.RunAsync(args);

public static class EveCli
{
    private const int MaxLimit = 200;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) }
    };

    public static async Task<int> RunAsync(
        string[] args,
        HttpClient? httpClient = null,
        ICharacterTokenStore? tokenStore = null,
        TextWriter? stdout = null,
        TextWriter? stderr = null,
        CancellationToken cancellationToken = default,
        IEveEntityCatalog? entityCatalog = null)
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
            entityCatalog ??= new SqliteEveEntityCatalog();
            var esi = new EsiClient(httpClient);
            var operation = string.Join(' ', parsed.Command).ToLowerInvariant();
            var (data, results, characters) = operation switch
            {
                "characters list" => await ListCharacters(tokenStore, cancellationToken).ConfigureAwait(false),
                "reference status" => await ReferenceStatus(entityCatalog, cancellationToken).ConfigureAwait(false),
                "reference update" => await ReferenceUpdate(httpClient, parsed.Has("force"), cancellationToken).ConfigureAwait(false),
                "universe resolve" => await ResolveUniverse(entityCatalog, parsed, cancellationToken).ConfigureAwait(false),
                "universe type" => await PublicEntityGet(esi, entityCatalog, parsed, EveEntityKind.Type, "types", cancellationToken).ConfigureAwait(false),
                "universe system" => await PublicEntityGet(esi, entityCatalog, parsed, EveEntityKind.SolarSystem, "systems", cancellationToken).ConfigureAwait(false),
                "universe station" => await PublicEntityGet(esi, entityCatalog, parsed, EveEntityKind.Station, "stations", cancellationToken).ConfigureAwait(false),
                "universe route" => await PublicGet(esi, $"latest/route/{parsed.RequiredLong("from")}/{parsed.RequiredLong("to")}/", cancellationToken).ConfigureAwait(false),
                "market prices" => await MarketPrices(esi, entityCatalog, parsed, cancellationToken).ConfigureAwait(false),
                "market orders" => await MarketOrders(esi, parsed, cancellationToken).ConfigureAwait(false),
                "market availability" => await MarketAvailability(esi, entityCatalog, parsed, cancellationToken).ConfigureAwait(false),
                "market history" => await MarketHistory(esi, parsed, cancellationToken).ConfigureAwait(false),
                "character summary" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterSummary(c, e, t, entityCatalog, ct), cancellationToken).ConfigureAwait(false),
                "character location" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterLocation(c, e, t, entityCatalog, ct), cancellationToken).ConfigureAwait(false),
                "character ship" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterShip(c, e, t, entityCatalog, ct), cancellationToken).ConfigureAwait(false),
                "character skills" => await WithCharacters(parsed, tokenStore, httpClient, CharacterSkills, cancellationToken).ConfigureAwait(false),
                "character skill-queue" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterSkillQueue(c, e, t, entityCatalog, parsed.LimitOrDefault(20), ct), cancellationToken).ConfigureAwait(false),
                "wallet summary" => await WithCharacters(parsed, tokenStore, httpClient, WalletSummary, cancellationToken).ConfigureAwait(false),
                "wallet journal" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterArray(c, e, t, "wallet/journal", parsed.LimitOrDefault(20), ct), cancellationToken).ConfigureAwait(false),
                "wallet transactions" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterArray(c, e, t, "wallet/transactions", parsed.LimitOrDefault(20), ct), cancellationToken).ConfigureAwait(false),
                "assets search" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => AssetSearch(c, e, t, entityCatalog, parsed, ct), cancellationToken).ConfigureAwait(false),
                "orders list" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterArray(c, e, t, "orders", parsed.LimitOrDefault(25), ct), cancellationToken).ConfigureAwait(false),
                "industry jobs" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterArray(c, e, t, "industry/jobs", parsed.LimitOrDefault(25), ct), cancellationToken).ConfigureAwait(false),
                "contracts list" => await WithCharacters(parsed, tokenStore, httpClient, (c, e, t, ct) => CharacterArray(c, e, t, "contracts", parsed.LimitOrDefault(25), ct), cancellationToken).ConfigureAwait(false),
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

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> ReferenceStatus(
        IEveEntityCatalog catalog,
        CancellationToken cancellationToken)
    {
        var metadata = await catalog.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        return (new JsonObject
        {
            ["ready"] = metadata is not null,
            ["metadata"] = metadata is null ? null : JsonSerializer.SerializeToNode(metadata, JsonOptions)
        }, [], []);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> ReferenceUpdate(
        HttpClient httpClient,
        bool force,
        CancellationToken cancellationToken)
    {
        var result = await new SdeUpdater(httpClient).EnsureCurrentAsync(
            force, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (JsonSerializer.SerializeToNode(result, JsonOptions)!, [], []);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> PublicGet(
        EsiClient esi,
        string path,
        CancellationToken cancellationToken)
    {
        var result = await esi.GetAsync(path, cancellationToken: cancellationToken).ConfigureAwait(false);
        return (JsonNode.Parse(result.Json)!, [result], []);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> PublicEntityGet(
        EsiClient esi,
        IEveEntityCatalog catalog,
        Arguments arguments,
        EveEntityKind kind,
        string route,
        CancellationToken cancellationToken)
    {
        var entity = await ResolveEntityAsync(
            catalog, arguments.Selector("id", "name"), [kind], cancellationToken).ConfigureAwait(false);
        var result = await esi.GetAsync(
            $"latest/universe/{route}/{entity.Id}/", cancellationToken: cancellationToken).ConfigureAwait(false);
        var raw = JsonNode.Parse(result.Json)!.AsObject();
        raw["id"] = entity.Id;
        raw["name"] = entity.Name;
        return (raw, [result], []);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> ResolveUniverse(
        IEveEntityCatalog catalog,
        Arguments arguments,
        CancellationToken cancellationToken)
    {
        if (!catalog.IsAvailable)
        {
            throw new CliUsageException("The local EVE reference index is not ready yet.");
        }
        EveEntityKind? kind = arguments.Optional("kind")?.ToLowerInvariant() switch
        {
            null or "any" => null,
            "type" or "item" => EveEntityKind.Type,
            "region" => EveEntityKind.Region,
            "constellation" => EveEntityKind.Constellation,
            "system" => EveEntityKind.SolarSystem,
            "station" => EveEntityKind.Station,
            "corporation" => EveEntityKind.Corporation,
            _ => throw new CliUsageException("--kind must be type, region, constellation, system, station, corporation, or any.")
        };
        var matches = await catalog.SearchAsync(
            arguments.Required("query"), kind, arguments.LimitOrDefault(10), cancellationToken).ConfigureAwait(false);
        return (JsonSerializer.SerializeToNode(matches, JsonOptions)!, [], []);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> MarketPrices(
        EsiClient esi,
        IEveEntityCatalog catalog,
        Arguments arguments,
        CancellationToken cancellationToken)
    {
        var item = await ResolveEntityAsync(
            catalog, arguments.Selector("type", "item"), [EveEntityKind.Type], cancellationToken).ConfigureAwait(false);
        var result = await esi.GetAsync("latest/markets/prices/", cancellationToken: cancellationToken).ConfigureAwait(false);
        var array = JsonNode.Parse(result.Json)!.AsArray();
        var match = array.FirstOrDefault(node => node?["type_id"]?.GetValue<long>() == item.Id);
        return (new JsonObject
        {
            ["item"] = JsonSerializer.SerializeToNode(item, JsonOptions),
            ["adjustedPrice"] = match?["adjusted_price"]?.DeepClone(),
            ["averagePrice"] = match?["average_price"]?.DeepClone()
        }, [result], []);
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

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>, IReadOnlyList<string>)> MarketAvailability(
        EsiClient esi,
        IEveEntityCatalog catalog,
        Arguments arguments,
        CancellationToken cancellationToken)
    {
        var item = await ResolveEntityAsync(
            catalog, arguments.Selector("type", "item"), [EveEntityKind.Type], cancellationToken).ConfigureAwait(false);
        var location = await ResolveEntityAsync(
            catalog,
            arguments.Selector("location", "location-id"),
            [EveEntityKind.Station, EveEntityKind.SolarSystem, EveEntityKind.Region],
            cancellationToken).ConfigureAwait(false);
        var regionId = location.Kind switch
        {
            EveEntityKind.Region => location.Id,
            EveEntityKind.SolarSystem => location.RegionId,
            EveEntityKind.Station when location.SolarSystemId is { } systemId =>
                (await catalog.FindByIdAsync(systemId, EveEntityKind.SolarSystem, cancellationToken)
                    .ConfigureAwait(false))?.RegionId,
            _ => null
        } ?? throw new CliUsageException($"Could not determine the market region for '{location.Name}'.");
        var side = arguments.Optional("side")?.ToLowerInvariant() ?? "sell";
        if (side is not ("sell" or "buy" or "both"))
        {
            throw new CliUsageException("--side must be sell, buy, or both.");
        }

        var result = await esi.GetAsync(
            $"latest/markets/{regionId}/orders/?order_type=all&type_id={item.Id}",
            cancellationToken: cancellationToken).ConfigureAwait(false);
        var orders = JsonNode.Parse(result.Json)!.AsArray()
            .Where(node => node is not null)
            .Where(node => location.Kind switch
            {
                EveEntityKind.Station => node!["location_id"]?.GetValue<long>() == location.Id,
                EveEntityKind.SolarSystem => node!["system_id"]?.GetValue<long>() == location.Id,
                _ => true
            })
            .Where(node => side switch
            {
                "sell" => node!["is_buy_order"]?.GetValue<bool>() != true,
                "buy" => node!["is_buy_order"]?.GetValue<bool>() == true,
                _ => true
            })
            .ToArray();
        var sellOrders = orders.Where(node => node!["is_buy_order"]?.GetValue<bool>() != true).ToArray();
        var buyOrders = orders.Where(node => node!["is_buy_order"]?.GetValue<bool>() == true).ToArray();
        return (new JsonObject
        {
            ["item"] = JsonSerializer.SerializeToNode(item, JsonOptions),
            ["location"] = JsonSerializer.SerializeToNode(location, JsonOptions),
            ["side"] = side,
            ["available"] = orders.Length > 0,
            ["sell"] = AvailabilitySummary(sellOrders, buy: false),
            ["buy"] = AvailabilitySummary(buyOrders, buy: true)
        }, [result], []);
    }

    private static JsonObject AvailabilitySummary(JsonNode?[] orders, bool buy)
    {
        if (orders.Length == 0)
        {
            return new JsonObject
            {
                ["orderCount"] = 0,
                ["totalQuantity"] = 0
            };
        }
        var best = buy
            ? orders.Max(node => node!["price"]!.GetValue<decimal>())
            : orders.Min(node => node!["price"]!.GetValue<decimal>());
        return new JsonObject
        {
            ["orderCount"] = orders.Length,
            ["totalQuantity"] = orders.Sum(node => node!["volume_remain"]?.GetValue<long>() ?? 0),
            ["bestPrice"] = best,
            ["quantityAtBest"] = orders.Where(node => node!["price"]!.GetValue<decimal>() == best)
                .Sum(node => node!["volume_remain"]?.GetValue<long>() ?? 0)
        };
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
        var clientId = (await new EveSsoConfigurationStore()
            .LoadAsync(cancellationToken).ConfigureAwait(false)).ClientId;
        var oauth = new EveOAuthClient(http, clientId);
        var jwtValidator = new EveJwtValidator(http);
        foreach (var character in selected)
        {
            try
            {
                var refreshed = await oauth.RefreshAsync(character.RefreshToken, cancellationToken).ConfigureAwait(false);
                var identity = await jwtValidator.ValidateAsync(
                    refreshed.AccessToken,
                    clientId,
                    character.Scopes,
                    DateTimeOffset.UtcNow,
                    cancellationToken).ConfigureAwait(false);
                if (identity.CharacterId != character.CharacterId ||
                    !string.Equals(identity.CharacterName, character.CharacterName, StringComparison.Ordinal))
                {
                    throw new InvalidDataException("Refreshed token identity did not match the linked character.");
                }
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

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> CharacterArray(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        string suffix,
        int limit,
        CancellationToken cancellationToken)
    {
        var value = await CharacterGet(character, esi, accessToken, suffix, cancellationToken).ConfigureAwait(false);
        return (Take(value.Item1.ToJsonString(), limit), value.Item2);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> CharacterLocation(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        IEveEntityCatalog catalog,
        CancellationToken cancellationToken)
    {
        var value = await CharacterGet(character, esi, accessToken, "location", cancellationToken).ConfigureAwait(false);
        var raw = value.Item1.AsObject();
        return (new JsonObject
        {
            ["solarSystem"] = await EntityNodeAsync(catalog, raw["solar_system_id"], EveEntityKind.SolarSystem, cancellationToken).ConfigureAwait(false),
            ["station"] = await EntityNodeAsync(catalog, raw["station_id"], EveEntityKind.Station, cancellationToken).ConfigureAwait(false),
            ["structureId"] = raw["structure_id"]?.DeepClone()
        }, value.Item2);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> CharacterShip(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        IEveEntityCatalog catalog,
        CancellationToken cancellationToken)
    {
        var value = await CharacterGet(character, esi, accessToken, "ship", cancellationToken).ConfigureAwait(false);
        var raw = value.Item1.AsObject();
        return (new JsonObject
        {
            ["name"] = raw["ship_name"]?.DeepClone(),
            ["type"] = await EntityNodeAsync(catalog, raw["ship_type_id"], EveEntityKind.Type, cancellationToken).ConfigureAwait(false)
        }, value.Item2);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> CharacterSkills(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var value = await CharacterGet(character, esi, accessToken, "skills", cancellationToken).ConfigureAwait(false);
        var raw = value.Item1.AsObject();
        var skills = raw["skills"]?.AsArray();
        return (new JsonObject
        {
            ["totalSkillPoints"] = raw["total_sp"]?.DeepClone(),
            ["unallocatedSkillPoints"] = raw["unallocated_sp"]?.DeepClone(),
            ["skillCount"] = skills?.Count ?? 0,
            ["levelFiveCount"] = skills?.Count(node => node?["trained_skill_level"]?.GetValue<int>() == 5) ?? 0
        }, value.Item2);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> CharacterSkillQueue(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        IEveEntityCatalog catalog,
        int limit,
        CancellationToken cancellationToken)
    {
        var value = await CharacterGet(character, esi, accessToken, "skillqueue", cancellationToken).ConfigureAwait(false);
        var compact = new JsonArray();
        foreach (var entry in value.Item1.AsArray().Take(limit))
        {
            if (entry is null)
            {
                continue;
            }
            compact.Add(new JsonObject
            {
                ["skill"] = await EntityNodeAsync(catalog, entry["skill_id"], EveEntityKind.Type, cancellationToken).ConfigureAwait(false),
                ["targetLevel"] = entry["finished_level"]?.DeepClone(),
                ["startsAt"] = entry["start_date"]?.DeepClone(),
                ["finishesAt"] = entry["finish_date"]?.DeepClone(),
                ["queuePosition"] = entry["queue_position"]?.DeepClone()
            });
        }
        return (compact, value.Item2);
    }

    private static async Task<(JsonNode, IReadOnlyList<EsiResult>)> CharacterSummary(
        CharacterToken character,
        EsiClient esi,
        string accessToken,
        IEveEntityCatalog catalog,
        CancellationToken cancellationToken)
    {
        var location = await CharacterLocation(character, esi, accessToken, catalog, cancellationToken).ConfigureAwait(false);
        var ship = await CharacterShip(character, esi, accessToken, catalog, cancellationToken).ConfigureAwait(false);
        var online = await CharacterGet(character, esi, accessToken, "online", cancellationToken).ConfigureAwait(false);
        var skills = await CharacterSkills(character, esi, accessToken, cancellationToken).ConfigureAwait(false);
        var queue = await CharacterSkillQueue(character, esi, accessToken, catalog, 5, cancellationToken).ConfigureAwait(false);
        var results = new List<EsiResult>();
        results.AddRange(location.Item2);
        results.AddRange(ship.Item2);
        results.AddRange(online.Item2);
        results.AddRange(skills.Item2);
        results.AddRange(queue.Item2);
        return (new JsonObject
        {
            ["location"] = location.Item1,
            ["ship"] = ship.Item1,
            ["online"] = online.Item1,
            ["skills"] = skills.Item1,
            ["nextSkills"] = queue.Item1
        }, results);
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
        IEveEntityCatalog catalog,
        Arguments arguments,
        CancellationToken cancellationToken)
    {
        var item = await ResolveEntityAsync(
            catalog, arguments.Selector("type", "item"), [EveEntityKind.Type], cancellationToken).ConfigureAwait(false);
        var limit = arguments.LimitOrDefault(50);
        var result = await esi.GetPagesAsync(
            $"latest/characters/{character.CharacterId}/assets/", 20, accessToken, cancellationToken).ConfigureAwait(false);
        var matches = JsonNode.Parse(result.Json)!.AsArray()
            .Where(node => node?["type_id"]?.GetValue<long>() == item.Id)
            .Take(limit)
            .Select(static node => new JsonObject
            {
                ["quantity"] = node?["quantity"]?.DeepClone(),
                ["locationId"] = node?["location_id"]?.DeepClone(),
                ["locationType"] = node?["location_type"]?.DeepClone(),
                ["locationFlag"] = node?["location_flag"]?.DeepClone(),
                ["singleton"] = node?["is_singleton"]?.DeepClone()
            })
            .ToArray();
        return (new JsonObject
        {
            ["item"] = JsonSerializer.SerializeToNode(item, JsonOptions),
            ["matchCount"] = matches.Length,
            ["totalQuantity"] = matches.Sum(node => node["quantity"]?.GetValue<long>() ?? 0),
            ["locations"] = new JsonArray(matches)
        }, [result]);
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

    private static async Task<JsonNode?> EntityNodeAsync(
        IEveEntityCatalog catalog,
        JsonNode? idNode,
        EveEntityKind kind,
        CancellationToken cancellationToken)
    {
        if (idNode is null)
        {
            return null;
        }
        var id = idNode.GetValue<long>();
        var entity = await catalog.FindByIdAsync(id, kind, cancellationToken).ConfigureAwait(false);
        return entity is null
            ? new JsonObject { ["id"] = id }
            : JsonSerializer.SerializeToNode(entity, JsonOptions);
    }

    private static async Task<EveEntity> ResolveEntityAsync(
        IEveEntityCatalog catalog,
        string selector,
        IReadOnlyList<EveEntityKind> kinds,
        CancellationToken cancellationToken)
    {
        if (long.TryParse(selector, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && id > 0)
        {
            foreach (var kind in kinds)
            {
                var found = await catalog.FindByIdAsync(id, kind, cancellationToken).ConfigureAwait(false);
                if (found is not null)
                {
                    return found;
                }
            }
            return new(id, $"ID {id}", kinds[0]);
        }
        if (!catalog.IsAvailable)
        {
            throw new CliUsageException(
                $"'{selector}' is a name, but the local EVE reference index is not ready. Use an ID or wait for indexing.");
        }
        var matches = new List<EveEntity>();
        foreach (var kind in kinds)
        {
            matches.AddRange(await catalog.SearchAsync(selector, kind, 10, cancellationToken).ConfigureAwait(false));
        }
        var exact = matches.Where(item =>
            string.Equals(item.Name, selector, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1)
        {
            return exact[0];
        }
        if (exact.Length > 1)
        {
            throw new CliUsageException($"'{selector}' is ambiguous; use an ID.");
        }
        var aliases = matches.Where(item => IsUnambiguousAlias(selector, item.Name)).ToArray();
        if (aliases.Length == 1)
        {
            return aliases[0];
        }
        if (matches.Count == 1)
        {
            return matches[0];
        }
        var suggestions = matches.Take(5).Select(item => $"{item.Name} ({item.Id})").ToArray();
        throw new CliUsageException(suggestions.Length == 0
            ? $"No EVE entity matched '{selector}'."
            : $"'{selector}' is ambiguous; use an exact name or ID. Matches: {string.Join(", ", suggestions)}");
    }

    private static bool IsUnambiguousAlias(string selector, string candidate)
    {
        static string Normalize(string value) => string.Join(
            ' ',
            value.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Where(token => !string.Equals(token, "scanner", StringComparison.OrdinalIgnoreCase)))
            .ToLowerInvariant();
        return string.Equals(Normalize(selector), Normalize(candidate), StringComparison.Ordinal);
    }

    private static JsonArray Take(string json, int limit) =>
        new(JsonNode.Parse(json)!.AsArray().Take(limit).Select(static item => item?.DeepClone()).ToArray());

    private static async Task WriteHelp(TextWriter writer, bool json)
    {
        var catalogue = new[]
        {
            "characters list",
            "reference status",
            "reference update [--force]",
            "character summary|location|ship|skills|skill-queue --character <id|name> [--all] [--limit <1..200>]",
            "wallet summary|journal|transactions --character <id|name> [--all] [--limit <1..200>]",
            "assets search --character <id|name> [--all] --item <id|name> [--limit <1..200>]",
            "orders list --character <id|name> [--all] [--limit <1..200>]",
            "industry jobs --character <id|name> [--all] [--limit <1..200>]",
            "contracts list --character <id|name> [--all] [--limit <1..200>]",
            "universe search --character <id|name> --category <category> --query <text>",
            "universe resolve --query <text> [--kind <kind>] [--limit <1..50>]",
            "universe type|system|station --id <id> | --name <exact-name>",
            "universe route --from <system-id> --to <system-id>",
            "market prices --item <id|name>",
            "market availability --item <id|name> --location <id|name> [--side sell|buy|both]",
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

    public string? Optional(string name) =>
        _options.TryGetValue(name, out var value) && !string.IsNullOrWhiteSpace(value) ? value : null;

    public string Selector(string primary, string alternate)
    {
        var first = Optional(primary);
        var second = Optional(alternate);
        if (first is not null && second is not null)
        {
            throw new CliUsageException($"Use either --{primary} or --{alternate}, not both.");
        }
        return first ?? second ?? throw new CliUsageException($"--{primary} or --{alternate} is required.");
    }

    public int RequiredLimit()
    {
        var value = RequiredLong("limit");
        return value <= MaxLimit
            ? (int)value
            : throw new CliUsageException($"--limit must be at most {MaxLimit}.");
    }

    public int LimitOrDefault(int defaultValue) =>
        Has("limit") ? RequiredLimit() : defaultValue;
}

public sealed class CliUsageException(string message) : Exception(message);
