using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Legenda.App;

public sealed record DatabaseVersion(long Modified, string Revision) : IComparable<DatabaseVersion>
{
    public int CompareTo(DatabaseVersion? other) => other is null ? 1 : Modified != other.Modified
        ? Modified.CompareTo(other.Modified) : StringComparer.Ordinal.Compare(Revision, other.Revision);
    public string FileName => $"shared-{Modified:D16}-{Revision}.db";
}

public sealed partial class DatabaseService
{
    private CloudDatabaseSync? _sharedSync;
    public CloudDatabaseSync SharedSync => _sharedSync ??= new CloudDatabaseSync(this, new YandexSharedDatabaseStore());

    private static void InitializeSync(SqliteConnection db, long initialModified, bool freshDatabase)
    {
        Execute(db, null, "CREATE TABLE IF NOT EXISTS SharedVersion(Id INTEGER PRIMARY KEY CHECK(Id=1),Modified INTEGER NOT NULL,Revision TEXT NOT NULL,SchemaVersion INTEGER NOT NULL,IsBootstrap INTEGER NOT NULL DEFAULT 0)");
        using(var schema=db.CreateCommand())
        {
            schema.CommandText="SELECT name FROM pragma_table_info('SharedVersion') WHERE name='IsBootstrap'";
            if(schema.ExecuteScalar() is null)Execute(db,null,"ALTER TABLE SharedVersion ADD COLUMN IsBootstrap INTEGER NOT NULL DEFAULT 0");
        }
        Execute(db, null, "INSERT OR IGNORE INTO SharedVersion VALUES(1,$modified,lower(hex(randomblob(16))),1,$bootstrap)", ("$modified", initialModified),("$bootstrap",freshDatabase?1:0));
        InstallSyncTriggers(db,null);
    }

    private static void InstallSyncTriggers(SqliteConnection db,SqliteTransaction? transaction)
    {
        // Local settings and audit messages do not create a new shared version.
        foreach (var table in new[] { "Clients", "Accounts", "Visits", "MembershipHistory" })
        foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
        {
            Execute(db,transaction,$"DROP TRIGGER IF EXISTS Sync_{table}_{operation}");
            Execute(db, transaction, $"""
                CREATE TRIGGER IF NOT EXISTS Sync_{table}_{operation} AFTER {operation} ON {table}
                BEGIN
                  UPDATE SharedVersion SET Modified=MAX(Modified+1,CAST((julianday('now')-2440587.5)*86400000 AS INTEGER)),Revision=lower(hex(randomblob(16))){(table=="Accounts"?"":",IsBootstrap=0")} WHERE Id=1;
                END;
                """);
        }
    }

    public bool IsBootstrapDatabase
    {
        get { using var db=OpenConnection();using var cmd=db.CreateCommand();cmd.CommandText="SELECT IsBootstrap FROM SharedVersion WHERE Id=1";return Convert.ToInt64(cmd.ExecuteScalar())==1; }
    }
    internal void MarkSharedInitialized() { using var db=OpenConnection();Execute(db,null,"UPDATE SharedVersion SET IsBootstrap=0 WHERE Id=1"); }

    public DatabaseVersion CurrentDatabaseVersion
    {
        get { using var db = OpenConnection(); return ReadVersion(db); }
    }

    private static DatabaseVersion ReadVersion(SqliteConnection db,SqliteTransaction? transaction=null)
    {
        using var command = db.CreateCommand();
        command.Transaction=transaction;
        command.CommandText = "SELECT Modified,Revision,SchemaVersion FROM SharedVersion WHERE Id=1";
        using var reader = command.ExecuteReader();
        if (!reader.Read() || reader.GetInt32(2) != 1) throw new InvalidOperationException("Версия общей базы не поддерживается. Обновите приложение.");
        return new(reader.GetInt64(0), reader.GetString(1));
    }

