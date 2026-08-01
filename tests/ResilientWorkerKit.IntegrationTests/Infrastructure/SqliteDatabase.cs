using Microsoft.Data.Sqlite;

namespace ResilientWorkerKit.IntegrationTests.Infrastructure;

/// <summary>
/// A temporary SQLite file database that survives host restarts inside one test and is deleted
/// afterwards. A file (not <c>:memory:</c>) is required precisely because the restart scenario
/// must outlive the first host's connections.
/// </summary>
internal sealed class SqliteDatabase : IDisposable
{
    public SqliteDatabase()
    {
        Path = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(),
            $"workerkit-tests-{Guid.NewGuid():n}.db");
        ConnectionString = $"Data Source={Path}";
    }

    public string Path { get; }

    public string ConnectionString { get; }

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { "", "-shm", "-wal" })
        {
            var file = Path + suffix;
            try
            {
                if (File.Exists(file))
                {
                    File.Delete(file);
                }
            }
            catch (IOException)
            {
                // A stray handle on Windows must not fail the test run.
            }
        }
    }
}
