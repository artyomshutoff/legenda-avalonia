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
        Width = 540; CanResize = false; SizeToContent = SizeToContent.Height;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = Brush.Parse("#FAFCFD"); FontFamily = "Inter"; FontSize = 13;
        Icon = new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Legenda.App/Assets/app-icon.ico")));
        var panel = new StackPanel { Margin = new Thickness(28), Spacing = 18 };
        var heading = new Grid { ColumnDefinitions = new ColumnDefinitions("54,*"), ColumnSpacing = 16 };
        heading.Children.Add(new Border { Width = 54, Height = 54, Background = Brush.Parse("#E6F1F4"), CornerRadius = new CornerRadius(16),
            Child = Glyph("M 7,18 L 5,18 A 4,4 0 0 1 5,10 A 7,7 0 0 1 18,8 A 5,5 0 0 1 19,18 L 17,18 M 12,11 L 12,22 M 8,15 L 12,11 L 16,15", "#487E8B", 27) });
        var headingText = new StackPanel { Spacing = 5, VerticalAlignment = VerticalAlignment.Center };
        headingText.Children.Add(new TextBlock { Text = "РЕЗЕРВНАЯ КОПИЯ", FontSize = 10, LetterSpacing = 1.3, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#76919A") });
        headingText.Children.Add(new TextBlock { Text = "Сохранить перед выходом?", FontSize = 23, FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#24363D"), TextWrapping = TextWrapping.Wrap });
        Grid.SetColumn(headingText, 1); heading.Children.Add(headingText); panel.Children.Add(heading);
        panel.Children.Add(new TextBlock { Text = "Данные клиентов изменились. Сохраните свежую копию базы на Яндекс Диск, чтобы их можно было восстановить.", TextWrapping = TextWrapping.Wrap, FontSize = 14, LineHeight = 21, Foreground = Brush.Parse("#607781") });
        var destination = new StackPanel { Spacing = 10 };
        var folderRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 9 };
        folderRow.Children.Add(Glyph("M 2,6 L 9,6 L 11,9 L 22,9 L 22,21 L 2,21 Z M 2,6 L 2,4 L 9,4 L 11,6 L 20,6 L 20,9", "#6F98A3", 18));
        folderRow.Children.Add(new TextBlock { Text = "Яндекс Диск", FontWeight = FontWeight.SemiBold, Foreground = Brush.Parse("#487E8B"), VerticalAlignment = VerticalAlignment.Center });
        destination.Children.Add(folderRow);
        destination.Children.Add(new TextBlock { Text = "Легенда / Резервные копии", FontSize = 15, FontWeight = FontWeight.Medium, Foreground = Brush.Parse("#273B43") });
        destination.Children.Add(new TextBlock { Text = "10 последних копий · дата и время в названии", FontSize = 12, Foreground = Brush.Parse("#78909A"), TextWrapping = TextWrapping.Wrap });
        panel.Children.Add(new Border { Background = Brush.Parse("#F0F6F8"), BorderBrush = Brush.Parse("#DFEAEE"), BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14), Padding = new Thickness(18,16), Child = destination });
        var status = new TextBlock { Name = "BackupStatus", TextWrapping = TextWrapping.Wrap, Foreground = Brush.Parse("#70868F"), FontSize = 12, IsVisible = false };
        status.PropertyChanged += (_, e) => { if (e.Property == TextBlock.TextProperty) status.IsVisible = !string.IsNullOrWhiteSpace(status.Text); };
        if (database.ReadYandexDiskToken() is null) status.Text = "Яндекс Диск ещё не подключён. Укажите токен в настройках под учётной записью администратора.";
        panel.Children.Add(status);
        var settings = MakeButton("Настроить Яндекс Диск", false);
        settings.IsVisible = database.IsAdministrator && database.ReadYandexDiskToken() is null;
        settings.HorizontalAlignment = HorizontalAlignment.Left;
        settings.Click += async (_, _) =>
        {
            var window = new ManagementWindow(database, openSettings: true);
            await window.ShowDialog(this);
            if (window.DatabaseRestored) { DatabaseRestored = true; Close(false); return; }
            status.Text = database.ReadYandexDiskToken() is null ? "Подключение не настроено." : "Подключение сохранено. Можно создать копию.";
            settings.IsVisible = database.ReadYandexDiskToken() is null;
        };
        panel.Children.Add(settings);
        var progress = new ProgressBar { IsIndeterminate = true, Height = 3, IsVisible = false, Foreground = Brush.Parse("#487E8B"), Background = Brush.Parse("#E6F1F4") };
        panel.Children.Add(progress);
        var buttons = new StackPanel { Spacing = 10, Margin = new Thickness(0,4,0,0) };
        var secondary = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*"), ColumnSpacing = 10 };
        var cancel = MakeButton("Вернуться", false); cancel.Name = "CancelExitButton";
        var skip = MakeButton("Выйти без копии", false); skip.Name = "SkipBackupButton";
        skip.BorderThickness = new Thickness(0); skip.Background = Brushes.Transparent; skip.Foreground = Brush.Parse("#78909A");
        var save = MakeButton("Сохранить и выйти", true); save.Name = "SaveBackupButton";
        var saveLabel = new TextBlock { Text = "Сохранить и выйти", VerticalAlignment = VerticalAlignment.Center };
        save.Content = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10, Children = {
            Glyph("M 12,3 L 12,16 M 7,11 L 12,16 L 17,11 M 4,16 L 4,21 L 20,21 L 20,16", "#FFFFFF", 17), saveLabel } };
        Grid.SetColumn(skip, 1); secondary.Children.Add(cancel); secondary.Children.Add(skip);
        buttons.Children.Add(save); buttons.Children.Add(secondary); panel.Children.Add(buttons);
        Content = panel;
        var busy = false;
        CancellationTokenSource? cancellation = null;
        Closing += (_, e) => { if (busy) { e.Cancel = true; cancellation?.Cancel(); } };
        cancel.Click += (_, _) => { if (busy) cancellation?.Cancel(); else Close(false); };
        skip.Click += (_, _) => Close(true);
        save.Click += async (_, _) =>
        {
            busy = true; save.IsEnabled = skip.IsEnabled = settings.IsEnabled = false;
            progress.IsVisible = true; saveLabel.Text = "Сохраняем копию…";
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
                progress.IsVisible = false; saveLabel.Text = "Сохранить и выйти";
                cancellation.Dispose(); cancellation = null;
            }
        };
    }

    private static Button MakeButton(string text, bool primary) => new() {
        Content = text, Padding = new Thickness(16, 11), CornerRadius = new CornerRadius(10), MinHeight = primary ? 46 : 40,
        HorizontalAlignment = HorizontalAlignment.Stretch, FontSize = 13, FontWeight = primary ? FontWeight.SemiBold : FontWeight.Normal,
        Cursor = new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand),
        Background = Brush.Parse(primary ? "#487E8B" : "#FAFCFD"), Foreground = primary ? Brushes.White : Brush.Parse("#487E8B"),
        BorderBrush = Brush.Parse("#D7E5EA"), BorderThickness = new Thickness(primary ? 0 : 1),
        HorizontalContentAlignment = HorizontalAlignment.Center, VerticalContentAlignment = VerticalAlignment.Center
    };

    private static Avalonia.Controls.Shapes.Path Glyph(string path, string color, double size) => new() {
        Data = Geometry.Parse(path), Stroke = Brush.Parse(color), StrokeThickness = 1.6, Width = size, Height = size,
        Stretch = Stretch.Uniform, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center
    };
}
