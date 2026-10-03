using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;

namespace PesPadHub;

/// <summary>控制器页里可选的一个设备。</summary>
sealed record DeviceChoice(Guid Instance, string Label, int VirtualNumber, string PadId, string Name);

/// <summary>控制器页的一行: 游戏内第 N 个控制器用哪个设备。</summary>
sealed class PlayerViewModel(int index) : INotifyPropertyChanged
{
    DeviceChoice? _selected;
    bool _active;

    /// <summary>选中的手柄当前有输入。</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value) return;
            _active = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Active)));
        }
    }

    public int Index { get; } = index;
    public string Label => Loc.T("PlayerN", Index + 1);
    public System.Collections.ObjectModel.ObservableCollection<DeviceChoice> Choices { get; } = [];
    public DeviceChoice? Selected
    {
        get => _selected;
        set
        {
            if (ReferenceEquals(_selected, value)) return;
            _selected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Selected)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void RaiseLabel() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Label)));
}

/// <summary>分辨率下拉框的一项, 显示成游戏设置程序那样的 "(1920x1080) 16:9"。</summary>
sealed record ResolutionChoice(int W, int H)
{
    static readonly (int X, int Y)[] CommonAspects = [(16, 9), (16, 10), (4, 3), (5, 4), (3, 2), (5, 3), (21, 9), (32, 9)];

    public override string ToString() => $"({W}x{H}) {Aspect}";

    string Aspect
    {
        get
        {
            // 1366x768 这类不是整比例的按最接近的常见比例算; 都不像就约分
            double ratio = (double)W / H;
            foreach ((int x, int y) in CommonAspects)
                if (Math.Abs(ratio - (double)x / y) / ((double)x / y) < 0.03) return $"{x}:{y}";
            int g = Gcd(W, H);
            return $"{W / g}:{H / g}";
        }
    }

    static int Gcd(int a, int b) => b == 0 ? a : Gcd(b, a % b);
}

/// <summary>游戏设置弹窗: "显示" 页 (含设置文件备份) 和 "控制器" 页, 都直接读写游戏的 settings.dat。</summary>
public partial class SettingsWindow : Window
{
    static readonly ResolutionChoice[] KnownResolutions =
    [
        new(1280, 720), new(1280, 768), new(1280, 800), new(1280, 960), new(1280, 1024), new(1360, 768), new(1366, 768), new(1440, 1050), new(1440, 900), new(1440, 1080),
        new(1600, 900), new(1600, 1200), new(1680, 1050), new(1920, 1080), new(1920, 1200), new(1920, 1440), new(2048, 1536), new(2160, 1440), new(2560, 1440),
        new(2560, 1600), new(2736, 1824), new(3000, 2000), new(3200, 1800), new(3840, 2160),
    ];

    const int WM_DEVICECHANGE = 0x0219;
    const int DBT_DEVNODES_CHANGED = 0x0007;

    readonly PadHub _hub;
    readonly PlayerViewModel[] _players = Enumerable.Range(0, GameSettingsFile.PlayerCount).Select(i => new PlayerViewModel(i)).ToArray();
    readonly DispatcherTimer _activityTimer;
    readonly DispatcherTimer _deviceRefresh;
    readonly Action _onHubChanged;
    readonly Action _onLanguageChanged;
    GameSettingsFile? _game;
    bool _loadingGame;
    bool _wasReady;

    static string GamePath => GameSettingsFile.DefaultPath;

    internal SettingsWindow(PadHub hub)
    {
        InitializeComponent();
        _hub = hub;
        PlayerList.ItemsSource = _players;
        _wasReady = _hub.Snapshot.Ready;
        Reload();

        // 总开关切换时虚拟手柄整批出现/消失, 刷新可选设备但保留用户已选的; 其余快照只影响冲突判断
        _onHubChanged = () => Dispatcher.BeginInvoke(() =>
        {
            bool ready = _hub.Snapshot.Ready;
            if (ready != _wasReady)
            {
                _wasReady = ready;
                RefreshDevices();
            }
            else
            {
                UpdateConflicts();
            }
        });
        _onLanguageChanged = Reload;
        _hub.Changed += _onHubChanged;
        Loc.Changed += _onLanguageChanged;

        // 实体手柄插拔靠系统的设备变更通知 (总开关关着时后台不再监听手柄); 通知会连发好几条, 稍等一下再枚举
        _deviceRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        _deviceRefresh.Tick += (_, _) => { _deviceRefresh.Stop(); RefreshDevices(); };

        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _activityTimer.Tick += (_, _) => UpdateActivity();
        _activityTimer.Start();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        System.Windows.Interop.HwndSource.FromHwnd(new System.Windows.Interop.WindowInteropHelper(this).Handle)?.AddHook(WndProc);
    }

    IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && wParam == DBT_DEVNODES_CHANGED)
        {
            _deviceRefresh.Stop();
            _deviceRefresh.Start();
        }
        return IntPtr.Zero;
    }

    protected override void OnClosed(EventArgs e)
    {
        _activityTimer.Stop();
        _deviceRefresh.Stop();
        _hub.Changed -= _onHubChanged;
        Loc.Changed -= _onLanguageChanged;
        base.OnClosed(e);
    }

    /// <summary>设备有插拔: 重新列出可选设备, 用户已经选好的保持不动 (不重读文件)。</summary>
    void RefreshDevices()
    {
        _loadingGame = true;
        try
        {
            RefreshPlayerChoices(keepSelection: true);
            UpdateConflicts();
        }
        finally
        {
            _loadingGame = false;
        }
    }

    void Page_Changed(object sender, RoutedEventArgs e)
    {
        if (OnlinePage == null) return;     // 还在 InitializeComponent 里
        foreach ((RadioButton tab, UIElement page) in new[] { (TabDisplay, DisplayPage), (TabPlayers, PlayersPage), (TabAudio, AudioPage), (TabOnline, OnlinePage) })
            page.Visibility = tab.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
    }

    void Info(string key, params object?[] args) => MessageBox.Show(this, Loc.T(key, args), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
    void Warn(string key, params object?[] args) => MessageBox.Show(this, Loc.T(key, args), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    bool Ask(string key, params object?[] args) =>
        MessageBox.Show(this, Loc.T(key, args), Loc.T("AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;
    void Fail(Exception ex) => MessageBox.Show(this, Loc.T("FileOpFailed", ex.Message), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);

    /// <summary>重新读取游戏设置文件和当前接着的 DirectInput 设备, 刷新两页和备份列表。</summary>
    public void Reload()
    {
        _loadingGame = true;
        try
        {
            foreach (PlayerViewModel p in _players) p.RaiseLabel();

            _game = null;
            if (File.Exists(GamePath))
            {
                try { _game = GameSettingsFile.Load(GamePath); }
                catch (Exception) { /* 格式不对或读不了: 页面显示为空, 保存时再提示 */ }
            }
            bool loaded = _game != null;
            PlayersPanel.IsEnabled = loaded;
            ModeDInput.IsEnabled = ModeXInput.IsEnabled = loaded;

            RefreshPlayerChoices();
            RefreshDisplayPage();
            RefreshAudioOnlinePages();
            UpdateInputModeState();
            UpdateConflicts();
        }
        finally
        {
            _loadingGame = false;
        }
    }

    void UpdateActivity()
    {
        // 选了虚拟手柄的看对应序号, 选了实体手柄的按 VID:PID 看那个手柄本身
        int active = _hub.ActivityMask;
        string[] activePads = _hub.ActivePadIds;
        foreach (PlayerViewModel p in _players)
        {
            DeviceChoice? c = p.Selected;
            p.Active = c != null && (c.VirtualNumber > 0 ? (active & (1 << (c.VirtualNumber - 1))) != 0 : c.PadId != "" && activePads.Contains(c.PadId));
        }
    }

    // ── 控制器页 ──

    /// <summary>
    /// 列出可选设备: 虚拟手柄 1~8 在前, 其余实体手柄在后。总开关关着 (或某个序号单独关了) 时那些虚拟手柄不在,
    /// 按注册表里记的实例 GUID 列成 "未运行", 这样照样能认出设置文件里记的是哪个, 实体手柄也照常能选。
    /// </summary>
    void RefreshPlayerChoices(bool keepSelection = false)
    {
        List<DirectInputDevice> devices = DirectInputDevices.Enumerate();
        if (!_hub.Snapshot.Ready) devices.RemoveAll(d => d.VirtualNumber > 0);    // 正在移除中的也不算
        var choices = new List<DeviceChoice> { new(Guid.Empty, Loc.T("DeviceNone"), 0, "", "") };
        for (int n = 1; n <= PadHub.SlotCount; n++)
        {
            ushort pid = VirtualPad.PidFor(n);
            if (devices.FirstOrDefault(d => d.VirtualNumber == n) is { } d)
                choices.Add(new DeviceChoice(d.Instance, Loc.T("DeviceVirtual", n), n, $"{d.Vid:X4}:{d.Pid:X4}", d.Name));
            else if (DirectInputDevices.RememberedInstance(VirtualPad.VendorId, pid) is { } remembered)
                choices.Add(new DeviceChoice(remembered, Loc.T("DeviceVirtualOff", n), n, $"{VirtualPad.VendorId:X4}:{pid:X4}", VirtualPad.NameFor(n)));
        }
        foreach (DirectInputDevice d in devices.Where(d => d.VirtualNumber == 0).OrderBy(d => d.Name))
            choices.Add(new DeviceChoice(d.Instance, $"{d.Name}  [{d.Vid:X4}:{d.Pid:X4}]", 0, $"{d.Vid:X4}:{d.Pid:X4}", d.Name));
        OneClickButton.IsEnabled = choices.Any(c => c.VirtualNumber > 0);

        if (_game != null && !keepSelection)
        {
            ModeXInput.IsChecked = _game.XInput;
            ModeDInput.IsChecked = !_game.XInput;
        }
        for (int i = 0; i < _players.Length; i++)
        {
            PlayerViewModel p = _players[i];
            Guid current = keepSelection ? p.Selected?.Instance ?? Guid.Empty : _game?.PlayerDevice(i) ?? Guid.Empty;
            p.Choices.Clear();
            foreach (DeviceChoice c in choices) p.Choices.Add(c);

            DeviceChoice? match = p.Choices.FirstOrDefault(c => c.Instance == current);
            if (match == null && current != Guid.Empty)
            {
                // 文件里记的设备现在没接着: 保留显示, 不保存时不动它
                match = new DeviceChoice(current, Loc.T("DeviceUnknown", current.ToString("B").ToUpperInvariant()), 0, "", "");
                p.Choices.Add(match);
            }
            p.Selected = match ?? p.Choices[0];
        }
    }

    void InputMode_Changed(object sender, RoutedEventArgs e) => UpdateInputModeState();

    void UpdateInputModeState()
    {
        // XInput 模式下游戏不看这些分配
        if (PlayersPanel != null) PlayersPanel.IsEnabled = _game != null && ModeDInput.IsChecked == true;
    }

    void PlayerDevice_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_loadingGame) UpdateConflicts();
    }

    /// <summary>
    /// 找出会让两个控制器收到同一个手柄输入的分配: 同一个设备选了两次; 或者选了某个实体手柄,
    /// 而这个实体手柄正通过本工具映射到的虚拟手柄又被另一个控制器选了。
    /// </summary>
    List<string> FindConflicts()
    {
        var problems = new List<string>();
        if (_players.Any(p => p.Choices.Count == 0)) return problems;
        HubSnapshot snapshot = _hub.Snapshot;

        for (int i = 0; i < _players.Length; i++)
        {
            DeviceChoice? a = _players[i].Selected;
            if (a == null || a.Instance == Guid.Empty) continue;
            for (int j = i + 1; j < _players.Length; j++)
                if (_players[j].Selected is { } b && b.Instance == a.Instance)
                    problems.Add(Loc.T("ConflictDuplicate", i + 1, j + 1));

            if (a.VirtualNumber != 0 || a.PadId == "") continue;
            // 这个实体手柄是否正映射到某个虚拟手柄 (按 VID:PID 对应), 而那个虚拟手柄又被别的控制器选了
            for (int slot = 0; slot < PadHub.SlotCount; slot++)
            {
                if (!snapshot.Slots[slot].Connected || snapshot.Slots[slot].PadId != a.PadId) continue;
                for (int j = 0; j < _players.Length; j++)
                    if (j != i && _players[j].Selected is { } b && b.VirtualNumber == slot + 1)
                        problems.Add(Loc.T("ConflictMapped", i + 1, a.Name, slot + 1, j + 1));
            }
        }
        return problems;
    }

    void UpdateConflicts()
    {
        if (ConflictText == null) return;
        List<string> problems = FindConflicts();
        ConflictText.Text = problems.Count == 0 ? Loc.T("NoConflict") : string.Join(Environment.NewLine, problems);
        ConflictText.Foreground = problems.Count == 0 ? (Brush)FindResource("GoBrush") : new SolidColorBrush(Color.FromRgb(0xE0, 0x6C, 0x00));
    }

    void OneClick_Click(object sender, RoutedEventArgs e)
    {
        for (int i = 0; i < _players.Length; i++)
            _players[i].Selected = _players[i].Choices.FirstOrDefault(c => c.VirtualNumber == i + 1) ?? _players[i].Selected;
        ModeDInput.IsChecked = true;
        UpdateConflicts();
    }

    void ReloadGame_Click(object sender, RoutedEventArgs e) => Reload();

    bool PrepareGameWrite()
    {
        if (GameProcess.IsRunning())
        {
            Warn("GameRunning");
            return false;
        }
        if (!File.Exists(GamePath))
        {
            Warn("GameFileMissing", GamePath);
            return false;
        }
        try
        {
            _game = GameSettingsFile.Load(GamePath);   // 以磁盘上的最新内容为准, 只改本页的字段
            return true;
        }
        catch (Exception)
        {
            Warn("GameFileInvalid");
            return false;
        }
    }

    void SavePlayers_Click(object sender, RoutedEventArgs e)
    {
        List<string> problems = FindConflicts();
        if (problems.Count > 0 && !Ask("ConflictSaveAnyway", string.Join(Environment.NewLine, problems))) return;
        if (!PrepareGameWrite()) return;

        try
        {
            _game!.XInput = ModeXInput.IsChecked == true;
            for (int i = 0; i < _players.Length; i++)
            {
                DeviceChoice? choice = _players[i].Selected;
                if (choice == null) continue;
                byte[] mapping = choice.VirtualNumber > 0 ? GameSettingsFile.XboxMapping : GameSettingsFile.MappingForName(choice.Name);
                _game.SetPlayerDevice(i, choice.Instance, mapping);
            }
            _game.Save();
            Info("PlayersSaved");
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
    }

    // ── 显示页 ──

    void RefreshDisplayPage()
    {
        bool loaded = _game != null;
        foreach (UIElement element in new UIElement[] { ScreenWindowed, ScreenFullscreen, ResolutionBox, GameFps60, GameFps30, ReplayFps60, ReplayFps30, HdrCheck, VsyncOff, Vsync1, Vsync2 })
            element.IsEnabled = loaded;

        // 分辨率列表: 游戏设置程序提供的档位, 去掉超过当前显示器的
        DpiScale dpi = VisualTreeHelper.GetDpi(this);
        int screenW = (int)Math.Round(SystemParameters.PrimaryScreenWidth * dpi.DpiScaleX);
        int screenH = (int)Math.Round(SystemParameters.PrimaryScreenHeight * dpi.DpiScaleY);
        var list = KnownResolutions.Where(r => r.W <= screenW && r.H <= screenH).ToList();
        var current = _game != null ? new ResolutionChoice(_game.Width, _game.Height) : null;
        if (current != null && !list.Contains(current)) list.Add(current);
        ResolutionBox.ItemsSource = list;
        ResolutionNote.Text = Loc.T("ResolutionNote", screenW, screenH);

        if (_game == null) return;
        ScreenFullscreen.IsChecked = _game.Fullscreen;
        ScreenWindowed.IsChecked = !_game.Fullscreen;
        ResolutionBox.SelectedItem = current;
        GameFps60.IsChecked = _game.GameFps60;
        GameFps30.IsChecked = !_game.GameFps60;
        ReplayFps60.IsChecked = _game.ReplayFps60;
        ReplayFps30.IsChecked = !_game.ReplayFps60;
        HdrCheck.IsChecked = _game.Hdr;
        VsyncOff.IsChecked = _game.Vsync == GameSettingsFile.VsyncMode.Off;
        Vsync1.IsChecked = _game.Vsync == GameSettingsFile.VsyncMode.Mode1;
        Vsync2.IsChecked = _game.Vsync == GameSettingsFile.VsyncMode.Mode2;
    }

    void SaveDisplay_Click(object sender, RoutedEventArgs e)
    {
        if (!PrepareGameWrite()) return;
        try
        {
            _game!.Fullscreen = ScreenFullscreen.IsChecked == true;
            if (ResolutionBox.SelectedItem is ResolutionChoice res)
            {
                _game.Width = res.W;
                _game.Height = res.H;
            }
            _game.GameFps60 = GameFps60.IsChecked == true;
            _game.ReplayFps60 = ReplayFps60.IsChecked == true;
            _game.Hdr = HdrCheck.IsChecked == true;
            _game.Vsync = VsyncOff.IsChecked == true ? GameSettingsFile.VsyncMode.Off
                        : Vsync2.IsChecked == true ? GameSettingsFile.VsyncMode.Mode2 : GameSettingsFile.VsyncMode.Mode1;
            _game.Save();
            Info("Saved");
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
    }

    // ── 音频 / 在线页 ──

    void RefreshAudioOnlinePages()
    {
        bool loaded = _game != null;
        foreach (UIElement element in new UIElement[] { AudioBufferBox, P2PAutoCheck, UdpAutoCheck, UdpPortsPanel, VoiceChatCheck })
            element.IsEnabled = loaded;
        AudioBufferBox.ItemsSource ??= Enumerable.Range(GameSettingsFile.AudioBufferMin, GameSettingsFile.AudioBufferMax - GameSettingsFile.AudioBufferMin + 1).ToArray();

        if (_game == null) return;
        AudioBufferBox.SelectedItem = _game.AudioBuffer;
        P2PAutoCheck.IsChecked = _game.P2PAuto;
        UdpAutoCheck.IsChecked = _game.UdpAuto;
        UdpPort1Box.Text = _game.UdpPort1.ToString();
        UdpPort2Box.Text = _game.UdpPort2.ToString();
        VoiceChatCheck.IsChecked = _game.VoiceChat;
        UdpAuto_Changed(this, new RoutedEventArgs());
    }

    void UdpAuto_Changed(object sender, RoutedEventArgs e)
    {
        if (UdpPortsPanel != null) UdpPortsPanel.IsEnabled = _game != null && UdpAutoCheck.IsChecked != true;
    }

    void SaveAudio_Click(object sender, RoutedEventArgs e)
    {
        if (AudioBufferBox.SelectedItem is not int buffer || !PrepareGameWrite()) return;
        try
        {
            _game!.AudioBuffer = buffer;
            _game.Save();
            Info("Saved");
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
    }

    void SoundProperties_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "mmsys.cpl") { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            Fail(ex);
        }
    }

    void SaveOnline_Click(object sender, RoutedEventArgs e)
    {
        if (!int.TryParse(UdpPort1Box.Text.Trim(), out int port1) || !int.TryParse(UdpPort2Box.Text.Trim(), out int port2)
            || port1 is < 1 or > ushort.MaxValue || port2 is < 1 or > ushort.MaxValue)
        {
            Warn("PortInvalid");
            return;
        }
        if (port1 == port2)
        {
            Warn("PortsSame");
            return;
        }
        if (!PrepareGameWrite()) return;
        try
        {
            _game!.P2PAuto = P2PAutoCheck.IsChecked == true;
            _game.UdpAuto = UdpAutoCheck.IsChecked == true;
            _game.UdpPort1 = port1;
            _game.UdpPort2 = port2;
            _game.VoiceChat = VoiceChatCheck.IsChecked == true;
            _game.Save();
            Info("Saved");
            Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Fail(ex);
        }
    }
}

/// <summary>游戏或它的设置程序正在运行时不能改 settings.dat, 它们退出时会覆盖写回。</summary>
static class GameProcess
{
    public static bool IsRunning() =>
        System.Diagnostics.Process.GetProcessesByName("PES2021").Length > 0 || System.Diagnostics.Process.GetProcessesByName("Settings").Length > 0;
}
