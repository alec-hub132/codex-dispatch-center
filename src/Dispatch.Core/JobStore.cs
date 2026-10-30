using Microsoft.Data.Sqlite;
using System.Text.Json;

namespace Dispatch.Core;

public sealed class JobStore : IDisposable
{
    private readonly SqliteConnection connection;
    private readonly FileStream owner;
    public string Root { get; }

    public JobStore(string root)
    {
        Root = Path.GetFullPath(root);
        Directory.CreateDirectory(Root);
        owner = new FileStream(Path.Combine(Root, "store.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Root, "dispatch.db"), Pooling = false }.ToString());
        try
        {
            connection.Open();
            using var version = connection.CreateCommand();
            version.CommandText = "PRAGMA user_version";
            if (Convert.ToInt32(version.ExecuteScalar()) > 1) throw new InvalidOperationException("数据库版本高于当前程序，拒绝修改。");
            using var command = connection.CreateCommand();
            command.CommandText = """
                PRAGMA journal_mode=WAL;
                PRAGMA synchronous=FULL;
                CREATE TABLE IF NOT EXISTS records(kind TEXT NOT NULL, id TEXT NOT NULL, body TEXT NOT NULL, PRIMARY KEY(kind,id));
                CREATE TABLE IF NOT EXISTS events(sequence INTEGER PRIMARY KEY AUTOINCREMENT, time TEXT NOT NULL, entity_id TEXT NOT NULL, body TEXT NOT NULL);
                PRAGMA user_version=1;
                """;
            command.ExecuteNonQuery();
        }
        catch { connection.Dispose(); owner.Dispose(); throw; }
    }

    public List<T> Load<T>(string kind)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT body FROM records WHERE kind=$kind";
        command.Parameters.AddWithValue("$kind", kind);
        using var reader = command.ExecuteReader();
        var result = new List<T>();
        while (reader.Read()) result.Add(JsonSerializer.Deserialize<T>(reader.GetString(0), Json.Options) ?? throw new InvalidDataException("无效数据库记录"));
        return result;
    }

    public void Save<T>(string kind, string id, T value)
        => SaveBatch(new StoreWrite(kind, id, value!));

    internal void SaveBatch(params StoreWrite[] writes)
    {
        using var transaction = connection.BeginTransaction();
        foreach (var write in writes)
        {
        var body = JsonSerializer.Serialize(write.Value, write.Value.GetType(), Json.Options);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO records(kind,id,body) VALUES($kind,$id,$body)
              ON CONFLICT(kind,id) DO UPDATE SET body=excluded.body;
            INSERT INTO events(time,entity_id,body) VALUES($time,$id,$body);
            """;
        command.Parameters.AddWithValue("$kind", write.Kind);
        command.Parameters.AddWithValue("$id", write.Id);
        command.Parameters.AddWithValue("$body", body);
        command.Parameters.AddWithValue("$time", DateTimeOffset.UtcNow.ToString("O"));
        command.ExecuteNonQuery();
        }
        transaction.Commit();
    }

    public void Dispose() { connection.Dispose(); owner.Dispose(); }
}
