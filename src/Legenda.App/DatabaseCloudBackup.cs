using System;
using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace Legenda.App;

public sealed partial class DatabaseService
{
    public long ClientChangeRevision => long.TryParse(Setting("ClientChangeRevision", "0"), out var value) ? value : 0;
    public bool NeedsCloudBackup => ClientChangeRevision >
        (long.TryParse(Setting("CloudBackupRevision", "0"), out var value) ? value : 0);

    private static void MarkClientsChanged(SqliteConnection db, SqliteTransaction tx) => Execute(db, tx,
        "INSERT INTO Settings(Key,Value) VALUES('ClientChangeRevision','1') ON CONFLICT(Key) DO UPDATE SET Value=CAST(Value AS INTEGER)+1");

    public void CompleteCloudBackup(long revision, string fileName)
    {
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        Execute(db, tx, "INSERT INTO Settings(Key,Value) VALUES('CloudBackupRevision',$value) ON CONFLICT(Key) DO UPDATE SET Value=MAX(CAST(Value AS INTEGER),CAST(excluded.Value AS INTEGER))",
            ("$value", revision.ToString(CultureInfo.InvariantCulture)));
        Execute(db, tx, "INSERT INTO Settings(Key,Value) VALUES('LastCloudBackup',$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value",
            ("$value", DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss", CultureInfo.InvariantCulture)));
        WriteAudit(db, tx, "Резервная копия на Яндекс Диске", fileName);
        tx.Commit();
    }

    public string? ReadYandexDiskToken()
    {
        var value = Setting("YandexDiskToken", "");
        if (string.IsNullOrEmpty(value) || !OperatingSystem.IsWindows()) return null;
        try
        {
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value), null, DataProtectionScope.CurrentUser));
        }
        catch (Exception ex) when (ex is CryptographicException or FormatException) { return null; }
    }

    public void SaveYandexDiskToken(string token)
    {
        RequireAdministrator();
        if (!OperatingSystem.IsWindows()) throw new InvalidOperationException("Подключение Диска доступно в Windows.");
        token = token.Trim();
        var encrypted = token.Length == 0 ? "" : Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(token), null, DataProtectionScope.CurrentUser));
        using var db = OpenConnection();
        using var tx = db.BeginTransaction();
        Execute(db, tx, "INSERT INTO Settings(Key,Value) VALUES('YandexDiskToken',$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value", ("$value", encrypted));
        WriteAudit(db, tx, "Подключение Яндекс Диска", token.Length == 0 ? "Отключено" : "Токен обновлён");
        tx.Commit();
    }
}
