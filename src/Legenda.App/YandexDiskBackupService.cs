using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Legenda.App;

public sealed class YandexDiskBackupService
{
    public const string Folder = "disk:/Легенда/Резервные копии";
    private const string Api = "https://cloud-api.yandex.net/v1/disk";
    private static readonly HttpClient SharedClient = new() { Timeout = TimeSpan.FromMinutes(3) };
    private readonly HttpClient _http;
    private static readonly SemaphoreSlim BackupLock = new(1, 1);
    private static readonly Regex BackupName = new(@"^Legenda-backup-\d{8}-\d{6}-\d{3}\.db$", RegexOptions.CultureInvariant);

    public YandexDiskBackupService(HttpClient? http = null) => _http = http ?? SharedClient;

    public async Task CheckConnectionAsync(string token, CancellationToken cancellationToken = default)
    {
        try
        {
            using var response = await SendAsync(HttpMethod.Get, Api, token, cancellationToken);
            Check(response);
            await EnsureFoldersAsync(token, cancellationToken);
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Не удалось связаться с Яндекс Диском. Проверьте интернет и повторите попытку."); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("Яндекс Диск не ответил вовремя. Повторите попытку."); }
    }

    public async Task<string> BackupAsync(DatabaseService database, CancellationToken cancellationToken = default)
    {
        await BackupLock.WaitAsync(cancellationToken);
        var temporary = Path.Combine(Path.GetTempPath(), "Legenda-backup-" + Guid.NewGuid().ToString("N") + ".db");
        try
        {
            var token = database.ReadYandexDiskToken() ?? throw new InvalidOperationException("Подключите Яндекс Диск в разделе «Журналы и настройки → Настройки».");
            var revision = database.ClientChangeRevision;
            database.Backup(temporary);
            var name = "Legenda-backup-" + DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + ".db";
            await UploadAsync(token, temporary, name, cancellationToken);
            database.CompleteCloudBackup(revision, name);
            return name;
        }
        catch (HttpRequestException) { throw new InvalidOperationException("Не удалось связаться с Яндекс Диском. Проверьте интернет и повторите попытку."); }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested) { throw new InvalidOperationException("Яндекс Диск не ответил вовремя. Повторите попытку."); }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
            finally { BackupLock.Release(); }
        }
    }

    public async Task UploadAsync(string token, string source, string name, CancellationToken cancellationToken = default)
    {
        if (!BackupName.IsMatch(name)) throw new ArgumentException("Неверное имя резервной копии.", nameof(name));
        await EnsureFoldersAsync(token, cancellationToken);
        var path = Folder + "/" + name;
        using var linkResponse = await SendAsync(HttpMethod.Get, Api + "/resources/upload?path=" + Uri.EscapeDataString(path) + "&overwrite=false", token, cancellationToken);
        Check(linkResponse);
        using var link = JsonDocument.Parse(await linkResponse.Content.ReadAsStringAsync(cancellationToken));
        var uri = new Uri(link.RootElement.GetProperty("href").GetString()!);
        if (uri.Scheme != "https") throw new InvalidOperationException("Диск вернул небезопасный адрес загрузки.");
        // The signed upload URL does not need the OAuth token.
        await using (var input = File.OpenRead(source))
        using (var request = new HttpRequestMessage(HttpMethod.Put, uri) { Content = new StreamContent(input) })
        using (var uploaded = await _http.SendAsync(request, cancellationToken)) Check(uploaded);

        var files = await ListBackupsAsync(token, cancellationToken);
        if (!files.Any(x => x.Name == name))
            throw new InvalidOperationException("Диск ещё не подтвердил сохранение копии. Повторите попытку позже.");
        // Remove only our own timestamped backups, and only after the new upload is confirmed.
        foreach (var old in files.Where(x => x.Name != name).OrderByDescending(x => x.Created).ThenByDescending(x => x.Name, StringComparer.Ordinal).Skip(9))
        {
            using var deleted = await SendAsync(HttpMethod.Delete, Api + "/resources?path=" + Uri.EscapeDataString(Folder + "/" + old.Name) + "&permanently=true", token, cancellationToken);
            Check(deleted);
            if (deleted.StatusCode == HttpStatusCode.Accepted)
                await WaitForOperationAsync(deleted, token, cancellationToken);
        }
    }

    private async Task EnsureFoldersAsync(string token, CancellationToken ct)
    {
        foreach (var folder in new[] { "disk:/Легенда", Folder })
        {
            using var response = await SendAsync(HttpMethod.Put, Api + "/resources?path=" + Uri.EscapeDataString(folder), token, ct);
            if (response.StatusCode != HttpStatusCode.Conflict) Check(response);
        }
    }

    private sealed record BackupFile(string Name, DateTimeOffset Created);

    private async Task<List<BackupFile>> ListBackupsAsync(string token, CancellationToken ct)
    {
        var result = new List<BackupFile>();
        for (var offset = 0; ; offset += 100)
        {
            using var response = await SendAsync(HttpMethod.Get, Api + "/resources?path=" + Uri.EscapeDataString(Folder) + "&limit=100&offset=" + offset, token, ct);
            Check(response);
            using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            var items = json.RootElement.GetProperty("_embedded").GetProperty("items");
            foreach (var item in items.EnumerateArray())
            {
                var name = item.GetProperty("name").GetString()!;
                if (item.GetProperty("type").GetString() == "file" && BackupName.IsMatch(name))
                    result.Add(new(name, item.GetProperty("created").GetDateTimeOffset()));
            }
            if (items.GetArrayLength() < 100) return result.DistinctBy(x => x.Name).ToList();
        }
    }

    private async Task WaitForOperationAsync(HttpResponseMessage response, string token, CancellationToken ct)
    {
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
        var uri = new Uri(json.RootElement.GetProperty("href").GetString()!);
        if (uri.Scheme != "https" || uri.Host != "cloud-api.yandex.net") throw new InvalidOperationException("Неверный адрес операции Диска.");
        for (var attempt = 0; attempt < 30; attempt++)
        {
            await Task.Delay(1000, ct);
            using var status = await SendAsync(HttpMethod.Get, uri.ToString(), token, ct);
            Check(status);
            using var state = JsonDocument.Parse(await status.Content.ReadAsStringAsync(ct));
            var value = state.RootElement.GetProperty("status").GetString();
            if (value == "success") return;
            if (value == "failed") break;
        }
        throw new InvalidOperationException("Копия загружена, но удалить старые копии пока не удалось. Повторите резервное копирование позже.");
    }

    private async Task<HttpResponseMessage> SendAsync(HttpMethod method, string url, string token, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(method, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("OAuth", token.Trim());
        return await _http.SendAsync(request, ct);
    }

    private static void Check(HttpResponseMessage response)
    {
        if (response.IsSuccessStatusCode) return;
        throw new InvalidOperationException(response.StatusCode switch
        {
            HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden => "Яндекс Диск отклонил доступ. Проверьте токен и права чтения и записи файлов.",
            HttpStatusCode.InsufficientStorage => "На Яндекс Диске недостаточно свободного места.",
            _ => $"Яндекс Диск вернул ошибку {(int)response.StatusCode}. Повторите попытку позже."
        });
    }
}
