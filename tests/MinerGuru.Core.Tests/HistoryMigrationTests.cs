using Microsoft.Data.Sqlite;
using MinerGuru.Core.Services;
using Xunit;

namespace MinerGuru.Core.Tests;

public class HistoryMigrationTests : IDisposable
{
    private readonly string _dir = Directory.CreateTempSubdirectory("mg-history").FullName;

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        Directory.Delete(_dir, recursive: true);
    }

    [Fact]
    public void StepsRunOnce()
    {
        string[] steps = ["ALTER TABLE samples ADD COLUMN extra REAL"];
        _ = new HistoryStore(_dir);
        // A second run of the step would throw on the existing column.
        _ = new HistoryStore(_dir, migrations: steps);
        _ = new HistoryStore(_dir, migrations: [.. steps, "ALTER TABLE samples ADD COLUMN more REAL"]);

        using var db = new SqliteConnection($"Data Source={Path.Combine(_dir, "history.sqlite")};Pooling=False");
        db.Open();
        using var read = db.CreateCommand();
        read.CommandText = "PRAGMA user_version";
        Assert.Equal(2L, read.ExecuteScalar());
    }

    [Fact]
    public void ADamagedFileMovesAsideAtStart()
    {
        var path = Path.Combine(_dir, "history.sqlite");
        File.WriteAllBytes(path, Enumerable.Repeat((byte)0x5A, 8192).ToArray());

        var store = new HistoryStore(_dir);

        Assert.NotNull(store.DamagedBackup);
        Assert.Equal(8192, new FileInfo(store.DamagedBackup!).Length);
        Assert.Empty(store.Points(Guid.NewGuid(), DateTime.Now.AddDays(-1), DateTime.Now));
    }

    [Fact]
    public void AFileDamagedWhileRunningMovesAside()
    {
        var store = new HistoryStore(_dir);
        var damaged = 0;
        store.Damaged += () => damaged++;
        File.WriteAllBytes(Path.Combine(_dir, "history.sqlite"), Enumerable.Repeat((byte)0x5A, 8192).ToArray());

        Assert.Empty(store.Points(Guid.NewGuid(), DateTime.Now.AddDays(-1), DateTime.Now));

        Assert.Equal(1, damaged);
        Assert.NotNull(store.DamagedBackup);
        Assert.Empty(store.Points(Guid.NewGuid(), DateTime.Now.AddDays(-1), DateTime.Now));
        Assert.Equal(1, damaged);
    }

    [Fact]
    public void AHealthyFileStaysInPlace()
    {
        var store = new HistoryStore(_dir);
        _ = new HistoryStore(_dir);
        Assert.Null(store.DamagedBackup);
    }
}
