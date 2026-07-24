using System.Globalization;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace EveEsi.Core;

public sealed record SdeUpdateProgress(string Stage, long Completed, long? Total = null);

public sealed record SdeUpdateResult(bool Updated, long BuildNumber, DateTimeOffset ReleaseDate);

public sealed class SdeUpdater
{
    public static readonly Uri LatestMetadataUri =
        new("https://developers.eveonline.com/static-data/tranquility/latest.jsonl");

    private readonly HttpClient _http;
    private readonly string _directory;

    public SdeUpdater(HttpClient httpClient, string? directory = null)
    {
        _http = httpClient;
        _directory = directory ?? SdePaths.DirectoryPath;
    }

    public async Task<SdeUpdateResult> EnsureCurrentAsync(
        bool force = false,
        IProgress<SdeUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_directory);
        var database = Path.Combine(_directory, "eve-static.db");
        var catalog = new SqliteEveEntityCatalog(database);
        var current = await catalog.GetMetadataAsync(cancellationToken).ConfigureAwait(false);
        if (!force && current is not null &&
            DateTimeOffset.UtcNow - current.LastCheckedAt < TimeSpan.FromHours(24))
        {
            return new(false, current.BuildNumber, current.ReleaseDate);
        }

        progress?.Report(new("Checking official EVE static data", 0));
        var latest = await GetLatestAsync(cancellationToken).ConfigureAwait(false);
        if (current?.BuildNumber == latest.BuildNumber)
        {
            await TouchLastCheckedAsync(database, cancellationToken).ConfigureAwait(false);
            return new(false, latest.BuildNumber, latest.ReleaseDate);
        }

