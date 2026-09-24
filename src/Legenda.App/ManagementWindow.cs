using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Threading.Tasks;
using System.Threading;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia.Styling;

namespace Legenda.App;

public sealed class ManagementWindow : Window
{
    private readonly DatabaseService _db;
    private readonly bool _openSettings;
    private CancellationTokenSource? _cloudCancellation;
    private readonly TextBlock _status = new() { TextWrapping=TextWrapping.Wrap, Foreground=Brushes.DarkSlateGray, Margin=new Thickness(0,12,0,0) };
    public bool DatabaseRestored { get; private set; }
    public ManagementWindow(DatabaseService database,bool setup=false,ClientRecord? client=null,bool openSettings=false)
    {
        _openSettings=openSettings;
        Closing+=(_,e)=>{if(_cloudCancellation is not null){e.Cancel=true;_cloudCancellation.Cancel();}};
        _db=database;Title=setup?"Легенда — Создание администратора":client is null?"Легенда — Журналы и настройки":$"Легенда — {client.LastName} {client.FirstName}";
        Width=1020;Height=700;MinWidth=740;MinHeight=500;WindowStartupLocation=WindowStartupLocation.CenterOwner;
        Background=Brush.Parse("#F4F9FC");FontFamily="Inter";FontSize=13;
        Icon=new WindowIcon(Avalonia.Platform.AssetLoader.Open(new Uri("avares://Legenda.App/Assets/app-icon.ico")));
        Styles.Add(new Style(s=>s.OfType<Button>()) { Setters={
            new Setter(Button.BackgroundProperty,Brush.Parse("#487E8B")),new Setter(Button.ForegroundProperty,Brushes.White),
            new Setter(Button.CornerRadiusProperty,new CornerRadius(7)),new Setter(Button.PaddingProperty,new Thickness(16,8)),
            new Setter(Button.HorizontalContentAlignmentProperty,HorizontalAlignment.Center)
        }});
        Styles.Add(new Style(s=>s.OfType<TextBox>()) { Setters={
            new Setter(TextBox.CornerRadiusProperty,new CornerRadius(8)),new Setter(TextBox.MinHeightProperty,40d),
            new Setter(TextBox.BackgroundProperty,Brush.Parse("#FCFDFD")),new Setter(TextBox.BorderBrushProperty,Brush.Parse("#D9E3E7")),
            new Setter(TextBox.PaddingProperty,new Thickness(12,9))
        }});
        Styles.Add(new Style(s=>s.OfType<Button>().Class("nav")) { Setters={
            new Setter(Button.BackgroundProperty,Brushes.Transparent),new Setter(Button.ForegroundProperty,Brush.Parse("#607984")),
            new Setter(Button.BorderThicknessProperty,new Thickness(0)),new Setter(Button.PaddingProperty,new Thickness(12,11)),
            new Setter(Button.CornerRadiusProperty,new CornerRadius(10)),new Setter(Button.HorizontalContentAlignmentProperty,HorizontalAlignment.Stretch)
        }});
        Styles.Add(new Style(s=>s.OfType<Button>().Class("nav").Class("active")) { Setters={
            new Setter(Button.BackgroundProperty,Brush.Parse("#E8F2F5")),new Setter(Button.ForegroundProperty,Brush.Parse("#315F69"))
        }});
        if(setup)
        {
            Width=540;Height=700;MinWidth=420;MinHeight=440;
            Content=SetupPage();
            return;
        }
        Content=Workspace(client);
    }
    private Control Workspace(ClientRecord? client)
    {
        var root=new Grid { ColumnDefinitions=new ColumnDefinitions("210,*"),Background=Brush.Parse("#F4F9FC") };
        var side=new Grid { RowDefinitions=new RowDefinitions("Auto,*,Auto"),Margin=new Thickness(16,20,0,20) };
        side.Children.Add(new StackPanel { Children={
            new TextBlock {Text="ЛЕГЕНДА",FontSize=11,FontWeight=FontWeight.Bold,Foreground=Brush.Parse("#487E8B"),Margin=new Thickness(14,7,0,2)},
            new TextBlock {Text="Управление клубом",FontSize=15,FontWeight=FontWeight.SemiBold,Foreground=Brush.Parse("#24363D"),Margin=new Thickness(14,0,0,0)}
        }});
        var navigation=new StackPanel { Spacing=5,Margin=new Thickness(0,40,10,0) };
        Grid.SetRow(navigation,1);side.Children.Add(navigation);
        var badge=new Border {Background=Brush.Parse("#E8F2F5"),CornerRadius=new CornerRadius(9),Padding=new Thickness(12,9),Margin=new Thickness(0,0,10,0),Child=new TextBlock {Text=_db.Session is null?"Локальная база":$"{_db.Session.Login} · {_db.Session.Role}",FontSize=11,Foreground=Brush.Parse("#487E8B"),TextTrimming=TextTrimming.CharacterEllipsis}};
        Grid.SetRow(badge,2);side.Children.Add(badge);
        root.Children.Add(side);

        var main=new Grid {RowDefinitions=new RowDefinitions("Auto,*"),Margin=new Thickness(8,18,20,18)};
        Grid.SetColumn(main,1);root.Children.Add(main);
        var header=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto"),Margin=new Thickness(10,3,0,18)};
        var heading=new TextBlock {FontSize=23,FontWeight=FontWeight.SemiBold,Foreground=Brush.Parse("#24363D")};
        var subtitle=new TextBlock {FontSize=12,Foreground=Brush.Parse("#78909A"),Margin=new Thickness(0,6,0,0),TextWrapping=TextWrapping.Wrap};
        header.Children.Add(new StackPanel {Children={heading,subtitle}});
        var close=new Button {Content="Закрыть",Background=Brushes.Transparent,Foreground=Brush.Parse("#487E8B"),BorderBrush=Brush.Parse("#B9D1D9"),BorderThickness=new Thickness(1),Padding=new Thickness(16,8),VerticalAlignment=VerticalAlignment.Center};
        close.Click+=(_,_)=>Close();Grid.SetColumn(close,1);header.Children.Add(close);main.Children.Add(header);

        var contentFrame=new Border {Background=Brushes.White,BorderBrush=Brush.Parse("#DDE8EC"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(18),Padding=new Thickness(22)};
        Grid.SetRow(contentFrame,1);main.Children.Add(contentFrame);
        var contentGrid=new Grid {RowDefinitions=new RowDefinitions("*,Auto")};contentFrame.Child=contentGrid;
        var host=new ContentControl();contentGrid.Children.Add(host);
        _status.FontSize=12;_status.Margin=new Thickness(0,8,0,0);Grid.SetRow(_status,1);contentGrid.Children.Add(_status);
        var buttons=new List<Button>();
        void Page(string title,string description,string icon,Func<Control> factory)
        {
            var button=new Button {Classes={"nav"},Cursor=new Avalonia.Input.Cursor(Avalonia.Input.StandardCursorType.Hand)};
            button.Tag=title;
            var label=new TextBlock {Text=title,FontSize=13,FontWeight=FontWeight.Medium,VerticalAlignment=VerticalAlignment.Center,TextTrimming=TextTrimming.CharacterEllipsis};
            var glyph=new Avalonia.Controls.Shapes.Path {Data=Geometry.Parse(icon),Stroke=Brush.Parse("#6F98A3"),StrokeThickness=1.5,Width=17,Height=17,Stretch=Stretch.Uniform,VerticalAlignment=VerticalAlignment.Center};
            var row=new Grid {ColumnDefinitions=new ColumnDefinitions("20,*"),ColumnSpacing=10};row.Children.Add(glyph);Grid.SetColumn(label,1);row.Children.Add(label);button.Content=row;
            ScrollViewer? page=null;
            button.Click+=(_,_)=>
            {
                foreach(var other in buttons) other.Classes.Remove("active");
                button.Classes.Add("active");heading.Text=title;subtitle.Text=description;_status.Text="";
                page ??= new ScrollViewer {Content=factory(),HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled};
                host.Content=page;
            };
            buttons.Add(button);navigation.Children.Add(button);
        }
        if(client is not null) Page("Абонемент","История и заморозка клиента","M 2,3 L 14,3 L 14,15 L 2,15 Z M 5,1 L 5,5 M 11,1 L 11,5 M 2,7 L 14,7",()=>ClientPage(client));
        else
        {
            Page("Посещения","История проходов клиентов","M 15,8 A 7,7 0 1 1 1,8 A 7,7 0 1 1 15,8 M 8,3 L 8,8 L 11,10",VisitsPage);
            Page("Продления и оплаты","Операции по абонементам","M 2,2 L 14,2 L 14,15 L 2,15 Z M 5,6 L 11,6 M 5,9 L 11,9 M 5,12 L 9,12",HistoryPage);
            Page("Мой пароль","Безопасность учётной записи","M 3,7 L 13,7 L 13,15 L 3,15 Z M 5,7 L 5,5 A 3,3 0 0 1 11,5 L 11,7",PasswordPage);
            if(_db.IsAdministrator)
            {
                Page("Корзина","Удалённые клиенты","M 2,4 L 14,4 M 5,4 L 5,1 L 11,1 L 11,4 M 3,4 L 4,15 L 12,15 L 13,4",TrashPage);
                Page("Сотрудники","Доступ к панели управления","M 8,2 A 3,3 0 1 1 7.99,2 M 2,15 C 2,9 14,9 14,15",()=>AccountPage(false));
                Page("Настройки","Сканер и резервные копии","M 8,1 L 9,3 L 12,3 L 13,5 L 15,7 L 13,9 L 13,12 L 10,13 L 8,15 L 6,13 L 3,13 L 3,10 L 1,8 L 3,6 L 3,3 L 6,3 Z M 8,5 A 3,3 0 1 1 7.99,5",SettingsPage);
                Page("Изменения","Действия сотрудников","M 2,2 L 14,2 L 14,15 L 2,15 Z M 5,6 L 11,6 M 5,9 L 11,9 M 5,12 L 9,12",LogPage);
            }
        }
        if(buttons.Count>0) buttonClick(_openSettings ? buttons.FirstOrDefault(b=>Equals(b.Tag,"Настройки")) ?? buttons[0] : buttons[0]);
        return root;
        static void buttonClick(Button button)=>button.RaiseEvent(new Avalonia.Interactivity.RoutedEventArgs(Button.ClickEvent));
    }
    private static StackPanel Panel() => new() { Spacing=14,Margin=new Thickness(0,2,0,0) };
    private static TextBlock Text(string value) => new() { Text=value,TextWrapping=TextWrapping.Wrap };
    private Button Action(string title,Func<Task> action)
    {
        var b=new Button { Content=title,HorizontalAlignment=HorizontalAlignment.Left };
        b.Click+=async (_,_) =>
        {
            b.IsEnabled=false;_status.Text="";
            try { await action(); }
            catch(Exception ex) { _status.Foreground=Brush.Parse("#C54F50");_status.Text=ex is OperationCanceledException ? "Операция отменена. Можно закрыть окно." : ex is Microsoft.Data.Sqlite.SqliteException ? "Не удалось сохранить данные. Проверьте, нет ли записи с таким логином, и повторите." : ex.Message; }
            finally { b.IsEnabled=true; }
        };
        return b;
    }
    private Button Action(string title,Action action) => Action(title,()=>{action();return Task.CompletedTask;});
    private async Task CloudOperation(Func<CancellationToken,Task> action)
    {
        if(_cloudCancellation is not null)return;
        using var cancellation=new CancellationTokenSource();_cloudCancellation=cancellation;
        var content=(Control)Content!;content.IsEnabled=false;
        _status.Foreground=Brush.Parse("#487E8B");_status.Text="Подождите, идёт обмен с Яндекс Диском…";
        try {await action(cancellation.Token);}
        finally {content.IsEnabled=true;_cloudCancellation=null;}
    }
    private void Success(string message) { _status.Foreground=Brush.Parse("#3C8B67");_status.Text=message; }
    private static Border Card(Control content) => new() { Background=Brush.Parse("#F8FBFC"),BorderBrush=Brush.Parse("#E0EAED"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=new Thickness(15),Child=content };
    private static TextBlock Muted(string value) => new() { Text=value,TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#70868F"),FontSize=12 };
    private static TextBlock SectionTitle(string value) => new() {Text=value,FontSize=16,FontWeight=FontWeight.SemiBold,Foreground=Brush.Parse("#273B43"),Margin=new Thickness(0,0,0,3)};
    private static StackPanel Section(string title,string description)
    {
        var panel=Panel();panel.Children.Add(SectionTitle(title));panel.Children.Add(Muted(description));return panel;
    }
    private Control SetupPage()
    {
        var panel=new StackPanel { Spacing=0 };
        var icon=new Avalonia.Controls.Shapes.Path
        {
            Data=Geometry.Parse("M 12,2 L 21,6 L 21,12 C 21,18 17,22 12,25 C 7,22 3,18 3,12 L 3,6 Z M 8,13 L 11,16 L 17,10"),
            Stroke=Brush.Parse("#487E8B"),StrokeThickness=1.7,Width=26,Height=28,Stretch=Stretch.Uniform,
            HorizontalAlignment=HorizontalAlignment.Center,VerticalAlignment=VerticalAlignment.Center
        };
        panel.Children.Add(new Border { Width=54,Height=54,CornerRadius=new CornerRadius(16),Background=Brush.Parse("#EAF3F5"),HorizontalAlignment=HorizontalAlignment.Left,Child=icon });
        panel.Children.Add(new TextBlock { Text="Первый вход",FontSize=11,FontWeight=FontWeight.SemiBold,Foreground=Brush.Parse("#487E8B"),Margin=new Thickness(0,18,0,6) });
        panel.Children.Add(new TextBlock { Text="Создайте администратора",FontSize=24,FontWeight=FontWeight.SemiBold,Foreground=Brush.Parse("#202F36"),TextWrapping=TextWrapping.Wrap });
        panel.Children.Add(new TextBlock { Text="Ваша учётная запись для управления клиентами и настройками клуба.",Foreground=Brush.Parse("#758A93"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,10,0,22) });
        TextBox Field(string label,string placeholder,bool password=false)
        {
            panel.Children.Add(new TextBlock { Text=label,FontWeight=FontWeight.Medium,Foreground=Brush.Parse("#27373D"),Margin=new Thickness(0,0,0,7) });
            var field=new TextBox { Watermark=placeholder,Height=42,Padding=new Thickness(12,10),Background=Brush.Parse("#FCFDFD"),BorderBrush=Brush.Parse("#D5E0E4"),CornerRadius=new CornerRadius(8),MaxLength=password?128:64,PasswordChar=password?'●':'\0',Margin=new Thickness(0,0,0,16) };
            Avalonia.Automation.AutomationProperties.SetName(field,label);
            panel.Children.Add(field);return field;
        }
        var login=Field("Логин","Например, admin");
        var password=Field("Пароль","Придумайте пароль",true);
        password.Margin=new Thickness(0,0,0,6);
        panel.Children.Add(new TextBlock { Text="10–128 символов, минимум одна буква и цифра",FontSize=11,Foreground=Brush.Parse("#758A93"),TextWrapping=TextWrapping.Wrap,Margin=new Thickness(0,0,0,16) });
        var repeat=Field("Повторите пароль","Введите пароль ещё раз",true);
        repeat.Margin=new Thickness(0,0,0,8);
        var show=new CheckBox { Content="Показать пароли",FontSize=12,Foreground=Brush.Parse("#58717B") };
        show.IsCheckedChanged+=(_,_)=>password.PasswordChar=repeat.PasswordChar=show.IsChecked==true?'\0':'●';
        panel.Children.Add(show);
        _status.FontSize=12;
        panel.Children.Add(_status);
        var create=Action("Создать администратора",()=>
        {
            if(password.Text!=repeat.Text) throw new InvalidOperationException("Пароли не совпадают");
            _db.CreateAccount(login.Text??"",password.Text??"","Администратор");
            password.Text=repeat.Text="";Close();
        });
        create.HorizontalAlignment=HorizontalAlignment.Stretch;create.Height=44;
        create.CornerRadius=new CornerRadius(8);create.Margin=new Thickness(0,16,0,0);
        create.FontWeight=FontWeight.SemiBold;create.IsDefault=true;
        panel.Children.Add(create);
        var card=new Border { Background=Brushes.White,BorderBrush=Brush.Parse("#DDE8EC"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(22),Padding=new Thickness(28),Margin=new Thickness(20),MaxWidth=460,HorizontalAlignment=HorizontalAlignment.Stretch,VerticalAlignment=VerticalAlignment.Center,Child=panel };
        Opened+=(_,_)=>login.Focus();
        return new ScrollViewer { Content=card,HorizontalScrollBarVisibility=Avalonia.Controls.Primitives.ScrollBarVisibility.Disabled };
    }
    private Control VisitsPage()
    {
        var panel=Section("Журнал посещений","Последние 100 проходов · повторное сканирование в течение минуты не создаёт дубль.");
        var search=new TextBox { Watermark="Поиск по имени, телефону, абонементу или дате" };
        var rows=Panel();
        var toolbar=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=10,Margin=new Thickness(0,8,0,2)};
        toolbar.Children.Add(search);var refresh=Action("Обновить",Refresh);Grid.SetColumn(refresh,1);toolbar.Children.Add(refresh);panel.Children.Add(toolbar);
        panel.Children.Add(rows);
        void Refresh()
        {
            rows.Children.Clear();
            foreach(var v in _db.RecentVisits())
            {
                var line=$"{v.VisitedAt} · {v.LastName} {v.FirstName}\n{v.DisplayPhone} · № {v.MembershipNumber}";
                if(!line.Contains(search.Text??"",StringComparison.OrdinalIgnoreCase)) continue;
                var entry=new Grid {ColumnDefinitions=new ColumnDefinitions("*,Auto"),ColumnSpacing=12};
                var info=new StackPanel {Spacing=5};
                info.Children.Add(new SelectableTextBlock {Text=$"{v.LastName} {v.FirstName}",FontWeight=FontWeight.SemiBold,Foreground=Brush.Parse("#263A42")});
                info.Children.Add(new SelectableTextBlock {Text=$"{v.DisplayPhone}   ·   № {v.MembershipNumber}",FontSize=12,Foreground=Brush.Parse("#667D87"),TextWrapping=TextWrapping.Wrap});
                entry.Children.Add(info);
                var date=new SelectableTextBlock {Text=v.VisitedAt,FontSize=12,Foreground=Brush.Parse("#487E8B"),VerticalAlignment=VerticalAlignment.Center};
                Grid.SetColumn(date,1);entry.Children.Add(date);rows.Children.Add(Card(entry));
            }
            if(rows.Children.Count==0) rows.Children.Add(Card(Muted("Посещений не найдено. Попробуйте изменить запрос или обновить журнал.")));
        }
        search.TextChanged+=(_,_)=>Refresh();Refresh();return panel;
    }
    private Control HistoryPage()
    {
        var panel=Section("Продления и оплаты","История операций. Если сумма не указана, оплата не вводилась.");
        var rows=Panel();panel.Children.Add(Action("Обновить журнал",()=>Fill(rows,_db.History())));panel.Children.Add(rows);
        Fill(rows,_db.History());return panel;
    }
    private static void Fill(StackPanel rows,System.Collections.Generic.IReadOnlyList<string> values)
    {
        rows.Children.Clear();
        foreach(var value in values) rows.Children.Add(Card(new SelectableTextBlock { Text=value,TextWrapping=TextWrapping.Wrap }));
        if(values.Count==0) rows.Children.Add(Card(Muted("Записей пока нет")));
    }
    private Control ClientPage(ClientRecord client)
    {
        var panel=Panel();var state=Text("");panel.Children.Add(state);
        var days=new NumericUpDown { Minimum=1,Maximum=90,Value=7,Width=150,HorizontalAlignment=HorizontalAlignment.Left,FormatString="0" };
        panel.Children.Add(Text("Дней заморозки (1–90)"));panel.Children.Add(days);
        var rows=Panel();
        void Refresh()
        {
            var current=_db.LoadClients().First(c=>c.Id==client.Id);
            state.Text=$"{current.LastName} {current.FirstName} · № {current.MembershipNumber}\nПериод: {current.PurchaseDate} — {current.ExpiryDate}\n"+
                (current.IsFrozen?$"Заморожен до {current.FrozenUntil:dd.MM.yyyy}":"Абонемент не заморожен");
            Fill(rows,_db.History(client.Id));
        }
        panel.Children.Add(Action("Заморозить",()=>{_db.FreezeClient(client.Id,(int)(days.Value??7));Refresh();Success("Срок окончания перенесён на дни заморозки");}));
        panel.Children.Add(Action("Разморозить сейчас",()=>{_db.UnfreezeClient(client.Id);Refresh();Success("Неиспользованные дни заморозки вычтены из срока");}));
        panel.Children.Add(Text("Во время заморозки проход запрещён. После указанной даты доступ возобновится автоматически."));
        panel.Children.Add(rows);Refresh();return panel;
    }
    private Control TrashPage()
    {
        var panel=Section("Корзина","Удалённые клиенты сохраняют номер, историю и посещения.");
        var rows=Panel();panel.Children.Add(rows);
        void Refresh()
        {
            rows.Children.Clear();
            foreach(var client in _db.LoadClients(true).Where(c=>c.Deleted))
            {
                var line=Panel();line.Children.Add(Text($"{client.LastName} {client.FirstName} · № {client.MembershipNumber}"));
                line.Children.Add(Action("Восстановить",()=>{_db.RestoreClient(client.Id);Refresh();Success("Клиент восстановлен");}));
                rows.Children.Add(Card(line));
            }
            if(rows.Children.Count==0) rows.Children.Add(Card(Muted("Корзина пуста")));
        }
        Refresh();return panel;
    }
    private Control AccountPage(bool setup)
    {
        var panel=Section("Новый сотрудник","Оператор работает с клиентами и посещениями. Администратор также управляет сотрудниками, копиями базы и корзиной.");
        var login=new TextBox { Watermark="Логин",MaxLength=64 };
        var password=new TextBox { Watermark="Пароль: минимум 10 символов, буквы и цифры",PasswordChar='●',MaxLength=128 };
        var confirm=new TextBox { Watermark="Повторите пароль",PasswordChar='●',MaxLength=128 };
        var role=new ComboBox { ItemsSource=new[]{"Оператор","Администратор"},SelectedIndex=0 };
        var form=Panel();
        form.Children.Add(Muted("Логин"));form.Children.Add(login);
        form.Children.Add(Muted("Пароль · минимум 10 символов, буквы и цифры"));form.Children.Add(password);
        form.Children.Add(Muted("Повторите пароль"));form.Children.Add(confirm);
        if(!setup) {form.Children.Add(Muted("Роль"));form.Children.Add(role);}
        var rows=Panel();
        void Refresh()
        {
            rows.Children.Clear();if(setup)return;
            foreach(var account in _db.Accounts())
            {
                var entry=Panel();entry.Children.Add(Text($"{account.Login} · {account.Role}"));
                if(account.Id!=_db.Session?.Id)
                    entry.Children.Add(Action("Отключить",async ()=>{if(await Confirm(this,"Отключить сотрудника?",account.Login)){_db.DisableAccount(account);Refresh();}}));
                rows.Children.Add(Card(entry));
            }
        }
        form.Children.Add(Action("Создать учётную запись",()=>{
            if(password.Text!=confirm.Text) throw new InvalidOperationException("Пароли не совпадают");
            _db.CreateAccount(login.Text??"",password.Text??"",setup?"Администратор":role.SelectedItem?.ToString()??"Оператор");
            password.Text=confirm.Text="";Success("Учётная запись создана");if(setup)Close();else Refresh();
        }));
        panel.Children.Add(Card(form));panel.Children.Add(SectionTitle("Сотрудники"));panel.Children.Add(rows);Refresh();return panel;
    }
    private Control PasswordPage()
    {
        var panel=Section("Смена пароля",$"Учётная запись: {_db.Session?.Login}");
        var current=new TextBox { Watermark="Текущий пароль",PasswordChar='●',MaxLength=128 };
        var next=new TextBox { Watermark="Новый пароль: минимум 10 символов, буквы и цифры",PasswordChar='●',MaxLength=128 };
        var repeat=new TextBox { Watermark="Повторите новый пароль",PasswordChar='●',MaxLength=128 };
        var form=Panel();form.Children.Add(Muted("Текущий пароль"));form.Children.Add(current);
        form.Children.Add(Muted("Новый пароль"));form.Children.Add(next);
        form.Children.Add(Muted("Повторите новый пароль"));form.Children.Add(repeat);
        form.Children.Add(Action("Сменить пароль",()=>{
            if(next.Text!=repeat.Text) throw new InvalidOperationException("Пароли не совпадают");
            _db.ChangePassword(current.Text??"",next.Text??"");current.Text=next.Text=repeat.Text="";Success("Пароль изменён");
        }));panel.Children.Add(Card(form));return panel;
    }
    private Control SettingsPage()
    {
        var panel=Panel();
        var sound=new CheckBox { Content="Звуковой сигнал результата",IsChecked=_db.Setting("Sound","true")=="true" };
        var seconds=new NumericUpDown { Minimum=0,Maximum=30,Value=int.TryParse(_db.Setting("ReturnSeconds","5"),out var n)?n:5,Width=150,HorizontalAlignment=HorizontalAlignment.Left,FormatString="0" };
        var scanner=Section("Сканер","Настройте сигнал результата и время показа экрана проверки.");
        scanner.Children.Add(sound);scanner.Children.Add(Muted("Возврат к камере через секунд · 0 — только вручную"));scanner.Children.Add(seconds);
        scanner.Children.Add(Action("Сохранить настройки",()=>{
            _db.SetSetting("Sound",sound.IsChecked==true?"true":"false");
            _db.SetSetting("ReturnSeconds",((int)(seconds.Value??5)).ToString());Success("Настройки сохранены");
        }));
        panel.Children.Add(Card(scanner));
        var cloud=Section("Яндекс Диск","При выходе приложение предложит создать копию, если изменились данные клиентов. Обычные посещения не вызывают это предложение.");
        var token=new TextBox { Name="YandexDiskTokenBox", Watermark="Вставьте OAuth-токен Яндекс Диска",PasswordChar='●',MaxLength=4096 };
        var connection=new TextBlock {Text=_db.ReadYandexDiskToken() is null ? "Диск не подключён." : "Токен сохранён. Для замены введите новый.",TextWrapping=TextWrapping.Wrap,Foreground=Brush.Parse("#70868F")};
        cloud.Children.Add(Muted("OAuth-токен · права чтения и записи файлов"));
        cloud.Children.Add(token);cloud.Children.Add(connection);
        cloud.Children.Add(Action("Сохранить и проверить подключение",()=>CloudOperation(async ct=>{
            var value=string.IsNullOrWhiteSpace(token.Text)?_db.ReadYandexDiskToken():token.Text.Trim();
            if(string.IsNullOrEmpty(value))throw new InvalidOperationException("Введите OAuth-токен Яндекс Диска.");
            await new YandexDiskBackupService().CheckConnectionAsync(value,ct);
            _db.SaveYandexDiskToken(value);token.Text="";connection.Text="Яндекс Диск подключён.";Success("Подключение проверено и сохранено.");
        })));
        cloud.Children.Add(Action("Как получить токен",()=>System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("https://yandex.ru/dev/disk/rest/"){UseShellExecute=true})));
        cloud.Children.Add(Muted("Папка: Легенда / Резервные копии. Хранятся последние 10 копий; более старые удаляются после успешной загрузки новой. Токен защищён учётной записью Windows — на другом компьютере его нужно ввести заново."));
        var lastBackup=Muted("Последняя копия: "+_db.Setting("LastCloudBackup","ещё не создавалась"));cloud.Children.Add(lastBackup);
        cloud.Children.Add(Action("Создать копию на Яндекс Диске",()=>CloudOperation(async ct=>{
            var name=await new YandexDiskBackupService().BackupAsync(_db,ct);
            lastBackup.Text="Последняя копия: "+_db.Setting("LastCloudBackup","");Success("Сохранено: "+name);
        })));
        cloud.Children.Add(Action("Отключить Яндекс Диск",()=>{_db.SaveYandexDiskToken("");token.Text="";connection.Text="Диск отключён.";Success("Подключение удалено.");}));
        panel.Children.Add(Card(cloud));
        var backup=Section("Резервная копия","Копия включает клиентов, историю и учётные записи. Восстановление завершит текущий вход.");
        backup.Children.Add(Action("Сохранить копию базы",async ()=>{
            var file=await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions {Title="Резервная копия",SuggestedFileName=$"Legenda-{DateTime.Now:yyyyMMdd-HHmmss}.db",DefaultExtension="db",ShowOverwritePrompt=true});
            if(file is null)return;
            var temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
            try
            {
                _db.Backup(temp);
                using(file) { await using var output=await file.OpenWriteAsync();if(output.CanSeek)output.SetLength(0);await using var input=File.OpenRead(temp);await input.CopyToAsync(output); }
                _db.Audit("Создана резервная копия",file.Name);Success("Копия сохранена");
            }
            finally { if(File.Exists(temp))File.Delete(temp); }
        }));
        backup.Children.Add(Action("Восстановить из копии",async ()=>{
            var files=await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions {Title="Копия базы новой версии",AllowMultiple=false,FileTypeFilter=new[]{new FilePickerFileType("SQLite"){Patterns=new[]{"*.db"}}}});
            if(files.Count==0)return;
            using var file=files[0];
            if(!await Confirm(this,"Восстановить базу?","Текущие данные будут заменены. Перед восстановлением автоматически сохранится страховочная копия. Потребуется вход с паролем из выбранной копии."))return;
            var temp=Path.Combine(Path.GetTempPath(),Guid.NewGuid()+".db");
            try
            {
                await using(var input=await file.OpenReadAsync())
                await using(var output=File.Create(temp)) await input.CopyToAsync(output);
                _db.RestoreBackup(temp);DatabaseRestored=true;Close();
            }
            finally {if(File.Exists(temp))File.Delete(temp);}
        }));
        backup.Children.Add(new SelectableTextBlock { Text="База: "+_db.DatabasePath,TextWrapping=TextWrapping.Wrap,FontSize=11,Foreground=Brush.Parse("#70868F") });
        panel.Children.Add(Card(backup));
        return panel;
    }
    private Control LogPage()
    {
        var panel=Section("Журнал изменений","Действия сотрудников и изменения данных в приложении.");var rows=Panel();panel.Children.Add(Action("Обновить журнал",()=>Fill(rows,_db.AuditEntries())));
        panel.Children.Add(rows);Fill(rows,_db.AuditEntries());return panel;
    }
    public static async Task<bool> Confirm(Window owner,string title,string message)
    {
        var dialog=new Window {Title="Легенда — "+title,Width=460,SizeToContent=SizeToContent.Height,CanResize=false,WindowStartupLocation=WindowStartupLocation.CenterOwner,Background=Brush.Parse("#F4F9FC")};
        var panel=new StackPanel {Margin=new Thickness(24),Spacing=16};
        panel.Children.Add(new TextBlock {Text=title,FontSize=20,FontWeight=FontWeight.SemiBold});
        panel.Children.Add(Text(message));
        var buttons=new StackPanel {Orientation=Orientation.Horizontal,Spacing=12,HorizontalAlignment=HorizontalAlignment.Right};
        var cancel=new Button {Content="Отмена",Padding=new Thickness(20,8)};
        var accept=new Button {Content="Подтвердить",Padding=new Thickness(20,8),Background=Brush.Parse("#487E8B"),Foreground=Brushes.White};
        cancel.Click+=(_,_)=>dialog.Close(false);accept.Click+=(_,_)=>dialog.Close(true);
        buttons.Children.Add(cancel);buttons.Children.Add(accept);panel.Children.Add(buttons);dialog.Content=panel;
        return await dialog.ShowDialog<bool>(owner);
    }
}
