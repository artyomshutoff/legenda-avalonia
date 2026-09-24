using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.LogicalTree;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Legenda.App;
using Xunit;

namespace Legenda.App.Tests;

public sealed class CloudBackupTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "LegendaCloudTests", Guid.NewGuid().ToString("N"));
    private readonly DatabaseService _db;
    public CloudBackupTests()
    {
        _db = new DatabaseService(Path.Combine(_folder, "test.db"));
        _db.Initialize();
        _db.CreateAccount("owner", "OwnerTest2026!", "Администратор");
        _db.Authenticate("owner", "OwnerTest2026!");
    }
    public void Dispose() => Directory.Delete(_folder, true);
    private ClientRecord AddClient() => _db.InsertClient(new ClientRecord("Тестов", "Клиент", "12345678",
        DateTime.Today.ToString("dd.MM.yyyy"), DateTime.Today.AddDays(30).ToString("dd.MM.yyyy"), false));

    [Fact]
    public void ClientChangesPersistButVisitsAndSettingsDoNotRequestBackup()
    {
        Assert.False(_db.NeedsCloudBackup);
        var client = AddClient();
        var revision = _db.ClientChangeRevision;
        Assert.True(_db.NeedsCloudBackup);
        _db.CompleteCloudBackup(revision, "test.db");
        _db.RecordVisit(client);
        _db.SetSetting("Sound", "false");
        _db.UpdateClient(client);
        Assert.Equal(revision, _db.ClientChangeRevision);
        Assert.False(_db.NeedsCloudBackup);
        _db.UpdateClient(client with { Phone = "+7 (900) 111-22-33" });
        Assert.True(_db.NeedsCloudBackup);
        Assert.True(new DatabaseService(_db.DatabasePath).NeedsCloudBackup);
        _db.CompleteCloudBackup(revision, "older-snapshot.db");
        Assert.True(_db.NeedsCloudBackup);
        _db.FreezeClient(client.Id, 3);
        _db.UnfreezeClient(client.Id);
        _db.DeleteClient(client.Id);
        _db.RestoreClient(client.Id);
        Assert.Equal(revision + 5, _db.ClientChangeRevision);
    }

    [Fact]
    public void TokenIsEncryptedAndNeverWrittenToAudit()
    {
        const string token = "secret-token-for-test-do-not-log";
        _db.SaveYandexDiskToken(token);
        Assert.Equal(token, _db.ReadYandexDiskToken());
        Assert.DoesNotContain(token, _db.Setting("YandexDiskToken", ""));
        Assert.DoesNotContain(_db.AuditEntries(), x => x.Contains(token));
        Assert.False(_db.NeedsCloudBackup);
        _db.SaveYandexDiskToken("");
        Assert.Null(_db.ReadYandexDiskToken());
    }

    [Fact]
    public async Task UploadKeepsTenBackupsAndDoesNotDeleteUnrelatedFiles()
    {
        using var handler = new DiskHandler();
        using var http = new HttpClient(handler);
        var source = Path.Combine(_folder, "snapshot.db");
        _db.Backup(source);
        const string name = "Legenda-backup-20000101-000000-000.db";
        await new YandexDiskBackupService(http).UploadAsync("test-token", source, name);
        Assert.True(handler.Uploaded);
        Assert.Equal(3, handler.Deleted.Count);
        Assert.Equal(new[] { "Legenda-backup-20250103-000000-000.db", "Legenda-backup-20250102-000000-000.db", "Legenda-backup-20250101-000000-000.db" }, handler.Deleted);
        Assert.DoesNotContain(name, handler.Deleted);
        Assert.Equal(File.ReadAllBytes(source), handler.UploadBytes);
    }

    [Fact]
    public async Task FailedUploadDoesNotDeleteOrClearPendingChanges()
    {
        AddClient(); _db.SaveYandexDiskToken("test-token");
        using var handler = new DiskHandler { FailUpload = true };
        using var http = new HttpClient(handler);
        await Assert.ThrowsAsync<InvalidOperationException>(() => new YandexDiskBackupService(http).BackupAsync(_db));
        Assert.Empty(handler.Deleted);
        Assert.True(_db.NeedsCloudBackup);
    }

    [Fact]
    public async Task SuccessfulBackupClearsPendingStateAndHasDatedName()
    {
        AddClient(); _db.SaveYandexDiskToken("test-token");
        using var handler = new DiskHandler();
        using var http = new HttpClient(handler);
        var name = await new YandexDiskBackupService(http).BackupAsync(_db);
        Assert.Matches(@"^Legenda-backup-\d{8}-\d{6}-\d{3}\.db$", name);
        Assert.False(_db.NeedsCloudBackup);
        var restoredPath = Path.Combine(_folder, "snapshot.db");
        File.WriteAllBytes(restoredPath, handler.UploadBytes!);
        var snapshot = new DatabaseService(restoredPath);
        Assert.Contains(snapshot.LoadClients(), x => x.MembershipNumber == "12345678");
    }

    [AvaloniaFact]
    public async Task SaveAndExitCompletesUploadBeforeClosingDialog()
    {
        AddClient(); _db.SaveYandexDiskToken("test-token");
        using var handler = new DiskHandler();
        using var http = new HttpClient(handler);
        var owner = new Window(); owner.Show();
        var prompt = new BackupOnExitWindow(_db, new YandexDiskBackupService(http));
        var result = prompt.ShowDialog<bool>(owner);
        RenderPrompt(prompt, "backup-ready");
        prompt.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == "SaveBackupButton")
            .RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
        Assert.True(await result.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(handler.Uploaded);
        Assert.False(_db.NeedsCloudBackup);
        Assert.False(prompt.IsVisible);
        owner.Close();
    }

    [AvaloniaFact]
    public async Task CloseOffersBackupOnlyForClientChangesAndSupportsCancelAndSkip()
    {
        var client = AddClient();
        _db.CompleteCloudBackup(_db.ClientChangeRevision, "previous.db");
        _db.RecordVisit(client);
        var clean = new MainWindow(new DemoAuthenticationService(), _db, new DisabledCameraScannerService());
        clean.Show(); clean.Close();
        Assert.False(clean.IsVisible);
        _db.UpdateClient(client with { FirstName = "Новое имя" });
        var window = new MainWindow(new DemoAuthenticationService(), _db, new DisabledCameraScannerService());
        window.Show(); window.Close();
        await Task.Delay(20);
        var prompt = Assert.Single(window.OwnedWindows.OfType<BackupOnExitWindow>());
        Assert.True(window.IsVisible);
        Button Button(string name) => prompt.GetLogicalDescendants().OfType<Button>().Single(b => b.Name == name);
        Button("SaveBackupButton").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Task.Delay(20);
        Assert.True(prompt.IsVisible);
        Assert.Contains("Подключите", prompt.GetLogicalDescendants().OfType<TextBlock>().Single(t => t.Name == "BackupStatus").Text);
        RenderPrompt(prompt, "backup-on-exit");
        Button("CancelExitButton").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Task.Delay(20);
        Assert.True(window.IsVisible);
        window.Close(); await Task.Delay(20);
        prompt = Assert.Single(window.OwnedWindows.OfType<BackupOnExitWindow>());
        Button("SkipBackupButton").RaiseEvent(new RoutedEventArgs(Avalonia.Controls.Button.ClickEvent));
        await Task.Delay(20);
        Assert.False(window.IsVisible);
        Assert.True(_db.NeedsCloudBackup);
    }

    private static void RenderPrompt(Window window, string name)
    {
        Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory("../../../../../artifacts/v2");
        using var image = new RenderTargetBitmap(new PixelSize((int)Math.Ceiling(window.ClientSize.Width), (int)Math.Ceiling(window.ClientSize.Height)), new Vector(96,96));
        image.Render(window); image.Save($"../../../../../artifacts/v2/{name}.png");
    }

    private sealed class DiskHandler : HttpMessageHandler
    {
        public bool FailUpload { get; init; }
        public bool Uploaded { get; private set; }
        public byte[]? UploadBytes { get; private set; }
        public List<string> Deleted { get; } = new();
        private string _newName = "";
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.RequestUri!.Host == "upload.yandex.net")
            {
                Assert.Null(request.Headers.Authorization);
                Assert.Equal(HttpMethod.Put, request.Method);
                UploadBytes = await request.Content!.ReadAsByteArrayAsync(cancellationToken);
                Uploaded = !FailUpload;
                return new(FailUpload ? HttpStatusCode.InsufficientStorage : HttpStatusCode.Created);
            }
            Assert.Equal("OAuth", request.Headers.Authorization?.Scheme);
            Assert.Equal("test-token", request.Headers.Authorization?.Parameter);
            var query = request.RequestUri.Query.TrimStart('?').Split('&').Select(x => x.Split('=', 2)).ToDictionary(x => x[0], x => Uri.UnescapeDataString(x[1]));
            if (request.RequestUri.AbsolutePath.EndsWith("/upload"))
            {
                _newName = query["path"].Split('/').Last();
                Assert.Equal("false", query["overwrite"]);
                return Json(new { href = "https://upload.yandex.net/signed", method = "PUT" });
            }
            if (request.Method == HttpMethod.Put) return new(HttpStatusCode.Conflict);
            if (request.Method == HttpMethod.Delete)
            {
                Assert.True(Uploaded); Assert.Equal("true", query["permanently"]);
                Deleted.Add(query["path"].Split('/').Last()); return new(HttpStatusCode.NoContent);
            }
            var items = Enumerable.Range(1, 12).Select(day => new { name = $"Legenda-backup-202501{day:00}-000000-000.db", type = "file", created = $"2025-01-{day:00}T00:00:00Z" }).ToList();
            items.Add(new { name = "Important-client-data.db", type = "file", created = "2020-01-01T00:00:00Z" });
            if (Uploaded) items.Add(new { name = _newName, type = "file", created = "2026-01-01T00:00:00Z" });
            return Json(new { _embedded = new { items } });
        }
        private static HttpResponseMessage Json(object value) => new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value), Encoding.UTF8, "application/json") };
    }
}
