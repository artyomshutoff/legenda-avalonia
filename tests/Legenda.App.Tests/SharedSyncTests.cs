using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Legenda.App;
using Microsoft.Data.Sqlite;
using Xunit;

namespace Legenda.App.Tests;

public sealed class SharedSyncTests : IDisposable
{
    private readonly string _folder=Path.Combine(Path.GetTempPath(),"LegendaSyncTests",Guid.NewGuid().ToString("N"));
    public void Dispose()=>Directory.Delete(_folder,true);
    private DatabaseService Create(string name)
    {
        var db=new DatabaseService(Path.Combine(_folder,name,"legenda.db"));db.Initialize();
        db.CreateAccount("owner","OwnerTest2026!","Администратор");db.Authenticate("owner","OwnerTest2026!");
        db.SaveYandexDiskToken("token-"+name);
        return db;
    }
    private static ClientRecord Add(DatabaseService db,string number="12345678")=>db.InsertClient(new ClientRecord("Общий","Клиент",number,
        DateTime.Today.ToString("dd.MM.yyyy"),DateTime.Today.AddMonths(3).ToString("dd.MM.yyyy"),false,Phone:"+7 (900) 123-45-67"));

    [Fact]
    public async Task OfflineChangesAreUploadedAfterRestartAndFreshComputerLoadsSharedData()
    {
        var store=new MemoryStore {Offline=true};var a=Create("a");Add(a);
        var version=a.CurrentDatabaseVersion;
        var first=new CloudDatabaseSync(a,store);
        await first.SynchronizeAsync();
        Assert.Empty(store.Files);Assert.Equal(version,a.CurrentDatabaseVersion);
        Assert.Contains("Нет связи",first.Status);
        store.Offline=false;
        var reopened=new DatabaseService(a.DatabasePath);reopened.Initialize();
        await new CloudDatabaseSync(reopened,store).SynchronizeAsync();
        Assert.Contains(version,store.Files.Keys);
        var b=Create("b");b.SetSetting("Sound","false");
        Assert.True(b.IsBootstrapDatabase);
        var second=new CloudDatabaseSync(b,store);
        Assert.True(await second.SynchronizeAsync());
        Assert.Equal(version,b.CurrentDatabaseVersion);
        Assert.Contains(b.LoadClients(),x=>x.MembershipNumber=="12345678");
        Assert.Equal("token-b",b.ReadYandexDiskToken());Assert.Equal("false",b.Setting("Sound","true"));
        Assert.Single(store.Files); // The fresh computer's sample database must never replace the club database.
        var uploads=store.Uploads;
        await second.SynchronizeAsync();Assert.Equal(uploads,store.Uploads);
        Assert.NotEmpty(Directory.GetFiles(Path.Combine(Path.GetDirectoryName(b.DatabasePath)!,"sync-safety"),"*.db"));
    }

    [Fact]
    public async Task VisitsAreSynchronizedButDoNotTriggerExitBackups()
    {
        var store=new MemoryStore();var a=Create("a");Add(a);
        var syncA=new CloudDatabaseSync(a,store);await syncA.SynchronizeAsync();
        var b=Create("b");var syncB=new CloudDatabaseSync(b,store);await syncB.SynchronizeAsync();
        Assert.False(b.NeedsCloudBackup);
        b.RecordVisit(b.LoadClients().Single(x=>x.MembershipNumber=="12345678"));
        Assert.False(b.NeedsCloudBackup);
        await syncB.SynchronizeAsync();await syncA.SynchronizeAsync();
        Assert.Single(a.RecentVisits());Assert.Equal(a.CurrentDatabaseVersion,b.CurrentDatabaseVersion);
        Assert.NotNull(a.Session); // Unchanged account credentials do not interrupt the administrator.
    }

    [Fact]
    public async Task IndependentEditsSelectNewerWholeDatabaseAndKeepBothVersions()
    {
        var store=new MemoryStore();var a=Create("a");var client=Add(a);
        var syncA=new CloudDatabaseSync(a,store);await syncA.SynchronizeAsync();
        var b=Create("b");var syncB=new CloudDatabaseSync(b,store);await syncB.SynchronizeAsync();
        a.UpdateClient(client with {FirstName="Первая версия"});
        var aVersion=a.CurrentDatabaseVersion;
        await Task.Delay(20);
        b.UpdateClient(b.LoadClients().Single(x=>x.Id==client.Id) with {FirstName="Новая версия"});
        var bVersion=b.CurrentDatabaseVersion;
        Assert.True(bVersion.CompareTo(aVersion)>0);
        await syncA.SynchronizeAsync();await syncB.SynchronizeAsync();await syncA.SynchronizeAsync();
        Assert.Equal("Новая версия",a.LoadClients().Single(x=>x.Id==client.Id).FirstName);
        Assert.Equal(bVersion,a.CurrentDatabaseVersion);
        Assert.Contains(aVersion,store.Files.Keys);Assert.Contains(bVersion,store.Files.Keys);
        var safety=Directory.GetFiles(Path.Combine(Path.GetDirectoryName(a.DatabasePath)!,"sync-safety"),"*.db").Single();
        Assert.Equal("Первая версия",new DatabaseService(safety).LoadClients().Single(x=>x.Id==client.Id).FirstName);
    }