        var archiveUri = new Uri(
            $"https://developers.eveonline.com/static-data/tranquility/eve-online-static-data-{latest.BuildNumber}-jsonl.zip");
        var archive = Path.Combine(_directory, $"sde-{latest.BuildNumber}.zip.download");
        var staging = database + ".new";
        try
        {
            var sha = await DownloadAsync(archiveUri, archive, progress, cancellationToken).ConfigureAwait(false);
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
            await SdeImporter.ImportAsync(
                archive,
                staging,
                latest.BuildNumber,
                latest.ReleaseDate,
                sha,
                archiveUri.AbsoluteUri,
                progress,
                cancellationToken).ConfigureAwait(false);
            File.Move(staging, database, true);
            return new(true, latest.BuildNumber, latest.ReleaseDate);
        }
        finally
        {
            if (File.Exists(archive))
            {
                File.Delete(archive);
            }
            if (File.Exists(staging))
            {
                File.Delete(staging);
            }
        }
    }

    private async Task<(long BuildNumber, DateTimeOffset ReleaseDate)> GetLatestAsync(
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(LatestMetadataUri, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken)
            .ConfigureAwait(false);
        return (
            document.RootElement.GetProperty("buildNumber").GetInt64(),
            document.RootElement.GetProperty("releaseDate").GetDateTimeOffset());
    }

    private async Task<string> DownloadAsync(
        Uri uri,
        string destination,
        IProgress<SdeUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;
        if (total > 256 * 1024 * 1024)
        {
            throw new InvalidDataException("The EVE static-data archive exceeded the 256 MiB safety limit.");
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var target = new FileStream(
            destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 1024, true);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var buffer = new byte[1024 * 1024];
        long copied = 0;
        while (true)
        {
            var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
            if (read == 0)
            {
                break;
            }
            copied += read;
            if (copied > 256L * 1024 * 1024)
            {
                throw new InvalidDataException("The EVE static-data archive exceeded the 256 MiB safety limit.");
            }
            await target.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            hash.AppendData(buffer, 0, read);
            progress?.Report(new("Downloading official EVE static data", copied, total));
        }
        await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    private static async Task TouchLastCheckedAsync(string database, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection($"Data Source={database}");
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO metadata(key, value) VALUES ('last_checked_at', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        command.Parameters.AddWithValue("$value", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}

public static class SdeImporter
{
    private const long MaxExpandedBytes = 1024L * 1024 * 1024;

    private sealed record Dataset(string FileName, EveEntityKind Kind);

    private static readonly Dataset[] EntityDatasets =
    [
        new("categories.jsonl", EveEntityKind.Category),
        new("groups.jsonl", EveEntityKind.Group),
        new("marketGroups.jsonl", EveEntityKind.MarketGroup),
        new("types.jsonl", EveEntityKind.Type),
        new("mapRegions.jsonl", EveEntityKind.Region),
        new("mapConstellations.jsonl", EveEntityKind.Constellation),
        new("mapSolarSystems.jsonl", EveEntityKind.SolarSystem),
        new("npcCorporations.jsonl", EveEntityKind.Corporation),
        new("stationOperations.jsonl", EveEntityKind.StationOperation),
        new("blueprints.jsonl", EveEntityKind.Blueprint)
    ];

    public static async Task ImportAsync(
        string archivePath,
        string databasePath,
        long buildNumber,
        DateTimeOffset releaseDate,
        string archiveSha256,
        string sourceUrl,
        IProgress<SdeUpdateProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        await Task.Run(() => Import(
            archivePath,
            databasePath,
            buildNumber,
            releaseDate,
            archiveSha256,
            sourceUrl,
            progress,
            cancellationToken), cancellationToken).ConfigureAwait(false);
    }

    private static void Import(
        string archivePath,
        string databasePath,
        long buildNumber,
        DateTimeOffset releaseDate,
        string archiveSha256,
        string sourceUrl,
        IProgress<SdeUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var archive = ZipFile.OpenRead(archivePath);
        ValidateArchive(archive);
        using var connection = new SqliteConnection($"Data Source={databasePath}");
        connection.Open();
        Execute(connection, """
            PRAGMA journal_mode=OFF;
            PRAGMA synchronous=OFF;
            PRAGMA temp_store=MEMORY;
            CREATE TABLE metadata(key TEXT PRIMARY KEY, value TEXT NOT NULL);
            CREATE TABLE entities(
                kind TEXT NOT NULL,
                id INTEGER NOT NULL,
                name TEXT NOT NULL,
                parent_id INTEGER,
                region_id INTEGER,
                solar_system_id INTEGER,
                security_status REAL,
                published INTEGER,
                PRIMARY KEY(kind, id)
            );
            CREATE TABLE blueprints(
                blueprint_type_id INTEGER PRIMARY KEY,
                manufacturing_time INTEGER,
                max_production_limit INTEGER,
                payload_json TEXT NOT NULL
            );
            CREATE TABLE dogma_attributes(
                attribute_id INTEGER PRIMARY KEY,
                name TEXT NOT NULL,
                display_name TEXT,
                unit_id INTEGER
            );
            CREATE TABLE type_dogma(
                type_id INTEGER NOT NULL,
                attribute_id INTEGER NOT NULL,
                value REAL NOT NULL,
                PRIMARY KEY(type_id, attribute_id)
            );
            """);

        using var transaction = connection.BeginTransaction();
        foreach (var dataset in EntityDatasets)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var entry = archive.GetEntry(dataset.FileName);
            if (entry is null)
            {
                continue;
            }
            progress?.Report(new($"Indexing {dataset.FileName}", 0, entry.Length));
            ImportEntities(entry, dataset.Kind, connection, transaction, progress, cancellationToken);
        }
        ImportStations(archive.GetEntry("npcStations.jsonl"), connection, transaction, progress, cancellationToken);
        ImportDogmaAttributes(archive.GetEntry("dogmaAttributes.jsonl"), connection, transaction, cancellationToken);
        ImportTypeDogma(archive.GetEntry("typeDogma.jsonl"), connection, transaction, progress, cancellationToken);
        WriteMetadata(connection, transaction, buildNumber, releaseDate, archiveSha256, sourceUrl);
        transaction.Commit();

        progress?.Report(new("Building search indexes", 0));
        Execute(connection, """
            CREATE INDEX entities_name ON entities(name COLLATE NOCASE);
            CREATE INDEX entities_id ON entities(id);
            CREATE INDEX entities_system ON entities(solar_system_id);
            CREATE INDEX type_dogma_attribute ON type_dogma(attribute_id);
            PRAGMA optimize;
            """);
    }

    private static void ValidateArchive(ZipArchive archive)
    {
        long expanded = 0;
        foreach (var entry in archive.Entries)
        {
            if (entry.FullName != Path.GetFileName(entry.FullName) ||
                entry.FullName.Contains('\\', StringComparison.Ordinal))
            {
                throw new InvalidDataException("The EVE static-data archive contained an unsafe entry path.");
            }
            expanded += entry.Length;
            if (expanded > MaxExpandedBytes)
            {
                throw new InvalidDataException("The EVE static-data archive exceeded the 1 GiB expanded safety limit.");
            }
        }
    }

    private static void ImportEntities(
        ZipArchiveEntry entry,
        EveEntityKind kind,
        SqliteConnection connection,
        SqliteTransaction transaction,
        IProgress<SdeUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        using var command = EntityInsert(connection, transaction);
        using var blueprint = connection.CreateCommand();
        blueprint.Transaction = transaction;
        blueprint.CommandText = """
            INSERT OR REPLACE INTO blueprints(
                blueprint_type_id, manufacturing_time, max_production_limit, payload_json)
            VALUES($id, $time, $limit, $json)
            """;
        blueprint.Parameters.Add("$id", SqliteType.Integer);
        blueprint.Parameters.Add("$time", SqliteType.Integer);
        blueprint.Parameters.Add("$limit", SqliteType.Integer);
        blueprint.Parameters.Add("$json", SqliteType.Text);

        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        long completed = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed += line.Length + 1;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = ReadId(root, kind);
            var name = ReadName(root, kind, id);
            if (name is null)
            {
                continue;
            }
            command.Parameters["$kind"].Value = kind.ToString();
            command.Parameters["$id"].Value = id;
            command.Parameters["$name"].Value = name;
            command.Parameters["$parent"].Value = NullableLong(root, ParentProperty(kind)) ?? (object)DBNull.Value;
            command.Parameters["$region"].Value = NullableLong(root, "regionID") ?? (object)DBNull.Value;
            command.Parameters["$system"].Value = NullableLong(root, "solarSystemID") ?? (object)DBNull.Value;
            command.Parameters["$security"].Value = NullableDouble(root, "securityStatus") ?? (object)DBNull.Value;
            command.Parameters["$published"].Value =
                root.TryGetProperty("published", out var published) && published.GetBoolean() ? 1 : 0;
            command.ExecuteNonQuery();

            if (kind == EveEntityKind.Blueprint)
            {
                var manufacturingTime = root.TryGetProperty("activities", out var activities) &&
                                        activities.TryGetProperty("manufacturing", out var manufacturing) &&
                                        manufacturing.TryGetProperty("time", out var time)
                    ? time.GetInt64()
                    : (long?)null;
                blueprint.Parameters["$id"].Value = id;
                blueprint.Parameters["$time"].Value = manufacturingTime ?? (object)DBNull.Value;
                blueprint.Parameters["$limit"].Value =
                    NullableLong(root, "maxProductionLimit") ?? (object)DBNull.Value;
                blueprint.Parameters["$json"].Value = line;
                blueprint.ExecuteNonQuery();
            }
            if (completed % (8 * 1024 * 1024) < line.Length + 1)
            {
                progress?.Report(new($"Indexing {entry.FullName}", completed, entry.Length));
            }
        }
    }

    private static void ImportStations(
        ZipArchiveEntry? entry,
        SqliteConnection connection,
        SqliteTransaction transaction,
        IProgress<SdeUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return;
        }
        var systems = LoadNames(connection, transaction, EveEntityKind.SolarSystem);
        var corporations = LoadNames(connection, transaction, EveEntityKind.Corporation);
        var operations = LoadNames(connection, transaction, EveEntityKind.StationOperation);
        using var command = EntityInsert(connection, transaction);
        using var stream = entry.Open();
        using var reader = new StreamReader(stream);
        long completed = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed += line.Length + 1;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var id = root.GetProperty("_key").GetInt64();
            var systemId = root.GetProperty("solarSystemID").GetInt64();
            var ownerId = root.GetProperty("ownerID").GetInt64();
            var operationId = root.GetProperty("operationID").GetInt64();
            var celestial = NullableLong(root, "celestialIndex");
            var orbit = NullableLong(root, "orbitIndex");
            var name = systems.GetValueOrDefault(systemId, $"System {systemId}");
            if (celestial is not null)
            {
                name += $" {ToRoman(celestial.Value)}";
            }
            if (orbit is not null)
            {
                name += $" - Moon {orbit}";
            }
            name += $" - {corporations.GetValueOrDefault(ownerId, $"Corporation {ownerId}")} " +
                    operations.GetValueOrDefault(operationId, $"Operation {operationId}");
            command.Parameters["$kind"].Value = EveEntityKind.Station.ToString();
            command.Parameters["$id"].Value = id;
            command.Parameters["$name"].Value = name;
            command.Parameters["$parent"].Value = systemId;
            command.Parameters["$region"].Value = DBNull.Value;
            command.Parameters["$system"].Value = systemId;
            command.Parameters["$security"].Value = DBNull.Value;
            command.Parameters["$published"].Value = 1;
            command.ExecuteNonQuery();
        }
        progress?.Report(new("Indexing npcStations.jsonl", completed, entry.Length));
    }

    private static void ImportDogmaAttributes(
        ZipArchiveEntry? entry,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO dogma_attributes(attribute_id, name, display_name, unit_id)
            VALUES($id, $name, $display, $unit)
            """;
        command.Parameters.Add("$id", SqliteType.Integer);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$display", SqliteType.Text);
        command.Parameters.Add("$unit", SqliteType.Integer);
        using var reader = new StreamReader(entry.Open());
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            command.Parameters["$id"].Value = root.GetProperty("_key").GetInt64();
            command.Parameters["$name"].Value = root.GetProperty("name").GetString() ?? "";
            command.Parameters["$display"].Value = LocalizedName(root, "displayName") ?? (object)DBNull.Value;
            command.Parameters["$unit"].Value = NullableLong(root, "unitID") ?? (object)DBNull.Value;
            command.ExecuteNonQuery();
        }
    }

    private static void ImportTypeDogma(
        ZipArchiveEntry? entry,
        SqliteConnection connection,
        SqliteTransaction transaction,
        IProgress<SdeUpdateProgress>? progress,
        CancellationToken cancellationToken)
    {
        if (entry is null)
        {
            return;
        }
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO type_dogma(type_id, attribute_id, value) VALUES($type, $attribute, $value)
            """;
        command.Parameters.Add("$type", SqliteType.Integer);
        command.Parameters.Add("$attribute", SqliteType.Integer);
        command.Parameters.Add("$value", SqliteType.Real);
        using var reader = new StreamReader(entry.Open());
        long completed = 0;
        while (reader.ReadLine() is { } line)
        {
            cancellationToken.ThrowIfCancellationRequested();
            completed += line.Length + 1;
            using var document = JsonDocument.Parse(line);
            var root = document.RootElement;
            var typeId = root.GetProperty("_key").GetInt64();
            if (!root.TryGetProperty("dogmaAttributes", out var attributes))
            {
                continue;
            }
            foreach (var attribute in attributes.EnumerateArray())
            {
                command.Parameters["$type"].Value = typeId;
                command.Parameters["$attribute"].Value = attribute.GetProperty("attributeID").GetInt64();
                command.Parameters["$value"].Value = attribute.GetProperty("value").GetDouble();
                command.ExecuteNonQuery();
            }
            if (completed % (8 * 1024 * 1024) < line.Length + 1)
            {
                progress?.Report(new("Indexing typeDogma.jsonl", completed, entry.Length));
            }
        }
    }

    private static SqliteCommand EntityInsert(SqliteConnection connection, SqliteTransaction transaction)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT OR REPLACE INTO entities(
                kind, id, name, parent_id, region_id, solar_system_id, security_status, published)
            VALUES($kind, $id, $name, $parent, $region, $system, $security, $published)
            """;
        command.Parameters.Add("$kind", SqliteType.Text);
        command.Parameters.Add("$id", SqliteType.Integer);
        command.Parameters.Add("$name", SqliteType.Text);
        command.Parameters.Add("$parent", SqliteType.Integer);
        command.Parameters.Add("$region", SqliteType.Integer);
        command.Parameters.Add("$system", SqliteType.Integer);
        command.Parameters.Add("$security", SqliteType.Real);
        command.Parameters.Add("$published", SqliteType.Integer);
        return command;
    }

    private static Dictionary<long, string> LoadNames(
        SqliteConnection connection,
        SqliteTransaction transaction,
        EveEntityKind kind)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "SELECT id, name FROM entities WHERE kind = $kind";
        command.Parameters.AddWithValue("$kind", kind.ToString());
        using var reader = command.ExecuteReader();
        var output = new Dictionary<long, string>();
        while (reader.Read())
        {
            output[reader.GetInt64(0)] = reader.GetString(1);
        }
        return output;
    }

    private static long ReadId(JsonElement root, EveEntityKind kind) =>
        kind == EveEntityKind.Blueprint && root.TryGetProperty("blueprintTypeID", out var blueprintType)
            ? blueprintType.GetInt64()
            : root.GetProperty("_key").GetInt64();

    private static string? ReadName(JsonElement root, EveEntityKind kind, long id)
    {
        if (kind == EveEntityKind.Blueprint)
        {
            return $"Blueprint {id}";
        }
        if (kind == EveEntityKind.StationOperation)
        {
            return LocalizedName(root, "operationName");
        }
        return LocalizedName(root, "name");
    }

    private static string? LocalizedName(JsonElement root, string property)
    {
        if (!root.TryGetProperty(property, out var value))
        {
            return null;
        }
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString();
        }
        return value.TryGetProperty("en", out var english) ? english.GetString() : null;
    }

    private static string? ParentProperty(EveEntityKind kind) => kind switch
    {
        EveEntityKind.Group => "categoryID",
        EveEntityKind.MarketGroup => "parentGroupID",
        EveEntityKind.Type => "groupID",
        EveEntityKind.Constellation => "regionID",
        EveEntityKind.SolarSystem => "constellationID",
        _ => null
    };

    private static long? NullableLong(JsonElement root, string? property) =>
        property is not null && root.TryGetProperty(property, out var value) &&
        value.ValueKind == JsonValueKind.Number
            ? value.GetInt64()
            : null;

    private static double? NullableDouble(JsonElement root, string property) =>
        root.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.Number
            ? value.GetDouble()
            : null;

    private static void WriteMetadata(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long buildNumber,
        DateTimeOffset releaseDate,
        string archiveSha256,
        string sourceUrl)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO metadata(key, value) VALUES($key, $value)";
        var key = command.Parameters.Add("$key", SqliteType.Text);
        var value = command.Parameters.Add("$value", SqliteType.Text);
        var metadata = new Dictionary<string, string>
        {
            ["build_number"] = buildNumber.ToString(CultureInfo.InvariantCulture),
            ["release_date"] = releaseDate.ToString("O", CultureInfo.InvariantCulture),
            ["last_checked_at"] = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture),
            ["archive_sha256"] = archiveSha256,
            ["source_url"] = sourceUrl,
            ["schema_version"] = "1"
        };
        foreach (var item in metadata)
        {
            key.Value = item.Key;
            value.Value = item.Value;
            command.ExecuteNonQuery();
        }
    }

    private static void Execute(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string ToRoman(long value)
    {
        if (value is < 1 or > 3999)
        {
            return value.ToString(CultureInfo.InvariantCulture);
        }
        (int Value, string Symbol)[] numerals =
        [
            (1000, "M"), (900, "CM"), (500, "D"), (400, "CD"),
            (100, "C"), (90, "XC"), (50, "L"), (40, "XL"),
            (10, "X"), (9, "IX"), (5, "V"), (4, "IV"), (1, "I")
        ];
        var remaining = (int)value;
        var output = new System.Text.StringBuilder();
        foreach (var numeral in numerals)
        {
            while (remaining >= numeral.Value)
            {
                output.Append(numeral.Symbol);
                remaining -= numeral.Value;
            }
        }
        return output.ToString();
    }
}
