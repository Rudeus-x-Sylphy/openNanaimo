using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace OpenNanaimo.Adapter.Services;

/// <summary>Versioned legacy record payloads in the same SQLite database as characters.
/// NULL is a durable deletion marker. Legacy files are read once, never updated or
/// removed; exact imported bytes remain in NativeStateImports for recovery.</summary>
public sealed class PersistentStateStore(string databasePath)
{
    public const int MaximumRecordBytes = 16 * 1024 * 1024;
    public string DatabasePath { get; } = Path.GetFullPath(databasePath);
    public static (string Scope, string Name) Key(string path, string databasePath)
    {
        var full = Path.GetFullPath(path);
        return (Path.GetRelativePath(Path.GetDirectoryName(Path.GetFullPath(databasePath))!, Path.GetDirectoryName(full)!).Replace('\\', '/'), Path.GetFileName(full));
    }
    public SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(DatabasePath)!);
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder {
            DataSource = DatabasePath, Pooling = false, DefaultTimeout = 10 }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS NativeState(Scope TEXT COLLATE NOCASE NOT NULL,Name TEXT NOT NULL,Content BLOB,UpdatedAt TEXT NOT NULL,PRIMARY KEY(Scope,Name));
            CREATE TABLE IF NOT EXISTS NativeStateImports(Scope TEXT COLLATE NOCASE NOT NULL,Name TEXT NOT NULL,Content BLOB NOT NULL,ImportedAt TEXT NOT NULL,PRIMARY KEY(Scope,Name));
            """;
        command.ExecuteNonQuery();
        return connection;
    }
    public byte[]? Read(string legacyPath)
    {
        if (!File.Exists(DatabasePath) && !File.Exists(legacyPath)) return null;
        using var connection = Open();
        var key = Key(legacyPath, DatabasePath);
        using var transaction = connection.BeginTransaction(deferred: false);
        MigrateScope(connection, transaction, Path.GetDirectoryName(Path.GetFullPath(legacyPath))!);
        using var command = connection.CreateCommand(); command.Transaction = transaction;
        command.CommandText = "SELECT Content FROM NativeState WHERE Scope=$scope AND Name=$name";
        command.Parameters.AddWithValue("$scope", key.Scope);command.Parameters.AddWithValue("$name", key.Name);
        using (var reader = command.ExecuteReader())
            if (reader.Read()) { var bytes = reader.IsDBNull(0) ? null : (byte[])reader[0]; reader.Close(); transaction.Commit(); return bytes; }
        byte[]? original = null;
        if (File.Exists(legacyPath))
        {
            if (new FileInfo(legacyPath).Length > MaximumRecordBytes) throw new InvalidDataException("Legacy record is too large: " + key.Name);
            original = File.ReadAllBytes(legacyPath);
        }
        if (original is null) { transaction.Commit(); return null; }
        Write(connection, transaction, legacyPath, original);
        if (original is not null)
        {
            command.CommandText = "INSERT OR IGNORE INTO NativeStateImports(Scope,Name,Content,ImportedAt) VALUES($scope,$name,$content,$now)";
            command.Parameters.AddWithValue("$content", original);command.Parameters.AddWithValue("$now", DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
        }
        transaction.Commit();return original;
    }
    public void Put(string path, byte[]? bytes)
    {
        Read(path); // capture pre-migration bytes before a write or tombstone
        using var connection = Open();using var transaction = connection.BeginTransaction(deferred:false);
        Write(connection,transaction,path,bytes);transaction.Commit();
    }
    public static void Write(SqliteConnection connection, SqliteTransaction transaction, string path, byte[]? bytes)
    {
        if (bytes?.Length > MaximumRecordBytes) throw new InvalidDataException("State record exceeds capacity.");
        MigrateScope(connection,transaction,Path.GetDirectoryName(Path.GetFullPath(path))!);
        var (scope,name)=Key(path,connection.DataSource);using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="INSERT INTO NativeState(Scope,Name,Content,UpdatedAt) VALUES($scope,$name,$content,$now) ON CONFLICT(Scope,Name) DO UPDATE SET Content=excluded.Content,UpdatedAt=excluded.UpdatedAt";
        command.Parameters.AddWithValue("$scope",scope);command.Parameters.AddWithValue("$name",name);command.Parameters.Add("$content",SqliteType.Blob).Value=(object?)bytes??DBNull.Value;command.Parameters.AddWithValue("$now",DateTime.UtcNow.ToString("O"));command.ExecuteNonQuery();
    }
    public string[] List(string directory, string suffix)
    {
        directory=Path.GetFullPath(directory);
        if(Directory.Exists(directory))foreach(var path in Directory.EnumerateFiles(directory,"*"+suffix))Read(path);
        if(!File.Exists(DatabasePath))return [];
        using var connection=Open();using(var transaction=connection.BeginTransaction(deferred:false)){MigrateScope(connection,transaction,directory);transaction.Commit();}
        using var command=connection.CreateCommand();command.CommandText="SELECT Name FROM NativeState WHERE Scope=$scope AND Content IS NOT NULL ORDER BY Name";command.Parameters.AddWithValue("$scope",Key(Path.Combine(directory,"record"),DatabasePath).Scope);
        using var reader=command.ExecuteReader();var names=new List<string>();while(reader.Read())if(reader.GetString(0).EndsWith(suffix,StringComparison.Ordinal))names.Add(reader.GetString(0));return names.ToArray();
    }
    private static void MigrateScope(SqliteConnection connection, SqliteTransaction transaction, string directory)
    {
        // Early development databases used absolute namespaces. Convert only the
        // requested directory; conflicts abort rather than choose a version.
        var relative=Key(Path.Combine(directory,"record"),connection.DataSource).Scope;
        using var command=connection.CreateCommand();command.Transaction=transaction;
        command.CommandText="UPDATE NativeState SET Scope=$new WHERE Scope=$old; UPDATE NativeStateImports SET Scope=$new WHERE Scope=$old";
        command.Parameters.AddWithValue("$new",relative);command.Parameters.AddWithValue("$old",directory);command.ExecuteNonQuery();
    }
    public string Backup(string backupRoot)
    {
        Directory.CreateDirectory(backupRoot);var path=Path.Combine(backupRoot,DateTime.UtcNow.ToString("yyyyMMdd-HHmmss")+"-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(path);path=Path.Combine(path,"game.db");
        using var src=Open();using var dest=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=path,Pooling=false}.ToString());dest.Open();src.BackupDatabase(dest);return path;
    }
}
