using System.IO;
using System.Text.RegularExpressions;
using MeowLauncher.WinUI.Controls;
using MeowLauncher.WinUI.Models;
using MeowLauncher.WinUI.Services;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.System;

namespace MeowLauncher.WinUI.Views;

/// <summary>
/// 账户页：当前账户 / 账户设置 / 其他账户三块 + 好友 / 皮肤与披风子视图。
/// 头像统一走真皮肤（HeadSource 优先，回退 SkinPath 裁切头像区）；没有皮肤图时
/// 离线账户用首字母兜底、正版账户用人物图标兜底，不造假头像。
/// </summary>
public sealed partial class AccountPage : Page
{
    private bool _loadingProfile;
    private MinecraftProfile? _profile;
    private AccountInfo? _activeAccount;
    private FriendsResult? _friends;
    private bool _friendsLoaded;
    private bool _loadingFriends;
    private bool _friendsReloadPending;   // 读取中又点了刷新：读完后立刻再读一遍
    private string _friendsAccountKey = "";
    private AccountPermsResult? _perms;
    private bool _permsUnavailable;
    private bool _loadingPerms;
    private bool _applyingPerms;
    private string _permsAccountKey = "";

    private sealed class FriendActionRequest
    {
        public FriendActionRequest(FriendInfo friend, string action)
        {
            Friend = friend;
            Action = action;
        }

        public FriendInfo Friend { get; }
        public string Action { get; }
    }

