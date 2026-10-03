using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;
using Microsoft.Win32;

namespace PesPadHub;

public partial class MainWindow : Window
{
    const int MaxLogLines = 300;

    readonly PadHub _hub;
    readonly SlotViewModel[] _slots;
    readonly DispatcherTimer _activityTimer;
    readonly Queue<string> _logLines = new();
    readonly System.Collections.ObjectModel.ObservableCollection<WaitingViewModel> _waiting = [];
    readonly bool _initialized;
    bool _applying;

    internal MainWindow(PadHub hub)
    {
        InitializeComponent();
        _hub = hub;
        _slots = Enumerable.Range(0, PadHub.SlotCount).Select(i => new SlotViewModel(i)).ToArray();
        SlotList.ItemsSource = _slots;
        WaitingList.ItemsSource = _waiting;
        LanguageBox.SelectedIndex = Loc.Language == Loc.English ? 1 : 0;
        _initialized = true;

        // 事件来自后台线程, 统一切回界面线程处理
        _hub.Changed += () => Dispatcher.BeginInvoke(ApplySnapshot);
        _hub.Log += line => Dispatcher.BeginInvoke(() => AppendLog(line));
        Loc.Changed += ApplySnapshot;
        ApplySnapshot();

        _activityTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
        _activityTimer.Tick += (_, _) => UpdateActivity();
        _activityTimer.Start();

        _ = CheckForUpdateAsync();
    }

    // ── 版本与更新 ──

    UpdateInfo? _update;

    /// <summary>启动时在后台查一次 GitHub 最新 Release; 查不到 (断网等) 就当没有, 不打扰用户。</summary>
    async Task CheckForUpdateAsync()
    {
        UpdateInfo? update = await UpdateCheck.FetchAsync();
        if (update == null || update.Tag == _hub.SkippedUpdate) return;
        _update = update;
        UpdateText.Text = Loc.T("UpdateAvailable", update.Tag, UpdateCheck.CurrentText);
        UpdateBanner.Visibility = Visibility.Visible;
    }

