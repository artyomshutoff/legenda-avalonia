using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Legenda.App;

public interface ISharedDatabaseStore
{
    Task<IReadOnlyList<DatabaseVersion>> ListAsync(string token, CancellationToken ct);
    Task UploadAsync(string token, DatabaseVersion version, string file, CancellationToken ct);
    Task DownloadAsync(string token, DatabaseVersion version, string file, CancellationToken ct);
    Task DeleteAsync(string token, DatabaseVersion version, CancellationToken ct);
}

public sealed class CloudDatabaseSync
{
    private readonly DatabaseService _database;
    private readonly ISharedDatabaseStore _store;
    public string Status { get; private set; } = "Общая база: ожидает подключения.";
    public bool IsRunning { get; private set; }
    public Func<bool>? CanApplyRemote { get; set; }
    public event Action? StatusChanged;
    public event Action? DatabaseApplied;
    public CloudDatabaseSync(DatabaseService database, ISharedDatabaseStore store) { _database=database; _store=store; }
    private void Report(string value) { Status=value; StatusChanged?.Invoke(); }

    public async Task<bool> SynchronizeAsync(bool allowApply = false, CancellationToken cancellationToken = default)
    {
        if (IsRunning) return false;
        if (!allowApply && _database.Setting("SharedSyncEnabled","true") != "true") { Report("Синхронизация отключена."); return false; }
        var token = _database.ReadYandexDiskToken();
        if (token is null) { Report("Общая база: подключите Яндекс Диск в настройках."); return false; }
        IsRunning=true;
        var temporary = Path.Combine(Path.GetTempPath(), "Legenda-sync-"+Guid.NewGuid().ToString("N")+".db");
        var download = temporary+".download";
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            Report("Проверяем общую базу…");
            var versions = await _store.ListAsync(token, timeout.Token);
            var local = _database.CurrentDatabaseVersion;
            var bootstrapFromCloud=versions.Count>0 && (_database.IsBootstrapDatabase || !_database.HasAccounts);
            // Every changed local version is first preserved under its immutable unique name.
            if (_database.HasAccounts && !bootstrapFromCloud && !versions.Contains(local))
            {
                local = _database.CreateSharedSnapshot(temporary);
                Report("Отправляем изменения на Яндекс Диск…");
                await _store.UploadAsync(token, local, temporary, timeout.Token);
                versions = await _store.ListAsync(token, timeout.Token);
                if (!versions.Contains(local)) throw new InvalidOperationException("Диск ещё не подтвердил загрузку. Повторим синхронизацию.");
            }
            var latest = versions.OrderByDescending(x=>x).FirstOrDefault();
            if (latest is null) { Report("Общая база появится после создания администратора."); return false; }
            if (_database.CurrentDatabaseVersion != local) { Report("Есть новые изменения. Отправим их при следующей проверке."); return false; }
            var applied=false;
            if (latest.CompareTo(local)>0 || bootstrapFromCloud)
            {
                if (!allowApply && CanApplyRemote?.Invoke()==false)
                { Report("Доступна новая база. Она обновится после закрытия формы."); return false; }
                Report("Загружаем более новую общую базу…");
                await _store.DownloadAsync(token, latest, download, timeout.Token);
                timeout.Token.ThrowIfCancellationRequested();
                if (_database.CurrentDatabaseVersion != local || (!allowApply && CanApplyRemote?.Invoke()==false))
                { Report("Данные изменились во время загрузки. Повторим проверку."); return false; }
                _database.ApplySharedSnapshot(download, local, latest);
                applied=true;
                DatabaseApplied?.Invoke();
            }
            // New snapshots never overwrite one another, so concurrent uploads remain recoverable.
            foreach (var old in versions.OrderByDescending(x=>x).Skip(10))
                await _store.DeleteAsync(token, old, timeout.Token);
            _database.SetLocalSyncSetting("LastSharedSync",DateTime.Now.ToString("dd.MM.yyyy HH:mm:ss"));
            _database.MarkSharedInitialized();
            Report(applied ? "Общая база обновлена. Предыдущая копия — в sync-safety." : "Синхронизировано · "+DateTime.Now.ToString("HH:mm:ss"));
            return applied;
        }
        catch (OperationCanceledException) { Report("Синхронизация отложена. Локальные данные сохранены."); return false; }
        catch (System.Net.Http.HttpRequestException) { Report("Нет связи с Диском. Работаем локально, повторим автоматически."); return false; }
        catch (Exception ex) { Report("Синхронизация отложена: "+ex.Message); return false; }
        finally
        {
            foreach (var file in new[]{temporary,download})
            { try { if(File.Exists(file))File.Delete(file); } catch(IOException){} catch(UnauthorizedAccessException){} }
            IsRunning=false;
        }
    }
}
