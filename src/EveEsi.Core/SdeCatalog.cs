using System.Globalization;
using Microsoft.Data.Sqlite;

namespace EveEsi.Core;

public enum EveEntityKind
{
    Type,
    Category,
    Group,
    MarketGroup,
    Region,
    Constellation,
    SolarSystem,
    Station,
    Corporation,
    StationOperation,
    Blueprint
}

public sealed record EveEntity(
    long Id,
    string Name,
    EveEntityKind Kind,
    long? ParentId = null,
    long? RegionId = null,
    long? SolarSystemId = null,
    double? SecurityStatus = null);

public sealed record SdeMetadata(
    long BuildNumber,
    DateTimeOffset ReleaseDate,
    DateTimeOffset LastCheckedAt,
    string ArchiveSha256);

public interface IEveEntityCatalog
{
    bool IsAvailable { get; }
    Task<SdeMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default);
    Task<EveEntity?> FindByIdAsync(
        long id,
        EveEntityKind? kind = null,
        CancellationToken cancellationToken = default);
    Task<IReadOnlyList<EveEntity>> SearchAsync(
        string query,
        EveEntityKind? kind = null,
        int limit = 10,
        CancellationToken cancellationToken = default);
}

public sealed class SqliteEveEntityCatalog : IEveEntityCatalog
{
    private readonly string _path;

    public SqliteEveEntityCatalog(string? path = null)
    {
        _path = path ?? SdePaths.DatabasePath;
    }

    public bool IsAvailable => File.Exists(_path);

    public async Task<SdeMetadata?> GetMetadataAsync(CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT key, value FROM metadata";
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            values[reader.GetString(0)] = reader.GetString(1);
        }

        return values.TryGetValue("build_number", out var build) &&
               values.TryGetValue("release_date", out var release) &&
               values.TryGetValue("last_checked_at", out var checkedAt)
            ? new(
                long.Parse(build, CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(release, CultureInfo.InvariantCulture),
                DateTimeOffset.Parse(checkedAt, CultureInfo.InvariantCulture),
                values.GetValueOrDefault("archive_sha256", ""))
            : null;
    }

    public async Task<EveEntity?> FindByIdAsync(
        long id,
        EveEntityKind? kind = null,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return null;
        }

        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id, name, kind, parent_id, region_id, solar_system_id, security_status
            FROM entities
            WHERE id = $id AND ($kind IS NULL OR kind = $kind)
            ORDER BY kind
            LIMIT 1
            """;
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$kind", kind?.ToString() ?? (object)DBNull.Value);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Read(reader) : null;
    }

    public async Task<IReadOnlyList<EveEntity>> SearchAsync(
        string query,
        EveEntityKind? kind = null,
        int limit = 10,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(query))
        {
            return [];
        }
        if (limit is < 1 or > 50)
        {
            throw new ArgumentOutOfRangeException(nameof(limit), "Search limit must be between 1 and 50.");
        }

        await using var connection = Open();
        await connection.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        var terms = query.Trim().Split(
            ' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var tokenPredicates = new List<string>();
        for (var index = 0; index < terms.Length; index++)
        {
            tokenPredicates.Add($"name LIKE $token{index} ESCAPE '\\'");
            command.Parameters.AddWithValue($"$token{index}", $"%{EscapeLike(terms[index])}%");
        }
        command.CommandText = $"""
            SELECT id, name, kind, parent_id, region_id, solar_system_id, security_status
            FROM entities
            WHERE ($kind IS NULL OR kind = $kind)
              AND {string.Join(" AND ", tokenPredicates)}
            ORDER BY CASE WHEN name = $exact COLLATE NOCASE THEN 0
                          WHEN name LIKE $prefix ESCAPE '\' THEN 1 ELSE 2 END,
                     length(name), name
            LIMIT $limit
            """;
        var escaped = EscapeLike(query.Trim());
        command.Parameters.AddWithValue("$kind", kind?.ToString() ?? (object)DBNull.Value);
        command.Parameters.AddWithValue("$prefix", $"{escaped}%");
        command.Parameters.AddWithValue("$exact", query.Trim());
        command.Parameters.AddWithValue("$limit", limit);
        var output = new List<EveEntity>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            output.Add(Read(reader));
        }
        return output;
    }

    private SqliteConnection Open() => new(new SqliteConnectionStringBuilder
    {
        DataSource = _path,
        Mode = SqliteOpenMode.ReadOnly,
        Cache = SqliteCacheMode.Shared
    }.ToString());

    private static EveEntity Read(SqliteDataReader reader) => new(
        reader.GetInt64(0),
        reader.GetString(1),
        Enum.Parse<EveEntityKind>(reader.GetString(2)),
        reader.IsDBNull(3) ? null : reader.GetInt64(3),
        reader.IsDBNull(4) ? null : reader.GetInt64(4),
        reader.IsDBNull(5) ? null : reader.GetInt64(5),
        reader.IsDBNull(6) ? null : reader.GetDouble(6));

    private static string EscapeLike(string value) =>
        value.Replace(@"\", @"\\", StringComparison.Ordinal)
            .Replace("%", @"\%", StringComparison.Ordinal)
            .Replace("_", @"\_", StringComparison.Ordinal);
}

public static class SdePaths
{
    public static string DirectoryPath =>
        Path.Combine(EvaDataDirectory.Get(), "sde");

    public static string DatabasePath =>
        Path.Combine(DirectoryPath, "eve-static.db");
}
