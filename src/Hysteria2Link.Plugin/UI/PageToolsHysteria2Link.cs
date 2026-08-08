using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using PCL;
using PCL.Core.UI.Controls.SvgIcon;
using PCL.Core.UI.Theme;
using Hysteria2Link.Plugin.Mixins;
using Hysteria2Link.Plugin.Models;
using Hysteria2Link.Plugin.Services;

namespace Hysteria2Link.Plugin.UI;

/// <summary>
/// Hysteria2 联机页面（纯代码构建，不依赖 XAML 资源，避免插件程序集在
/// 可回收加载上下文中无法被 WPF 资源解析器定位的问题）。
/// </summary>
public sealed class PageToolsHysteria2Link : MyPageRight
{
    private readonly MyScrollViewer _panBack;
    private readonly StackPanel _panMain;
    private readonly StackPanel _panSelect;
    private readonly StackPanel _panActive;
    private readonly MyCard _cardActive;
    private readonly TextBlock _labActiveRole;
    private readonly TextBlock _labActiveRealm;
    private readonly TextBlock _labActiveEndpoint;
    private readonly TextBlock _labActiveMinecraft;
    private readonly TextBlock _labActiveDescription;
    private readonly TextBlock _labActiveState;
    private readonly MyButton _btnJoin;
    private readonly MyButton _btnPasteCode;
    private readonly MyButton _btnClearCode;
    private readonly MyTextBox _textGuestCode;
    private readonly MyTextBox _textRoomIntro;
    private readonly MyButton _btnCreate;
    private readonly MyButton _btnRefreshWorlds;
    private readonly MyButton _btnManualPort;
    private readonly MyComboBox _comboWorldList;
    private readonly MyIconTextButton _btnActiveCopy;
    private readonly MyIconTextButton _btnActiveStop;

    private bool _subscribed;
    private bool _worldsLoaded;
    private bool _refreshingWorlds;