    [Fact]
    public async Task LocalEditDuringDownloadPreventsReplacement()
    {
        var store=new MemoryStore();var a=Create("a");var client=Add(a);
        var syncA=new CloudDatabaseSync(a,store);await syncA.SynchronizeAsync();
        var b=Create("b");var syncB=new CloudDatabaseSync(b,store);await syncB.SynchronizeAsync();
        a.UpdateClient(client with {FirstName="На сервере"});await syncA.SynchronizeAsync();
        store.BeforeDownload=()=>Add(b,"87654321");
        Assert.False(await syncB.SynchronizeAsync());
        Assert.Contains(b.LoadClients(),x=>x.MembershipNumber=="87654321");
        Assert.Contains("Данные изменились",syncB.Status);
        await syncB.SynchronizeAsync();await syncA.SynchronizeAsync();
        Assert.Contains(a.LoadClients(),x=>x.MembershipNumber=="87654321");
    }

    [Fact]
    public async Task CorruptDownloadLeavesLocalDatabaseAndTokenUntouched()
    {
        var store=new MemoryStore();var a=Create("a");Add(a);await new CloudDatabaseSync(a,store).SynchronizeAsync();
        var b=Create("b");var before=b.CurrentDatabaseVersion;store.CorruptDownload=true;
        Assert.False(await new CloudDatabaseSync(b,store).SynchronizeAsync());
        Assert.Equal(before,b.CurrentDatabaseVersion);Assert.Equal("token-b",b.ReadYandexDiskToken());
        Assert.True(b.Authenticate("owner","OwnerTest2026!"));
    }

    [Fact]
    public async Task OpenFormDefersIncomingDatabaseAndSettingsDoNotCauseUploadLoops()
    {
        var store=new MemoryStore();var a=Create("a");Add(a);await new CloudDatabaseSync(a,store).SynchronizeAsync();
        var b=Create("b");var sync=new CloudDatabaseSync(b,store){CanApplyRemote=()=>false};
        Assert.False(await sync.SynchronizeAsync());Assert.True(b.IsBootstrapDatabase);
        sync.CanApplyRemote=()=>true;Assert.True(await sync.SynchronizeAsync());
        b.Authenticate("owner","OwnerTest2026!");b.SetSetting("ReturnSeconds","10");b.SaveYandexDiskToken("new-token");
        var uploads=store.Uploads;await sync.SynchronizeAsync();Assert.Equal(uploads,store.Uploads);
    }

    [Fact]
    public async Task SharedSnapshotsExcludeSettingsAndKeepAtMostTenVersions()
    {
        var store=new MemoryStore();var a=Create("a");var client=Add(a);var sync=new CloudDatabaseSync(a,store);
        for(var i=0;i<12;i++){a.UpdateClient(client with {FirstName="Клиент "+i});await sync.SynchronizeAsync();}
        Assert.Equal(10,store.Files.Count);
        Assert.Contains(a.CurrentDatabaseVersion,store.Files.Keys);
        var snapshot=Path.Combine(_folder,"cloud.db");File.WriteAllBytes(snapshot,store.Files[a.CurrentDatabaseVersion]);
        var remote=new DatabaseService(snapshot);
        Assert.Equal("",remote.Setting("YandexDiskToken",""));Assert.Null(remote.ReadYandexDiskToken());
        Assert.True(remote.Authenticate("owner","OwnerTest2026!"));
    }

    [Fact]
    public async Task ManualSyncWorksWhenAutomaticSyncIsDisabled()
    {
        var store=new MemoryStore();var db=Create("a");Add(db);db.SetSetting("SharedSyncEnabled","false");
        var sync=new CloudDatabaseSync(db,store);
        await sync.SynchronizeAsync();Assert.Empty(store.Files);
        await sync.SynchronizeAsync(allowApply:true);Assert.Single(store.Files);
    }

    [Fact]
    public void ApplyingSnapshotChecksRevisionAndRestoringBackupCreatesNewVersion()
    {
        var a=Create("a");var client=Add(a);var path=Path.Combine(_folder,"snapshot.db");
        var original=a.CreateSharedSnapshot(path);
        a.UpdateClient(client with {FirstName="Изменено"});var changed=a.CurrentDatabaseVersion;
        Assert.Throws<InvalidOperationException>(()=>a.ApplySharedSnapshot(path,original,original));
        Assert.Equal(changed,a.CurrentDatabaseVersion);
        a.RestoreBackup(path);
        Assert.True(a.CurrentDatabaseVersion.CompareTo(changed)>0);
        Assert.Equal("Клиент",a.LoadClients().Single(x=>x.Id==client.Id).FirstName);
    }

    private sealed class MemoryStore : ISharedDatabaseStore
    {
        public Dictionary<DatabaseVersion,byte[]> Files {get;}=new();
        public bool Offline {get;set;}
        public bool CorruptDownload {get;set;}
        public Action? BeforeDownload {get;set;}
        public int Uploads {get;private set;}
        public Task<IReadOnlyList<DatabaseVersion>> ListAsync(string token,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();if(Offline)throw new HttpRequestException("offline");
            return Task.FromResult<IReadOnlyList<DatabaseVersion>>(Files.Keys.ToList());
        }
        public Task UploadAsync(string token,DatabaseVersion version,string file,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();Files.TryAdd(version,File.ReadAllBytes(file));Uploads++;return Task.CompletedTask;}
        public Task DownloadAsync(string token,DatabaseVersion version,string file,CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();BeforeDownload?.Invoke();BeforeDownload=null;
            File.WriteAllBytes(file,CorruptDownload?new byte[]{1,2,3}:Files[version]);return Task.CompletedTask;
        }
        public Task DeleteAsync(string token,DatabaseVersion version,CancellationToken ct)
        {ct.ThrowIfCancellationRequested();Files.Remove(version);return Task.CompletedTask;}
    }
}
