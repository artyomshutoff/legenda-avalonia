using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Platform.Storage;

namespace Legenda.App;

public partial class MainWindow : Window
{
    private readonly IAuthenticationService _authentication;
    private readonly DatabaseService _database;
    private readonly ICameraScannerService _cameraScanner;
    private readonly ObservableCollection<ClientRecord> _clients = new();
    private Bitmap? _previewBitmap;
    private Bitmap? _clientQrBitmap;
    private byte[]? _clientQrPng;
    private string _qrFileName = "Абонемент.png";
    private bool _busy;
    private bool _blacklistMode;
    private ClientRecord? _editingClient;
    private bool _updatingCameraDevices;
    private bool? _adminActionsCompact;
    private readonly DispatcherTimer _resultTimer = new();
    private bool _exitApproved;
    private bool _exitPromptOpen;
    public MainWindow() : this(new DatabaseService()) { }
    private MainWindow(DatabaseService database) : this(new DatabaseAuthenticationService(database), database, new CameraScannerService()) { }
    public MainWindow(IAuthenticationService authentication) : this(authentication, new DatabaseService(), new CameraScannerService()) { }
    public MainWindow(IAuthenticationService authentication, DatabaseService database) : this(authentication, database, new CameraScannerService()) { }
    public MainWindow(IAuthenticationService authentication, DatabaseService database, ICameraScannerService cameraScanner, bool promptOnClose = true)
    {
        _authentication = authentication;
        _database = database;
        _cameraScanner = cameraScanner;
        InitializeComponent();
        foreach (var panel in new Control[] { GuestPanel, AdminPanel, ClientsOverlay, AddClientOverlay, ClientQrOverlay })
            panel.PropertyChanged += (_, change) =>
            {
                if (change.Property == IsVisibleProperty) UpdateWindowTitle();
            };
        ClientsTitle.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.TextProperty) UpdateWindowTitle();
        };
        ClientFormTitle.PropertyChanged += (_, change) =>
        {
            if (change.Property == TextBlock.TextProperty) UpdateWindowTitle();
        };
        UpdateWindowTitle();
        _cameraScanner.FrameReady += OnCameraFrame;
        _cameraScanner.CodeDetected += OnCameraCodeDetected;
        _cameraScanner.StatusChanged += OnCameraStatusChanged;
        Closed += async (_, _) => { CloseClientQr(null, new RoutedEventArgs()); await StopCameraAsync(); };
        LoadClients();
        if (promptOnClose) Closing += ConfirmBackupOnClosing;
        SetupAccountButton.IsVisible = _authentication is DatabaseAuthenticationService && !_database.HasAccounts;
        _resultTimer.Tick += (_, _) => { _resultTimer.Stop(); if(GuestPanel.IsVisible) ResetScanResult(null,new RoutedEventArgs()); };
        Closed += (_, _) => _resultTimer.Stop();
        ClientsList.ItemsSource = _clients;
        RefreshVisits();
        UpdateClock();
        var clock = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        clock.Tick += (_, _) => UpdateClock();
        Activated += (_, _) => UpdateClock();
        Closed += (_, _) => clock.Stop();
        clock.Start();
        if (_authentication is DatabaseAuthenticationService) StartSharedSynchronization();
    }

    private void StartSharedSynchronization()
    {
        var sync = _database.SharedSync;
        var cancellation = new CancellationTokenSource();
        var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(30) };
        sync.CanApplyRemote = () => !_busy && !AddClientOverlay.IsVisible && !ClientsOverlay.IsVisible &&
            !ClientQrOverlay.IsVisible && !OwnedWindows.Any(w => w.IsVisible);
        void StatusChanged()
        {
            SharedSyncStatus.Text = sync.Status;
            ToolTip.SetTip(SharedSyncStatus, sync.Status);
        }
        void DatabaseApplied()
        {
            ReloadClients(); RefreshVisits(); RefreshClientList();
            SetupAccountButton.IsVisible = !_database.HasAccounts;
            if (AdminPanel.IsVisible && _database.Session is null) SignOut(null, new RoutedEventArgs());
        }
        sync.StatusChanged += StatusChanged;
        sync.DatabaseApplied += DatabaseApplied;
        timer.Tick += async (_, _) => await sync.SynchronizeAsync(cancellationToken:cancellation.Token);
        Opened += async (_, _) => { timer.Start(); await sync.SynchronizeAsync(cancellationToken:cancellation.Token); };
        Closed += (_, _) =>
        {
            timer.Stop(); cancellation.Cancel();
            sync.StatusChanged -= StatusChanged; sync.DatabaseApplied -= DatabaseApplied;
            sync.CanApplyRemote = null;
        };
    }

    private async void ConfirmBackupOnClosing(object? sender, WindowClosingEventArgs e)
    {
        if (_exitApproved || !_database.NeedsCloudBackup) return;
        e.Cancel = true;
        if (_exitPromptOpen) return;
        _exitPromptOpen = true;
        try
        {
            var dialog = new BackupOnExitWindow(_database);
            if (await dialog.ShowDialog<bool>(this))
            {
                _exitApproved = true;
                Close();
            }
            else if (dialog.DatabaseRestored) SignOut(null, new RoutedEventArgs());
        }
        finally { _exitPromptOpen = false; }
    }

    private void OnRootSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyResponsiveLayout(e.NewSize);

    private void UpdateWindowTitle()
    {
        var page = ClientQrOverlay.IsVisible ? "QR-код клиента"
            : AddClientOverlay.IsVisible ? ClientFormTitle.Text
            : ClientsOverlay.IsVisible ? ClientsTitle.Text
            : GuestPanel.IsVisible ? "Гостевой режим"
            : AdminPanel.IsVisible ? "Панель администратора"
            : "Авторизация";
        Title = $"Легенда — {page}";
    }

    private void CameraViewportSizeChanged(object? sender, SizeChangedEventArgs e)
    {
        if (sender is Grid viewport)
            viewport.Clip = new RectangleGeometry(new Rect(e.NewSize), 45, 45);
    }

    private void ApplyResponsiveLayout(Size size)
    {
        var compact = size.Width < 900 || size.Height < 600;
        var singleColumnLogin = size.Width < 820;

        AuthLayout.Margin = compact ? new Thickness(20) : new Thickness(Math.Clamp(size.Width * 0.035, 36, 64));
        LoginPanel.Margin = compact ? new Thickness(24) : new Thickness(40);
        AuthLayout.ColumnDefinitions[0].Width = new GridLength(1, GridUnitType.Star);
        AuthLayout.ColumnDefinitions[1].Width = singleColumnLogin ? new GridLength(0) : new GridLength(1.15, GridUnitType.Star);
        LogoView.IsVisible = LoginPanel.IsVisible && !singleColumnLogin;
        LogoView.Margin = compact ? new Thickness(8) : new Thickness(24);

        var pageMargin = compact ? new Thickness(14) : new Thickness(24);
        GuestPanel.Margin = pageMargin;
        AdminPanel.Margin = pageMargin;
        MembershipColumnHeader.Text = size.Width < 900 ? "Абонемент" : "Номер абонемента";
        VisitDateColumnHeader.Text = size.Width < 900 ? "Дата и время" : "Дата и время посещения";

        var compactActions = size.Width < 960;
        if (_adminActionsCompact != compactActions)
        {
            _adminActionsCompact = compactActions;
            AdminActionsGrid.ColumnDefinitions = new ColumnDefinitions(compactActions ? "*,12,*" : "*,16,*,16,*,16,*");
            AdminActionsGrid.RowDefinitions = new RowDefinitions(compactActions ? "94,12,94" : "145");
            var cards = new[] { AllClientsCard, AddClientCard, BlacklistCard, ManagementCard };
            for (var i = 0; i < cards.Length; i++)
            {
                Grid.SetRow(cards[i], compactActions ? i / 2 * 2 : 0);
                Grid.SetColumn(cards[i], compactActions ? i % 2 * 2 : i * 2);
                cards[i].Height = compactActions ? 94 : 145;
                cards[i].Padding = compactActions ? new Thickness(12,8) : new Thickness(18,15);
                if (cards[i].Content is StackPanel content)
                {
                    content.Spacing = compactActions ? 4 : 8;
                    if (content.Children[0] is Border icon) { icon.Width = icon.Height = compactActions ? 27 : 38; }
                    if (content.Children[1] is TextBlock heading) heading.FontSize = compactActions ? 14 : 16;
                    content.Children[2].IsVisible = !compactActions;
                }
            }
        }

        var dialogMargin = compact ? new Thickness(14) : new Thickness(32);
        var dialogPadding = compact ? new Thickness(22, 18) : new Thickness(42, 28);
        ClientsDialog.Margin = dialogMargin;
        ClientsDialog.Padding = dialogPadding;
        AddClientDialog.Margin = dialogMargin;
        AddClientDialog.Padding = dialogPadding;
    }
    private void CredentialsChanged(object? sender, TextChangedEventArgs e)
    {
        if (SignInButton is null || PasswordBox is null) return;
        ErrorText.IsVisible = false;
        LoginBox.Classes.Remove("invalid");
        PasswordBox.Classes.Remove("invalid");
        ShowPasswordButton.IsVisible = !string.IsNullOrEmpty(PasswordBox.Text);
        SignInButton.IsEnabled = !_busy && !string.IsNullOrWhiteSpace(LoginBox.Text) && !string.IsNullOrEmpty(PasswordBox.Text);
    }
    private void TogglePassword(object? sender, RoutedEventArgs e)
    {
        PasswordBox.PasswordChar = PasswordBox.PasswordChar == '\0' ? '●' : '\0';
        var label = PasswordBox.PasswordChar == '\0' ? "Скрыть пароль" : "Показать пароль";
        ToolTip.SetTip(ShowPasswordButton, label);
        Avalonia.Automation.AutomationProperties.SetName(ShowPasswordButton, label);
    }
    private void OnInputKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter && SignInButton.IsEnabled)
        {
            e.Handled = true;
            SignIn(sender, new RoutedEventArgs());
        }
    }
    private async void SignIn(object? sender, RoutedEventArgs e)
    {
        if (_busy || !SignInButton.IsEnabled) return;
        var login = LoginBox.Text?.Trim() ?? "";
        var password = PasswordBox.Text ?? "";
        if (!DemoAuthenticationService.IsValidLogin(login))
        {
            ShowError("Некорректный формат логина");
            LoginBox.Classes.Add("invalid");
            return;
        }
        _busy = true;
        SignInButton.IsEnabled = false;
        GuestButton.IsEnabled = false;
        LoginBox.IsEnabled = false;
        PasswordBox.IsEnabled = false;
        SignInButton.Content = "Вход…";
        try
        {
            if (await _authentication.SignInAsync(login, password))
                OpenAdmin();
            else
            {
                ShowError("Неверный логин или пароль");
                LoginBox.Classes.Add("invalid");
                PasswordBox.Classes.Add("invalid");
            }
        }
        catch (Exception)
        {
            ShowError("Не удалось выполнить вход. Попробуйте ещё раз.");
        }
        finally
        {
            _busy = false;
            GuestButton.IsEnabled = true;
            LoginBox.IsEnabled = true;
            PasswordBox.IsEnabled = true;
            SignInButton.Content = "Войти";
            SignInButton.IsEnabled = !string.IsNullOrWhiteSpace(LoginBox.Text) && !string.IsNullOrEmpty(PasswordBox.Text);
        }
    }
    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.IsVisible = true;
    }
    private void EnterGuest(object? sender, RoutedEventArgs e)
    {
        LoginPanel.IsVisible = false;
        LogoView.IsVisible = false;
        GuestPanel.IsVisible = true;
        ResetScanResult(null, e);
        ManualCodeBox.Focus();
        _ = StartCameraAsync();
    }

    private void OpenAdmin()
    {
        ReloadClients();
        RefreshVisits();
        LoginPanel.IsVisible = false;
        LogoView.IsVisible = false;
        AdminPanel.IsVisible = true;
        PasswordBox.Text = "";
    }

    private void SignOut(object? sender, RoutedEventArgs e)
    {
        _resultTimer.Stop();
        _database.SignOut();
        CloseClientQr(null, e);
        AdminPanel.IsVisible = false;
        GuestPanel.IsVisible = false;
        ClientsOverlay.IsVisible = false;
        AddClientOverlay.IsVisible = false;
        _ = StopCameraAsync();
        LoginPanel.IsVisible = true;
        LogoCanvas.IsVisible = true;
        ApplyResponsiveLayout(RootLayout.Bounds.Size);
        LoginBox.Text = "";
        PasswordBox.Text = "";
        LoginBox.Focus();
    }

    private void ManualCodeKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        ProcessScannedCode(ManualCodeBox.Text?.Trim() ?? string.Empty);
    }

    private void ProcessScannedCode(string code)
    {
        if (ScanResultPanel.IsVisible) return;
        MembershipExpiryBanner.IsVisible = false;
        MembershipExpiryText.Text = string.Empty;
        ReloadClients();
        var client = _clients.FirstOrDefault(x => x.MembershipNumber.Replace(" ", "") == code.Replace(" ", ""));
        var state = "accepted";
        string message;
        if (client is null)
        {
            state = "unknown";
            message = "Хм, не считалось…\nПоднеси ближе или введи номер заново";
        }
        else if (client.IsFrozen)
        {
            state="denied";
            message=$"Абонемент заморожен\nВозобновление: {client.FrozenUntil:dd.MM.yyyy}";
        }
        else if (client.IsBlacklisted)
        {
            state = "denied";
            message = "Доступ запрещён.\nОбратитесь к администратору.";
        }
        else if (!DateTime.TryParseExact(client.ExpiryDate, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var expiry)
                 || !DateTime.TryParseExact(client.PurchaseDate, "dd.MM.yyyy", CultureInfo.InvariantCulture, DateTimeStyles.None, out var purchase)
                 || expiry.Date < DateTime.Today || purchase.Date > DateTime.Today)
        {
            state = "denied";
            message = "Код не работает\nАбонемент истёк или неактивен. Обратитесь к администратору";
        }
        else
        {
            try { _database.RecordVisit(client); RefreshVisits(); }
            catch { state="denied"; }
            message = $"Код принят!\nОтличной тренировки, {client.FirstName}!";
            if(state=="denied") message="Не удалось записать посещение\nОбратитесь к администратору";
            else
            {
                var daysRemaining = (expiry.Date - DateTime.Today).Days;
                if (daysRemaining < 7)
                {
                    var remaining = daysRemaining == 0 ? "сегодня" :
                        $"через {daysRemaining} {(daysRemaining == 1 ? "день" : daysRemaining < 5 ? "дня" : "дней")}";
                    MembershipExpiryText.Text = $"Абонемент заканчивается {remaining}. Продлите его у администратора.";
                    MembershipExpiryBanner.IsVisible = true;
                }
            }
        }
        AcceptedMascot.IsVisible = state == "accepted";
        DeniedMascot.IsVisible = state == "denied";
        UnknownMascot.IsVisible = state == "unknown";
        ScanResultText.Text = message;
        ScanResultText.Foreground = new SolidColorBrush(Color.Parse(state switch
        {
            "accepted" => "#458C22", "denied" => "#C54F50", _ => "#B96913"
        }));
        ScannerFrame.IsVisible = false;
        ManualCodeBox.IsVisible = false;
        ScanMessage.IsVisible = false;
        ScanResultPanel.IsVisible = true;
        NextScanButton.Focus();
        if (_database.Setting("Sound", "true") == "true")
            ClientAccessSound.Play(state);
        if(int.TryParse(_database.Setting("ReturnSeconds","5"),out var seconds) && seconds>0)
        {
            _resultTimer.Interval=TimeSpan.FromSeconds(Math.Clamp(seconds,1,30));
            _resultTimer.Start();
        }
    }

    private void ResetScanResult(object? sender, RoutedEventArgs e)
    {
        _resultTimer.Stop();
        ScanResultPanel.IsVisible = false;
        ScannerFrame.IsVisible = true;
        ManualCodeBox.IsVisible = true;
        ManualCodeBox.Text = "";
        ScanMessage.IsVisible = false;
        ManualCodeBox.Focus();
    }

    private async Task StartCameraAsync()
    {
        CameraPreview.IsVisible = false;
        CameraPlaceholder.IsVisible = true;
        CameraPermissionActions.IsVisible = false;
        CameraStatusText.Text = "Подключение к камере…";
        await LoadCameraDevicesAsync();
        await _cameraScanner.StartAsync();
    }

    private async Task LoadCameraDevicesAsync()
    {
        var devices = await _cameraScanner.GetAvailableDevicesAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _updatingCameraDevices = true;
            CameraDeviceBox.ItemsSource = devices;
            CameraDeviceBox.SelectedItem = devices.FirstOrDefault(device => device.Index == _cameraScanner.SelectedDeviceIndex);
            CameraDeviceHint.Text = devices.Count == 0
                ? "Подключённые камеры не найдены"
                : devices.Count == 1 ? "Доступна одна камера" : $"Доступно камер: {devices.Count}";
            CameraDeviceBox.IsEnabled = devices.Count > 0;
            _updatingCameraDevices = false;
        });
    }

    private async void CameraDeviceChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_updatingCameraDevices || CameraDeviceBox.SelectedItem is not CameraDeviceInfo device ||
            device.Index == _cameraScanner.SelectedDeviceIndex) return;

        CameraDeviceBox.IsEnabled = false;
        CameraDeviceHint.Text = $"Подключение: {device.Name}…";
        await _cameraScanner.StopAsync();
        _cameraScanner.SelectedDeviceIndex = device.Index;
        CameraPreview.IsVisible = false;
        CameraPlaceholder.IsVisible = true;
        CameraStatusText.Text = $"Подключение к «{device.Name}»…";
        await _cameraScanner.StartAsync();
        CameraDeviceHint.Text = $"Выбрано: {device.Name}";
        CameraDeviceBox.IsEnabled = true;
    }

    private void OpenCameraSettings(object? sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo("ms-settings:privacy-webcam") { UseShellExecute = true });
            CameraStatusText.Text = "Включите доступ к камере для классических приложений, затем нажмите «Повторить»";
        }
        catch
        {
            CameraStatusText.Text = "Откройте Параметры Windows → Конфиденциальность и безопасность → Камера";
        }
    }

    private async void RetryCamera(object? sender, RoutedEventArgs e)
    {
        CameraPermissionActions.IsVisible = false;
        CameraStatusText.Text = "Повторное подключение…";
        await _cameraScanner.StopAsync();
        await StartCameraAsync();
    }

    private async Task StopCameraAsync()
    {
        await _cameraScanner.StopAsync();
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            CameraPreview.Source = null;
            CameraPreview.IsVisible = false;
            CameraPlaceholder.IsVisible = true;
            _previewBitmap?.Dispose();
            _previewBitmap = null;
        });
    }

    private void OnCameraFrame(byte[] frame)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!GuestPanel.IsVisible || ScanResultPanel.IsVisible) return;
            try
            {
                using var stream = new MemoryStream(frame, writable: false);
                var bitmap = new Bitmap(stream);
                var previous = _previewBitmap;
                _previewBitmap = bitmap;
                CameraPreview.Source = bitmap;
                CameraPreview.IsVisible = true;
                CameraPlaceholder.IsVisible = false;
                previous?.Dispose();
            }
            catch
            {
                CameraStatusText.Text = "Не удалось отобразить изображение с камеры";
            }
        }, DispatcherPriority.Background);
    }

    private void OnCameraCodeDetected(string code)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (!GuestPanel.IsVisible || ScanResultPanel.IsVisible) return;
            ManualCodeBox.Text = code;
            ProcessScannedCode(code);
        });
    }

    private void OnCameraStatusChanged(string message, bool isError)
    {
        Dispatcher.UIThread.Post(() =>
        {
            CameraStatusText.Text = message;
            CameraPermissionActions.IsVisible = isError;
            if (isError)
            {
                CameraPreview.IsVisible = false;
                CameraPlaceholder.IsVisible = true;
            }
        });
    }

    private void OpenClients(object? sender, RoutedEventArgs e)
    {
        ReloadClients();
        _blacklistMode = false;
        ClientsTitle.Text = "Все клиенты";
        ClientActionMessage.Text = "";
        ClientSearchBox.Text = "";
        ClientsList.ItemsSource = _clients;
        ClientsOverlay.IsVisible = true;
        ClientSearchBox.Focus();
    }

    private void OpenBlacklist(object? sender, RoutedEventArgs e)
    {
        ReloadClients();
        _blacklistMode = true;
        ClientsTitle.Text = "Черный список";
        ClientActionMessage.Text = "";
        ClientSearchBox.Text = "";
        ClientsList.ItemsSource = _clients.Where(x => x.IsBlacklisted).ToList();
        ClientsOverlay.IsVisible = true;
    }

    private void OpenAddClient(object? sender, RoutedEventArgs e)
    {
        _editingClient = null;
        ClientFormTitle.Text = "Добавление клиента";
        RenewMembership.IsVisible = false;
        RenewMembership.IsChecked = true;
        MembershipStartDate.SelectedDate = DateTimeOffset.Now.Date;
        CurrentMembershipDates.IsVisible = false;
        SaveClientButton.Content = "Добавить";
        AddClientMessage.IsVisible = false;
        NewLastName.Text = NewFirstName.Text = NewMiddleName.Text = "";
        NewPhone.Text = "";
        PhoneError.IsVisible = false;
        NewPhone.Classes.Remove("invalid");
        NewBirthDate.SelectedDate = DateTimeOffset.Now.AddYears(-18);
        MembershipPeriod.SelectedIndex = 0;
        PaymentAmount.Text = "";
        AddClientOverlay.IsVisible = true;
        NewLastName.Focus();
    }

    private void CloseOverlay(object? sender, RoutedEventArgs e)
    {
        ClientsOverlay.IsVisible = false;
        AddClientOverlay.IsVisible = false;
    }

    private void FilterClients(object? sender, TextChangedEventArgs e)
    {
        if (ClientsList is null) return;
        var query = ClientSearchBox.Text?.Trim() ?? "";
        IEnumerable<ClientRecord> result = _blacklistMode ? _clients.Where(x => x.IsBlacklisted) : _clients;
        if (query.Length > 0)
            result = result.Where(x => MatchesClient(x, query));
        ClientsList.ItemsSource = result.ToList();
    }

    private void ExtendMembership(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ClientRecord client }) return;
        EditClient(sender, e);
        RenewMembership.IsChecked = true;
        ClientFormTitle.Text = "Продление абонемента";
        MembershipStartDate.Focus();
    }

    private async void CopyClientData(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        var text = item.Tag switch
        {
            ClientRecord client => client.ClipboardText,
            VisitRecord visit => $"{visit.LastName} {visit.FirstName}\nТелефон: {visit.DisplayPhone}\nАбонемент: {visit.MembershipNumber}\nПосещение: {visit.VisitedAt}",
            _ => null
        };
        if (text is null) return;
        try
        {
            var clipboard = Clipboard;
            if (clipboard is null) throw new InvalidOperationException("Clipboard unavailable");
            await clipboard.SetTextAsync(text);
            ClientActionMessage.Foreground = new SolidColorBrush(Color.Parse("#3C8B67"));
            ClientActionMessage.Text = "Данные клиента скопированы";
        }
        catch (Exception)
        {
            ClientActionMessage.Foreground = new SolidColorBrush(Color.Parse("#C54F50"));
            ClientActionMessage.Text = "Не удалось скопировать данные. Попробуйте ещё раз.";
        }
    }

    private void ToggleBlacklist(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ClientRecord client }) return;
        try
        {
            ReplaceClient(client, client with { IsBlacklisted = !client.IsBlacklisted });
            RefreshClientList();
            ClientActionMessage.Foreground = new SolidColorBrush(Color.Parse("#3C8B67"));
            ClientActionMessage.Text = client.IsBlacklisted
                ? $"{client.LastName} {client.FirstName} убран из чёрного списка"
                : $"{client.LastName} {client.FirstName} добавлен в чёрный список";
        }
        catch (Exception)
        {
            ClientActionMessage.Foreground = new SolidColorBrush(Color.Parse("#C54F50"));
            ClientActionMessage.Text = "Не удалось сохранить изменение. Попробуйте ещё раз.";
        }
    }

    private void ShowClientQr(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ClientRecord client }) return;
        CloseClientQr(null, e);
        try
        {
            _clientQrPng = ClientQrCode.CreatePng(client.MembershipNumber);
            using var stream = new MemoryStream(_clientQrPng);
            _clientQrBitmap = new Bitmap(stream);
            ClientQrImage.Source = _clientQrBitmap;
            QrClientName.Text = $"{client.LastName} {client.FirstName} {client.MiddleName}".Trim();
            QrMembershipNumber.Text = $"Абонемент № {client.MembershipNumber}";
            var number = string.Concat(client.MembershipNumber.Where(char.IsLetterOrDigit));
            _qrFileName = $"Абонемент_{number}.png";
            QrSaveMessage.Text = "";
            ClientQrOverlay.IsVisible = true;
        }
        catch (Exception)
        {
            ClientActionMessage.Text = "Не удалось создать QR-код.";
        }
    }

    private void CloseClientQr(object? sender, RoutedEventArgs e)
    {
        ClientQrOverlay.IsVisible = false;
        ClientQrImage.Source = null;
        _clientQrBitmap?.Dispose();
        _clientQrBitmap = null;
        _clientQrPng = null;
    }

    private async void SaveClientQr(object? sender, RoutedEventArgs e)
    {
        var png = _clientQrPng;
        if (png is null) return;
        SaveQrButton.IsEnabled = false;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Сохранить QR-код",
                SuggestedFileName = _qrFileName,
                DefaultExtension = "png",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[] { new FilePickerFileType("Изображение PNG") { Patterns = new[] { "*.png" } } }
            });
            if (file is null) return;
            using (file)
            {
                await using var stream = await file.OpenWriteAsync();
                if (stream.CanSeek) stream.SetLength(0);
                await stream.WriteAsync(png);
            }
            QrSaveMessage.Foreground = new SolidColorBrush(Color.Parse("#3C8B67"));
            QrSaveMessage.Text = "QR-код сохранён";
        }
        catch (Exception)
        {
            QrSaveMessage.Foreground = new SolidColorBrush(Color.Parse("#C54F50"));
            QrSaveMessage.Text = "Не удалось сохранить файл. Выберите другую папку и повторите.";
        }
        finally { SaveQrButton.IsEnabled = true; }
    }

    private async void ExportClients(object? sender, RoutedEventArgs e)
    {
        var clients = ClientsList.ItemsSource?.Cast<ClientRecord>().ToArray() ?? Array.Empty<ClientRecord>();
        ExportClientsButton.IsEnabled = false;
        try
        {
            var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
            {
                Title = "Экспорт клиентов в Excel",
                SuggestedFileName = $"{(_blacklistMode ? "Черный список" : "Клиенты")}_{DateTime.Now:yyyy-MM-dd}.xlsx",
                DefaultExtension = "xlsx",
                ShowOverwritePrompt = true,
                FileTypeChoices = new[] { new FilePickerFileType("Книга Excel") { Patterns = new[] { "*.xlsx" } } }
            });
            if (file is null) return;
            using (file)
            {
                var bytes = await Task.Run(() => ClientExcelExporter.Export(clients));
                await using var stream = await file.OpenWriteAsync();
                if (stream.CanSeek) stream.SetLength(0);
                await stream.WriteAsync(bytes);
            }
            ClientActionMessage.Foreground = new SolidColorBrush(Color.Parse("#3C8B67"));
            ClientActionMessage.Text = $"Экспортировано клиентов: {clients.Length}";
        }
        catch (Exception)
        {
            ClientActionMessage.Foreground = new SolidColorBrush(Color.Parse("#C54F50"));
            ClientActionMessage.Text = "Не удалось сохранить Excel. Закройте файл, если он открыт, и повторите.";
        }
        finally { ExportClientsButton.IsEnabled = true; }
    }

    private void EditClient(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ClientRecord client }) return;
        _editingClient = client;
        RenewMembership.IsVisible = true;
        RenewMembership.IsChecked = false;
        MembershipStartDate.SelectedDate = DateTimeOffset.Now.Date;
        CurrentMembershipDates.IsVisible = true;
        CurrentMembershipDates.Text = $"Текущий абонемент: {client.PurchaseDate} — {client.ExpiryDate}";
        ClientFormTitle.Text = "Изменение данных клиента";
        SaveClientButton.Content = "Сохранить";
        NewLastName.Text = client.LastName;
        NewFirstName.Text = client.FirstName;
        NewMiddleName.Text = client.MiddleName;
        NewPhone.Text = client.Phone;
        PhoneError.IsVisible = false;
        NewPhone.Classes.Remove("invalid");
        NewBirthDate.SelectedDate = client.BirthDate ?? DateTimeOffset.Now.AddYears(-18);
        MembershipPeriod.SelectedIndex = 0;
        PaymentAmount.Text = "";
        AddClientMessage.IsVisible = false;
        ClientsOverlay.IsVisible = false;
        AddClientOverlay.IsVisible = true;
        NewLastName.Focus();
    }

    private async void DeleteClient(object? sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: ClientRecord client }) return;
        if(!await ManagementWindow.Confirm(this,"Удалить клиента?", $"{client.LastName} {client.FirstName} будет перемещён в корзину. Администратор сможет восстановить запись.")) return;
        _database.DeleteClient(client.Id);
        _clients.Remove(client);
        RefreshVisits();
        ClientActionMessage.Text = $"Клиент {client.LastName} {client.FirstName} удалён";
        RefreshClientList();
    }

    private void PhoneChanged(object? sender, TextChangedEventArgs e)
    {
        if (PhoneError is null || !PhoneNumber.TryNormalize(NewPhone.Text, out _)) return;
        PhoneError.IsVisible = false;
        NewPhone.Classes.Remove("invalid");
    }

    private void AddClient(object? sender, RoutedEventArgs e)
    {
        var lastName = NewLastName.Text?.Trim() ?? "";
        var firstName = NewFirstName.Text?.Trim() ?? "";
        if (lastName.Length < 2 || firstName.Length < 2)
        {
            AddClientMessage.Text = "Заполните фамилию и имя";
            AddClientMessage.IsVisible = true;
            return;
        }
        var phone = "";
        var legacyWithoutPhone = _editingClient is { Phone: "" } && string.IsNullOrWhiteSpace(NewPhone.Text);
        if (!legacyWithoutPhone && !PhoneNumber.TryNormalize(NewPhone.Text, out phone))
        {
            PhoneError.IsVisible = true;
            NewPhone.Classes.Add("invalid");
            NewPhone.Focus();
            return;
        }
        PhoneError.IsVisible = false;
        NewPhone.Classes.Remove("invalid");
        var months = MembershipPeriod.SelectedIndex switch { 1 => 2, 2 => 3, 3 => 6, 4 => 12, _ => 1 };
        var changeDates = _editingClient is null || RenewMembership.IsChecked == true;
        if(changeDates && _editingClient?.IsFrozen==true)
        {
            AddClientMessage.Text="Сначала разморозьте абонемент в разделе «История и заморозка»";
            AddClientMessage.IsVisible=true;return;
        }
        decimal? payment=null;
        if(changeDates && !string.IsNullOrWhiteSpace(PaymentAmount.Text))
        {
            if(!decimal.TryParse(PaymentAmount.Text.Replace(',','.'),NumberStyles.AllowDecimalPoint,CultureInfo.InvariantCulture,out var amount) || amount<0 || amount>10000000 || decimal.Round(amount,2)!=amount)
            {
                AddClientMessage.Text="Сумма оплаты: от 0 до 10 000 000, максимум два знака после запятой";
                AddClientMessage.IsVisible=true;return;
            }
            payment=amount;
        }
        if (changeDates && MembershipStartDate.SelectedDate is null)
        {
            AddClientMessage.Text = "Выберите дату начала абонемента";
            AddClientMessage.IsVisible = true;
            return;
        }
        var purchase = MembershipStartDate.SelectedDate?.Date ?? DateTime.Today;
        if (_editingClient is not null)
        {
            var updated = _editingClient with
            {
                LastName = lastName,
                FirstName = firstName,
                MiddleName = NewMiddleName.Text?.Trim() ?? "",
                BirthDate = NewBirthDate.SelectedDate,
                Phone = phone
            };
            if (changeDates)
                updated = updated with
                {
                    PurchaseDate = purchase.ToString("dd.MM.yyyy"),
                    ExpiryDate = purchase.AddMonths(months).ToString("dd.MM.yyyy")
                };
            ReplaceClient(_editingClient, updated, changeDates ? payment : null);
            _editingClient = null;
        }
        else
        {
            var number = GenerateMembershipNumber();
            var client = new ClientRecord(lastName, firstName, number, purchase.ToString("dd.MM.yyyy"), purchase.AddMonths(months).ToString("dd.MM.yyyy"), false, NewMiddleName.Text?.Trim() ?? "", NewBirthDate.SelectedDate);
            var inserted=_database.InsertClient(client with { Phone = phone },payment);
            _clients.Insert(0, inserted);
        }
        RefreshVisits();
        AddClientOverlay.IsVisible = false;
        OpenClients(sender, e);
    }

    private void ReplaceClient(ClientRecord original, ClientRecord updated, decimal? payment = null)
    {
        var index = _clients.IndexOf(original);
        if (index >= 0)
        {
            _database.UpdateClient(updated,payment);
            _clients[index] = updated;
        }
        RefreshVisits();
    }

    private string GenerateMembershipNumber()
    {
        var existing = _database.LoadClients(true)
            .Select(client => client.MembershipNumber.Replace(" ", ""))
            .ToHashSet(StringComparer.Ordinal);
        string number;
        do
        {
            number = System.Security.Cryptography.RandomNumberGenerator
                .GetInt32(10_000_000, 100_000_000).ToString(CultureInfo.InvariantCulture);
        } while (existing.Contains(number));
        return number;
    }

    private void RefreshClientList()
    {
        var query = ClientSearchBox.Text?.Trim() ?? "";
        IEnumerable<ClientRecord> result = _blacklistMode ? _clients.Where(x => x.IsBlacklisted) : _clients;
        if (query.Length > 0)
            result = result.Where(x => MatchesClient(x, query));
        ClientsList.ItemsSource = result.ToList();
    }
    private static bool MatchesClient(ClientRecord client,string query)
    {
        if($"{client.LastName} {client.FirstName} {client.MiddleName} {client.MembershipNumber} {client.Phone}".Contains(query,StringComparison.OrdinalIgnoreCase)) return true;
        var digits=new string(query.Where(char.IsDigit).ToArray());
        return digits.Length>=3 && new string(client.Phone.Where(char.IsDigit).ToArray()).Contains(digits);
    }

    private void UpdateClock()
    {
        var culture = CultureInfo.GetCultureInfo("ru-RU");
        var value = DateTime.Now.ToString("dd MMM HH:mm:ss", culture);
        GuestClock.Text = value;
        AdminClock.Text = value;
    }

    private void LoadClients()
    {
        _database.Initialize();
        foreach (var client in _database.LoadClients()) _clients.Add(client);
    }

    private void ReloadClients()
    {
        _clients.Clear();
        foreach(var client in _database.LoadClients()) _clients.Add(client);
        var expiring=_clients.Count(c=>c.ExpiringSoon);
        ExpiryWarning.Text=expiring>0 ? $"Заканчиваются в течение 7 дней: {expiring}" : "";
    }
    private void RefreshVisits()
    {
        var visits=_database.RecentVisits(30);
        RecentVisitsList.ItemsSource=visits;
        NoVisitsText.IsVisible=visits.Count==0;
    }
    private async void OpenManagement(object? sender,RoutedEventArgs e)
    {
        var window=new ManagementWindow(_database);
        await window.ShowDialog(this);
        if(window.DatabaseRestored) { SignOut(null,e);return; }
        ReloadClients();RefreshVisits();RefreshClientList();
    }
    private async void SetupAccount(object? sender,RoutedEventArgs e)
    {
        await new ManagementWindow(_database,setup:true).ShowDialog(this);
        SetupAccountButton.IsVisible=!_database.HasAccounts;
    }
    private async void ClientHistory(object? sender,RoutedEventArgs e)
    {
        if(sender is not MenuItem {Tag:ClientRecord client}) return;
        await new ManagementWindow(_database,client:client).ShowDialog(this);
        ReloadClients();RefreshVisits();RefreshClientList();
    }
}

