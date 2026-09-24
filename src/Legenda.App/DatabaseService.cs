using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace Legenda.App;

public sealed partial class DatabaseService
{
    private readonly string _connectionString;

    public string DatabasePath { get; }

    public DatabaseService(string? databasePath = null)
    {
        DatabasePath = databasePath ?? GetDefaultDatabasePath();
        var directory = Path.GetDirectoryName(DatabasePath);
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = DatabasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Cache = SqliteCacheMode.Shared,
            Pooling = false
        }.ToString();
    }

    public void Initialize()
    {
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS Clients (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                LastName TEXT NOT NULL,
                FirstName TEXT NOT NULL,
                MiddleName TEXT NOT NULL DEFAULT '',
                BirthDate TEXT NULL,
                MembershipNumber TEXT NOT NULL UNIQUE,
                PurchaseDate TEXT NOT NULL,
                ExpiryDate TEXT NOT NULL,
                IsBlacklisted INTEGER NOT NULL DEFAULT 0
            );
            CREATE INDEX IF NOT EXISTS IX_Clients_Name ON Clients(LastName, FirstName);
            CREATE INDEX IF NOT EXISTS IX_Clients_Blacklist ON Clients(IsBlacklisted);
            """;
        command.ExecuteNonQuery();

        // Existing installations gain a phone column without changing client records.
        var hasPhone = false;
        using (var schema = connection.CreateCommand())
        {
            schema.CommandText = "PRAGMA table_info(Clients);";
            using var columns = schema.ExecuteReader();
            while (columns.Read())
                if (columns.GetString(1) == "Phone") hasPhone = true;
        }
        if (!hasPhone)
        {
            using var migration = connection.CreateCommand();
            migration.CommandText = "ALTER TABLE Clients ADD COLUMN Phone TEXT NOT NULL DEFAULT '';";
            migration.ExecuteNonQuery();
        }

        using var countCommand = connection.CreateCommand();
        countCommand.CommandText = "SELECT COUNT(*) FROM Clients;";
        if (Convert.ToInt64(countCommand.ExecuteScalar(), CultureInfo.InvariantCulture) == 0)
            Seed(connection);
        InitializeV2(connection);
    }

    public IReadOnlyList<ClientRecord> LoadClients(bool includeDeleted = false)
    {
        var clients = new List<ClientRecord>();
        using var connection = OpenConnection();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT Id, LastName, FirstName, MiddleName, BirthDate,
                   MembershipNumber, PurchaseDate, ExpiryDate, IsBlacklisted, Phone, Deleted, FrozenUntil
            FROM Clients
            WHERE Deleted=0 OR $includeDeleted=1
            ORDER BY Id;
            """;
        command.Parameters.AddWithValue("$includeDeleted", includeDeleted ? 1 : 0);
        using var reader = command.ExecuteReader();
        while (reader.Read())
        {
            DateTimeOffset? birthDate = null;
            if (!reader.IsDBNull(4) && DateTimeOffset.TryParse(reader.GetString(4), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var parsed))
                birthDate = parsed;
            clients.Add(new ClientRecord(
                reader.GetString(1), reader.GetString(2), reader.GetString(5),
                reader.GetString(6), reader.GetString(7), reader.GetInt64(8) == 1,
                reader.GetString(3), birthDate, reader.GetInt64(0), reader.GetString(9), reader.GetBoolean(10),
                reader.IsDBNull(11) ? null : DateTime.ParseExact(reader.GetString(11), "yyyy-MM-dd", CultureInfo.InvariantCulture)));
        }
        return clients;
    }

    public ClientRecord InsertClient(ClientRecord client, decimal? payment = null)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO Clients
                (LastName, FirstName, MiddleName, BirthDate, MembershipNumber, PurchaseDate, ExpiryDate, IsBlacklisted, Phone)
            VALUES
                ($lastName, $firstName, $middleName, $birthDate, $number, $purchaseDate, $expiryDate, $blacklisted, $phone);
            SELECT last_insert_rowid();
            """;
        AddClientParameters(command, client);
        var id = Convert.ToInt64(command.ExecuteScalar(), CultureInfo.InvariantCulture);
        Execute(connection,transaction,"INSERT INTO MembershipHistory(ClientId,At,Actor,StartDate,EndDate,Amount,Note) VALUES($id,$at,$actor,$start,$end,$amount,$note)",
            ("$id",id),("$at",Stamp()),("$actor",Actor),("$start",client.PurchaseDate),("$end",client.ExpiryDate),
            ("$amount",payment.HasValue ? (object)(long)(payment.Value*100) : DBNull.Value),("$note",$"Первичный абонемент: {client.PurchaseDate} — {client.ExpiryDate}"));
        WriteAudit(connection,transaction,"Добавлен клиент", $"{client.LastName} {client.FirstName}, № {client.MembershipNumber}");
        MarkClientsChanged(connection, transaction);
        transaction.Commit();
        return client with { Id = id };
    }

    public void UpdateClient(ClientRecord client, decimal? payment = null)
    {
        var previous = LoadClients(true).FirstOrDefault(c => c.Id == client.Id);
        if (previous is null) throw new InvalidOperationException("Клиент не найден.");
        if (previous == client && !payment.HasValue) return;
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            UPDATE Clients SET
                LastName=$lastName, FirstName=$firstName, MiddleName=$middleName, BirthDate=$birthDate,
                MembershipNumber=$number, PurchaseDate=$purchaseDate, ExpiryDate=$expiryDate, IsBlacklisted=$blacklisted, Phone=$phone
            WHERE Id=$id;
            """;
        AddClientParameters(command, client);
        command.Parameters.AddWithValue("$id", client.Id);
        command.ExecuteNonQuery();
        if (previous is not null && (previous.PurchaseDate != client.PurchaseDate || previous.ExpiryDate != client.ExpiryDate || payment.HasValue))
            Execute(connection, transaction, """
                INSERT INTO MembershipHistory(ClientId,At,Actor,StartDate,EndDate,Amount,Note)
                VALUES($id,$at,$actor,$start,$end,$amount,$note)
                """, ("$id",client.Id),("$at",Stamp()),("$actor",Actor),("$start",client.PurchaseDate),
                ("$end",client.ExpiryDate),("$amount",payment.HasValue ? (object)(long)(payment.Value*100) : DBNull.Value),
                ("$note",$"Продление: {previous?.PurchaseDate} — {previous?.ExpiryDate} → {client.PurchaseDate} — {client.ExpiryDate}"));
        WriteAudit(connection, transaction, "Изменён клиент",
            $"ID {client.Id}: {previous?.LastName} {previous?.FirstName} {previous?.MiddleName} → {client.LastName} {client.FirstName} {client.MiddleName}; " +
            $"телефон: {previous?.DisplayPhone} → {client.DisplayPhone}; № {previous?.MembershipNumber} → {client.MembershipNumber}; " +
            $"период: {previous?.PurchaseDate} — {previous?.ExpiryDate} → {client.PurchaseDate} — {client.ExpiryDate}; " +
            $"чёрный список: {previous?.IsBlacklisted} → {client.IsBlacklisted}");
        MarkClientsChanged(connection, transaction);
        transaction.Commit();
    }

    public void DeleteClient(long id)
    {
        using var connection = OpenConnection();
        using var transaction = connection.BeginTransaction();
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "UPDATE Clients SET Deleted=1 WHERE Id=$id AND Deleted=0;";
        command.Parameters.AddWithValue("$id", id);
        if (command.ExecuteNonQuery() == 0) return;
        WriteAudit(connection, transaction, "Клиент в корзине", $"ID {id}");
        MarkClientsChanged(connection, transaction);
        transaction.Commit();
    }

    private SqliteConnection OpenConnection()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        using var pragma = connection.CreateCommand();
        pragma.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        return connection;
    }

    private static void AddClientParameters(SqliteCommand command, ClientRecord client)
    {
        command.Parameters.AddWithValue("$lastName", client.LastName);
        command.Parameters.AddWithValue("$firstName", client.FirstName);
        command.Parameters.AddWithValue("$middleName", client.MiddleName);
        command.Parameters.AddWithValue("$birthDate", client.BirthDate?.ToString("O", CultureInfo.InvariantCulture) is { } date ? date : DBNull.Value);
        command.Parameters.AddWithValue("$number", client.MembershipNumber);
        command.Parameters.AddWithValue("$purchaseDate", client.PurchaseDate);
        command.Parameters.AddWithValue("$expiryDate", client.ExpiryDate);
        command.Parameters.AddWithValue("$blacklisted", client.IsBlacklisted ? 1 : 0);
        command.Parameters.AddWithValue("$phone", client.Phone);
    }

    private static string GetDefaultDatabasePath()
        => ResolveDatabasePath(AppContext.BaseDirectory,
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData));

    private static string ResolveDatabasePath(string applicationDirectory, string localDataDirectory)
    {
        var target = Path.Combine(applicationDirectory, "legenda.db");
        if (!File.Exists(target))
        {
            Directory.CreateDirectory(applicationDirectory);
            var old = Path.Combine(localDataDirectory, "LegendaV2", "legenda.db");
            if (!File.Exists(old)) old = Path.Combine(localDataDirectory, "Legenda", "legenda.db");
            if (File.Exists(old))
            {
                // Publish the copy only after SQLite has finished, including any WAL data.
                var temporary = target + ".migration-" + Guid.NewGuid().ToString("N");
                try
                {
                    using (var source = new SqliteConnection(new SqliteConnectionStringBuilder
                        { DataSource=old, Mode=SqliteOpenMode.ReadOnly, Pooling=false }.ToString()))
                    using (var destination = new SqliteConnection(new SqliteConnectionStringBuilder
                        { DataSource=temporary, Pooling=false }.ToString()))
                    {
                        source.Open(); destination.Open(); source.BackupDatabase(destination);
                    }
                    File.Move(temporary, target, overwrite:false);
                }
                finally { if (File.Exists(temporary)) File.Delete(temporary); }
            }
        }
        return target;
    }

    private static void Seed(SqliteConnection connection)
    {
        var clients = new[]
        {
            new ClientRecord("Иванов", "Александр", "32174", "10.01.2026", "10.07.2026", false),
            new ClientRecord("Федоров", "Олег", "65489", "05.02.2026", "05.03.2026", false),
            new ClientRecord("Смирнов", "Даниил", "123 789", "12.11.2025", "12.11.2026", false),
            new ClientRecord("Орлова", "Мария", "51824", "03.02.2026", "03.08.2026", false),
            new ClientRecord("Ким", "Анна", "77102", "18.02.2026", "18.05.2026", true),
            new ClientRecord("Волков", "Илья", "29031", "01.12.2025", "01.06.2026", false),
            new ClientRecord("Соколова", "Елена", "44107", "09.01.2026", "09.07.2026", true)
        };
        foreach (var client in clients)
        {
            using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO Clients
                    (LastName, FirstName, MiddleName, BirthDate, MembershipNumber, PurchaseDate, ExpiryDate, IsBlacklisted)
                VALUES
                    ($lastName, $firstName, $middleName, $birthDate, $number, $purchaseDate, $expiryDate, $blacklisted);
                """;
            AddClientParameters(command, client);
            command.ExecuteNonQuery();
        }
    }
}
