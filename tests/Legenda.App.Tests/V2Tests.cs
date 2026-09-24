using System;
using System.IO;
using System.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Legenda.App;
using Xunit;

namespace Legenda.App.Tests;

public sealed class V2Tests : IDisposable
{
    private readonly string _folder=Path.Combine(Path.GetTempPath(),"LegendaV2Tests",Guid.NewGuid().ToString());
    private readonly DatabaseService _db;
    public V2Tests()
    {
        _db=new DatabaseService(Path.Combine(_folder,"test.db"));
        _db.Initialize();
        _db.CreateAccount("owner","OwnerTest2026!","Администратор");
        Assert.True(_db.Authenticate("owner","OwnerTest2026!"));
    }
    public void Dispose() => Directory.Delete(_folder,true);
    private ClientRecord Active(string number="87654321") =>
        _db.InsertClient(new ClientRecord("Проверка","Клиент",number,DateTime.Today.ToString("dd.MM.yyyy"),
            DateTime.Today.AddDays(30).ToString("dd.MM.yyyy"),false,Phone:"+7 (900) 123-45-67"),2500.50m);

    [Fact]
    public void PortableDatabaseCopiesLatestDataAndNeverOverwritesExistingFile()
    {
        var client=Active();
        var local=Path.Combine(_folder,"local");
        var app=Path.Combine(_folder,"portable");
        Directory.CreateDirectory(Path.Combine(local,"LegendaV2"));
        var legacy=Path.Combine(local,"LegendaV2","legenda.db");
        _db.Backup(legacy);
        var resolve=typeof(DatabaseService).GetMethod("ResolveDatabasePath",System.Reflection.BindingFlags.Static|System.Reflection.BindingFlags.NonPublic)!;
        string Resolve() => (string)resolve.Invoke(null,new object[]{app,local})!;
        var target=Resolve();
        Assert.Equal(Path.Combine(app,"legenda.db"),target);
        var portable=new DatabaseService(target);portable.Initialize();
        Assert.True(portable.Authenticate("owner","OwnerTest2026!"));
        Assert.Equal(client.Phone,portable.LoadClients().Single(c=>c.Id==client.Id).Phone);
        portable.UpdateClient(client with {FirstName="Перенесён"});
        Assert.Equal(target,Resolve());
        Assert.Equal("Перенесён",portable.LoadClients().Single(c=>c.Id==client.Id).FirstName);
        Assert.Equal(client.FirstName,new DatabaseService(legacy).LoadClients().Single(c=>c.Id==client.Id).FirstName);
    }
    [Fact]
    public void PasswordsAndRolesSurviveRestart()
    {
        _db.CreateAccount("operator","OperatorTest2026!","Оператор");
        var reopened=new DatabaseService(_db.DatabasePath);
        reopened.Initialize();
        Assert.False(reopened.Authenticate("owner","wrong"));
        Assert.True(reopened.Authenticate("operator","OperatorTest2026!"));
        Assert.Throws<InvalidOperationException>(()=>reopened.Accounts());
        Assert.Throws<InvalidOperationException>(()=>reopened.CreateAccount("intruder","Password2026!","Администратор"));
        Assert.Throws<InvalidOperationException>(()=>reopened.RestoreClient(1));
        Assert.Throws<InvalidOperationException>(()=>reopened.SetSetting("Sound","false"));
        reopened.ChangePassword("OperatorTest2026!","ChangedPass2026!");
        Assert.False(reopened.Authenticate("operator","OperatorTest2026!"));
        Assert.True(reopened.Authenticate("operator","ChangedPass2026!"));
        Assert.True(_db.Authenticate("owner","OwnerTest2026!"));
        _db.DisableAccount(_db.Accounts().Single(a=>a.Login=="operator"));
        Assert.False(reopened.Authenticate("operator","ChangedPass2026!"));
        Assert.Throws<InvalidOperationException>(()=>_db.DisableAccount(_db.Session!));
        var bytes=File.ReadAllBytes(_db.DatabasePath);
        Assert.DoesNotContain("OwnerTest2026!",System.Text.Encoding.UTF8.GetString(bytes));
    }
    [Fact]
    public void VisitsDeduplicateAndRejectBlockedExpiredFrozenDeleted()
    {
        var client=Active();
        Assert.True(_db.RecordVisit(client));Assert.False(_db.RecordVisit(client));
        Assert.Single(new DatabaseService(_db.DatabasePath).RecentVisits());
        _db.UpdateClient(client with {IsBlacklisted=true});
        Assert.Throws<InvalidOperationException>(()=>_db.RecordVisit(client));
        _db.UpdateClient(client with {ExpiryDate=DateTime.Today.AddDays(-1).ToString("dd.MM.yyyy")});
        Assert.Throws<InvalidOperationException>(()=>_db.RecordVisit(client));
        _db.UpdateClient(client);_db.FreezeClient(client.Id,7);
        Assert.Throws<InvalidOperationException>(()=>_db.RecordVisit(client));
        _db.UnfreezeClient(client.Id);_db.DeleteClient(client.Id);
        Assert.Throws<InvalidOperationException>(()=>_db.RecordVisit(client));
        Assert.Single(_db.RecentVisits());
    }
    [Fact]
    public void FreezeAndTrashPreserveDataAndHistory()
    {
        var client=Active();
        _db.FreezeClient(client.Id,7);
        var frozen=_db.LoadClients().Single(c=>c.Id==client.Id);
        Assert.True(frozen.IsFrozen);
        Assert.Equal(DateTime.Today.AddDays(37).ToString("dd.MM.yyyy"),frozen.ExpiryDate);
        Assert.Throws<InvalidOperationException>(()=>_db.FreezeClient(client.Id,7));
        _db.UnfreezeClient(client.Id);
        Assert.Equal(client.ExpiryDate,_db.LoadClients().Single(c=>c.Id==client.Id).ExpiryDate);
        Assert.False(_db.LoadClients().Single(c=>c.Id==client.Id).IsFrozen);
        _db.UpdateClient(client with {ExpiryDate=DateTime.Today.AddMonths(3).ToString("dd.MM.yyyy")},3000m);
        Assert.Contains(_db.History(client.Id),h=>h.Contains("3") && h.Contains("Продление"));
        var history=_db.History(client.Id).Count;
        _db.DeleteClient(client.Id);
        Assert.DoesNotContain(_db.LoadClients(),c=>c.Id==client.Id);
        Assert.True(_db.LoadClients(true).Single(c=>c.Id==client.Id).Deleted);
        _db.RestoreClient(client.Id);
        Assert.Equal(client.Phone,_db.LoadClients().Single(c=>c.Id==client.Id).Phone);
        Assert.Equal(history,_db.History(client.Id).Count);
        Assert.Contains(_db.AuditEntries(),s=>s.Contains("восстановлен"));
    }
    [Fact]
    public void BackupRestoreAndInvalidBackupLeaveSafeCopies()
    {
        var client=Active();
        var path=Path.Combine(_folder,"copy.db");_db.Backup(path);
        _db.UpdateClient(client with {FirstName="Изменён"});
        var invalid=Path.Combine(_folder,"invalid.db");File.WriteAllText(invalid,"not a database");
        Assert.ThrowsAny<Exception>(()=>_db.RestoreBackup(invalid));
        Assert.Equal("Изменён",_db.LoadClients().Single(c=>c.Id==client.Id).FirstName);
        var safety=_db.RestoreBackup(path);
        Assert.True(File.Exists(safety));
        Assert.Null(_db.Session);
        Assert.Equal(client.FirstName,_db.LoadClients().Single(c=>c.Id==client.Id).FirstName);
        Assert.True(_db.Authenticate("owner","OwnerTest2026!"));
    }
    [Fact]
    public void ExpiryWarningHasSevenDayBoundary()
    {
        var c=Active();
        Assert.True((c with {ExpiryDate=DateTime.Today.AddDays(7).ToString("dd.MM.yyyy")}).ExpiringSoon);
        Assert.False((c with {ExpiryDate=DateTime.Today.AddDays(8).ToString("dd.MM.yyyy")}).ExpiringSoon);
        Assert.False((c with {ExpiryDate=DateTime.Today.AddDays(-1).ToString("dd.MM.yyyy")}).ExpiringSoon);
    }
    [AvaloniaFact]
    public void RenderManagementTabs()
    {
        var displayedClient=Active();
        _db.RecordVisit(displayedClient);
        foreach(var width in new[]{540,420})
        {
            var setup=new ManagementWindow(_db,setup:true) { Width=width,Height=700 };
            setup.Show();Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("../../../../../artifacts/v2");
            using var preview=new RenderTargetBitmap(new PixelSize(width,700),new Vector(96,96));
            preview.Render(setup);preview.Save($"../../../../../artifacts/v2/first-admin-{width}.png");setup.Close();
        }
        foreach(var size in new[]{new PixelSize(1200,705),new PixelSize(720,450)})
        {
            var login=new MainWindow(new DemoAuthenticationService(),_db,new DisabledCameraScannerService(),promptOnClose:false) { Width=size.Width,Height=size.Height };
            login.FindControl<Button>("SetupAccountButton")!.IsVisible=true;
            login.Show();Dispatcher.UIThread.RunJobs();
            Directory.CreateDirectory("../../../../../artifacts/v2");
            using var preview=new RenderTargetBitmap(size,new Vector(96,96));
            preview.Render(login);preview.Save($"../../../../../artifacts/v2/setup-{size.Width}.png");login.Close();
        }
        var window=new ManagementWindow(_db);window.Show();Dispatcher.UIThread.RunJobs();
        Directory.CreateDirectory("../../../../../artifacts/v2");
        var root=(Grid)window.Content!;
        var sidebar=(Grid)root.Children[0];
        var navigation=(StackPanel)sidebar.Children[1];
        Assert.Equal(7,navigation.Children.Count);
        for(var i=0;i<navigation.Children.Count;i++)
        {
            var button=(Button)navigation.Children[i];
            button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
            Assert.Contains("active",button.Classes);
            using var image=new RenderTargetBitmap(new PixelSize(1020,700),new Vector(96,96));
            image.Render(window);image.Save($"../../../../../artifacts/v2/management-{i}.png");
        }
        window.Width=740;window.Height=500;
        ((Button)navigation.Children[0]).RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));Dispatcher.UIThread.RunJobs();
        using(var compact=new RenderTargetBitmap(new PixelSize(740,500),new Vector(96,96)))
        {compact.Render(window);compact.Save("../../../../../artifacts/v2/management-compact.png");}
        window.Close();
    }
}
