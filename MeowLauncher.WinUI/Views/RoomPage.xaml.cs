using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using MeowRoom.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Windows.ApplicationModel.DataTransfer;

namespace MeowLauncher.WinUI.Views;

/// <summary>
/// 联机房间页。状态全部来自 <see cref="App.Room"/>（RoomService），页面只负责画和转发点击：
/// 没有房间 → 创建 / 加入 + 在线好友；在房间里 → 房主（三步引导、选世界、开放、邀请）或成员（房主地址）。
/// 异步结果回来后一律先看页面还在不在（_alive），不去碰已经切走的页面。
/// </summary>
public sealed partial class RoomPage : Page
{
    private bool _alive;
    private List<FriendInfo> _friends = new();
    private List<GameChoice> _games = new();
    private string _gamesSignature = "";
    private int? _selectedPort;
    private DispatcherTimer? _timer;
    private int _ticks;
    private bool _autoRouted;

    public RoomPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        _alive = true;
        App.Room.Changed += RoomChanged;
        App.Room.Log += OnRoomLog;
        OnlineComponent.UpdateAvailable += OnComponentUpdate;
        _ = OnlineComponent.CheckUpdateAsync();            // 后台看看有没有新版本的联机组件，每次运行只查一次
        Refresh();
        _ = LoadFriendsAsync();
        _ = ScanAsync();