    internal void SetLocalSyncSetting(string key, string value)
    {
        using var db = OpenConnection();
        Execute(db, null, "INSERT INTO Settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ("$key",key),("$value",value));
    }

    public DatabaseVersion CreateSharedSnapshot(string path)
    {
        Backup(path);
        using var snapshot = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path,Pooling=false }.ToString());
        snapshot.Open();
        Execute(snapshot, null, "PRAGMA secure_delete=ON; DELETE FROM Settings; UPDATE SharedVersion SET IsBootstrap=0 WHERE Id=1; VACUUM;");
        return ReadVersion(snapshot);
    }

    public string ApplySharedSnapshot(string path, DatabaseVersion expectedLocal, DatabaseVersion expectedRemote)
    {
        if (CurrentDatabaseVersion != expectedLocal) throw new InvalidOperationException("Локальная база изменилась во время загрузки. Синхронизация повторится.");
        using var incoming = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource=path,Mode=SqliteOpenMode.ReadOnly,Pooling=false }.ToString());
        incoming.Open();
        using (var check = incoming.CreateCommand())
        {
            check.CommandText = "PRAGMA integrity_check";
            if (check.ExecuteScalar() as string != "ok") throw new InvalidOperationException("Общая база повреждена. Локальные данные сохранены.");
            if (ReadVersion(incoming) != expectedRemote) throw new InvalidOperationException("Версия скачанной базы не совпадает с выбранной.");
            foreach (var sql in new[] {
                "SELECT Id,LastName,FirstName,MiddleName,BirthDate,MembershipNumber,PurchaseDate,ExpiryDate,IsBlacklisted,Phone,Deleted,FrozenUntil FROM Clients LIMIT 0",
                "SELECT Id,Login,PasswordHash,Salt,Role,Disabled FROM Accounts LIMIT 0",
                "SELECT Id,ClientId,At FROM Visits LIMIT 0",
                "SELECT Id,ClientId,At,Actor,StartDate,EndDate,Amount,Note FROM MembershipHistory LIMIT 0",
                "SELECT Id,At,Actor,Action,Details FROM AuditLog LIMIT 0",
                "SELECT Key,Value FROM Settings LIMIT 0" })
            { check.CommandText = sql; check.ExecuteNonQuery(); }
            check.CommandText = "SELECT count(*) FROM Accounts WHERE Disabled=0 AND Role='Администратор'";
            if (Convert.ToInt64(check.ExecuteScalar()) == 0) throw new InvalidOperationException("В общей базе нет активного администратора.");
        }
        using var local = OpenConnection();
        var sessionSignature = AccountSignature(local);
        var remoteSignature = AccountSignature(incoming);
        var directory = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(DatabasePath))!, "sync-safety");
        Directory.CreateDirectory(directory);
        var safety = Path.Combine(directory, $"before-sync-{DateTime.Now:yyyyMMdd-HHmmss-fff}-{Guid.NewGuid():N}.db");
        Backup(safety);
        Execute(local,null,"ATTACH DATABASE $path AS incoming",("$path",Path.GetFullPath(path)));
        using(var tx=local.BeginTransaction())
        {
            // Recheck under SQLite's write lock: even another local process cannot lose a write.
            if(ReadVersion(local,tx)!=expectedLocal)throw new InvalidOperationException("Локальная база изменилась. Повторим синхронизацию.");
            foreach(var table in new[]{"Clients","Accounts","Visits","MembershipHistory"})
            foreach(var operation in new[]{"INSERT","UPDATE","DELETE"})
                Execute(local,tx,$"DROP TRIGGER IF EXISTS Sync_{table}_{operation}");
            var tables=new Dictionary<string,string> {
                ["Visits"]="Id,ClientId,At",
                ["MembershipHistory"]="Id,ClientId,At,Actor,StartDate,EndDate,Amount,Note",
                ["AuditLog"]="Id,At,Actor,Action,Details",
                ["Accounts"]="Id,Login,PasswordHash,Salt,Role,Disabled",
                ["Clients"]="Id,LastName,FirstName,MiddleName,BirthDate,MembershipNumber,PurchaseDate,ExpiryDate,IsBlacklisted,Phone,Deleted,FrozenUntil",
                ["SharedVersion"]="Id,Modified,Revision,SchemaVersion,IsBootstrap"
            };
            foreach(var entry in tables)
                Execute(local,tx,$"DELETE FROM main.{entry.Key}; INSERT INTO main.{entry.Key}({entry.Value}) SELECT {entry.Value} FROM incoming.{entry.Key}");
            Execute(local,tx,"DELETE FROM main.sqlite_sequence WHERE name='Clients'; INSERT INTO main.sqlite_sequence(name,seq) SELECT name,seq FROM incoming.sqlite_sequence WHERE name='Clients'");
            InstallSyncTriggers(local,tx);
            tx.Commit();
        }
        if (sessionSignature != remoteSignature) Session = null;
        foreach (var old in new DirectoryInfo(directory).GetFiles("before-sync-*.db").OrderByDescending(x=>x.CreationTimeUtc).Skip(10))
        { try { old.Delete(); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        return safety;
    }

    private void MarkSharedRestoration(long previousModified)
    {
        using var db=OpenConnection();
        Execute(db,null,"UPDATE SharedVersion SET Modified=MAX(Modified+1,$previous+1,$now),Revision=lower(hex(randomblob(16))),IsBootstrap=0 WHERE Id=1",
            ("$previous",previousModified),("$now",DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()));
    }

    private string? AccountSignature(SqliteConnection db)
    {
        if (Session is null) return null;
        using var command = db.CreateCommand();
        command.CommandText = "SELECT Login || '|' || Role || '|' || PasswordHash || '|' || Salt FROM Accounts WHERE Id=$id AND Disabled=0";
        command.Parameters.AddWithValue("$id",Session.Id);
        return command.ExecuteScalar() as string;
    }
}