public sealed record ClientRecord(
    string LastName,
    string FirstName,
    string MembershipNumber,
    string PurchaseDate,
    string ExpiryDate,
    bool IsBlacklisted,
    string MiddleName = "",
    DateTimeOffset? BirthDate = null,
    long Id = 0,
    string Phone = "",
    bool Deleted = false,
    DateTime? FrozenUntil = null)
{
    public bool IsFrozen => FrozenUntil?.Date > DateTime.Today;
    public bool ExpiringSoon => !Deleted && !IsBlacklisted && !IsFrozen &&
        DateTime.TryParseExact(ExpiryDate,"dd.MM.yyyy",CultureInfo.InvariantCulture,DateTimeStyles.None,out var end)
        && end.Date>=DateTime.Today && end.Date<=DateTime.Today.AddDays(7);
    public string ExpiryColor => ExpiringSoon ? "#B96913" : "#27373D";
    public string ExpiryHint => ExpiringSoon ? "Абонемент заканчивается в течение 7 дней" : IsFrozen ? $"Заморожен до {FrozenUntil:dd.MM.yyyy}" : ExpiryDate;
    public string BlacklistActionLabel => IsBlacklisted ? "Убрать из чёрного списка" : "В чёрный список";
    public string DisplayPhone => string.IsNullOrWhiteSpace(Phone) ? "—" : Phone;
    public string ClipboardText => string.Join(Environment.NewLine,
        $"ФИО: {LastName} {FirstName} {MiddleName}".TrimEnd(),
        $"Телефон: {DisplayPhone}",
        $"Дата рождения: {BirthDate?.ToString("dd.MM.yyyy") ?? "—"}",
        $"Номер абонемента: {MembershipNumber}",
        $"Дата начала: {PurchaseDate}",
        $"Дата окончания: {ExpiryDate}",
        $"Чёрный список: {(IsBlacklisted ? "Да" : "Нет")}");
}
