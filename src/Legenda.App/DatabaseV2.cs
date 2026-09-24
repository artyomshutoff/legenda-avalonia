using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Legenda.App;

public sealed record AdminAccount(long Id, string Login, string Role);
public sealed record VisitRecord(string LastName, string FirstName, string DisplayPhone, string MembershipNumber, string VisitedAt);

public sealed partial class DatabaseService
{
    public AdminAccount? Session { get; private set; }
    public string Actor => Session?.Login ?? "Гостевой режим";
    public bool IsAdministrator => Session?.Role == "Администратор";
    private static string Stamp() => DateTimeOffset.Now.ToString("O", CultureInfo.InvariantCulture);
    private void InitializeV2(SqliteConnection connection)
    {
        var columns = new HashSet<string>();
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(Clients)";
            using var reader = schema.ExecuteReader();
            while (reader.Read()) columns.Add(reader.GetString(1));
        }
        if (!columns.Contains("Deleted")) Execute(connection,null,"ALTER TABLE Clients ADD COLUMN Deleted INTEGER NOT NULL DEFAULT 0");
        if (!columns.Contains("FrozenUntil")) Execute(connection,null,"ALTER TABLE Clients ADD COLUMN FrozenUntil TEXT NULL");
        Execute(connection,null,"""
            CREATE TABLE IF NOT EXISTS Visits(Id INTEGER PRIMARY KEY, ClientId INTEGER NOT NULL, At TEXT NOT NULL);
            CREATE INDEX IF NOT EXISTS IX_Visits_Client ON Visits(ClientId, At);
            CREATE TABLE IF NOT EXISTS MembershipHistory(Id INTEGER PRIMARY KEY,ClientId INTEGER NOT NULL,At TEXT NOT NULL,Actor TEXT NOT NULL,StartDate TEXT,EndDate TEXT,Amount INTEGER,Note TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS AuditLog(Id INTEGER PRIMARY KEY,At TEXT NOT NULL,Actor TEXT NOT NULL,Action TEXT NOT NULL,Details TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS Accounts(Id INTEGER PRIMARY KEY,Login TEXT NOT NULL UNIQUE COLLATE NOCASE,PasswordHash TEXT NOT NULL,Salt TEXT NOT NULL,Role TEXT NOT NULL,Disabled INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS Settings(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            """);
    }

    private static void Execute(SqliteConnection db, SqliteTransaction? tx, string sql, params (string Name,object Value)[] parameters)
    {
        using var cmd=db.CreateCommand(); cmd.Transaction=tx; cmd.CommandText=sql;
        foreach(var (name,value) in parameters) cmd.Parameters.AddWithValue(name,value);
        cmd.ExecuteNonQuery();
    }
    private void WriteAudit(SqliteConnection db, SqliteTransaction? tx,string action,string details) =>
        Execute(db,tx,"INSERT INTO AuditLog(At,Actor,Action,Details) VALUES($at,$actor,$action,$details)",
            ("$at",Stamp()),("$actor",Actor),("$action",action),("$details",details));
    public void Audit(string action,string details)
    {
        using var db=OpenConnection(); WriteAudit(db,null,action,details);
    }
    public void SignOut() => Session=null;
    public bool HasAccounts
    {
        get { using var db=OpenConnection(); using var cmd=db.CreateCommand();cmd.CommandText="SELECT count(*) FROM Accounts";return (long)cmd.ExecuteScalar()!>0; }
    }
    public IReadOnlyList<AdminAccount> Accounts()
    {
        RequireAdministrator();
        using var db=OpenConnection();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Id,Login,Role FROM Accounts WHERE Disabled=0 ORDER BY Login";
        using var r=cmd.ExecuteReader();var result=new List<AdminAccount>();
        while(r.Read()) result.Add(new(r.GetInt64(0),r.GetString(1),r.GetString(2)));
        return result;
    }
    private static byte[] Hash(string password,byte[] salt) => Rfc2898DeriveBytes.Pbkdf2(password,salt,210000,HashAlgorithmName.SHA256,32);
    private static void ValidatePassword(string password)
    {
        if(password.Length<10 || password.Length>128 || !password.Any(char.IsLetter) || !password.Any(char.IsDigit))
            throw new InvalidOperationException("Пароль: 10–128 символов, минимум одна буква и цифра.");
    }
    public void CreateAccount(string login,string password,string role)
    {
        if(HasAccounts) RequireAdministrator(); else role="Администратор";
        login=login.Trim();
        if(!DemoAuthenticationService.IsValidLogin(login)) throw new InvalidOperationException("Логин: 3–64 латинских символа, цифры, . _ @ -");
        ValidatePassword(password);
        if(role is not ("Администратор" or "Оператор")) throw new InvalidOperationException("Неверная роль");
        var salt=RandomNumberGenerator.GetBytes(16);
        using var db=OpenConnection();
        Execute(db,null,"INSERT INTO Accounts(Login,PasswordHash,Salt,Role) VALUES($login,$hash,$salt,$role)",
            ("$login",login),("$hash",Convert.ToBase64String(Hash(password,salt))),("$salt",Convert.ToBase64String(salt)),("$role",role));
        Audit("Создана учётная запись",login+" / "+role);
    }
    public bool Authenticate(string login,string password)
    {
        Session=null;
        using var db=OpenConnection();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT Id,Login,Role,PasswordHash,Salt FROM Accounts WHERE Login=$login AND Disabled=0";
        cmd.Parameters.AddWithValue("$login",login.Trim());
        using var r=cmd.ExecuteReader();
        if(!r.Read() || !CryptographicOperations.FixedTimeEquals(Hash(password,Convert.FromBase64String(r.GetString(4))),Convert.FromBase64String(r.GetString(3)))) return false;
        Session=new(r.GetInt64(0),r.GetString(1),r.GetString(2));
        r.Close();WriteAudit(db,null,"Вход администратора",Session.Login);
        return true;
    }
    public void ChangePassword(string oldPassword,string newPassword)
    {
        var account=Session ?? throw new InvalidOperationException("Войдите в учётную запись");
        // Preserve the session on an incorrect current password.
        if(!Authenticate(account.Login,oldPassword)) { Session=account;throw new InvalidOperationException("Текущий пароль неверен"); }
        ValidatePassword(newPassword);var salt=RandomNumberGenerator.GetBytes(16);
        using var db=OpenConnection();
        Execute(db,null,"UPDATE Accounts SET PasswordHash=$hash,Salt=$salt WHERE Id=$id",
            ("$id",account.Id),("$hash",Convert.ToBase64String(Hash(newPassword,salt))),("$salt",Convert.ToBase64String(salt)));
        Audit("Изменён пароль",account.Login);
    }
    public void DisableAccount(AdminAccount account)
    {
        RequireAdministrator();
        if(account.Id==Session!.Id) throw new InvalidOperationException("Нельзя отключить собственную учётную запись");
        using var db=OpenConnection();Execute(db,null,"UPDATE Accounts SET Disabled=1 WHERE Id=$id",("$id",account.Id));
        Audit("Отключена учётная запись",account.Login);
    }
    public void RequireAdministrator()
    {
        if(!IsAdministrator) throw new InvalidOperationException("Это действие доступно администратору");
    }
    public bool RecordVisit(ClientRecord client)
    {
        using var db=OpenConnection(); using var tx=db.BeginTransaction();
        using(var check=db.CreateCommand())
        {
            check.Transaction=tx;
            check.CommandText="SELECT Deleted,IsBlacklisted,FrozenUntil,PurchaseDate,ExpiryDate FROM Clients WHERE Id=$id";
            check.Parameters.AddWithValue("$id",client.Id);
            using var r=check.ExecuteReader();
            if(!r.Read() || r.GetBoolean(0) || r.GetBoolean(1) ||
                (!r.IsDBNull(2) && DateTime.ParseExact(r.GetString(2),"yyyy-MM-dd",CultureInfo.InvariantCulture)>DateTime.Today) ||
                !DateTime.TryParseExact(r.GetString(3),"dd.MM.yyyy",CultureInfo.InvariantCulture,DateTimeStyles.None,out var start) ||
                !DateTime.TryParseExact(r.GetString(4),"dd.MM.yyyy",CultureInfo.InvariantCulture,DateTimeStyles.None,out var end) ||
                start>DateTime.Today || end<DateTime.Today)
                throw new InvalidOperationException("Абонемент недействителен");
        }
        using var cmd=db.CreateCommand();cmd.Transaction=tx;
        cmd.CommandText="SELECT At FROM Visits WHERE ClientId=$id ORDER BY Id DESC LIMIT 1";cmd.Parameters.AddWithValue("$id",client.Id);
        if(cmd.ExecuteScalar() is string last && DateTimeOffset.Now-DateTimeOffset.Parse(last,CultureInfo.InvariantCulture)<TimeSpan.FromMinutes(1)) return false;
        Execute(db,tx,"INSERT INTO Visits(ClientId,At) VALUES($id,$at)",("$id",client.Id),("$at",Stamp()));
        tx.Commit();return true;
    }
    public IReadOnlyList<VisitRecord> RecentVisits(int limit=100)
    {
        using var db=OpenConnection();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT c.LastName,c.FirstName,c.Phone,c.MembershipNumber,v.At FROM Visits v JOIN Clients c ON c.Id=v.ClientId ORDER BY v.Id DESC LIMIT $limit";
        cmd.Parameters.AddWithValue("$limit",limit);
        using var r=cmd.ExecuteReader();var rows=new List<VisitRecord>();
        while(r.Read()) rows.Add(new(r.GetString(0),r.GetString(1),string.IsNullOrEmpty(r.GetString(2))?"—":r.GetString(2),r.GetString(3),
            DateTimeOffset.Parse(r.GetString(4),CultureInfo.InvariantCulture).ToLocalTime().ToString("dd.MM.yyyy HH:mm:ss")));
        return rows;
    }
    public void RestoreClient(long id)
    {
        RequireAdministrator();
        using var db=OpenConnection();Execute(db,null,"UPDATE Clients SET Deleted=0 WHERE Id=$id",("$id",id));
        Audit("Клиент восстановлен",$"ID {id}");
    }
    public void FreezeClient(long id,int days)
    {
        if(days<1 || days>90) throw new InvalidOperationException("Укажите от 1 до 90 дней");
        var client=LoadClients().First(c=>c.Id==id);
        if(client.IsFrozen) throw new InvalidOperationException("Абонемент уже заморожен");
        if(!DateTime.TryParseExact(client.ExpiryDate,"dd.MM.yyyy",CultureInfo.InvariantCulture,DateTimeStyles.None,out var expiry)
            || expiry.Date<DateTime.Today || DateTime.ParseExact(client.PurchaseDate,"dd.MM.yyyy",CultureInfo.InvariantCulture)>DateTime.Today)
            throw new InvalidOperationException("Заморозка доступна только для действующего абонемента");
        using var db=OpenConnection();using var tx=db.BeginTransaction();
        var until=DateTime.Today.AddDays(days);
        Execute(db,tx,"UPDATE Clients SET FrozenUntil=$until,ExpiryDate=$expiry WHERE Id=$id",
            ("$id",id),("$until",until.ToString("yyyy-MM-dd")),("$expiry",expiry.AddDays(days).ToString("dd.MM.yyyy")));
        Execute(db,tx,"INSERT INTO MembershipHistory(ClientId,At,Actor,StartDate,EndDate,Note) VALUES($id,$at,$actor,$start,$end,$note)",
            ("$id",id),("$at",Stamp()),("$actor",Actor),("$start",client.PurchaseDate),("$end",expiry.AddDays(days).ToString("dd.MM.yyyy")),
            ("$note",$"Заморозка на {days} дн., до {until:dd.MM.yyyy}"));
        WriteAudit(db,tx,"Заморозка",$"№ {client.MembershipNumber}, {days} дн.");tx.Commit();
    }
    public void UnfreezeClient(long id)
    {
        var client=LoadClients().First(c=>c.Id==id);
        if(!client.IsFrozen) return;
        var unused=(client.FrozenUntil!.Value.Date-DateTime.Today).Days;
        var expiry=DateTime.ParseExact(client.ExpiryDate,"dd.MM.yyyy",CultureInfo.InvariantCulture).AddDays(-unused);
        using var db=OpenConnection();using var tx=db.BeginTransaction();
        Execute(db,tx,"UPDATE Clients SET FrozenUntil=NULL,ExpiryDate=$expiry WHERE Id=$id",("$id",id),("$expiry",expiry.ToString("dd.MM.yyyy")));
        Execute(db,tx,"INSERT INTO MembershipHistory(ClientId,At,Actor,StartDate,EndDate,Note) VALUES($id,$at,$actor,$start,$end,$note)",
            ("$id",id),("$at",Stamp()),("$actor",Actor),("$start",client.PurchaseDate),("$end",expiry.ToString("dd.MM.yyyy")),
            ("$note",$"Досрочная разморозка: вычтено {unused} неиспользованных дней"));
        WriteAudit(db,tx,"Разморозка",$"№ {client.MembershipNumber}");tx.Commit();
    }
    public IReadOnlyList<string> History(long? clientId=null)
    {
        using var db=OpenConnection();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT h.At,h.Actor,c.LastName,c.FirstName,h.Note,h.Amount FROM MembershipHistory h LEFT JOIN Clients c ON c.Id=h.ClientId WHERE $id IS NULL OR h.ClientId=$id ORDER BY h.Id DESC LIMIT 500";
        cmd.Parameters.AddWithValue("$id",clientId.HasValue ? (object)clientId.Value : DBNull.Value);
        using var r=cmd.ExecuteReader();var result=new List<string>();
        while(r.Read()) result.Add($"{DateTimeOffset.Parse(r.GetString(0)).ToLocalTime():dd.MM.yyyy HH:mm} · {r.GetString(1)}\n{r.GetValue(2)} {r.GetValue(3)} · {r.GetString(4)}\nОплата: {(r.IsDBNull(5)?"не указана":(r.GetInt64(5)/100m).ToString("N2")+" ₽")}");
        return result;
    }
    public IReadOnlyList<string> AuditEntries()
    {
        RequireAdministrator();using var db=OpenConnection();using var cmd=db.CreateCommand();
        cmd.CommandText="SELECT At,Actor,Action,Details FROM AuditLog ORDER BY Id DESC LIMIT 500";
        using var r=cmd.ExecuteReader();var result=new List<string>();
        while(r.Read()) result.Add($"{DateTimeOffset.Parse(r.GetString(0)).ToLocalTime():dd.MM.yyyy HH:mm:ss} · {r.GetString(1)}\n{r.GetString(2)}: {r.GetString(3)}");
        return result;
    }
    public string Setting(string key,string fallback)
    {
        using var db=OpenConnection();using var cmd=db.CreateCommand();cmd.CommandText="SELECT Value FROM Settings WHERE Key=$key";
        cmd.Parameters.AddWithValue("$key",key);return cmd.ExecuteScalar() as string ?? fallback;
    }
    public void SetSetting(string key,string value)
    {
        RequireAdministrator();using var db=OpenConnection();
        Execute(db,null,"INSERT INTO Settings(Key,Value) VALUES($key,$value) ON CONFLICT(Key) DO UPDATE SET Value=excluded.Value",("$key",key),("$value",value));
        Audit("Настройка",key+"="+value);
    }
    public void Backup(string destination)
    {
        using var db=OpenConnection();using var target=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=destination,Pooling=false}.ToString());
        target.Open();db.BackupDatabase(target);
    }
    public string RestoreBackup(string source)
    {
        RequireAdministrator();
        if(Path.GetFullPath(source).Equals(Path.GetFullPath(DatabasePath),StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Выберите файл резервной копии");
        using var restored=new SqliteConnection(new SqliteConnectionStringBuilder{DataSource=source,Mode=SqliteOpenMode.ReadOnly,Pooling=false}.ToString());
        restored.Open();
        using(var check=restored.CreateCommand())
        {
            check.CommandText="PRAGMA integrity_check";
            if(check.ExecuteScalar() as string!="ok") throw new InvalidOperationException("Повреждённая база");
            check.CommandText="SELECT Id,LastName,FirstName,MiddleName,BirthDate,MembershipNumber,PurchaseDate,ExpiryDate,IsBlacklisted,Phone,Deleted,FrozenUntil FROM Clients LIMIT 0";
            check.ExecuteNonQuery();
            check.CommandText="SELECT count(*) FROM Accounts WHERE Disabled=0 AND Role='Администратор'";
            if(Convert.ToInt64(check.ExecuteScalar())==0) throw new InvalidOperationException("В копии нет активного администратора");
            check.CommandText="SELECT * FROM Visits LIMIT 0";check.ExecuteNonQuery();
            check.CommandText="SELECT * FROM MembershipHistory LIMIT 0";check.ExecuteNonQuery();
            check.CommandText="SELECT At,Actor,Action,Details FROM AuditLog LIMIT 0";check.ExecuteNonQuery();
            check.CommandText="SELECT Key,Value FROM Settings LIMIT 0";check.ExecuteNonQuery();
        }
        var safety=Path.Combine(Path.GetDirectoryName(DatabasePath)!,"before-restore-"+DateTime.Now.ToString("yyyyMMdd-HHmmss-fff")+".db");
        Backup(safety);
        using(var destination=OpenConnection()) restored.BackupDatabase(destination);
        Initialize();Audit("Восстановлена база",Path.GetFileName(source));Session=null;
        return safety;
    }
}
