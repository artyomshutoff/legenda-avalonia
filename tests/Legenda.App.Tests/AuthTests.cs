using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Interactivity;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Legenda.App;
using Xunit;
[assembly: AvaloniaTestApplication(typeof(Legenda.App.Tests.TestApp))]
namespace Legenda.App.Tests;
public class TestApp
{
 public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>().UseSkia().WithInterFont().UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = false });
}
public class AuthTests
{
 [AvaloniaFact]
 public void WindowTitleFollowsNavigationAndDialogs()
 {
  var w = CreateWindow(); w.Show();
  void Navigate(string method, object? sender = null) => typeof(MainWindow).GetMethod(method,
   System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
   .Invoke(w, method == "OpenAdmin" ? null : new object?[] { sender, new RoutedEventArgs() });
  try
  {
   Assert.Equal("Легенда — Авторизация", w.Title);
   Click(Get<Button>(w,"GuestButton"));
   Assert.Equal("Легенда — Гостевой режим",w.Title);
   Navigate("SignOut");
   Assert.Equal("Легенда — Авторизация",w.Title);
   Navigate("OpenAdmin");
   Assert.Equal("Легенда — Панель администратора",w.Title);
   Navigate("OpenClients");
   Assert.Equal("Легенда — Все клиенты",w.Title);
   Navigate("OpenBlacklist");
   Assert.Equal("Легенда — Черный список",w.Title);
   var client = new ClientRecord("Иванов","Иван","12345678","01.01.2026","01.01.2027",false);
   Navigate("ShowClientQr",new MenuItem {Tag=client});
   Assert.Equal("Легенда — QR-код клиента",w.Title);
   Navigate("CloseClientQr");
   Assert.Equal("Легенда — Черный список",w.Title);
   Navigate("CloseOverlay");
   Assert.Equal("Легенда — Панель администратора",w.Title);
   Navigate("OpenAddClient");
   Assert.Equal("Легенда — Добавление клиента",w.Title);
   Navigate("EditClient",new MenuItem {Tag=client});
   Assert.Equal("Легенда — Изменение данных клиента",w.Title);
   Navigate("ExtendMembership",new MenuItem {Tag=client});
   Assert.Equal("Легенда — Продление абонемента",w.Title);
   Navigate("CloseOverlay");
   Assert.Equal("Легенда — Панель администратора",w.Title);
  }
  finally {w.Close();}
 }
 [AvaloniaFact]
 public void RenewalUsesSelectedStartAndOrdinaryEditKeepsDates()
 {
  var folder = Path.Combine(Path.GetTempPath(), "LegendaTests", Path.GetRandomFileName());
  var db = new DatabaseService(Path.Combine(folder,"test.db"));
  var w = new MainWindow(new DemoAuthenticationService(),db,new DisabledCameraScannerService());
  try
  {
   w.Show();
   var client = db.LoadClients().First();
   void Open(string method, ClientRecord c) => typeof(MainWindow).GetMethod(method,
    System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
    .Invoke(w, new object[] {new MenuItem { Tag=c }, new RoutedEventArgs()});
   Open("EditClient", client);
   Get<ComboBox>(w,"MembershipPeriod").SelectedIndex=4;
   Click(Get<Button>(w,"SaveClientButton"));
   var unchanged=db.LoadClients().Single(c=>c.Id==client.Id);
   Assert.Equal(client.PurchaseDate,unchanged.PurchaseDate);
   Assert.Equal(client.ExpiryDate,unchanged.ExpiryDate);
   // Use the current UI record so ReplaceClient updates the same record.
   var list=Get<ItemsControl>(w,"ClientsList").ItemsSource!.Cast<ClientRecord>();
   Open("ExtendMembership",list.Single(c=>c.Id==client.Id));
   Assert.True(Get<CheckBox>(w,"RenewMembership").IsChecked);
   Get<DatePicker>(w,"MembershipStartDate").SelectedDate=new DateTimeOffset(2028,1,31,0,0,0,TimeSpan.Zero);
   Get<ComboBox>(w,"MembershipPeriod").SelectedIndex=0;
   Dispatcher.UIThread.RunJobs();
   using var image=new RenderTargetBitmap(new PixelSize(1200,705),new Vector(96,96));
   image.Render(w);
   Directory.CreateDirectory("../../../../../artifacts");
   image.Save("../../../../../artifacts/renew-membership.png");
   Click(Get<Button>(w,"SaveClientButton"));
   var renewed=new DatabaseService(db.DatabasePath).LoadClients().Single(c=>c.Id==client.Id);
   Assert.Equal("31.01.2028",renewed.PurchaseDate);
   Assert.Equal("29.02.2028",renewed.ExpiryDate);
   Assert.Equal(client.MembershipNumber,renewed.MembershipNumber);
  }
  finally {w.Close();Directory.Delete(folder,true);}
 }
 [Theory]
 [InlineData("+7 (999) 123-45-67", true)]
 [InlineData("89991234567", true)]
 [InlineData("+79991234567", true)]
 [InlineData("8 999 123 45 67", true)]
 [InlineData("", false)]
 [InlineData("+7999123456", false)]
 [InlineData("+799912345678", false)]
 [InlineData("89991234567abc", false)]
 [InlineData("+19991234567", false)]
 [InlineData("8(9991234567", false)]
 public void PhoneFormatValidation(string input, bool valid)
 {
  Assert.Equal(valid, PhoneNumber.TryNormalize(input, out var phone));
  if (valid) Assert.Equal("+7 (999) 123-45-67", phone);
 }

 [Fact]
 public void ExistingDatabaseMigratesPhoneAndPersistsChanges()
 {
  var folder = Path.Combine(Path.GetTempPath(), "LegendaTests", Path.GetRandomFileName());
  var path = Path.Combine(folder, "test.db");
  try
  {
   var database = new DatabaseService(path);
   database.Initialize();
   var first = database.LoadClients().First();
   using (var connection = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={path};Pooling=False"))
   {
    connection.Open();
    using var command = connection.CreateCommand();
    command.CommandText = "ALTER TABLE Clients DROP COLUMN Phone";
    command.ExecuteNonQuery();
   }
   database.Initialize();
   database.Initialize();
   var restored = database.LoadClients().Single(c => c.Id == first.Id);
   Assert.Equal(first.MembershipNumber, restored.MembershipNumber);
   Assert.Equal("", restored.Phone);
   database.UpdateClient(restored with { Phone = "+7 (999) 123-45-67" });
   Assert.Equal("+7 (999) 123-45-67", new DatabaseService(path).LoadClients().Single(c => c.Id == first.Id).Phone);
  }
  finally { Directory.Delete(folder, true); }
 }

 [AvaloniaFact]
 public void ClientFormRejectsInvalidPhoneAndSavesNormalizedPhone()
 {
  var folder = Path.Combine(Path.GetTempPath(), "LegendaTests", Path.GetRandomFileName());
  var database = new DatabaseService(Path.Combine(folder, "test.db"));
  var w = new MainWindow(new DemoAuthenticationService(), database, new DisabledCameraScannerService());
  try
  {
   w.Show();
   typeof(MainWindow).GetMethod("OpenAddClient", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
    .Invoke(w, new object?[] { null, new RoutedEventArgs() });
   Get<TextBox>(w,"NewLastName").Text = "Тестов";
   Get<TextBox>(w,"NewFirstName").Text = "Клиент";
   var count = database.LoadClients().Count;
   Get<TextBox>(w,"NewPhone").Text = "123";
   Click(Get<Button>(w,"SaveClientButton"));
   Assert.True(Get<TextBlock>(w,"PhoneError").IsVisible);
   Assert.Equal(count, database.LoadClients().Count);
   Dispatcher.UIThread.RunJobs();
   using var image = new RenderTargetBitmap(new PixelSize(1200,705), new Vector(96,96));
   image.Render(w);
   Directory.CreateDirectory("../../../../../artifacts");
   image.Save("../../../../../artifacts/client-phone.png");
   Get<TextBox>(w,"NewPhone").Text = "89991234567";
   Click(Get<Button>(w,"SaveClientButton"));
   Assert.Equal("+7 (999) 123-45-67", database.LoadClients().Single(c => c.LastName == "Тестов").Phone);
   Assert.Matches("^[1-9][0-9]{7}$", database.LoadClients().Single(c => c.LastName == "Тестов").MembershipNumber);
   Assert.Equal(count + 1, database.LoadClients().Count);
  }
  finally { w.Close(); Directory.Delete(folder, true); }
 }
 private static T Get<T>(MainWindow w, string name) where T : Control => w.FindControl<T>(name)!;
 private static void Click(Button b) => b.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
 private static MainWindow CreateWindow(double width = 1200, double height = 705) =>
  new(new DemoAuthenticationService(), new DatabaseService(), new DisabledCameraScannerService()) { Width = width, Height = height };
 [AvaloniaFact]
 public void InitialStateAndPasswordVisibility()
 {
  var w = CreateWindow(); w.Show();
  Assert.False(Get<Button>(w,"SignInButton").IsEnabled);
  Get<TextBox>(w,"LoginBox").Text="admin";
  Get<TextBox>(w,"PasswordBox").Text="secret";
  Dispatcher.UIThread.RunJobs();
  Assert.True(Get<Button>(w,"SignInButton").IsEnabled);
  Click(Get<Button>(w,"ShowPasswordButton"));
  Assert.Equal('\0',Get<TextBox>(w,"PasswordBox").PasswordChar);
  Click(Get<Button>(w,"ShowPasswordButton"));
  Assert.Equal('●',Get<TextBox>(w,"PasswordBox").PasswordChar);
  w.Close();
 }
 [AvaloniaFact]
 public async Task WrongPasswordAndSuccessfulLogin()
 {
  var w=CreateWindow(); w.Show();
  Get<TextBox>(w,"LoginBox").Text="admin";
  Get<TextBox>(w,"PasswordBox").Text="wrong";
  Dispatcher.UIThread.RunJobs();
  Click(Get<Button>(w,"SignInButton"));
  Assert.False(Get<Button>(w,"SignInButton").IsEnabled);
  await Task.Delay(500);
  Assert.True(Get<TextBlock>(w,"ErrorText").IsVisible);
  Get<TextBox>(w,"PasswordBox").Text="Legenda2026!";
  Dispatcher.UIThread.RunJobs();
  Click(Get<Button>(w,"SignInButton"));
  await Task.Delay(500);
  Assert.True(Get<Grid>(w,"AdminPanel").IsVisible);
  Assert.False(Get<StackPanel>(w,"LoginPanel").IsVisible);
  Assert.Equal("",Get<TextBox>(w,"PasswordBox").Text);
  w.Close();
 }
 [AvaloniaFact]
 public void InvalidFormatAndGuestEntry()
 {
  var w=CreateWindow(); w.Show();
  Get<TextBox>(w,"LoginBox").Text="bad login";
  Get<TextBox>(w,"PasswordBox").Text="secret";
  Dispatcher.UIThread.RunJobs();
  Click(Get<Button>(w,"SignInButton"));
  Assert.Equal("Некорректный формат логина",Get<TextBlock>(w,"ErrorText").Text);
  Click(Get<Button>(w,"GuestButton"));
  Assert.True(Get<Grid>(w,"GuestPanel").IsVisible);
  Assert.False(Get<StackPanel>(w,"LoginPanel").IsVisible);
  w.Close();
 }
 [AvaloniaFact]
 public void RenderReference()
 {
  var w = CreateWindow(1440,810); w.Show();
  Dispatcher.UIThread.RunJobs();
  Directory.CreateDirectory("../../../../../artifacts");
  using var bitmap = new RenderTargetBitmap(new PixelSize(1440,810), new Vector(96,96));
  bitmap.Render(w);
  bitmap.Save("../../../../../artifacts/authorization.png");
  w.Close();
  var dateWindow = new Window
  {
   Width = 340, Height = 450,
   Background = Avalonia.Media.Brushes.White,
   Content = new Avalonia.Controls.DatePickerPresenter
   {
    Date = new DateTimeOffset(2001,7,20,0,0,0,TimeSpan.Zero),
    HorizontalAlignment = Avalonia.Layout.HorizontalAlignment.Center,
    VerticalAlignment = Avalonia.Layout.VerticalAlignment.Center
   }
  };
  dateWindow.Show(); Dispatcher.UIThread.RunJobs();
  using var dateBitmap = new RenderTargetBitmap(new PixelSize(340,450), new Vector(96,96));
  dateBitmap.Render(dateWindow);
  dateBitmap.Save("../../../../../artifacts/date-picker.png");
  dateWindow.Close();
 }

 [AvaloniaFact]
 public void RenderResponsiveAuthorization()
 {
  Directory.CreateDirectory("../../../../../artifacts");
  var wide = CreateWindow(1460,630); wide.Show();
  Dispatcher.UIThread.RunJobs();
  using (var image = new RenderTargetBitmap(new PixelSize(1460,630), new Vector(96,96)))
  {
   image.Render(wide); image.Save("../../../../../artifacts/authorization-wide.png");
  }
  wide.Close();

  var compact = CreateWindow(720,450); compact.Show();
  Dispatcher.UIThread.RunJobs();
  using (var image = new RenderTargetBitmap(new PixelSize(720,450), new Vector(96,96)))
  {
   image.Render(compact); image.Save("../../../../../artifacts/authorization-compact.png");
  }
  compact.Close();
 }

 [AvaloniaFact]
 public async Task RenderCompactApplicationScreens()
 {
  Directory.CreateDirectory("../../../../../artifacts");
  var guest = CreateWindow(720,450); guest.Show();
  Click(Get<Button>(guest,"GuestButton")); Dispatcher.UIThread.RunJobs();
  using (var image = new RenderTargetBitmap(new PixelSize(720,450), new Vector(96,96)))
  {
   image.Render(guest); image.Save("../../../../../artifacts/guest-compact.png");
  }
  guest.Close();

  var admin = CreateWindow(720,450); admin.Show();
  Get<TextBox>(admin,"LoginBox").Text="admin";
  Get<TextBox>(admin,"PasswordBox").Text="Legenda2026!";
  Dispatcher.UIThread.RunJobs(); Click(Get<Button>(admin,"SignInButton"));
  await Task.Delay(500); Dispatcher.UIThread.RunJobs();
  using (var image = new RenderTargetBitmap(new PixelSize(720,450), new Vector(96,96)))
  {
   image.Render(admin); image.Save("../../../../../artifacts/admin-compact.png");
  }
  Click(Get<Button>(admin,"AllClientsCard"));
  Dispatcher.UIThread.RunJobs();
  using (var image = new RenderTargetBitmap(new PixelSize(720,450), new Vector(96,96)))
  {
   image.Render(admin); image.Save("../../../../../artifacts/clients-compact.png");
  }
  admin.Close();
 }

 [AvaloniaFact]
 public async Task RenderContinuedScreens()
 {
  Directory.CreateDirectory("../../../../../artifacts");
  var guest = CreateWindow(1440,810); guest.Show();
  Click(Get<Button>(guest,"GuestButton")); Dispatcher.UIThread.RunJobs();
  using (var image = new RenderTargetBitmap(new PixelSize(1440,810), new Vector(96,96)))
  {
   image.Render(guest); image.Save("../../../../../artifacts/guest-scanner.png");
  }
  guest.Close();

  var admin = CreateWindow(1440,810); admin.Show();
  Get<TextBox>(admin,"LoginBox").Text="admin";
  Get<TextBox>(admin,"PasswordBox").Text="Legenda2026!";
  Dispatcher.UIThread.RunJobs(); Click(Get<Button>(admin,"SignInButton"));
  await Task.Delay(500); Dispatcher.UIThread.RunJobs();
  using (var image = new RenderTargetBitmap(new PixelSize(1440,810), new Vector(96,96)))
  {
   image.Render(admin); image.Save("../../../../../artifacts/admin-dashboard.png");
  }
  Click(Get<Button>(admin,"AllClientsCard"));
  Dispatcher.UIThread.RunJobs();
  using (var image = new RenderTargetBitmap(new PixelSize(1440,810), new Vector(96,96)))
  {
   image.Render(admin); image.Save("../../../../../artifacts/clients-table.png");
  }
  admin.Close();
 }

 [Fact]
 public void SqlitePersistsClientChanges()
 {
  var directory = Path.Combine(Path.GetTempPath(), "LegendaTests", Path.GetRandomFileName());
  Directory.CreateDirectory(directory);
  var path = Path.Combine(directory, "legenda.db");
  try
  {
   var database = new DatabaseService(path);
   database.Initialize();
   var originalCount = database.LoadClients().Count;
   var added = database.InsertClient(new ClientRecord("Тестов", "Клиент", "999001", "22.09.2026", "22.10.2026", false));
   database.UpdateClient(added with { ExpiryDate = "22.11.2026" });

   var reopened = new DatabaseService(path);
   reopened.Initialize();
   var persisted = reopened.LoadClients().Single(x => x.MembershipNumber == "999001");
   Assert.Equal("22.11.2026", persisted.ExpiryDate);
   Assert.Equal(originalCount + 1, reopened.LoadClients().Count);

   reopened.UpdateClient(persisted with { IsBlacklisted = true });
   Assert.True(new DatabaseService(path).LoadClients().Single(x => x.Id == persisted.Id).IsBlacklisted);
   reopened.UpdateClient(persisted with { IsBlacklisted = false });
   Assert.False(new DatabaseService(path).LoadClients().Single(x => x.Id == persisted.Id).IsBlacklisted);

   reopened.DeleteClient(persisted.Id);
   Assert.DoesNotContain(reopened.LoadClients(), x => x.MembershipNumber == "999001");
  }
  finally
  {
   Directory.Delete(directory, true);
  }
 }

 [Fact]
 public void ExcelExportPreservesTextAndBlacklist()
 {
  var clients = new[] { new ClientRecord("=1+1", "Анна & Олег", "00123", "01.01.2026", "01.02.2026", true) };
  var bytes = ClientExcelExporter.Export(clients);
  using var stream = new MemoryStream(bytes);
  using var zip = new System.IO.Compression.ZipArchive(stream);
  using var sheetStream = zip.GetEntry("xl/worksheets/sheet1.xml")!.Open();
  var sheet = System.Xml.Linq.XDocument.Load(sheetStream);
  System.Xml.Linq.XNamespace ns = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
  Assert.Empty(sheet.Descendants(ns + "f"));
  var cells = sheet.Descendants(ns + "row").Last().Elements(ns + "c").ToArray();
  Assert.Equal("00123", cells[4].Value);
  Assert.Equal("Да", cells[7].Value);
  Assert.Equal("=1+1", cells[0].Value);
  Assert.Equal("Анна & Олег", cells[1].Value);
  Directory.CreateDirectory("../../../../../artifacts");
  File.WriteAllBytes("../../../../../artifacts/clients-export.xlsx", bytes);
 }

 [AvaloniaFact]
 public void GuestMascotsReflectMembershipStatus()
 {
  var folder = Path.Combine(Path.GetTempPath(), "LegendaTests", Path.GetRandomFileName());
  var database = new DatabaseService(Path.Combine(folder, "test.db"));
  database.Initialize();
  foreach (var c in database.LoadClients()) database.DeleteClient(c.Id);
  var today = DateTime.Today.ToString("dd.MM.yyyy");
  database.InsertClient(new ClientRecord("Тестов", "Александр", "active", today, today, false));
  database.InsertClient(new ClientRecord("Тестов", "Александр", "expired", today, DateTime.Today.AddDays(-1).ToString("dd.MM.yyyy"), false));
  database.InsertClient(new ClientRecord("Тестов", "Александр", "blocked", today, today, true));
  var w = new MainWindow(new DemoAuthenticationService(), database, new DisabledCameraScannerService()) { Width=720, Height=450 };
  try
  {
   w.Show(); Click(Get<Button>(w,"GuestButton"));
   var process = typeof(MainWindow).GetMethod("ProcessScannedCode", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
   foreach (var (code, mascot) in new[] { ("active","AcceptedMascot"), ("expired","DeniedMascot"), ("blocked","DeniedMascot"), ("unknown","UnknownMascot") })
   {
    process.Invoke(w, new object[] { code });
    Dispatcher.UIThread.RunJobs();
    Assert.True(Get<Grid>(w,"ScanResultPanel").IsVisible);
    Assert.True(Get<Image>(w,mascot).IsVisible);
    Assert.False(Get<Grid>(w,"ScannerFrame").IsVisible);
    using var image = new RenderTargetBitmap(new PixelSize(720,450), new Vector(96,96));
    image.Render(w);
    Directory.CreateDirectory("../../../../../artifacts");
    image.Save($"../../../../../artifacts/guest-{code}.png");
    Click(Get<Button>(w,"NextScanButton"));
    Assert.True(Get<TextBox>(w,"ManualCodeBox").IsVisible);
    Assert.False(Get<Grid>(w,"ScanResultPanel").IsVisible);
   }
  }
  finally { w.Close(); Directory.Delete(folder, true); }
 }

 [AvaloniaFact]
 public void ClientQrPngCanBeScannedAndPreviewed()
 {
  var png = ClientQrCode.CreatePng("001 234");
  using var frame = OpenCvSharp.Cv2.ImDecode(png, OpenCvSharp.ImreadModes.Color);
  Assert.Equal(768, frame.Width);
  var pixels = new byte[frame.Width * frame.Height * 3];
  System.Runtime.InteropServices.Marshal.Copy(frame.Data, pixels, 0, pixels.Length);
  var decoded = new ZXing.BarcodeReaderGeneric().Decode(new ZXing.RGBLuminanceSource(
   pixels, frame.Width, frame.Height, ZXing.RGBLuminanceSource.BitmapFormat.BGR24));
  Assert.NotNull(decoded);
  Assert.Equal("001234", decoded.Text);

  var w = CreateWindow(720,450); w.Show();
  var client = new ClientRecord("Иванов", "Александр", "001 234", "01.01.2026", "01.12.2026", false);
  var method = typeof(MainWindow).GetMethod("ShowClientQr", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
  method.Invoke(w, new object[] { new MenuItem { Tag = client }, new RoutedEventArgs() });
  Dispatcher.UIThread.RunJobs();
  Assert.True(Get<Grid>(w,"ClientQrOverlay").IsVisible);
  Assert.Contains("001 234", Get<TextBlock>(w,"QrMembershipNumber").Text);
  using var image = new RenderTargetBitmap(new PixelSize(720,450), new Vector(96,96));
  image.Render(w);
  Directory.CreateDirectory("../../../../../artifacts");
  image.Save("../../../../../artifacts/client-qr-preview.png");
  File.WriteAllBytes("../../../../../artifacts/client-qr.png", png);
  w.Close();
 }

 [Fact]
 public void WindowsCameraEnumeratorCanBeCreated()
 {
  if (!OperatingSystem.IsWindows()) return;
  var enumerator = typeof(CameraScannerService).Assembly.GetType("Legenda.App.CameraDeviceEnumerator")!;
  var flags = System.Reflection.BindingFlags.NonPublic;
  var componentType = enumerator.GetNestedType("CreateDevEnum", flags)!;
  var interfaceType = enumerator.GetNestedType("ICreateDevEnum", flags)!;
  var component = Activator.CreateInstance(componentType)!;
  try
  {
   Assert.True(interfaceType.IsInstanceOfType(component), "Windows must support the declared camera enumerator interface.");
  }
  finally { System.Runtime.InteropServices.Marshal.ReleaseComObject(component); }
 }

 [AvaloniaFact]
 public void CameraPreviewClipsAtBothWindowSizes()
 {
  using var frame = new WriteableBitmap(new PixelSize(640,480), new Vector(96,96),
   Avalonia.Platform.PixelFormat.Bgra8888, Avalonia.Platform.AlphaFormat.Opaque);
  using (var buffer = frame.Lock())
  {
   var pixels = new byte[buffer.RowBytes * 480];
   for (var y = 0; y < 480; y++)
    for (var x = 0; x < 640; x++)
    {
     var i = y * buffer.RowBytes + x * 4;
     var line = x % 80 < 3 || y % 80 < 3;
     pixels[i] = line ? (byte)240 : (byte)140;
     pixels[i+1] = line ? (byte)240 : (byte)(80 + y / 4);
     pixels[i+2] = line ? (byte)240 : (byte)(40 + x / 4);
     pixels[i+3] = 255;
    }
   System.Runtime.InteropServices.Marshal.Copy(pixels, 0, buffer.Address, pixels.Length);
  }
  var w = CreateWindow(); w.Show();
  Click(Get<Button>(w,"GuestButton")); Dispatcher.UIThread.RunJobs();
  var preview = Get<Image>(w,"CameraPreview");
  preview.Source = frame; preview.IsVisible = true;
  Get<StackPanel>(w,"CameraPlaceholder").IsVisible = false;
  foreach (var size in new[] { new PixelSize(1200,705), new PixelSize(720,450) })
  {
   w.Width = size.Width; w.Height = size.Height;
   Dispatcher.UIThread.RunJobs();
   var viewport = Get<Grid>(w,"CameraViewport");
   var clip = Assert.IsType<Avalonia.Media.RectangleGeometry>(viewport.Clip);
   Assert.Equal(viewport.Bounds.Size, clip.Rect.Size);
   using var image = new RenderTargetBitmap(size, new Vector(96,96));
   image.Render(w);
   Directory.CreateDirectory("../../../../../artifacts");
   image.Save($"../../../../../artifacts/camera-preview-{size.Width}.png");
  }
  preview.Source = null;
  w.Close();
 }

 [Fact]
 public async Task CameraBackendLoadsAndStopsCleanly()
 {
  var scanner = new CameraScannerService();
  var status = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
  scanner.StatusChanged += (message, _) => status.TrySetResult(message);
  await scanner.StartAsync();
  var completed = await Task.WhenAny(status.Task, Task.Delay(TimeSpan.FromSeconds(8)));
  await scanner.StopAsync();
  Assert.Same(status.Task, completed);
  Assert.False(string.IsNullOrWhiteSpace(await status.Task));
 }
}
