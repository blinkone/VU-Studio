using Microsoft.Data.Sqlite;
using VuStudio.Core.History;

namespace VuStudio.Server.Storage;

/// <summary>Per-minute history aggregates in a small SQLite database (WAL mode).</summary>
public sealed class SqliteHistoryStore : IHistoryPersistence, IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly Lock _lock = new();

    public SqliteHistoryStore(string path)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL;");
        Execute("PRAGMA synchronous=NORMAL;");
        Execute("""
            CREATE TABLE IF NOT EXISTS history (
                sensor TEXT NOT NULL,
                minute INTEGER NOT NULL,
                min REAL NOT NULL,
                avg REAL NOT NULL,
                max REAL NOT NULL,
                count INTEGER NOT NULL,
                PRIMARY KEY (sensor, minute)
            ) WITHOUT ROWID;
            """);
    }

    public void Append(IReadOnlyList<HistoryAggregate> rows)
    {
        lock (_lock)
        {
            using var transaction = _connection.BeginTransaction();
            using var command = _connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = "INSERT OR REPLACE INTO history(sensor, minute, min, avg, max, count) VALUES ($s, $m, $min, $avg, $max, $c)";
            var s = command.Parameters.Add("$s", SqliteType.Text);
            var m = command.Parameters.Add("$m", SqliteType.Integer);
            var min = command.Parameters.Add("$min", SqliteType.Real);
            var avg = command.Parameters.Add("$avg", SqliteType.Real);
            var max = command.Parameters.Add("$max", SqliteType.Real);
            var c = command.Parameters.Add("$c", SqliteType.Integer);
            foreach (var row in rows)
            {
                s.Value = row.SensorId;
                m.Value = row.Minute.ToUnixTimeSeconds() / 60;
                min.Value = row.Min;
                avg.Value = row.Avg;
                max.Value = row.Max;
                c.Value = row.Count;
                command.ExecuteNonQuery();
            }

            transaction.Commit();
        }
    }

    public IReadOnlyList<HistoryAggregate> Query(string sensorId, DateTimeOffset from, DateTimeOffset to)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "SELECT minute, min, avg, max, count FROM history WHERE sensor = $s AND minute BETWEEN $from AND $to ORDER BY minute LIMIT 50000";
            command.Parameters.AddWithValue("$s", sensorId);
            command.Parameters.AddWithValue("$from", from.ToUnixTimeSeconds() / 60);
            command.Parameters.AddWithValue("$to", to.ToUnixTimeSeconds() / 60);
            using var reader = command.ExecuteReader();
            var list = new List<HistoryAggregate>();
            while (reader.Read())
            {
                list.Add(new HistoryAggregate(
                    sensorId,
                    DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(0) * 60),
                    reader.GetDouble(1),
                    reader.GetDouble(2),
                    reader.GetDouble(3),
                    reader.GetInt32(4)));
            }

            return list;
        }
    }

    public void Prune(DateTimeOffset olderThan)
    {
        lock (_lock)
        {
            using var command = _connection.CreateCommand();
            command.CommandText = "DELETE FROM history WHERE minute < $before";
            command.Parameters.AddWithValue("$before", olderThan.ToUnixTimeSeconds() / 60);
            command.ExecuteNonQuery();
        }
    }

    public void Dispose()
    {
        lock (_lock)
        {
            _connection.Dispose();
        }
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }
}
