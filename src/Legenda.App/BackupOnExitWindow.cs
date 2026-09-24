using System;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace Legenda.App;

public sealed class BackupOnExitWindow : Window
{
    public bool DatabaseRestored { get; private set; }
    public BackupOnExitWindow(DatabaseService database, YandexDiskBackupService? backupService = null)
    {
        Title = "Легенда — Резервная копия";
        Width = 600; CanResize = false; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#F4F9FC"); FontFamily = "Inter";
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Legenda.App/Assets/app-icon.ico")));
        var panel = new StackPanel { Margin = new Thickness(26), Spacing = 16 };
        panel.Children.Add(new TextBlock { Text = "Сохранить резервную копию?", FontSize = 23, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#24363D") });
        panel.Children.Add(new TextBlock { Text = "Данные клиентов изменились. Перед выходом можно сохранить копию базы на Яндекс Диск.", TextWrapping = TextWrapping.Wrap, FontSize = 14 });
        panel.Children.Add(new Border { Background = Brush.Parse("#E8F2F5"), CornerRadius = new CornerRadius(10), Padding = new Thickness(14), Child = new TextBlock {
            Text = "Папка: Легенда / Резервные копии\nХранятся 10 последних копий. Дата и время указаны в названии.", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#487E8B") } });
        var status = new TextBlock { Name = "BackupStatus", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#70868F") };
        if (database.ReadYandexDiskToken() is null) status.Text = "Яндекс Диск ещё не подключён. Укажите токен в настройках под учётной записью администратора.";
        panel.Children.Add(status);
        var settings = MakeButton("Настроить Яндекс Диск", false);
        settings.IsVisible = database.IsAdministrator;
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        settings.Click += async (_, _) =>
        {
            var window = new ManagementWindow(database, openSettings: true);
            await window.ShowDialog(this);
            if (window.DatabaseRestored) { DatabaseRestored = true; Close(false); return; }
            status.Text = database.ReadYandexDiskToken() is null ? "Подключение не настроено." : "Подключение сохранено. Можно создать копию.";
        };
        panel.Children.Add(settings);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        var cancel = MakeButton("Отмена", false); cancel.Name = "CancelExitButton";
        var skip = MakeButton("Закрыть без копии", false); skip.Name = "SkipBackupButton";
        var save = MakeButton("Сохранить и выйти", true); save.Name = "SaveBackupButton";
        buttons.Children.Add(cancel); buttons.Children.Add(skip); buttons.Children.Add(save); panel.Children.Add(buttons);
        Content = panel;
        var busy = false;
        CancellationTokenSource? cancellation = null;
        Closing += (_, e) => { if (busy) { e.Cancel = true; cancellation?.Cancel(); } };
        cancel.Click += (_, _) => { if (busy) cancellation?.Cancel(); else Close(false); };
        skip.Click += (_, _) => Close(true);
        save.Click += async (_, _) =>
        {
            busy = true; save.IsEnabled = skip.IsEnabled = settings.IsEnabled = false;
            cancellation = new CancellationTokenSource();
            status.Foreground = Brush.Parse("#487E8B"); status.Text = "Сохраняем копию на Яндекс Диск…";
            try
            {
                await (backupService ?? new YandexDiskBackupService()).BackupAsync(database, cancellation.Token);
                busy = false; Close(true);
            }
            catch (OperationCanceledException) { status.Text = "Копирование отменено. Приложение остаётся открытым."; }
            catch (Exception ex) { status.Foreground = Brush.Parse("#C54F50"); status.Text = ex.Message; }
            finally
            {
                busy = false; save.IsEnabled = skip.IsEnabled = settings.IsEnabled = true;
                cancellation.Dispose(); cancellation = null;
            }
        };
    }

    private static Button MakeButton(string text, bool primary) => new() {
        Content = text, Padding = new Thickness(13, 10), CornerRadius = new CornerRadius(8),
        Background = Brush.Parse(primary ? "#487E8B" : "#F4F9FC"), Foreground = primary ? Brushes.White : Brush.Parse("#487E8B"),
        BorderBrush = Brush.Parse("#B9D1D9"), BorderThickness = new Thickness(primary ? 0 : 1),
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
    };
}