    /// <summary>宽窗左右两栏（预览 340×420 + 右侧选项），窄窗改为上下排；只改布局，不改内容。</summary>
    private void UpdateSkinLayout(double width)
    {
        if (width <= 0) return;
        var stack = width < 900;
        if (stack)
        {
            PreviewColumn.Width = new GridLength(1, GridUnitType.Star);
            OptionsColumn.Width = new GridLength(0);
            Grid.SetRow(SkinOptions, 1);
            Grid.SetColumn(SkinOptions, 0);
            Grid.SetColumnSpan(SkinOptions, 2);
            Grid.SetColumnSpan(SkinStage, 2);
            SkinStage.Width = 320;
            SkinStage.Height = 360;
            SkinStage.HorizontalAlignment = HorizontalAlignment.Center;
        }
        else
        {
            PreviewColumn.Width = new GridLength(340);
            OptionsColumn.Width = new GridLength(1, GridUnitType.Star);
            Grid.SetRow(SkinOptions, 0);
            Grid.SetColumn(SkinOptions, 1);
            Grid.SetColumnSpan(SkinOptions, 1);
            Grid.SetColumnSpan(SkinStage, 1);
            SkinStage.Width = 340;
            SkinStage.Height = 420;
            SkinStage.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    public AccountPage()
    {
        InitializeComponent();
        // 展示台高度跟页面宽度分档（宽窗给满，窄窗收窄，避免把下面的单选与披风挤出视口）。
        SizeChanged += (_, e) => UpdateSkinLayout(e.NewSize.Width);
        WireRowHover();
        Loaded += async (_, _) =>
        {
            ApplySnapshot();
            PlayEntrance();
            // 冷启动时快照可能还没回来：补拉一次再渲染，避免把「加载中」误显示成「还没有账户」。
            if (App.MainWindow?.Snapshot is null && App.MainWindow is not null)
            {
                await App.MainWindow.RefreshShellAsync(force: true);
                ApplySnapshot();
            }
            if (PendingSubView == "skin")
            {
                PendingSubView = null;
                SkinEntry_Click(this, new RoutedEventArgs());
            }
        };
    }

    // ------------------------------------------------------------------ //
    // 快照 → 三块
    // ------------------------------------------------------------------ //
    private void ApplySnapshot()
    {
        var snapshot = App.MainWindow?.Snapshot;

        // 首帧加载态：快照还没到时只显示「正在读取账户…」，不把空态摆出来。
        if (snapshot is null)
        {
            OtherRows.Children.Clear();
            OtherEmpty.Visibility = Visibility.Collapsed;
            AccountCard.Visibility = Visibility.Visible;
            CurrentBody.Visibility = Visibility.Visible;
            AvatarHost.Children.Clear();
            CurrentName.Text = "正在读取账户…";
            CurrentType.Text = "";
            CurrentLoadingRing.IsActive = true;
            CurrentLoadingRing.Visibility = Visibility.Visible;
            SettingsTitle.Visibility = Visibility.Visible;
            SettingsCard.Visibility = Visibility.Visible;
            FriendsRow.Visibility = Visibility.Collapsed;
            InfoCard.Visibility = Visibility.Collapsed;
            PermsTitle.Visibility = Visibility.Collapsed;
            PermsCard.Visibility = Visibility.Collapsed;
            _profile = null;
            _perms = null;
            return;
        }

        CurrentLoadingRing.IsActive = false;
        CurrentLoadingRing.Visibility = Visibility.Collapsed;

        var accounts = snapshot.Accounts ?? [];
        var active = snapshot?.Account ?? accounts.FirstOrDefault(account => account.Active);
        _activeAccount = active;
        var friendsAccountChanged = !string.Equals(
            _friendsAccountKey, active?.Key ?? "", StringComparison.OrdinalIgnoreCase);
        if (friendsAccountChanged)
        {
            _friendsAccountKey = active?.Key ?? "";
            _friends = null;
            _friendsLoaded = false;
            FriendsSummary.Text = "好友";
            FriendList.Children.Clear();
            IncomingList.Children.Clear();
            OutgoingList.Children.Clear();
            IncomingSection.Visibility = Visibility.Collapsed;
            OutgoingSection.Visibility = Visibility.Collapsed;
            FriendEmpty.Visibility = Visibility.Collapsed;
            SetFriendsStatus("");
        }
        var others = accounts
            .Where(account => !account.Active && !SameAccount(account, active))
            .ToList();

        BuildOtherRows(others);
        OtherEmpty.Visibility = others.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        // 对齐 Qt：无当前账户时整块隐藏（acc_cur_card 与账户设置都不显示）。
        var hasActive = active is not null;
        AccountCard.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        CurrentBody.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        SettingsTitle.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;
        SettingsCard.Visibility = hasActive ? Visibility.Visible : Visibility.Collapsed;

        if (active is null)
        {
            _profile = null;
            SkinPreviewFigure.SkinPath = null;
            FriendsRow.Visibility = Visibility.Collapsed;
            InfoCard.Visibility = Visibility.Collapsed;
            PermsTitle.Visibility = Visibility.Collapsed;
            PermsCard.Visibility = Visibility.Collapsed;
            _perms = null;
            ShowRootView();
            SettingsTitle.Text = "账户设置";
            ClearProfileError();
            ProfileStatus.Text = "";
            ProfileVariant.Text = "";
            CapeList.Items.Clear();
            CapeEmpty.Visibility = Visibility.Collapsed;
            CapeCard.Visibility = Visibility.Visible;
            OfflineCapeCard.Visibility = Visibility.Collapsed;
            SkinImageHint.Text = "请选择 64×64 的 PNG 皮肤文件";
            ProfileLoadingRing.IsActive = false;
            ProfileLoadingRing.Visibility = Visibility.Collapsed;
            ApplyVariantVisual("");
            UploadSkinButton.IsEnabled = true;
            ResetSkinButton.IsEnabled = true;
            SkinRefreshButton.IsEnabled = false;
            ClassicRow.IsEnabled = true;
            SlimRow.IsEnabled = true;
            return;
        }

        var isMicrosoft = IsMicrosoft(active);
        FillAvatar(AvatarHost, active, 96);
        var fallbackSkin = System.IO.Path.Combine(
            AppContext.BaseDirectory, "Assets", "skinviewer", "demo",
            active.Slim ? "default_slim.png" : "default_classic.png");
        SkinPreviewFigure.SkinPath = !string.IsNullOrWhiteSpace(active.SkinPath)
            ? active.SkinPath
            : (isMicrosoft ? null : (File.Exists(fallbackSkin) ? fallbackSkin : null));
        SkinPreviewFigure.Slim = active.Slim;
        CurrentName.Text = active.Name;
        CurrentType.Text = active.TypeDisplay;
        RenameRow.Visibility = isMicrosoft ? Visibility.Visible : Visibility.Collapsed;
        FriendsRow.Visibility = isMicrosoft ? Visibility.Visible : Visibility.Collapsed;
        if (!isMicrosoft && FriendsView.Visibility == Visibility.Visible) ShowRootView();
        if (isMicrosoft && friendsAccountChanged && FriendsView.Visibility == Visibility.Visible)
            _ = LoadFriendsAsync(force: true);

        // 账户信息（默认展开的键值卡）与账户权限（只对正版展示）：数据来自 Mojang 档案与 /player/attributes。
        InfoCard.Visibility = Visibility.Visible;
        ApplyInfoRows(active);
        PermsTitle.Visibility = isMicrosoft ? Visibility.Visible : Visibility.Collapsed;
        PermsCard.Visibility = isMicrosoft ? Visibility.Visible : Visibility.Collapsed;
        if (isMicrosoft)
        {
            var permsChanged = !string.Equals(_permsAccountKey, active.Key, StringComparison.OrdinalIgnoreCase);
            if (permsChanged)
            {
                _permsAccountKey = active.Key;
                _perms = null;
            }
            _ = LoadPermsAsync(force: permsChanged);
        }
        else
        {
            _perms = null;
            _permsAccountKey = "";
        }

        // 皮肤与披风：正版才有 Mojang 档案；离线账户只显示本地皮肤，披风区换成说明卡。
        SkinImageHint.Text = isMicrosoft
            ? "请选择 64×64 的 PNG 皮肤文件"
            : (active.DefaultSkin
                ? "默认皮肤，可以换成自己的 64×64 PNG"
                : (string.IsNullOrWhiteSpace(active.SkinPath)
                    ? "还没有本地皮肤，选一张 64×64 的 PNG"
                    : "本地皮肤，只在这台电脑上生效"));
        CapeCard.Visibility = isMicrosoft ? Visibility.Visible : Visibility.Collapsed;
        OfflineCapeCard.Visibility = isMicrosoft ? Visibility.Collapsed : Visibility.Visible;
        if (isMicrosoft)
        {
            UploadSkinButton.Content = "上传皮肤";
            ResetSkinButton.Content = "重置皮肤";
            UploadSkinButton.IsEnabled = true;
            ResetSkinButton.IsEnabled = true;
            ClassicRow.IsEnabled = true;
            SlimRow.IsEnabled = true;
            SkinRefreshButton.IsEnabled = true;
            SkinRefreshButton.Visibility = Visibility.Visible;
            _ = LoadProfileAsync();
        }
        else
        {
            _profile = null;
            ClearProfileError();
            ProfileStatus.Text = "";
            ProfileVariant.Text = active.Slim ? "纤细模型 · Slim" : "经典模型 · Classic";
            CapeList.Items.Clear();
            CapeEmpty.Visibility = Visibility.Collapsed;
            ProfileLoadingRing.IsActive = false;
            ProfileLoadingRing.Visibility = Visibility.Collapsed;
            SkinPreviewFigure.CapeUrl = string.Empty;
            SkinPreviewFigure.ForceRefresh();
            ApplyVariantVisual(active.Slim ? "SLIM" : "CLASSIC");
            UploadSkinButton.Content = "选择皮肤";
            ResetSkinButton.Content = "移除本地";
            UploadSkinButton.IsEnabled = true;
            SkinRefreshButton.IsEnabled = false;
            SkinRefreshButton.Visibility = Visibility.Collapsed;
            ClassicRow.IsEnabled = true;
            SlimRow.IsEnabled = true;
            ResetSkinButton.IsEnabled = !string.IsNullOrWhiteSpace(active.SkinPath) && !active.DefaultSkin;
        }
    }

    private static bool IsMicrosoft(AccountInfo account) =>
        account.Type.Equals("microsoft", StringComparison.OrdinalIgnoreCase);

    private static bool SameAccount(AccountInfo? left, AccountInfo? right)
    {
        if (left is null || right is null) return false;
        if (!string.IsNullOrWhiteSpace(left.Uuid) && !string.IsNullOrWhiteSpace(right.Uuid))
            return string.Equals(left.Uuid, right.Uuid, StringComparison.OrdinalIgnoreCase);
        return string.Equals(left.Key, right.Key, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------------------------ //
    // 头像：72 / 44 / 36 方形真皮肤大头（用户要求不裁切、不做圆角）。
    // 链：HeadSource（sidecar 已裁大头）→ SkinPath 裁头（CropHeadAsync）→
    //     首字母兜底（任何账户，包括正版；图片失败时回退首字母）。
    // FillAvatarShared 是给主窗口侧栏复用的静态入口。
    // ------------------------------------------------------------------ //
    internal static void FillAvatarShared(Grid host, AccountInfo? account, double size) =>
        FillAvatar(host, account, size);

    private static void FillAvatar(Grid host, AccountInfo? account, double size)
    {
        host.Children.Clear();
        host.Width = size;
        host.Height = size;

        // 3D 方块头自带透明边角，底下不再垫任何底色块（以前垫的绿色方块会从头像四角露出来）
        var accent = (Brush)Application.Current.Resources["AccountGreenBrush"];

        // 头像一律用 SkinHeadView 从皮肤 PNG 直接裁头层 + 帽层、按物理像素最近邻绘制：
        // 不依赖后端预渲染的头像（那份要 Pillow，内嵌 Python 里没有时整条链就断了），也不会被二次缩放弄糊。
        var painted = false;
        if (account is not null && !string.IsNullOrWhiteSpace(account.SkinPath) && File.Exists(account.SkinPath))
        {
            host.Children.Add(new Controls.SkinHeadView
            {
                Width = size,
                Height = size,
                SkinPath = account.SkinPath,
                UseDefaultWhenMissing = false,
                IsHitTestVisible = false,
            });
            painted = true;
        }

        if (!painted)
        {
            AddAvatarFallback(host, account, size, accent);
        }

        AutomationProperties.SetName(host, string.IsNullOrWhiteSpace(account?.Name)
            ? "账户头像"
            : $"{account!.Name} 的头像");
    }

    /// <summary>
    /// SkinPath 回退：WinUI 3 的 ImageBrush 没有 Viewbox（那是 WPF），所以用
    /// BitmapDecoder + BitmapTransform.Bounds 把 64/128 宽皮肤图的头部区域（8..16 单位）
    /// 裁成一张小图，再交给方形 Border 填充。正常路径是 sidecar 已裁好的 HeadSource，
    /// 这里只在 HeadPath 缺文件时兜底，失败就退回首字母/人物图标。
    /// </summary>
    private static async Task FillSkinFaceAsync(Grid host, AccountInfo account, double size, Brush accent)
    {
        ImageSource? head = null;
        try
        {
            head = await CropHeadAsync(account.SkinPath);
        }
        catch
        {
            head = null;
        }

        if (head is null)
        {
            AddAvatarFallback(host, account, size, accent);
            return;
        }

        var face = new Border
        {
            Width = size,
            Height = size,
            Background = new ImageBrush { ImageSource = head, Stretch = Stretch.Uniform },
        };
        host.Children.Insert(0, face);
    }

    private static async Task<ImageSource?> CropHeadAsync(string skinPath)
    {
        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(skinPath);
        using var stream = await file.OpenReadAsync();
        var decoder = await Windows.Graphics.Imaging.BitmapDecoder.CreateAsync(stream);
        var unit = Math.Max(1, (int)decoder.PixelWidth / 64);
        var transform = new Windows.Graphics.Imaging.BitmapTransform
        {
            Bounds = new Windows.Graphics.Imaging.BitmapBounds
            {
                X = (uint)(8 * unit),
                Y = (uint)(8 * unit),
                Width = (uint)(8 * unit),
                Height = (uint)(8 * unit),
            },
        };
        using var bitmap = await decoder.GetSoftwareBitmapAsync(
            Windows.Graphics.Imaging.BitmapPixelFormat.Bgra8,
            Windows.Graphics.Imaging.BitmapAlphaMode.Premultiplied,
            transform,
            Windows.Graphics.Imaging.ExifOrientationMode.IgnoreExifOrientation,
            Windows.Graphics.Imaging.ColorManagementMode.DoNotColorManage);
        var source = new SoftwareBitmapSource();
        await source.SetBitmapAsync(bitmap);
        return source;
    }

    /// <summary>没有皮肤图时的兜底：离线账户首字母，正版账户人物图标（不造假头像）。</summary>
    private static void AddAvatarFallback(Grid host, AccountInfo? account, double size, Brush accent)
    {
        var showInitial = account is not null && !string.IsNullOrWhiteSpace(account.Name);
        if (showInitial)
        {
            host.Children.Add(new TextBlock
            {
                Text = account!.Name[..1].ToUpperInvariant(),
                FontSize = size >= 60 ? 28 : 15,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = accent,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            });
            return;
        }

        host.Children.Add(new FontIcon
        {
            Glyph = "\uE77B",
            FontSize = size >= 60 ? 28 : 15,
            Foreground = accent,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        });
    }

    private string? _dragKey;

    private void OtherRow_DragStarting(UIElement sender, DragStartingEventArgs args)
    {
        _dragKey = (sender as FrameworkElement)?.Tag as string;
        args.Data.SetText(_dragKey ?? "");
        args.Data.RequestedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
    }

    private void OtherRow_DragOver(object sender, DragEventArgs e)
    {
        e.AcceptedOperation = string.IsNullOrEmpty(_dragKey)
            ? Windows.ApplicationModel.DataTransfer.DataPackageOperation.None
            : Windows.ApplicationModel.DataTransfer.DataPackageOperation.Move;
        e.DragUIOverride.IsCaptionVisible = false;
    }

    private async void OtherRow_Drop(object sender, DragEventArgs e)
    {
        var dragged = _dragKey;
        _dragKey = null;
        if (string.IsNullOrEmpty(dragged) || (sender as FrameworkElement)?.Tag is not string target || target == dragged) return;

        var order = OtherRows.Children.OfType<FrameworkElement>()
            .Select(c => c.Tag as string).Where(k => !string.IsNullOrEmpty(k)).Select(k => k!).ToList();
        var from = order.IndexOf(dragged);
        var to = order.IndexOf(target);
        if (from < 0 || to < 0) return;
        order.RemoveAt(from);
        order.Insert(to, dragged);

        var result = await App.Backend.ReorderAccountsAsync(order);
        if (!result.Ok)
        {
            ShowToast(result.Message, InfoBarSeverity.Error);
            return;
        }
        await App.MainWindow.RefreshShellAsync(force: true);
        ApplySnapshot();
    }

    private void BuildOtherRows(List<AccountInfo> others)
    {
        OtherRows.Children.Clear();
        foreach (var account in others)
        {
            // 整行点击=切换，行尾只有「删除」小图标钮；整行可拖动排序（拖到另一行上方放下）。
            var row = new Button
            {
                CanDrag = true,
                AllowDrop = true,
                Height = 52,
                Padding = new Thickness(8, 0, 10, 0),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Stretch,
                Background = new SolidColorBrush(Colors.Transparent),
                BorderThickness = new Thickness(0),
                CornerRadius = new CornerRadius(4),
                Tag = account.Key,
            };
            AutomationProperties.SetName(row, $"切换到 {account.Name}");
            row.Click += Switch_Click;
            row.DragStarting += OtherRow_DragStarting;
            row.DragOver += OtherRow_DragOver;
            row.Drop += OtherRow_Drop;

            var grid = new Grid { ColumnSpacing = 12 };
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            var avatar = new Grid { Width = 44, Height = 44, VerticalAlignment = VerticalAlignment.Center };
            FillAvatar(avatar, account, 44);
            grid.Children.Add(avatar);

            var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(new TextBlock
            {
                Text = account.Name,
                FontSize = 13,
                Foreground = (Brush)Application.Current.Resources["AppTextBrush"],
            });
            text.Children.Add(new TextBlock
            {
                Text = account.TypeDisplay,
                FontSize = 11.5,
                Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
            });
            Grid.SetColumn(text, 1);
            grid.Children.Add(text);

            var deleteButton = new Button
            {
                Height = 30,
                Width = 32,
                Padding = new Thickness(0),
                CornerRadius = new CornerRadius(15),
                BorderThickness = new Thickness(0),
                Background = (Brush)Application.Current.Resources["AppChipBrush"],
                Content = new FontIcon
                {
                    Glyph = "\uE74D",
                    FontSize = 11.5,
                    Foreground = (Brush)Application.Current.Resources["AppBadBrush"],
                },
                Tag = account.Key,
                VerticalAlignment = VerticalAlignment.Center,
            };
            AutomationProperties.SetName(deleteButton, $"删除 {account.Name}");
            deleteButton.Click += Delete_Click;
            Grid.SetColumn(deleteButton, 2);
            grid.Children.Add(deleteButton);

            row.Content = grid;
            OtherRows.Children.Add(row);
        }
    }

    // ------------------------------------------------------------------ //
    // 档案：加载 / 成功 / 失败三态
    // ------------------------------------------------------------------ //
    private async Task LoadProfileAsync()
    {
        if (_loadingProfile) return;
        _loadingProfile = true;
        SetProfileBusy(true);
        try
        {
            var profile = await App.Backend.GetMinecraftProfileAsync();
            _profile = profile;
            if (!profile.Ok)
            {
                ShowProfileError(profile);
                return;
            }

            ClearProfileError();
            ProfileVariant.Text = profile.VariantText;
            SkinPreviewFigure.Slim = profile.Variant.Equals("SLIM", StringComparison.OrdinalIgnoreCase);
            SkinPreviewFigure.CapeUrl = profile.Capes
                .FirstOrDefault(c => c.IsActive)?.Url ?? string.Empty;
            ProfileStatus.Text = profile.Capes.Count == 0 ? "暂无披风" : $"共 {profile.Capes.Count} 件披风";
            ApplyVariantVisual(profile.Variant);

            CapeList.Items.Clear();
            CapeList.Items.Add(BuildCapeTile("不显示", "", "", profile.ActiveCapeId.Length == 0));
            foreach (var cape in profile.Capes)
                CapeList.Items.Add(BuildCapeTile(cape.Alias, cape.Url, cape.Id, cape.Id == profile.ActiveCapeId));
            CapeEmpty.Visibility = profile.Capes.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            // 换成新的皮肤/披风后把当前模型强制重下发一次，不重建 WebView。
            SkinPreviewFigure.ForceRefresh();
            ApplyInfoRows(_activeAccount);

            // 账户信息刷新时，后端会把皮肤 / 披风缓存到本地。头像还是空白（新账号、缓存被清过）就刷新一次外壳，
            // 让头像立刻出来；已经有头像就不多此一举。_profile 已经有值，ApplySnapshot 不会再触发读档案。
            if (_activeAccount is { } acc && IsMicrosoft(acc)
                && (string.IsNullOrWhiteSpace(acc.HeadPath) || !System.IO.File.Exists(acc.HeadPath)))
            {
                await App.MainWindow.RefreshShellAsync(force: true);
                ApplySnapshot();
            }
        }
        catch (Exception ex)
        {
            ShowProfileError(new MinecraftProfile { Message = ex.Message });
        }
        finally
        {
            _loadingProfile = false;
            SetProfileBusy(false);
        }
    }

    private void SetProfileBusy(bool busy)
    {
        ProfileLoadingRing.IsActive = busy;
        ProfileLoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        CurrentLoadingRing.IsActive = busy;
        CurrentLoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        var offline = _activeAccount is { } account && !IsMicrosoft(account);
        var hasLocalSkin = offline && !_activeAccount!.DefaultSkin
                           && !string.IsNullOrWhiteSpace(_activeAccount.SkinPath);
        UploadSkinButton.IsEnabled = !busy;
        ResetSkinButton.IsEnabled = !busy && (!offline || hasLocalSkin);
        SkinRefreshButton.IsEnabled = !busy && !offline;
        ClassicRow.IsEnabled = !busy;
        SlimRow.IsEnabled = !busy;
        if (busy) ProfileStatus.Text = "正在读取档案…";
    }

    private void ShowProfileError(MinecraftProfile profile)
    {
        var message = string.IsNullOrWhiteSpace(profile.Message)
            ? profile.NeedLogin ? "登录凭据已失效，重新登录 Microsoft 账户就好。" : "档案读取失败，请稍后重试。"
            : profile.Message;
        ProfileErrorText.Text = message;
        ReloginButton.Visibility = profile.NeedLogin ? Visibility.Visible : Visibility.Collapsed;
        ProfileErrorPanel.Visibility = Visibility.Visible;
        ProfileStatus.Text = profile.NeedLogin ? "登录凭据已失效" : "档案读取失败";
        ProfileVariant.Text = "";
        ApplyVariantVisual("");
        CapeList.Items.Clear();
        CapeEmpty.Visibility = Visibility.Collapsed;
    }

    private void ClearProfileError()
    {
        ProfileErrorPanel.Visibility = Visibility.Collapsed;
        ProfileErrorText.Text = "";
        ReloginButton.Visibility = Visibility.Collapsed;
    }

    private Button BuildCapeTile(string label, string capeUrl, string capeId, bool active)
    {
        var tile = new Button
        {
            Width = 96,
            Height = 64,
            Padding = new Thickness(0),
            CornerRadius = new CornerRadius(8),
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
            BorderThickness = new Thickness(1),
            Tag = capeId,
        };
        AutomationProperties.SetName(tile, string.IsNullOrEmpty(capeId) ? "披风：不显示" : $"披风：{label}");

        var content = new Grid();
        if (string.IsNullOrEmpty(capeId))
        {
            content.Children.Add(new TextBlock
            {
                Text = "不显示",
                FontSize = 12,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = (Brush)Application.Current.Resources["AppText2Brush"],
            });
        }
        else
        {
            content.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            content.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var image = new Image
            {
                Stretch = Stretch.Uniform,
                Margin = new Thickness(5, 4, 5, 0),
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            if (!string.IsNullOrWhiteSpace(capeUrl))
            {
                try { image.Source = new BitmapImage(new Uri(capeUrl)); } catch { }
            }
            content.Children.Add(image);
            var name = new TextBlock
            {
                Text = label,
                FontSize = (double)Application.Current.Resources["FontSizeCaption"],
                MaxWidth = 88,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
                Margin = new Thickness(2, 0, 2, 3),
                Foreground = (Brush)Application.Current.Resources["AppText2Brush"],
            };
            Grid.SetRow(name, 1);
            content.Children.Add(name);
        }

        tile.Content = content;
        ApplyCapeTileVisual(tile, active);
        tile.Click += async (_, _) =>
        {
            var result = await App.Backend.SetCapeAsync(capeId);
            ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (result.Ok) await LoadProfileAsync();
        };
        return tile;
    }

    private static void ApplyCapeTileVisual(Button tile, bool active)
    {
        tile.Background = (Brush)Application.Current.Resources[
            active ? "AccountGreenSoftBrush" : "AppChipBrush"];
        tile.BorderBrush = (Brush)Application.Current.Resources[
            active ? "AccountGreenBrush" : "CardStrokeBrush"];
    }

    /// <summary>把当前模型档位画在「经典 Steve / 纤细 Alex」两个按钮上：选中项淡粉底 + 粉描边 + 勾。</summary>
    private void ApplyVariantVisual(string variant)
    {
        var classic = variant.Equals("CLASSIC", StringComparison.OrdinalIgnoreCase);
        var slim = variant.Equals("SLIM", StringComparison.OrdinalIgnoreCase);
        ClassicCheck.Visibility = classic ? Visibility.Visible : Visibility.Collapsed;
        SlimCheck.Visibility = slim ? Visibility.Visible : Visibility.Collapsed;
        StyleVariantButton(ClassicRow, classic);
        StyleVariantButton(SlimRow, slim);
    }

    private static void StyleVariantButton(Button button, bool selected)
    {
        button.Background = (Brush)Application.Current.Resources[
            selected ? "AccountGreenSoftBrush" : "AppChipBrush"];
        button.BorderBrush = (Brush)Application.Current.Resources[
            selected ? "AccountGreenBrush" : "CardStrokeBrush"];
        button.BorderThickness = new Thickness(1);
    }

    private async void OpenMinecraftProfile_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            await Launcher.LaunchUriAsync(new Uri("https://www.minecraft.net/profile/skin"));
        }
        catch (Exception ex)
        {
            ShowToast($"打开官网失败：{ex.Message}", InfoBarSeverity.Error);
        }
    }

    // ------------------------------------------------------------------ //
    // 动作
    // ------------------------------------------------------------------ //
    private async void UploadSkin_Click(object sender, RoutedEventArgs e)
    {
        var picker = new Windows.Storage.Pickers.FileOpenPicker();
        WinRT.Interop.InitializeWithWindow.Initialize(picker, App.MainWindow.WindowHandle);
        picker.FileTypeFilter.Add(".png");
        var file = await picker.PickSingleFileAsync();
        if (file is null) return;

        if (_activeAccount is { } localSkinAccount && !IsMicrosoft(localSkinAccount))
        {
            var localResult = await App.Backend.SetLocalSkinAsync(localSkinAccount.Key, file.Path);
            ShowToast(localResult.Message, localResult.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (!localResult.Ok) return;
            await App.MainWindow.RefreshShellAsync(force: true);
            ApplySnapshot();
            return;
        }

        var variant = string.Equals(_profile?.Variant, "SLIM", StringComparison.OrdinalIgnoreCase) ? "slim" : "classic";
        SetProfileBusy(true);
        var result = await App.Backend.UploadSkinAsync(file.Path, variant);
        ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (!result.Ok)
        {
            SetProfileBusy(false);
            return;
        }

        await App.MainWindow.RefreshShellAsync(force: true);
        ApplySnapshot();
        await LoadProfileAsync();
    }

    private async void Variant_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string variant }) return;
        if (_activeAccount is { } localVariantAccount && !IsMicrosoft(localVariantAccount))
        {
            var localResult = await App.Backend.SetLocalSkinVariantAsync(localVariantAccount.Key, variant);
            ShowToast(localResult.Message, localResult.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (!localResult.Ok) return;
            await App.MainWindow.RefreshShellAsync(force: true);
            ApplySnapshot();
            return;
        }

        SetProfileBusy(true);
        var result = await App.Backend.SetSkinVariantAsync(variant);
        ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (!result.Ok)
        {
            SetProfileBusy(false);
            return;
        }

        await App.MainWindow.RefreshShellAsync(force: true);
        ApplySnapshot();
        await LoadProfileAsync();
    }

    private async void ResetSkin_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAccount is { } localResetAccount && !IsMicrosoft(localResetAccount))
        {
            var localDialog = new ContentDialog
            {
                Title = "移除本地皮肤",
                Content = "会把这台电脑上给离线账户设的皮肤删掉，恢复默认模型。继续吗？",
                PrimaryButtonText = "移除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
            };
            if (await localDialog.ShowAsync() != ContentDialogResult.Primary) return;
            var localResult = await App.Backend.ClearLocalSkinAsync(localResetAccount.Key);
            ShowToast(localResult.Message, localResult.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
            if (!localResult.Ok) return;
            await App.MainWindow.RefreshShellAsync(force: true);
            ApplySnapshot();
            return;
        }

        var dialog = new ContentDialog
        {
            Title = "恢复默认皮肤",
            Content = "会把这个账户的皮肤恢复成默认 Steve/Alex，当前皮肤不会保留。继续吗？",
            PrimaryButtonText = "恢复默认",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        SetProfileBusy(true);
        var result = await App.Backend.ResetSkinAsync();
        ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (!result.Ok)
        {
            SetProfileBusy(false);
            return;
        }

        await App.MainWindow.RefreshShellAsync(force: true);
        ApplySnapshot();
        await LoadProfileAsync();
    }

    private void RefreshProfile_Click(object sender, RoutedEventArgs e) => _ = LoadProfileAsync();

    /// <summary>模型左上角的显眼刷新：转一圈再重拉档案，成功后 LoadProfileAsync 会再刷一次模型。</summary>
    private void SkinRefresh_Click(object sender, RoutedEventArgs e)
    {
        Motion.Spin(SkinRefreshIcon);
        _ = LoadProfileAsync();
    }

    // 退出账户：单击只提示，双击才真正退出（双击本身就是确认，不再弹确认框）。
    private void Logout_Click(object sender, RoutedEventArgs e) =>
        ShowToast("双击以从启动器退出此账户的登录", InfoBarSeverity.Informational);

    private async void Logout_DoubleTapped(object sender, Microsoft.UI.Xaml.Input.DoubleTappedRoutedEventArgs e)
    {
        e.Handled = true;
        var active = _activeAccount;
        if (active is null) return;

        var result = await App.Backend.DeleteAccountAsync(active.Key);
        ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (!result.Ok) return;
        await App.MainWindow.RefreshShellAsync(force: true);
        ApplySnapshot();
    }

    private async void Switch_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string key }) await SwitchToAsync(key);
    }

    private async Task SwitchToAsync(string key)
    {
        var result = await App.Backend.UseAccountAsync(key);
        ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        if (!result.Ok) return;
        await App.MainWindow.RefreshShellAsync(force: true);
        ApplySnapshot();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string key }) return;
        var name = key.Contains('|') ? key[(key.IndexOf('|') + 1)..] : key;
        var dialog = new ContentDialog
        {
            Title = "删除账户",
            Content = $"删除「{name}」？本地登录信息会被移除，无法撤销。",
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = await App.Backend.DeleteAccountAsync(key);
        ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Error);
        if (!result.Ok) return;
        await App.MainWindow.RefreshShellAsync(force: true);
        ApplySnapshot();
    }

    private bool _msBusy;

    private async void AddMicrosoft_Click(object sender, RoutedEventArgs e)
    {
        if (_msBusy) return;

        _msBusy = true;
        var start = await App.Backend.StartMicrosoftAsync();
        if (!start.Ok || string.IsNullOrWhiteSpace(start.DeviceCode))
        {
            _msBusy = false;
            ShowToast(string.IsNullOrWhiteSpace(start.Message) ? "获取设备码失败。" : start.Message,
                InfoBarSeverity.Error);
            return;
        }

        var codeText = new TextBlock
        {
            Text = start.UserCode,
            FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas"),
            FontSize = 28,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextSelectionEnabled = true,
        };

        var copied = false;
        try
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(start.UserCode);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            copied = true;
        }
        catch
        {
            // 剪贴板被占用时保留手动复制入口。
        }

        var hint = new TextBlock
        {
            Text = copied
                ? "授权码已复制到剪贴板，3 秒后打开微软授权页。"
                : $"3 秒后打开微软授权页；没打开的话访问 {start.VerificationUri}。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var status = new TextBlock
        {
            Text = "3 秒后打开浏览器…",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
        };
        var copy = new Button
        {
            Content = "复制授权码",
            HorizontalAlignment = HorizontalAlignment.Center,
            Visibility = copied ? Visibility.Collapsed : Visibility.Visible,
        };
        AutomationProperties.SetName(copy, "复制授权码");
        copy.Click += (_, _) =>
        {
            var package = new Windows.ApplicationModel.DataTransfer.DataPackage();
            package.SetText(start.UserCode);
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(package);
            status.Text = "授权码已复制，请粘贴到微软页面。";
            copy.Visibility = Visibility.Collapsed;
        };

        var panel = new StackPanel { Width = 400, Spacing = 12 };
        panel.Children.Add(hint);
        panel.Children.Add(codeText);
        panel.Children.Add(copy);
        panel.Children.Add(status);
        panel.Children.Add(new TextBlock
        {
            Text = "授权码 15 分钟内有效，登录成功后会切换到该账户。",
            FontSize = 11.5,
        });

        var dialog = new ContentDialog
        {
            Title = "微软账户登录",
            Content = panel,
            CloseButtonText = "取消",
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };

        using var cts = new CancellationTokenSource();
        bool succeeded = false;
        string loggedName = "";
        var openTask = OpenAfterCountdownAsync(status, start.VerificationUri, cts.Token);
        var pollTask = PollMicrosoftAsync(start, status, dialog, cts.Token,
            name => { succeeded = true; loggedName = name; });
        await dialog.ShowAsync();
        cts.Cancel();
        try { await pollTask; } catch (OperationCanceledException) { }
        try { await openTask; } catch (OperationCanceledException) { }
        _msBusy = false;

        if (succeeded)
        {
            await App.MainWindow.RefreshShellAsync(force: true);
            ApplySnapshot();
            ShowToast($"已登录 {loggedName}，账户已切换。", InfoBarSeverity.Success);
        }
    }

    /// <summary>登录弹窗先出、验证码先复制，倒计时结束再打开浏览器，避免浏览器抢焦点。</summary>
    private static async Task OpenAfterCountdownAsync(TextBlock status, string uri, CancellationToken token)
    {
        for (var seconds = 3; seconds >= 1; seconds--)
        {
            if (token.IsCancellationRequested) return;
            status.Text = $"{seconds} 秒后打开浏览器…";
            try
            {
                await Task.Delay(1000, token);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }

        if (token.IsCancellationRequested) return;
        try
        {
            var opened = await Launcher.LaunchUriAsync(new Uri(uri));
            status.Text = opened
                ? "已打开浏览器，请在微软页面完成授权。"
                : $"没能打开浏览器，请手动访问 {uri}。";
        }
        catch
        {
            status.Text = $"没能打开浏览器，请手动访问 {uri}。";
        }
    }

    private async Task PollMicrosoftAsync(MicrosoftStartResult start, TextBlock status,
        ContentDialog dialog, CancellationToken token, Action<string> onSuccess)
    {
        var deadline = DateTime.UtcNow.AddSeconds(Math.Max(60, start.ExpiresInSeconds));
        await Task.Delay(500, token);
        while (!token.IsCancellationRequested && DateTime.UtcNow < deadline)
        {
            await Task.Delay(TimeSpan.FromSeconds(Math.Max(3, start.IntervalSeconds)), token);
            var poll = await App.Backend.PollMicrosoftAsync(start.DeviceCode);
            if (poll.Pending)
            {
                status.DispatcherQueue.TryEnqueue(() => status.Text = "等待授权完成...");
                continue;
            }

            if (poll.Ok)
            {
                status.DispatcherQueue.TryEnqueue(() => status.Text = $"登录成功：{poll.Name}");
                onSuccess(poll.Name);
                dialog.DispatcherQueue.TryEnqueue(() => dialog.Hide());
                return;
            }

            status.DispatcherQueue.TryEnqueue(() => status.Text = poll.Message);
            return;
        }

        if (!token.IsCancellationRequested)
        {
            status.DispatcherQueue.TryEnqueue(() => status.Text = "验证码已过期，请取消后重新获取。");
        }
    }

    private async void AddOffline_Click(object sender, RoutedEventArgs e)
    {
        var input = new TextBox
        {
            PlaceholderText = "请输入玩家名",
            MinWidth = 180,
        };
        var hint = new TextBlock
        {
            Text = "离线账户不适用于开启了正版验证的服务器或正版账号玩家所开启的非自定义局域网联机。"
                 + "此外，在联机时为了显示自己的皮肤，需要玩家同时安装皮肤补丁模组。"
                 + "离线账户仅供体验使用，如需完整的游玩体验，请支持正版 Minecraft。",
            FontSize = 11.5,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
        };
        var buyLink = new HyperlinkButton
        {
            Content = "购买 Minecraft",
            Padding = new Thickness(0, 0, 0, 0),
            FontSize = 11.5,
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        buyLink.Click += async (_, _) =>
            await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.minecraft.net/store"));
        var model = new RadioButtons
        {
            Header = "皮肤模型",
            MaxColumns = 2,
            SelectedIndex = 0,
        };
        model.Items.Add("经典 Steve");
        model.Items.Add("纤细 Alex");
        var body = new StackPanel { Spacing = 12, Width = 340 };
        body.Children.Add(input);
        body.Children.Add(hint);
        body.Children.Add(buyLink);
        body.Children.Add(model);

        var dialog = new ContentDialog
        {
            Title = "添加离线账户",
            Content = body,
            PrimaryButtonText = "添加",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot,
        };
        if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;

        var result = await App.Backend.AddOfflineAccountAsync(input.Text.Trim(), model.SelectedIndex == 1);
        ShowToast(result.Message, result.Ok ? InfoBarSeverity.Success : InfoBarSeverity.Warning);
        if (!result.Ok) return;

        await App.MainWindow.RefreshShellAsync(force: true);
        ApplySnapshot();
    }

    // ------------------------------------------------------------------ //
    // 账户信息卡 / 账户权限（Mojang /player/attributes）
    // ------------------------------------------------------------------ //
    /// <summary>把账户快照里的基本信息写进键值行；皮肤与披风在上面的档案卡里看。</summary>
    private void ApplyInfoRows(AccountInfo? active)
    {
        if (active is null) return;

        InfoPlayerName.Text = string.IsNullOrWhiteSpace(active.Name) ? "—" : active.Name;
        InfoUuid.Text = string.IsNullOrWhiteSpace(active.Uuid) ? "—" : active.Uuid;
        InfoAccountType.Text = active.TypeDisplay;
        var shortUuid = string.IsNullOrWhiteSpace(active.Uuid) ? "" : active.Uuid.Replace("-", "");
        // 小字固定为「显示玩家账户的基础信息」（在 XAML 里），不再被改写成「类型 · UUID 前缀」
    }

    private async Task LoadPermsAsync(bool force = false)
    {
        if (_activeAccount is null || !IsMicrosoft(_activeAccount)) return;
        if (_loadingPerms) return;
        if (!force && _perms is not null)
        {
            RenderPerms(_perms);
            return;
        }

        _loadingPerms = true;
        SetPermsBusy(true);
        try
        {
            var result = await App.Backend.GetAccountPermsAsync();
            if (result.Ok)
            {
                _perms = result;
                RenderPerms(result);
            }
            else
            {
                SetPermsError(string.IsNullOrEmpty(result.Message) ? "账户权限读取失败。" : result.Message);
            }
        }
        catch (Exception ex)
        {
            SetPermsError(ex.Message);
        }
        finally
        {
            _loadingPerms = false;
            SetPermsBusy(false);
        }
    }

    private void RenderPerms(AccountPermsResult perms)
    {
        _applyingPerms = true;
        PermFriendsSwitch.IsOn = perms.Friends.Equals("ENABLED", StringComparison.OrdinalIgnoreCase);
        PermInvitesSwitch.IsOn = perms.AcceptInvites.Equals("ENABLED", StringComparison.OrdinalIgnoreCase);
        PermProfanitySwitch.IsOn = perms.ProfanityFilterOn;
        _applyingPerms = false;

        _permsUnavailable = false;
        PermsReloginButton.Visibility = Visibility.Collapsed;
        PermsStatus.Foreground = (Brush)Application.Current.Resources["AppText3Brush"];
        SetPermsStatus(perms.Banned
            ? "该账户当前无法使用在线服务。"
            : "");
        PermPrivileges.Text = BuildPrivilegesText(perms);
    }

    private static string BuildPrivilegesText(AccountPermsResult perms)
    {
        if (perms.Privileges.Count == 0) return "";
        var names = new (string Key, string Label)[]
        {
            ("onlineChat", "在线聊天"),
            ("multiplayerServer", "多人游戏"),
            ("multiplayerRealms", "Realms"),
        };
        var enabled = new List<string>();
        var disabled = new List<string>();
        foreach (var (key, label) in names)
        {
            if (!perms.Privileges.TryGetValue(key, out var value)) continue;
            (value ? enabled : disabled).Add(label);
        }

        var parts = new List<string>();
        if (enabled.Count > 0) parts.Add("可用：" + string.Join("、", enabled));
        if (disabled.Count > 0) parts.Add("不可用：" + string.Join("、", disabled));
        return string.Join("；", parts);
    }

    private void SetPermsStatus(string message)
    {
        PermsStatus.Text = message;
        PermsFeedback.Visibility = string.IsNullOrWhiteSpace(message)
            && PermsReloginButton.Visibility != Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetPermsBusy(bool busy)
    {
        PermsLoadingRing.IsActive = busy;
        PermsLoadingRing.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        var enabled = !busy && !_permsUnavailable;
        PermFriendsSwitch.IsEnabled = enabled;
        PermInvitesSwitch.IsEnabled = enabled;
        PermProfanitySwitch.IsEnabled = enabled;
        if (busy)
        {
            PermsStatus.Foreground = (Brush)Application.Current.Resources["AppText3Brush"];
            SetPermsStatus("");
        }
    }

    private void SetPermsError(string message)
    {
        var needsLogin = message.Contains("登录") || message.Contains("凭据");
        _permsUnavailable = needsLogin;
        if (needsLogin)
        {
            PermFriendsSwitch.IsEnabled = false;
            PermInvitesSwitch.IsEnabled = false;
            PermProfanitySwitch.IsEnabled = false;
        }
        PermsReloginButton.Visibility = needsLogin ? Visibility.Visible : Visibility.Collapsed;
        PermsStatus.Foreground = (Brush)Application.Current.Resources["AppBadBrush"];
        SetPermsStatus(message);
    }

    private async void Perm_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applyingPerms || _loadingPerms) return;
        if (sender is not ToggleSwitch toggle) return;
        var kind = toggle.Tag as string ?? "";
        if (kind.Length == 0) return;

        var value = toggle.IsOn ? "ENABLED" : "DISABLED";
        if (kind == "profanity") value = toggle.IsOn ? "ON" : "OFF";

        SetPermsBusy(true);
        SetPermsStatus("正在写入 Minecraft 账户…");
        try
        {
            var result = await App.Backend.SetAccountPermAsync(kind, value);
            if (!result.Ok)
            {
                SetPermsError(string.IsNullOrEmpty(result.Message) ? "写入失败。" : result.Message);
                if (_perms is not null) RenderPerms(_perms);
                return;
            }

            _perms = result;
            RenderPerms(result);
        }
        catch (Exception ex)
        {
            SetPermsError(ex.Message);
            if (_perms is not null) RenderPerms(_perms);
        }
        finally
        {
            SetPermsBusy(false);
        }
    }

    private void ShowToast(string message, InfoBarSeverity severity)
    {
        Toast.Message = message;
        Toast.Severity = severity;
        Toast.IsOpen = true;
    }

    // ------------------------------------------------------------------ //
    // 子视图切换
    // ------------------------------------------------------------------ //
    /// <summary>其他页（如首页账户条的「皮肤与披风」）要求打开账户页后直接进入某个子页。</summary>
    public static string? PendingSubView { get; set; }

    private bool _infoAnimating;

#if DEBUG
    /// <summary>仅 DEBUG：直接把玩家档案卡置为展开/收起（走与点击相同的动画路径），供截图。</summary>
    public void DebugSetInfoExpanded(bool expanded)
    {
        var open = InfoBodyCard.Visibility == Visibility.Visible;
        App.Trace("Account info open=" + open + " want=" + expanded + " animating=" + _infoAnimating);
        if (open != expanded) InfoToggle_Click(InfoToggleRow, new RoutedEventArgs());
    }
#endif

    /// <summary>
    /// 玩家档案折叠行：展开 = 高度 0 → 目标高度（250 ms ease-out）+ 内容淡入；收起反向。
    /// 容器按自身尺寸裁剪，所以内容在高度还没撑开时不会溢出卡外。
    /// </summary>
    private void InfoToggle_Click(object sender, RoutedEventArgs e)
    {
        App.Trace("Account InfoToggle_Click animating=" + _infoAnimating + " bodyVisible=" + InfoBodyCard.Visibility);
        if (_infoAnimating) return;
        var show = InfoBodyCard.Visibility != Visibility.Visible;
        InfoChevron.Glyph = show ? "\uE70E" : "\uE70D";   // 上箭头=已展开，下箭头=已收起
        InfoToggleRow.CornerRadius = show ? new CornerRadius(7, 7, 0, 0) : new CornerRadius(7);

        InfoBodyCard.SizeChanged -= InfoBodyCard_SizeChanged;
        InfoBodyCard.SizeChanged += InfoBodyCard_SizeChanged;

        var animations = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
        double target;
        if (show)
        {
            InfoBodyCard.Height = double.NaN;
            InfoBodyCard.Visibility = Visibility.Visible;
            InfoBodyCard.Measure(new Windows.Foundation.Size(Math.Max(1, InfoCard.ActualWidth), double.PositiveInfinity));
            target = InfoBodyCard.DesiredSize.Height;
            InfoBodyCard.Height = 0;
            InfoBodyCard.Opacity = 0;
            AddInfoAnimation(animations, InfoBodyCard, "Height", 0, target);
            AddInfoAnimation(animations, InfoBodyCard, "Opacity", 0, 1);
        }
        else
        {
            target = InfoBodyCard.ActualHeight;
            InfoBodyCard.Height = target;
            AddInfoAnimation(animations, InfoBodyCard, "Height", target, 0);
            AddInfoAnimation(animations, InfoBodyCard, "Opacity", 1, 0);
        }

        _infoAnimating = true;
        animations.Completed += (_, _) =>
        {
            _infoAnimating = false;
            if (show)
            {
                InfoBodyCard.Height = double.NaN;   // 展开后恢复自适应，窗口缩放时不会被固定死
                InfoBodyCard.Opacity = 1;
            }
            else
            {
                InfoBodyCard.Visibility = Visibility.Collapsed;
                InfoBodyCard.Height = double.NaN;
                InfoBodyCard.Opacity = 1;
            }
        };
        animations.Begin();
    }

    private void InfoBodyCard_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        InfoBodyCard.Clip = new Microsoft.UI.Xaml.Media.RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, 0, e.NewSize.Width, e.NewSize.Height),
        };
    }

    private static void AddInfoAnimation(Microsoft.UI.Xaml.Media.Animation.Storyboard storyboard,
        DependencyObject target, string property, double from, double to)
    {
        var animation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
        {
            From = from,
            To = to,
            Duration = new Duration(TimeSpan.FromMilliseconds(250)),
            EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase
            {
                EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut,
            },
            EnableDependentAnimation = true,   // Height 不是合成器属性，必须显式允许
        };
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(animation, target);
        Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(animation, property);
        storyboard.Children.Add(animation);
    }

    private void SkinEntry_Click(object sender, RoutedEventArgs e)
    {
        ShowSkinView(true);
        if (_activeAccount is not null && IsMicrosoft(_activeAccount) && _profile is null)
            _ = LoadProfileAsync();
    }

    private void FriendsEntry_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAccount is null || !IsMicrosoft(_activeAccount)) return;
        ShowFriendsView(true);
        _ = LoadFriendsAsync(force: true);
    }

    private void BackToRoot_Click(object sender, RoutedEventArgs e) => ShowRootView();

    private void CrumbRoot_Tapped(object sender, TappedRoutedEventArgs e) => ShowRootView();

    private void ShowRootView()
    {
        RootView.Visibility = Visibility.Visible;
        SkinView.Visibility = Visibility.Collapsed;
        FriendsView.Visibility = Visibility.Collapsed;
        CrumbSep.Visibility = Visibility.Collapsed;
        CrumbSkin.Visibility = Visibility.Collapsed;
        CrumbFriends.Visibility = Visibility.Collapsed;
        HeaderBackButton.Visibility = Visibility.Collapsed;
        CrumbRoot.FontWeight = Microsoft.UI.Text.FontWeights.SemiBold;
        CrumbRoot.Opacity = 1;
        Motion.Settle(RootView);
    }

    private void ShowSkinView(bool show)
    {
        RootView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SkinView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        FriendsView.Visibility = Visibility.Collapsed;
        CrumbSep.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CrumbSkin.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CrumbFriends.Visibility = Visibility.Collapsed;
        HeaderBackButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CrumbRoot.FontWeight = show ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        CrumbRoot.Opacity = show ? 0.72 : 1;
        if (show) Motion.Settle(SkinView);
        else Motion.Settle(RootView);
    }

    private void ShowFriendsView(bool show)
    {
        SetFriendsAutoRefresh(show);
        RootView.Visibility = show ? Visibility.Collapsed : Visibility.Visible;
        SkinView.Visibility = Visibility.Collapsed;
        FriendsView.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CrumbSep.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CrumbFriends.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CrumbSkin.Visibility = Visibility.Collapsed;
        HeaderBackButton.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        CrumbRoot.FontWeight = show ? Microsoft.UI.Text.FontWeights.Normal : Microsoft.UI.Text.FontWeights.SemiBold;
        CrumbRoot.Opacity = show ? 0.72 : 1;
        if (show) Motion.Settle(FriendsView);
        else Motion.Settle(RootView);
    }

    // ------------------------------------------------------------------ //
    // 好友
    // ------------------------------------------------------------------ //
    private async Task LoadFriendsAsync(bool force = false)
    {
        // 上一次读取还没结束时点刷新：不再静默丢掉，而是记下来，这次结束后立刻再读一遍
        if (_loadingFriends)
        {
            if (force) _friendsReloadPending = true;
            return;
        }
        if (_friendsLoaded && !force)
        {
            RenderFriends();
            return;
        }

        _loadingFriends = true;
        FriendsLoadingRing.IsActive = true;
        FriendsLoadingRing.Visibility = Visibility.Visible;
        FriendsRefreshButton.IsEnabled = false;
        SetFriendsStatus("");
        try
        {
            var result = await App.Backend.GetFriendsAsync();
            if (!result.Ok)
            {
                // 到 Mojang 的网络偶发失败很常见（慢网络 / 代理），自动重试一次再认定失败
                SetFriendsStatus("");
                result = await App.Backend.GetFriendsAsync();
            }

            if (result.Ok)
            {
                _friends = result;
                _friendsLoaded = true;
                App.Online.UpdateFriends((result.Friends ?? new()).Select(f => (f.Id, f.Name)));
                RenderFriends();
                // 显示上次成功更新的时间，让人一眼看出列表是不是新的
                var note = $"上次更新 {DateTime.Now:HH:mm:ss}";
                FriendsUpdatedAt.Text = note;
                SetFriendsStatus(result.Message ?? "");
            }
            else
            {
                if (_friends is null) FriendsSummary.Text = "好友列表读取失败";
                // 保留旧列表，但明确说"显示的是旧数据"，不要让人以为刷新成功了
                var stale = _friends is null ? "" : "；下面显示的是上一次的结果，可能不是最新的";
                SetFriendsStatus($"读取失败：{result.Message}{stale}。网络慢或不稳定时可再点一次刷新。", error: true);
            }
        }
        finally
        {
            _loadingFriends = false;
            FriendsLoadingRing.IsActive = false;
            FriendsLoadingRing.Visibility = Visibility.Collapsed;
            FriendsRefreshButton.IsEnabled = true;
            if (_friendsReloadPending)
            {
                _friendsReloadPending = false;
                _ = LoadFriendsAsync(force: true);
            }
        }
    }

    // 好友页打开期间每 60 秒自动刷新一次：新好友 / 好友上下线不用手动重进页面
    private DispatcherTimer? _friendsTimer;

    private void SetFriendsAutoRefresh(bool on)
    {
        if (!on)
        {
            _friendsTimer?.Stop();
            return;
        }
        if (_friendsTimer is null)
        {
            _friendsTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(60) };
            _friendsTimer.Tick += (_, _) =>
            {
                // 离开好友页 / 页面已卸载就停掉；读取中就跳过这一轮
                if (!IsLoaded || FriendsView.Visibility != Visibility.Visible) { _friendsTimer?.Stop(); return; }
                if (_loadingFriends) return;
                _ = LoadFriendsAsync(force: true);
            };
        }
        _friendsTimer.Start();
    }

    private void RenderFriends()
    {
        var result = _friends;
        if (result is null) return;

        var friends = result.Friends ?? new List<FriendInfo>();
        var incoming = result.Incoming ?? new List<FriendInfo>();
        var outgoing = result.Outgoing ?? new List<FriendInfo>();

        FriendsSummary.Text = "好友";

        // 提示：上报失败 / 因为游戏在运行或用户关了「显示在线」而没有刷新在线状态
        var hint = !string.IsNullOrWhiteSpace(result.PresenceError)
            ? $"在线状态暂时不可用：{result.PresenceError}"
            : result.PresenceSkipped switch
            {
                "online-disabled" => "联机功能未启用，不显示好友在线状态。",
                "game-running" => "游戏运行中：没有刷新在线状态，避免覆盖游戏里的状态。",
                "disabled" => "已关闭「向好友显示在线」，所以看不到好友的在线状态。",
                _ => "",
            };
        FriendsPresenceHint.Text = hint;
        FriendsPresenceHint.Visibility = hint.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // 排序：在用 MeowLauncher 的在线好友 → 其他在线好友 → 离线
        friends = friends
            .OrderByDescending(friend => friend.Meow)
            .ThenByDescending(friend => friend.Online)
            .ThenBy(friend => friend.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        FriendList.Children.Clear();
        foreach (var friend in friends)
        {
            AddFriendRow(FriendList, friend, "REMOVE", "删除", destructive: true);
        }
        FriendEmpty.Visibility = friends.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

        IncomingList.Children.Clear();
        foreach (var request in incoming)
        {
            AddFriendRow(IncomingList, request, "ADD", "接受", destructive: false);
        }
        IncomingSection.Visibility = incoming.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        // 好友申请常驻显示：收到的和已发送的都没有时，给出明确的空态文字
        RequestsEmptyCard.Visibility = incoming.Count == 0 && outgoing.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        OutgoingList.Children.Clear();
        foreach (var request in outgoing)
        {
            AddFriendRow(OutgoingList, request, "REMOVE", "取消", destructive: false);
        }
        OutgoingSection.Visibility = outgoing.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>好友头像：有皮肤渲染出的头像就显示头像，否则退回首字母。</summary>
    private static UIElement FriendAvatarContent(FriendInfo friend)
    {
        if (!string.IsNullOrWhiteSpace(friend.HeadPath) && System.IO.File.Exists(friend.HeadPath))
        {
            try
            {
                return new Image
                {
                    Source = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri(friend.HeadPath)),
                    Width = 30,
                    Height = 30,
                    Stretch = Stretch.Uniform,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                };
            }
            catch { /* 图片坏了就退回首字母 */ }
        }

        return new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(friend.Name) ? "?" : friend.Name[..1].ToUpperInvariant(),
            FontSize = 15,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = (Brush)Application.Current.Resources["AccountGreenBrush"],
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    private void AddFriendRow(StackPanel host, FriendInfo friend, string action, string actionText, bool destructive)
    {
        var row = new Grid { Height = 56, ColumnSpacing = 12 };
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var avatar = new Border
        {
            Width = 36,
            Height = 36,
            CornerRadius = new CornerRadius(10),
            Background = (Brush)Application.Current.Resources["AccountGreenSoftBrush"],
            VerticalAlignment = VerticalAlignment.Center,
            Child = FriendAvatarContent(friend),
        };
        Grid.SetColumn(avatar, 0);
        row.Children.Add(avatar);

        var subtitle = friend.Kind switch
        {
            "incoming" => "请求添加你为好友",
            "outgoing" => "等待对方接受",
            _ => friend.StatusText + (friend.Joinable ? " · 可加入" : ""),
        };
        var text = new StackPanel { Spacing = 2, VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock
        {
            Text = string.IsNullOrWhiteSpace(friend.Name) ? "未知玩家" : friend.Name,
            FontSize = (double)Application.Current.Resources["FontSizeBody"],
            Foreground = (Brush)Application.Current.Resources["AppTextBrush"],
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        text.Children.Add(new TextBlock
        {
            Text = subtitle,
            FontSize = (double)Application.Current.Resources["FontSizeCaption"],
            Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
        });
        Grid.SetColumn(text, 1);
        row.Children.Add(text);

        var button = new Button
        {
            Content = actionText,
            Style = (Style)Resources["PillButtonStyle"],
            VerticalAlignment = VerticalAlignment.Center,
            Tag = new FriendActionRequest(friend, action),
        };

        // 「邀请」：只对「在线且在用 MeowLauncher」的好友显示，并且自己的联机会话已经登录（走服务器推送）
        if (friend.Kind == "friend" && friend.Online && friend.Meow && App.Online.IsLoggedIn)
        {
            var invite = new Button
            {
                Content = "邀请",
                Style = (Style)Resources["PillButtonStyle"],
                VerticalAlignment = VerticalAlignment.Center,
                Tag = friend,
            };
            if (App.Online.IsInvitePending(friend.Id, App.Room.IsHost ? App.Room.Code : ""))
            {
                invite.Content = "已邀请";
                invite.IsEnabled = false;
            }
            invite.Click += InviteFriend_Click;
            var pair = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
            pair.Children.Add(invite);
            pair.Children.Add(button);
            if (destructive) button.Foreground = (Brush)Application.Current.Resources["AppBadBrush"];
            button.Click += FriendAction_Click;
            Grid.SetColumn(pair, 2);
            row.Children.Add(pair);
            host.Children.Add(row);
            return;
        }
        if (destructive)
        {
            button.Foreground = (Brush)Application.Current.Resources["AppBadBrush"];
        }
        AutomationProperties.SetName(button, $"{actionText} {friend.Name}");
        button.Click += FriendAction_Click;
        Grid.SetColumn(button, 2);
        row.Children.Add(button);

        host.Children.Add(row);
    }

    private async void InviteFriend_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: FriendInfo friend } button) return;
        button.IsEnabled = false;
        try
        {
            // 自己正当房主时，邀请带上房间码：对方点「接受」就自动加入
            var (ok, delivered, message) = await App.Online.InviteAsync(friend.Id, App.Room.IsHost ? App.Room.Code : "");
            ShowToast(message, !ok ? InfoBarSeverity.Error : delivered ? InfoBarSeverity.Success : InfoBarSeverity.Informational);
            if (ok) { button.Content = "已邀请"; return; }       // 等对方回复，不再能点
            button.IsEnabled = true;
        }
        catch { button.IsEnabled = true; throw; }
    }

    private async void RefreshFriends_Click(object sender, RoutedEventArgs e) =>
        await LoadFriendsAsync(force: true);

    private async void AddFriend_Click(object sender, RoutedEventArgs e)
    {
        var name = FriendNameBox.Text.Trim();
        if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
        {
            SetFriendsStatus("玩家名需为 3–16 位字母、数字或下划线。", error: true);
            return;
        }

        AddFriendButton.IsEnabled = false;
        FriendNameBox.IsEnabled = false;
        SetFriendsStatus("正在发送好友请求…");
        try
        {
            var result = await App.Backend.UpdateFriendAsync("ADD", name, "");
            if (!result.Ok)
            {
                SetFriendsStatus(result.Message, error: true);
                return;
            }

            FriendNameBox.Text = "";
            SetFriendsStatus(result.Message);
            await LoadFriendsAsync(force: true);
        }
        finally
        {
            AddFriendButton.IsEnabled = true;
            FriendNameBox.IsEnabled = true;
        }
    }

    private async void FriendAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not FriendActionRequest request) return;
        if (request.Action == "REMOVE" && request.Friend.Kind == "friend")
        {
            var dialog = new ContentDialog
            {
                Title = "删除好友",
                Content = $"把「{request.Friend.Name}」从好友列表移除？",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = XamlRoot,
                RequestedTheme = ActualTheme,
            };
            if (await dialog.ShowAsync() != ContentDialogResult.Primary) return;
        }

        button.IsEnabled = false;
        SetFriendsStatus("正在更新好友关系…");
        var result = await App.Backend.UpdateFriendAsync(
            request.Action, request.Friend.Name, request.Friend.Id);
        button.IsEnabled = true;
        if (!result.Ok)
        {
            SetFriendsStatus(result.Message, error: true);
            return;
        }

        SetFriendsStatus(result.Message);
        await LoadFriendsAsync(force: true);
    }

    private void SetFriendsStatus(string message, bool error = false)
    {
        FriendsStatus.Text = message;
        FriendsStatus.Visibility = string.IsNullOrWhiteSpace(message)
            ? Visibility.Collapsed
            : Visibility.Visible;
        FriendsStatus.Foreground = (Brush)Application.Current.Resources[
            error ? "AppBadBrush" : "AppText3Brush"];
    }

    /// <summary>主页空态直接落到「添加账户」这一步，不用先找按钮。</summary>
    public void OpenAddAccountMenu()
    {
        if (!AddAccountButton.IsLoaded)
        {
            void OnLoaded(object sender, RoutedEventArgs e)
            {
                AddAccountButton.Loaded -= OnLoaded;
                AddAccountMenu_Click(AddAccountButton, new RoutedEventArgs());
            }
            AddAccountButton.Loaded += OnLoaded;
            return;
        }
        AddAccountMenu_Click(AddAccountButton, new RoutedEventArgs());
    }

    private void AddAccountMenu_Click(object sender, RoutedEventArgs e)
    {
        var flyout = new MenuFlyout();
        var ms = new MenuFlyoutItem { Text = "添加正版账户" };
        ms.Click += (_, _) => AddMicrosoft_Click(this, new RoutedEventArgs());
        var offline = new MenuFlyoutItem { Text = "添加离线账户" };
        offline.Click += (_, _) => AddOffline_Click(this, new RoutedEventArgs());
        flyout.Items.Add(ms);
        flyout.Items.Add(offline);
        flyout.ShowAt((FrameworkElement)sender);
    }

    private async void Rename_Click(object sender, RoutedEventArgs e)
    {
        if (_activeAccount is null || !IsMicrosoft(_activeAccount)) return;
        var current = _activeAccount.Name;

        var input = new TextBox
        {
            Text = current,
            PlaceholderText = "3–16 位字母、数字或下划线",
            MaxLength = 16,
            MinWidth = 180,
        };
        input.Loaded += (_, _) => input.SelectAll();

        var hint = new TextBlock
        {
            Text = "请确保您的新昵称长度为 3 到 16 个字符，仅包含字母、数字和下划线（_）。\n您每 30 天只能更改一次昵称。",
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["AppText3Brush"],
        };
        var status = new TextBlock
        {
            FontSize = 12,
            TextWrapping = TextWrapping.Wrap,
            Foreground = (Brush)Application.Current.Resources["AppWarnBrush"],
        };
        var panel = new StackPanel { Width = 360, Spacing = 10 };
        panel.Children.Add(hint);
        var nameRow = new Grid { ColumnSpacing = 8 };
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        nameRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var checkName = new Button { Content = "检查可用性", VerticalAlignment = VerticalAlignment.Center };
        Grid.SetColumn(checkName, 1);
        nameRow.Children.Add(input);
        nameRow.Children.Add(checkName);
        checkName.Click += async (_, _) =>
        {
            checkName.IsEnabled = false;
            var name = input.Text.Trim();
            var result = await App.Backend.CheckMinecraftNameAsync(name);
            if (input.Text.Trim() == name)
            {
                status.Text = result.Message;
                status.Foreground = (Brush)Application.Current.Resources[result.Ok ? "AppGoodBrush" : "AppBadBrush"];
            }
            checkName.IsEnabled = true;
        };
        input.TextChanged += (_, _) => status.Text = "";
        panel.Children.Add(nameRow);
        panel.Children.Add(status);

        var dialog = new ContentDialog
        {
            Title = "更改玩家名",
            Content = panel,
            PrimaryButtonText = "确认改名",
            CloseButtonText = "取消",
            DefaultButton = ContentDialogButton.Close,
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };

        dialog.PrimaryButtonClick += async (_, args) =>
        {
            var name = input.Text.Trim();
            if (!Regex.IsMatch(name, "^[A-Za-z0-9_]{3,16}$"))
            {
                args.Cancel = true;
                status.Text = "玩家名需为 3–16 位字母、数字或下划线。";
                return;
            }
            if (string.Equals(name, current, StringComparison.OrdinalIgnoreCase))
            {
                args.Cancel = true;
                status.Text = "和当前名称相同，不需要改。";
                return;
            }

            var deferral = args.GetDeferral();
            dialog.IsPrimaryButtonEnabled = false;
            status.Text = "正在提交改名…";
            var result = await App.Backend.RenameMinecraftAsync(name);
            if (!result.Ok)
            {
                args.Cancel = true;
                status.Text = result.Message;
                dialog.IsPrimaryButtonEnabled = true;
                deferral.Complete();
                return;
            }

            deferral.Complete();
            await App.MainWindow.RefreshShellAsync(force: true);
            ApplySnapshot();
            await LoadProfileAsync();
            ShowToast(result.Message, InfoBarSeverity.Success);
        };

        await dialog.ShowAsync();
    }

    private void Home_Tapped(object sender, TappedRoutedEventArgs e)
        => App.MainWindow.Navigate("home");

    // ------------------------------------------------------------------ //
    // 动效：进场错峰 / 行悬停推箭头
    // ------------------------------------------------------------------ //
    private bool _entered;

    private void PlayEntrance()
    {
        if (_entered) return;
        _entered = true;
        Motion.Enter(PageHeader, 0, 8);
        if (AccountCard.Visibility == Visibility.Visible) Motion.Enter(AccountCard, 50, 12);
        Motion.Enter(SettingsTitle, 90, 8);
        for (var i = 0; i < SettingsCard.Children.Count; i++)
            if (SettingsCard.Children[i] is FrameworkElement row)
                Motion.Enter(row, 110 + i * 30, 10);
        Motion.Enter(OtherTitle, 290, 8);
        Motion.Enter(OtherCard, 320, 10);
    }

    private void WireRowHover()
    {
        foreach (var child in SettingsCard.Children)
        {
            if (child is not Button row) continue;
            row.PointerEntered += Row_PointerEntered;
            row.PointerExited += Row_PointerExited;
        }
    }

    private void Row_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button row && FindChevron(row) is { } chevron) Motion.Nudge(chevron, 3);
    }

    private void Row_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Button row && FindChevron(row) is { } chevron) Motion.Nudge(chevron, 0);
    }

    private static FontIcon? FindChevron(DependencyObject root)
    {
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FontIcon icon && icon.Glyph == "\uE76C") return icon;
            if (FindChevron(child) is { } nested) return nested;
        }
        return null;
    }
}