    public PageToolsHysteria2Link()
    {
        _panBack = new MyScrollViewer
        {
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };

        _panMain = new StackPanel { Margin = new Thickness(15, 10, 15, 10) };

        var iconBrush = System.Windows.Application.Current?.TryFindResource("ColorBrushGray1") as Brush;

        _panSelect = new StackPanel();

        _btnJoin = new MyButton
        {
            Width = 58,
            Margin = new Thickness(10, 0, 0, 0),
            Text = "加入",
            ColorType = MyButton.ColorState.Highlight
        };
        _btnPasteCode = new MyButton
        {
            Width = 58,
            Margin = new Thickness(10, 0, 0, 0),
            Text = "粘贴"
        };
        _btnClearCode = new MyButton
        {
            Width = 58,
            Margin = new Thickness(10, 0, 0, 0),
            Text = "清空"
        };
        _textGuestCode = new MyTextBox
        {
            Height = 28,
            HintText = "房主分享的联机码（67 - 387 位）",
            MaxLength = 387
        };
        var joinDock = new DockPanel { Margin = new Thickness(10), Height = 28 };
        DockPanel.SetDock(_btnJoin, Dock.Right);
        DockPanel.SetDock(_btnPasteCode, Dock.Right);
        DockPanel.SetDock(_btnClearCode, Dock.Right);
        joinDock.Children.Add(_btnJoin);
        joinDock.Children.Add(_btnPasteCode);
        joinDock.Children.Add(_btnClearCode);
        joinDock.Children.Add(_textGuestCode);

        var joinCard = new MyCard { Title = "加入联机", Margin = new Thickness(10) };
        var joinPanel = new StackPanel { Margin = new Thickness(10, 30, 10, 10) };
        joinPanel.Children.Add(new TextBlock
        {
            Margin = new Thickness(10, 10, 10, -2),
            FontSize = 14,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap,
            Text = "加入步骤：1. 向房主要一份联机码（含房间介绍的联机码更长，介绍会出现在你的局域网列表里）；2. 点击「粘贴」自动读取剪贴板，或手动输入；3. 点击「加入」。插件会通过 realm.hy2.io 进行 UDP 打洞，成功后建立本机入口（127.0.0.1:随机端口）并广播到 Minecraft 局域网服务器列表，游戏中进入「多人游戏」即可看到房主的房间。"
        });
        joinPanel.Children.Add(joinDock);
        joinCard.Children.Add(joinPanel);
        _panSelect.Children.Add(joinCard);

        _btnCreate = new MyButton
        {
            Width = 58,
            Margin = new Thickness(10, 0, 0, 0),
            Text = "创建",
            ColorType = MyButton.ColorState.Highlight
        };
        _btnRefreshWorlds = new MyButton
        {
            Width = 58,
            Margin = new Thickness(10, 0, 0, 0),
            Text = "刷新"
        };
        _btnManualPort = new MyButton
        {
            Width = 82,
            Margin = new Thickness(10, 0, 0, 0),
            Text = "手动输入"
        };
        _comboWorldList = new MyComboBox
        {
            Height = 28,
            DropDownWidthSync = false
        };
        var createDock = new DockPanel { Margin = new Thickness(10), Height = 28 };
        DockPanel.SetDock(_btnCreate, Dock.Right);
        DockPanel.SetDock(_btnRefreshWorlds, Dock.Right);
        DockPanel.SetDock(_btnManualPort, Dock.Right);
        createDock.Children.Add(_btnCreate);
        createDock.Children.Add(_btnRefreshWorlds);
        createDock.Children.Add(_btnManualPort);
        createDock.Children.Add(_comboWorldList);

        _textRoomIntro = new MyTextBox
        {
            Height = 64,
            Margin = new Thickness(10, 10, 10, 0),
            MaxLength = 80,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalContentAlignment = VerticalAlignment.Top,
            HintText = "房间介绍（最多 80 字，可选；作为联机码 metadata 分享，好友加入后会填充到其局域网列表的世界介绍）"
        };

        var createCard = new MyCard { Title = "创建联机", Margin = new Thickness(10), CanSwap = true };
        var createPanel = new StackPanel { Margin = new Thickness(10, 30, 10, 10) };
        createPanel.Children.Add(new TextBlock
        {
            Margin = new Thickness(10, 10, 10, 0),
            FontSize = 14,
            LineHeight = 23,
            TextWrapping = TextWrapping.Wrap,
            Text = "创建步骤：1. 先在 Minecraft 中对局域网开放世界；2. 从列表选择检测到的世界，未检测到可点「手动输入」端口；3. 填写房间介绍（可选）；4. 点击「创建」。首次使用会从 GitHub Release 下载并校验 Hysteria2 内核，随后通过 STUN 注册 Realm、等待好友 UDP 打洞直连。"
        });
        createPanel.Children.Add(createDock);
        createPanel.Children.Add(_textRoomIntro);
        createCard.Children.Add(createPanel);
        _panSelect.Children.Add(createCard);

        _panSelect.Children.Add(new MyHint
        {
            Margin = new Thickness(10),
            Theme = MyHint.Themes.Yellow,
            Text = "Hysteria Realms 依赖 UDP 打洞，随机对称 NAT 等网络可能无法直连。realm.hy2.io 是无可用性保证的公益牵线服务；它不转发游戏流量，但会获知用于打洞的公网地址。联机码包含密钥与房间介绍，请勿公开。"
        });

        _panActive = new StackPanel { Visibility = Visibility.Collapsed };

        _labActiveRole = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontWeight = FontWeights.Bold };
        _labActiveRealm = new TextBlock { VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        _labActiveEndpoint = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontWeight = FontWeights.Bold,
            TextTrimming = TextTrimming.CharacterEllipsis
        };
        _labActiveMinecraft = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        _labActiveDescription = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.8
        };
        _labActiveState = new TextBlock
        {
            Margin = new Thickness(0, 3, 0, 0),
            TextWrapping = TextWrapping.Wrap,
            Opacity = 0.75
        };

        var infoGrid = new Grid { Margin = new Thickness(20, 38, 20, 16) };
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        infoGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(30) });
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        infoGrid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        AddInfoRow(infoGrid, 0, "lucide/user", _labActiveRole, iconBrush);
        AddInfoRow(infoGrid, 1, "lucide/globe", _labActiveRealm, iconBrush);
        AddInfoRow(infoGrid, 2, "lucide/link-2", _labActiveEndpoint, iconBrush);
        AddInfoRow(infoGrid, 3, "lucide/server", _labActiveMinecraft, iconBrush);
        AddInfoRow(infoGrid, 4, "lucide/file-text", _labActiveDescription, iconBrush, topAligned: true);
        AddInfoRow(infoGrid, 5, "lucide/info", _labActiveState, iconBrush, topAligned: true);

        _cardActive = new MyCard { Title = "联机信息", Margin = new Thickness(10) };
        _cardActive.Children.Add(infoGrid);
        _panActive.Children.Add(_cardActive);

        _btnActiveCopy = new MyIconTextButton
        {
            Margin = new Thickness(-2, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Text = "复制联机信息",
            SvgIcon = "lucide/copy"
        };
        _btnActiveStop = new MyIconTextButton
        {
            Margin = new Thickness(-2, 1, 0, 0),
            HorizontalAlignment = HorizontalAlignment.Left,
            Text = "退出联机",
            SvgIcon = "lucide/power"
        };
        var opCard = new MyCard { Title = "操作", Margin = new Thickness(10) };
        var opPanel = new StackPanel { Margin = new Thickness(10, 35, 10, 10) };
        opPanel.Children.Add(_btnActiveCopy);
        opPanel.Children.Add(_btnActiveStop);
        opCard.Children.Add(opPanel);
        _panActive.Children.Add(opCard);

        _panMain.Children.Add(_panSelect);
        _panMain.Children.Add(_panActive);
        _panBack.Content = _panMain;

        PanScroll = _panBack;
        Child = _panBack;

        _btnJoin.Click += BtnJoin_Click;
        _btnPasteCode.Click += BtnPasteCode_Click;
        _btnClearCode.Click += (_, _) => _textGuestCode.Text = string.Empty;
        _btnCreate.Click += BtnCreate_Click;
        _btnRefreshWorlds.Click += BtnRefreshWorlds_Click;
        _btnManualPort.Click += BtnManualPort_Click;
        _btnActiveCopy.Click += BtnActiveCopy_Click;
        _btnActiveStop.Click += BtnActiveStop_Click;
        Loaded += Page_Loaded;
        Unloaded += (_, _) => Unsubscribe();
    }

    private static void AddInfoRow(Grid grid, int row, string icon, TextBlock text, Brush? iconBrush, bool topAligned = false)
    {
        var svg = new SvgIcon
        {
            Width = 16,
            Height = 16,
            Icon = icon,
            IconBrush = iconBrush,
            VerticalAlignment = topAligned ? VerticalAlignment.Top : VerticalAlignment.Center,
            Margin = topAligned ? new Thickness(2, 3, 12, 0) : new Thickness(2, 0, 12, 0)
        };
        Grid.SetRow(svg, row);
        Grid.SetColumn(svg, 0);
        Grid.SetRow(text, row);
        Grid.SetColumn(text, 1);
        grid.Children.Add(svg);
        grid.Children.Add(text);
    }

    private HysteriaSessionService Service => PluginRuntime.Service;

    private async void Page_Loaded(object sender, RoutedEventArgs e)
    {
        Subscribe();
        if (!_worldsLoaded && Service.Snapshot.Phase == SessionPhase.Stopped)
            await RefreshLocalWorldsAsync();
    }

    private void Subscribe()
    {
        if (_subscribed)
            return;

        _subscribed = true;
        Service.SnapshotChanged += OnSnapshotChanged;
        RefreshSnapshot(Service.Snapshot);
    }

    private void Unsubscribe()
    {
        if (!_subscribed)
            return;

        _subscribed = false;
        Service.SnapshotChanged -= OnSnapshotChanged;
    }

    private async void BtnRefreshWorlds_Click(object sender, MouseButtonEventArgs e)
    {
        await RefreshLocalWorldsAsync();
    }

    private async Task RefreshLocalWorldsAsync()
    {
        if (_refreshingWorlds)
            return;

        _refreshingWorlds = true;
        _btnRefreshWorlds.IsEnabled = false;
        _btnCreate.IsEnabled = false;
        _comboWorldList.IsEnabled = false;
        _comboWorldList.Items.Clear();
        _comboWorldList.Items.Add("正在检测 Minecraft 世界...");
        _comboWorldList.SelectedIndex = 0;
        try
        {
            var worlds = await MinecraftServerDetector.FindAsync();
            _comboWorldList.Items.Clear();
            foreach (var world in worlds)
                _comboWorldList.Items.Add(new WorldChoice(world.Port, world.DisplayName));

            if (worlds.Count == 0)
            {
                _comboWorldList.Items.Add("未检测到已开放的世界");
                _comboWorldList.SelectedIndex = 0;
                _comboWorldList.IsEnabled = false;
            }
            else
            {
                _comboWorldList.SelectedIndex = 0;
                _comboWorldList.IsEnabled = true;
            }

            _worldsLoaded = true;
            Service.Log.Info($"本地 Minecraft 世界检测完成，共发现 {worlds.Count} 个世界。");
        }
        catch (Exception exception)
        {
            _comboWorldList.Items.Clear();
            _comboWorldList.Items.Add("检测失败，请手动输入端口");
            _comboWorldList.SelectedIndex = 0;
            Service.Log.Error("检测本地 Minecraft 世界失败", exception);
            HintService.Hint("自动检测 Minecraft 世界失败，请手动输入端口。", HintType.Error);
        }
        finally
        {
            _refreshingWorlds = false;
            _btnRefreshWorlds.IsEnabled = Service.Snapshot.Phase == SessionPhase.Stopped;
            _btnCreate.IsEnabled = Service.Snapshot.Phase == SessionPhase.Stopped;
        }
    }

    private async void BtnCreate_Click(object sender, MouseButtonEventArgs e)
    {
        if (_comboWorldList.SelectedItem is not WorldChoice world)
        {
            await StartHostFromManualPortAsync();
            return;
        }

        await StartHostAsync(world.Port);
    }

    private async void BtnManualPort_Click(object sender, MouseButtonEventArgs e)
    {
        await StartHostFromManualPortAsync();
    }

    private async Task StartHostFromManualPortAsync()
    {
        var defaultPort = _comboWorldList.SelectedItem is WorldChoice world ? world.Port.ToString() : "25565";
        var input = ModMain.MyMsgBoxInput(
            "手动输入端口",
            "请输入 Minecraft 对局域网开放后显示的端口。",
            defaultPort,
            hintText: "1 - 65535");
        if (string.IsNullOrWhiteSpace(input))
            return;

        if (!int.TryParse(input.Trim(), out var port) || port is <= 0 or > 65535)
        {
            HintService.Hint("请输入 1 到 65535 之间的 Minecraft 端口。", HintType.Error);
            return;
        }

        await StartHostAsync(port);
    }

    private async Task StartHostAsync(int port)
    {
        var description = _textRoomIntro.Text?.Trim();
        try
        {
            await Service.StartHostAsync(port, string.IsNullOrWhiteSpace(description) ? null : description);
            HintService.Hint("P2P 联机已创建，联机码已就绪。", HintType.Success);
        }
        catch (Exception exception)
        {
            HintService.Hint($"创建联机失败：{exception.Message}", HintType.Error);
        }
    }

    private async void BtnJoin_Click(object sender, MouseButtonEventArgs e)
    {
        try
        {
            await Service.StartGuestAsync(_textGuestCode.Text);
            HintService.Hint("P2P 连接已建立，本地入口已广播到 Minecraft。", HintType.Success);
        }
        catch (Exception exception)
        {
            HintService.Hint($"加入联机失败：{exception.Message}", HintType.Error);
        }
    }

    private async void BtnPasteCode_Click(object sender, MouseButtonEventArgs e)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                var text = Clipboard.GetText().Trim();
                if (string.IsNullOrWhiteSpace(text))
                {
                    HintService.Hint("剪贴板中没有可用的联机码。", HintType.Warning);
                    return;
                }

                _textGuestCode.Text = text;
                HintService.Hint("已粘贴联机码。", HintType.Success);
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
                if (attempt < 5)
                    await Task.Delay(50 * (attempt + 1));
            }
        }

        Service.Log.Error("读取剪贴板失败", lastException);
        HintService.Hint("剪贴板正被其他程序占用，请稍后重试。", HintType.Error);
    }

    private async void BtnActiveCopy_Click(object sender, ModBase.RouteEventArgs e)
    {
        var snapshot = Service.Snapshot;
        string? text = null;
        if (snapshot.Role == SessionRole.Host && !string.IsNullOrWhiteSpace(snapshot.Code))
            text = snapshot.Code;
        else if (snapshot.Role == SessionRole.Guest && snapshot.LocalPort is { } port)
            text = $"127.0.0.1:{port}";

        if (string.IsNullOrWhiteSpace(text))
        {
            HintService.Hint("当前没有可复制的联机信息。", HintType.Warning);
            return;
        }

        await CopyToClipboardAsync(text);
    }

    private async void BtnActiveStop_Click(object sender, ModBase.RouteEventArgs e)
    {
        try
        {
            await Service.StopAsync();
            HintService.Hint("联机已断开。", HintType.Success);
        }
        catch (Exception exception)
        {
            HintService.Hint($"断开联机失败：{exception.Message}", HintType.Error);
        }
    }

    private async Task CopyToClipboardAsync(string text)
    {
        Exception? lastException = null;
        for (var attempt = 0; attempt < 6; attempt++)
        {
            try
            {
                Clipboard.SetDataObject(text, copy: true);
                HintService.Hint("联机信息已复制。", HintType.Success);
                return;
            }
            catch (Exception exception)
            {
                lastException = exception;
                if (attempt < 5)
                    await Task.Delay(50 * (attempt + 1));
            }
        }

        Service.Log.Error("复制联机信息失败", lastException);
        HintService.Hint("剪贴板正被其他程序占用，请稍后重试。", HintType.Error);
    }

    private void OnSnapshotChanged(SessionSnapshot snapshot)
    {
        Dispatcher.BeginInvoke(() => RefreshSnapshot(snapshot));
    }

    private void RefreshSnapshot(SessionSnapshot snapshot)
    {
        var stopped = snapshot.Phase == SessionPhase.Stopped;
        var active = snapshot.Role != SessionRole.None && !stopped;
        _panSelect.Visibility = active ? Visibility.Collapsed : Visibility.Visible;
        _panActive.Visibility = active ? Visibility.Visible : Visibility.Collapsed;

        _btnJoin.IsEnabled = stopped;
        _btnPasteCode.IsEnabled = stopped;
        _btnClearCode.IsEnabled = stopped;
        _textGuestCode.IsEnabled = stopped;
        _btnCreate.IsEnabled = stopped && !_refreshingWorlds;
        _btnRefreshWorlds.IsEnabled = stopped && !_refreshingWorlds;
        _btnManualPort.IsEnabled = stopped;
        _comboWorldList.IsEnabled = stopped && !_refreshingWorlds && _comboWorldList.Items.OfType<WorldChoice>().Any();

        if (!active)
            return;

        var isHost = snapshot.Role == SessionRole.Host;
        _cardActive.Title = isHost ? "已创建 P2P 联机" : "已加入 P2P 联机";
        _labActiveRole.Text = isHost ? "房主 · Hysteria2 Realm" : "加入者 · Hysteria2 P2P 入口";
        _labActiveRealm.Text = snapshot.RealmName is { } realmName
            ? $"Realm: realm://public@realm.hy2.io/{realmName}"
            : "正在注册 Realm...";
        _labActiveEndpoint.Text = isHost
            ? snapshot.RealmName is { } hostRealm ? $"Realm 名称: {hostRealm}" : "正在注册 Realm..."
            : snapshot.LocalPort is { } localPort ? $"本地入口: 127.0.0.1:{localPort}" : "正在建立本地入口...";
        _labActiveMinecraft.Text = isHost
            ? snapshot.HostPort is { } hostPort ? $"Minecraft 端口 {hostPort}（仅此端口可访问）" : "正在检测 Minecraft 端口..."
            : "已广播到 Minecraft 局域网服务器列表";
        _labActiveDescription.Text = snapshot.Description is { Length: > 0 } description
            ? $"房间介绍: {description}"
            : "房间介绍: 未填写";
        _labActiveState.Text = snapshot.Message;
        _btnActiveCopy.Text = isHost ? "复制联机码" : "复制本地地址";
        _btnActiveCopy.IsEnabled = isHost ? !string.IsNullOrWhiteSpace(snapshot.Code) : snapshot.LocalPort is not null;
        _btnActiveStop.IsEnabled = snapshot.Phase is SessionPhase.Preparing or SessionPhase.Running or SessionPhase.Error;
    }

    private sealed record WorldChoice(int Port, string DisplayName)
    {
        public override string ToString() => DisplayName;
    }
}
