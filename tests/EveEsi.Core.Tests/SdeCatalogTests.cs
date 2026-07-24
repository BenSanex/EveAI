using System.IO.Compression;
using EveEsi.Core;

namespace EveEsi.Core.Tests;

public sealed class SdeCatalogTests
{
    [Fact]
    public async Task SsoConfiguration_RoundTripsThroughSharedStore()
    {
        var directory = TemporaryDirectory();
        try
        {
            var store = new EveSsoConfigurationStore(directory);
            var expected = new EveSsoConfiguration("client-id", "http://127.0.0.1:41793/callback/");
            await store.SaveAsync(expected);
            Assert.Equal(expected, await store.LoadAsync());
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task SsoConfiguration_MigratesLegacyEvaSettingsForCli()
    {
        var directory = TemporaryDirectory();
        try
        {
            await File.WriteAllTextAsync(
                Path.Combine(directory, "settings.json"),
                """{"EveClientId":"legacy-client","CallbackUri":"http://localhost:41793/callback/"}""");
            var configuration = await new EveSsoConfigurationStore(directory).LoadAsync();

            Assert.Equal("legacy-client", configuration.ClientId);
            Assert.True(File.Exists(Path.Combine(directory, "eve-sso.json")));
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    [Fact]
    public async Task Importer_BuildsSearchableQueryFocusedIndex()
    {
        var directory = TemporaryDirectory();
        var archivePath = Path.Combine(directory, "fixture.zip");
        var databasePath = Path.Combine(directory, "eve-static.db");
        try
        {
            using (var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create))
            {
                Write(archive, "mapRegions.jsonl",
                    """{"_key":10000032,"name":{"en":"Sinq Laison"}}""");
                Write(archive, "mapConstellations.jsonl",
                    """{"_key":20000468,"name":{"en":"Coriault"},"regionID":10000032}""");
                Write(archive, "mapSolarSystems.jsonl",
                    """{"_key":30002659,"name":{"en":"Dodixie"},"constellationID":20000468,"regionID":10000032,"securityStatus":0.87}""");
                Write(archive, "types.jsonl",
                    """{"_key":30013,"name":{"en":"Core Probe I"},"groupID":479,"published":true}""");
                Write(archive, "npcCorporations.jsonl",
                    """{"_key":1000120,"name":{"en":"Federation Navy"}}""");
                Write(archive, "stationOperations.jsonl",
                    """{"_key":7,"operationName":{"en":"Assembly Plant"}}""");
                Write(archive, "npcStations.jsonl",
                    """{"_key":60011866,"solarSystemID":30002659,"ownerID":1000120,"operationID":7,"celestialIndex":9,"orbitIndex":20}""");
            }

            await SdeImporter.ImportAsync(
                archivePath,
                databasePath,
                123,
                new DateTimeOffset(2026, 7, 23, 0, 0, 0, TimeSpan.Zero),
                "abc",
                "https://example.invalid/sde.zip");
            var catalog = new SqliteEveEntityCatalog(databasePath);

            var probe = Assert.Single(await catalog.SearchAsync("Core Probe I", EveEntityKind.Type));
            Assert.Equal(30013, probe.Id);
            var dodixie = Assert.Single(await catalog.SearchAsync("Dodixie", EveEntityKind.SolarSystem));
            Assert.Equal(10000032, dodixie.RegionId);
            var station = Assert.Single(await catalog.SearchAsync("Federation Navy", EveEntityKind.Station));
            Assert.Equal("Dodixie IX - Moon 20 - Federation Navy Assembly Plant", station.Name);
            Assert.Equal(123, (await catalog.GetMetadataAsync())!.BuildNumber);
        }
        finally
        {
            Directory.Delete(directory, true);
        }
    }

    private static void Write(ZipArchive archive, string name, string line)
    {
        using var writer = new StreamWriter(archive.CreateEntry(name).Open());
        writer.WriteLine(line);
    }

    private static string TemporaryDirectory()
    {
        var path = Path.Combine(Path.GetTempPath(), $"eva-core-test-{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }
}