        _timer ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _timer.Tick -= Timer_Tick;
        _timer.Tick += Timer_Tick;
        _timer.Start();
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _alive = false;
        App.Room.Changed -= RoomChanged;
        App.Room.Log -= OnRoomLog;
        OnlineComponent.UpdateAvailable -= OnComponentUpdate;
        _timer?.Stop();
    }

    private void OnComponentUpdate() => DispatcherQueue.TryEnqueue(() => { if (_alive) Refresh(); });

    private void UpdateComponent_Click(object sender, RoutedEventArgs e) =>
        App.MainWindow?.Navigate("roomupdate", recordHistory: false);

    private void RoomChanged() => DispatcherQueue.TryEnqueue(() => { if (_alive) Refresh(); });

    private static void OnRoomLog(string line) => App.Trace("Room: " + line);

    private void Timer_Tick(object? sender, object e)
    {
        _ticks++;
        Refresh();                                                                         // 让过期的提示自己消失
        if (App.Room.IsHost && App.Room.Phase == RoomPhase.Hosting) _ = ScanAsync();      // 房主在等的时候，每 3 秒自动刷新一次世界列表
        if (_ticks % 10 == 0) _ = LoadFriendsAsync();                                      // 好友列表每 30 秒刷新一次
    }

    // ------------------------------------------------------------------ 数据

    private async Task LoadFriendsAsync()
    {
        try
        {
            var result = await App.Backend.GetFriendsAsync();
            if (!_alive) return;
            _friends = result.Ok ? (result.Friends ?? new()).Where(f => f.Kind == "friend" && f.Online && f.Meow).ToList() : new();
            BuildFriendLists();
        }
        catch { /* 好友读不到就保持原样 */ }
    }

    private async Task ScanAsync()
    {
        if (!App.Room.IsHost) return;
        List<GameChoice> found;
        try { found = await Task.Run(() => App.Room.ScanGames()); }
        catch { return; }
        if (!_alive) return;

        var signature = string.Join("|", found.Select(g => $"{g.Port}:{g.Label}"));
        if (signature != _gamesSignature)
        {
            _gamesSignature = signature;
            _games = found;
            if (_selectedPort is null || !_games.Any(g => g.Port == _selectedPort)) _selectedPort = _games.FirstOrDefault()?.Port;
            BuildGameList();
        }
        Refresh();
    }

    // ------------------------------------------------------------------ 画面

    private void Refresh()
    {
        var room = App.Room;
        var inRoom = room.Code.Length == 6;
        var busy = room.Phase is RoomPhase.Creating or RoomPhase.Joining;

        var ver = OnlineComponent.Loaded?.Version ?? "";
        ComponentText.Text = EasyTierService.ResolveCorePath() is not null
            ? (ver.Length > 0 ? $"联机组件 v{ver}" : "联机组件已就绪")
            : "缺少联机组件 EasyTier，开放 / 加入会失败";
        var pending = OnlineComponent.PendingUpdate;
        UpdateButton.Visibility = pending is not null ? Visibility.Visible : Visibility.Collapsed;
        if (pending is not null) ComponentText.Text += $" · 有新版本 v{pending.Version}";
        // 必须更新的版本（云端标了 required）：不在房间里时直接带用户去更新页，只带一次
        if (pending is { Required: true } && !inRoom && !_autoRouted)
        {
            _autoRouted = true;
            App.MainWindow?.Navigate("roomupdate", recordHistory: false);
            return;
        }

        // 在房间里的提示一直留着（它说明下一步）；没在房间里的提示只留 25 秒，免得「还没登录好」这种旧提示一直挂着
        var message = room.Message;
        if (!inRoom && (DateTime.Now - room.MessageAt).TotalSeconds > 25) message = "";
        if (!inRoom && !App.Online.IsLoggedIn && message.Length == 0) message = "联机账号还在登录，稍等几秒。";
        StatusBar.Visibility = message.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        StatusText.Text = message;

        IdlePanel.Visibility = inRoom ? Visibility.Collapsed : Visibility.Visible;
        RoomPanel.Visibility = inRoom ? Visibility.Visible : Visibility.Collapsed;
        CreateButton.IsEnabled = !busy;
        JoinButton.IsEnabled = !busy;
        JoinCodeBox.IsEnabled = !busy;
        if (!inRoom) return;

        var host = room.IsHost;
        CodeText.Text = room.Code.Length == 6 ? room.Code[..3] + " " + room.Code[3..] : room.Code;

        // 状态标签
        string phase;
        var good = false;
        switch (room.Phase)
        {
            case RoomPhase.Hosting: phase = "等待开放"; break;
            case RoomPhase.Opening: phase = "隧道建立中…"; break;
            case RoomPhase.Opened: phase = "已开放"; good = true; break;
            case RoomPhase.Joining: phase = "连接中…"; break;
            default: phase = room.HostAddr.Length > 0 ? "已连接" : "等房主开放"; good = room.HostAddr.Length > 0; break;
        }
        PhaseText.Text = phase;
        PhaseChip.Background = (Brush)Resources[good ? "RoomGreenSoftBrush" : "RoomWarnSoftBrush"];

        // 房主：三步引导 + 选世界
        HostSteps.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        if (host)
        {
            var opened = room.Phase == RoomPhase.Opened;
            var haveGame = _games.Count > 0;
            Step1Text.Text = "✓  房间已创建";
            Step2Text.Text = haveGame || opened ? "✓  游戏已开放到局域网" : "②  在游戏里打开世界，按 Esc →「对局域网开放」";
            Step3Text.Text = opened ? "✓  已开放，好友可以加入" : haveGame ? "③  选好世界，点下面的「开放」" : "③  回到这里选择世界，点「开放」";
        }

        var opening = room.Phase == RoomPhase.Opening;
        HostOpen.Visibility = host && room.Phase is RoomPhase.Hosting or RoomPhase.Opening ? Visibility.Visible : Visibility.Collapsed;
        GameEmpty.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        OpenButton.IsEnabled = !opening && _selectedPort is not null;
        OpenButton.Content = opening ? "建立中…" : "开放";
        RefreshGamesButton.IsEnabled = !opening;

        // 地址：房主开放后显示；成员一直显示（房主开放前是占位）
        var showAddr = !host || room.Phase == RoomPhase.Opened;
        AddrBlock.Visibility = showAddr ? Visibility.Visible : Visibility.Collapsed;
        AddrLabel.Text = host ? "房间地址（好友加入后会自动拿到，不用发给他们）" : "房主地址";
        AddrText.Text = room.HostAddr.Length > 0 ? room.HostAddr : "等房主开放游戏…";
        CopyAddrButton.IsEnabled = room.HostAddr.Length > 0;
        AddrHint.Text = host
            ? "好友在游戏里「多人游戏 → 直接连接」填的就是这个地址。"
            : "在游戏里：多人游戏 → 直接连接，粘贴这个地址。";

        // 成员 / 邀请
        MembersTitle.Text = $"成员 · {room.Players.Count}";
        BuildMembers(room);
        InviteCard.Visibility = host ? Visibility.Visible : Visibility.Collapsed;
        InviteColumn.Width = new GridLength(host ? 1 : 0, GridUnitType.Star);

        CloseButton.Content = host ? "关闭房间" : "离开房间";
    }

    private void BuildGameList()
    {
        GameList.Children.Clear();
        foreach (var game in _games)
        {
            var radio = new RadioButton
            {
                Content = game.Label,
                Tag = game.Port,
                GroupName = "RoomGames",
                IsChecked = game.Port == _selectedPort,
            };
            radio.Checked += (s, _) =>
            {
                if (s is RadioButton { Tag: int port }) { _selectedPort = port; OpenButton.IsEnabled = App.Room.Phase == RoomPhase.Hosting; }
            };
            GameList.Children.Add(radio);
        }
    }

    private void BuildMembers(RoomService room)
    {
        MembersList.Children.Clear();
        foreach (var player in room.Players)
        {
            var row = new Grid { ColumnSpacing = 10 };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            row.Children.Add(MakeAvatar(player.Name, ""));
            var name = new TextBlock
            {
                Text = player.Name, VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["AppTextBrush"], FontSize = (double)Application.Current.Resources["FontSizeBody"],
            };
            Grid.SetColumn(name, 1);
            row.Children.Add(name);
            if (player.IsOwner)
            {
                var chip = new Border
                {
                    Padding = new Thickness(9, 2, 9, 2), CornerRadius = new CornerRadius(9), VerticalAlignment = VerticalAlignment.Center,
                    Background = (Brush)Application.Current.Resources["AppChipBrush"],
                    Child = new TextBlock { Text = "房主", FontSize = 11.5, Foreground = (Brush)Application.Current.Resources["AppText2Brush"] },
                };
                Grid.SetColumn(chip, 2);
                row.Children.Add(chip);
            }
            MembersList.Children.Add(row);
        }
    }

    private void BuildFriendLists()
    {
        IdleFriendsList.Children.Clear();
        InviteList.Children.Clear();
        foreach (var friend in _friends)
        {
            IdleFriendsList.Children.Add(MakeFriendRow(friend, invite: false));
            InviteList.Children.Add(MakeFriendRow(friend, invite: true));
        }
        IdleFriendsEmpty.Visibility = _friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        InviteEmpty.Visibility = _friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private UIElement MakeFriendRow(FriendInfo friend, bool invite)
    {
        var row = new Grid { ColumnSpacing = 10 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.Children.Add(MakeAvatar(friend.Name, friend.HeadPath));

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = friend.Name, FontSize = (double)Application.Current.Resources["FontSizeBody"],
            Foreground = (Brush)Application.Current.Resources["AppTextBrush"],
        });
        text.Children.Add(new TextBlock
        {
            Text = friend.StatusText, FontSize = (double)Application.Current.Resources["FontSizeCaption"],
            Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        if (invite)
        {
            var button = new Button { Content = "邀请", Style = (Style)Resources["RoomPillStyle"], Tag = friend };
            if (App.Online.IsInvitePending(friend.Id, App.Room.Code)) { button.Content = "已邀请"; button.IsEnabled = false; }
            button.Click += InviteFriend_Click;
            Grid.SetColumn(button, 2);
            row.Children.Add(button);
        }
        else
        {
            var hint = new TextBlock
            {
                Text = "创建房间后可以邀请", VerticalAlignment = VerticalAlignment.Center, FontSize = (double)Application.Current.Resources["FontSizeCaption"],
                Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
            };
            Grid.SetColumn(hint, 2);
            row.Children.Add(hint);
        }
        return row;
    }

    private static UIElement MakeAvatar(string name, string headPath)
    {
        var box = new Border
        {
            Width = 30, Height = 30, CornerRadius = new CornerRadius(8), VerticalAlignment = VerticalAlignment.Center,
            Background = (Brush)Application.Current.Resources["AppChipBrush"],
        };
        try
        {
            if (headPath.Length > 0 && System.IO.File.Exists(headPath))
            {
                box.Child = new Image { Source = new BitmapImage(new Uri(headPath)) { DecodePixelWidth = 64 }, Stretch = Stretch.UniformToFill };
                return box;
            }
        }
        catch { /* 头像读不出来就用首字母 */ }
        box.Child = new TextBlock
        {
            Text = name.Length > 0 ? name[..1].ToUpperInvariant() : "?", FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)Application.Current.Resources["AppText2Brush"],
        };
        return box;
    }

    // ------------------------------------------------------------------ 点击

    private async void Create_Click(object sender, RoutedEventArgs e)
    {
        await App.Room.CreateAsync();
        if (_alive) { Refresh(); _ = ScanAsync(); }
    }

    private async void Join_Click(object sender, RoutedEventArgs e)
    {
        await App.Room.JoinAsync(JoinCodeBox.Text);
        if (_alive) Refresh();
    }

    private void JoinCode_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == Windows.System.VirtualKey.Enter) Join_Click(sender, e);
    }

    private void RefreshGames_Click(object sender, RoutedEventArgs e) => _ = ScanAsync();

    private async void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedPort is not { } port) return;
        await App.Room.OpenAsync(port);
        if (_alive) Refresh();
    }

    private async void Close_Click(object sender, RoutedEventArgs e)
    {
        CloseButton.IsEnabled = false;
        try { await App.Room.CloseAsync(); }
        finally { if (_alive) { CloseButton.IsEnabled = true; Refresh(); } }
    }

    private async void InviteFriend_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FriendInfo friend } button) return;
        button.IsEnabled = false;
        try
        {
            var (ok, _, message) = await App.Online.InviteAsync(friend.Id, App.Room.Code);
            if (_alive) { StatusText.Text = $"{friend.Name}：{message}"; StatusBar.Visibility = Visibility.Visible; }
            if (ok) { button.Content = "已邀请"; return; }
            button.IsEnabled = true;
        }
        catch { button.IsEnabled = true; throw; }
    }

    private void CopyCode_Click(object sender, RoutedEventArgs e) => CopyText(App.Room.Code, "房间码已复制");

    private void CopyAddr_Click(object sender, RoutedEventArgs e) => CopyText(App.Room.HostAddr, "地址已复制");

    private void CopyText(string text, string done)
    {
        if (text.Length == 0) return;
        var package = new DataPackage();
        package.SetText(text);
        Clipboard.SetContent(package);
        StatusText.Text = done;
        StatusBar.Visibility = Visibility.Visible;
    }
}