    void UpdateDownload_Click(object sender, RoutedEventArgs e)
    {
        string url = _update?.PageUrl ?? UpdateCheck.ReleasesUrl;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException)
        {
            MessageBox.Show(this, Loc.T("FileOpFailed", ex.Message), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    void UpdateSkip_Click(object sender, RoutedEventArgs e)
    {
        if (_update != null) _hub.SetSkippedUpdate(_update.Tag);
        UpdateBanner.Visibility = Visibility.Collapsed;
    }

    /// <summary>从托盘恢复 (或第二次启动本程序时) 把窗口调到前台。</summary>
    public void ShowFromTray()
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
    }

    protected override void OnStateChanged(EventArgs e)
    {
        base.OnStateChanged(e);
        if (WindowState == WindowState.Minimized)
        {
            _settings?.Close();     // 收进托盘时弹窗一起收起
            Hide();
        }
    }

    void ApplySnapshot()
    {
        _applying = true;
        try
        {
            ApplySnapshotCore();
        }
        finally
        {
            _applying = false;
        }
    }

    void ApplySnapshotCore()
    {
        Title = $"{Loc.T("AppTitle")}  v{UpdateCheck.CurrentText}";
        HubSnapshot snapshot = _hub.Snapshot;
        for (int i = 0; i < _slots.Length; i++)
            _slots[i].Apply(snapshot.Slots[i], snapshot.Ready);

        int connected = snapshot.Slots.Count(s => s.Connected);
        StatusText.Text = snapshot switch
        {
            { Ready: true } => Loc.T("StatusRunning", PadHub.SlotCount, connected),
            { FatalError: not null } => Loc.T("StatusFailed"),
            { Enabled: true } => Loc.T("StatusStarting"),
            _ => Loc.T("StatusOff"),
        };
        MasterSwitch.IsChecked = snapshot.Enabled;

        ErrorBanner.Visibility = snapshot.FatalError != null ? Visibility.Visible : Visibility.Collapsed;
        ErrorText.Text = snapshot.FatalError;
        BridgeStatus.Text = Loc.T(snapshot.BridgeConnected ? "BridgeConnected" : "BridgeIdle");

        // 就地增删, 不整体替换, 让已有的行保持原样 (也便于辅助工具跟踪)
        for (int i = _waiting.Count - 1; i >= 0; i--)
            if (!snapshot.Waiting.Any(w => w.Id == _waiting[i].Id)) _waiting.RemoveAt(i);
        foreach (WaitingSnapshot w in snapshot.Waiting)
            if (!_waiting.Any(v => v.Id == w.Id)) _waiting.Add(new WaitingViewModel(w.Id, $"{w.Name}  [{w.PadId}]"));
        WaitingPanel.Visibility = _waiting.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        try { AutoStartCheck.IsChecked = AutoStart.IsEnabled; } catch (Exception) { /* 注册表读不到就按未开启显示 */ }
    }

    void UpdateActivity()
    {
        int active = _hub.ActivityMask, rumbling = _hub.RumbleMask;
        for (int i = 0; i < _slots.Length; i++)
        {
            _slots[i].Active = (active & (1 << i)) != 0;
            _slots[i].Rumbling = (rumbling & (1 << i)) != 0;
        }
    }

    void AppendLog(string line)
    {
        _logLines.Enqueue(line);
        while (_logLines.Count > MaxLogLines) _logLines.Dequeue();
        LogBox.Text = string.Join(Environment.NewLine, _logLines);
        LogBox.ScrollToEnd();
    }

    static SlotViewModel SlotOf(object sender) => (SlotViewModel)((FrameworkElement)sender).DataContext;

    static bool GameIsRunning() => GameProcess.IsRunning();

    void Info(string key, params object?[] args) => MessageBox.Show(this, Loc.T(key, args), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
    void Warn(string key, params object?[] args) => MessageBox.Show(this, Loc.T(key, args), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
    bool Ask(string key, params object?[] args) =>
        MessageBox.Show(this, Loc.T(key, args), Loc.T("AppTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question) == MessageBoxResult.Yes;

    // ── 总览 ──

    // 开关类控件监听 Checked/Unchecked 而不是 Click, 这样键盘和辅助工具切换时同样生效;
    // _applying 用来区分 "用户切换" 和 "按后台状态回填"
    void Master_Toggled(object sender, RoutedEventArgs e)
    {
        if (!_applying) _hub.SetEnabled(MasterSwitch.IsChecked == true);
    }

    void AutoStart_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        try
        {
            AutoStart.SetEnabled(AutoStartCheck.IsChecked == true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, Loc.T("AutoStartFailed", ex.Message), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            _applying = true;
            try { AutoStartCheck.IsChecked = AutoStart.IsEnabled; } finally { _applying = false; }
        }
    }

    void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_initialized || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string language }) return;
        Loc.SetLanguage(language);
        // 保存设置; 后台随后会按新语言重新发布状态, 界面上由代码生成的文字随之刷新
        _hub.SetLanguage(language);
    }

    // ── 手柄映射 ──

    void SlotEnable_Toggled(object sender, RoutedEventArgs e)
    {
        if (_applying) return;
        var slot = SlotOf(sender);
        bool wanted = ((CheckBox)sender).IsChecked == true;
        if (wanted != _hub.Snapshot.Slots[slot.Index].Enabled) _hub.SetSlotEnabled(slot.Index, wanted);
    }

    void Pin_Click(object sender, RoutedEventArgs e) => _hub.TogglePin(SlotOf(sender).Index);

    void MoveUp_Click(object sender, RoutedEventArgs e)
    {
        int index = SlotOf(sender).Index;
        _hub.SwapSlots(index, index - 1);
    }

    void MoveDown_Click(object sender, RoutedEventArgs e)
    {
        int index = SlotOf(sender).Index;
        _hub.SwapSlots(index, index + 1);
    }

    void Identify_Click(object sender, RoutedEventArgs e) => _hub.Identify(SlotOf(sender).Index);

    /// <summary>"放到…": 弹出 1~8 的菜单, 选一个序号把待分配的手柄放过去。</summary>
    void Assign_Click(object sender, RoutedEventArgs e)
    {
        var button = (Button)sender;
        if (button.Tag is not uint id) return;
        var menu = new ContextMenu { PlacementTarget = button, Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom };
        for (int slot = 0; slot < PadHub.SlotCount; slot++)
        {
            int target = slot;
            var item = new MenuItem { Header = (slot + 1).ToString(), IsEnabled = _hub.Snapshot.Slots[slot].Enabled };
            item.Click += (_, _) => _hub.AssignPad(id, target);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }

    void IdentifyWaiting_Click(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).Tag is uint id) _hub.IdentifyPad(id);
    }

    /// <summary>
    /// 猜游戏 (PES2021.exe) 在哪个文件夹, 用作选择框的起始位置: 上次选过的 → 正在运行的游戏 → 各磁盘根目录和 Steam 库下的一层文件夹。
    /// </summary>
    static string? FindGameDirectory()
    {
        try
        {
            foreach (var process in System.Diagnostics.Process.GetProcessesByName("PES2021"))
            {
                string? dir = Path.GetDirectoryName(process.MainModule?.FileName);
                if (dir != null) return dir;
            }
        }
        catch (Exception) { /* 没权限读进程路径就跳过 */ }

        var roots = new List<string>();
        foreach (DriveInfo drive in DriveInfo.GetDrives().Where(d => d.DriveType == DriveType.Fixed && d.IsReady))
        {
            roots.Add(drive.RootDirectory.FullName);
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "SteamLibrary", "steamapps", "common"));
            roots.Add(Path.Combine(drive.RootDirectory.FullName, "Program Files (x86)", "Steam", "steamapps", "common"));
        }
        foreach (string root in roots.Where(Directory.Exists))
        {
            try
            {
                foreach (string dir in Directory.EnumerateDirectories(root))
                    if (File.Exists(Path.Combine(dir, "PES2021.exe"))) return dir;
            }
            catch (Exception) { /* 个别目录读不了就跳过 */ }
        }
        return null;
    }

    string? GuessGameDirectory()
    {
        string? remembered = _hub.GameDirectory;
        if (remembered != null && File.Exists(Path.Combine(remembered, "PES2021.exe"))) return remembered;
        return FindGameDirectory();
    }

    /// <summary>把游戏内震动插件装进用户选的游戏文件夹; 那里已经装有我们的插件时改为询问更新还是卸载。</summary>
    void InstallBridge_Click(object sender, RoutedEventArgs e)
    {
        string title = Loc.T("AppTitle");
        if (!RumbleBridgeInstaller.IsBundled)
        {
            Warn("BridgeNotBundled");
            return;
        }

        var dialog = new OpenFolderDialog { Title = Loc.T("BridgePickFolder") };
        string? guess = GuessGameDirectory();
        if (guess != null) dialog.InitialDirectory = guess;
        if (dialog.ShowDialog(this) != true) return;
        string folder = dialog.FolderName;
        string target = Path.Combine(folder, RumbleBridgeInstaller.FileName);

        try
        {
            if (File.Exists(Path.Combine(folder, "PES2021.exe"))) _hub.SetGameDirectory(folder);
            if (File.Exists(target))
            {
                // 不是我们的 dinput8.dll 就不碰, 那可能是别的插件
                if (!RumbleBridgeInstaller.IsOurs(target))
                {
                    Warn("BridgeForeign");
                    return;
                }
                MessageBoxResult choice = MessageBox.Show(this, Loc.T("BridgeExists"), title, MessageBoxButton.YesNoCancel, MessageBoxImage.Question);
                if (choice == MessageBoxResult.Cancel) return;
                if (choice == MessageBoxResult.No)
                {
                    RumbleBridgeInstaller.Remove(folder);
                    Info("BridgeRemoved", folder);
                    return;
                }
            }
            else if (!File.Exists(Path.Combine(folder, "PES2021.exe")) && !Ask("BridgeNoGame", folder))
            {
                return;
            }

            RumbleBridgeInstaller.Install(folder);
            Info("BridgeInstalled", folder);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Loc.T("BridgeFailed", ex.Message), title, MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
