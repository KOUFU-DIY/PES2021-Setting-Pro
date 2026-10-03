using System.IO;
using System.Windows;

namespace PesPadHub;

public partial class App : Application
{
    const string InstanceName = "PesPadHub.SingleInstance";

    Mutex? _instanceMutex;
    EventWaitHandle? _showSignal;
    PadHub? _hub;
    TrayIcon? _tray;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        DispatcherUnhandledException += (_, args) => WriteCrashLog(args.Exception);

        // 只允许一个实例, 否则会创建出 16 个虚拟手柄; 重复启动时把已有窗口调出来
        _instanceMutex = new Mutex(true, InstanceName, out bool isFirst);
        if (!isFirst)
        {
            try
            {
                using var signal = EventWaitHandle.OpenExisting(InstanceName + ".Show");
                signal.Set();
            }
            catch (Exception)
            {
                // 已有实例正在退出等情况, 直接结束即可
            }
            Shutdown();
            return;
        }

        // 可选参数: --minimized 启动后直接收到托盘; --demo N 用 N 个模拟手柄演示 (无实体手柄时自测用)
        bool startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        int demoPads = 0;
        int demoIndex = Array.FindIndex(e.Args, a => a.Equals("--demo", StringComparison.OrdinalIgnoreCase));
        if (demoIndex >= 0 && demoIndex + 1 < e.Args.Length) int.TryParse(e.Args[demoIndex + 1], out demoPads);

        // 配置在这里读一次用来确定界面语言, 之后交给后台线程独占读写
        AppConfig config = AppConfig.Load();
        Loc.SetLanguage(config.Language ?? Loc.DefaultLanguage);
        AutoStart.RepairPath();

        _hub = new PadHub(config, Math.Clamp(demoPads, 0, PadHub.SlotCount));
        var window = new MainWindow(_hub);
        MainWindow = window;
        _tray = new TrayIcon(window.ShowFromTray, window.Close);

        _showSignal = new EventWaitHandle(false, EventResetMode.AutoReset, InstanceName + ".Show");
        ThreadPool.RegisterWaitForSingleObject(_showSignal, (_, _) => Dispatcher.BeginInvoke(window.ShowFromTray), null, Timeout.Infinite, false);

        _hub.Start();
        if (!startMinimized) window.Show();
    }

    /// <summary>界面线程出现未处理异常时留下记录, 方便事后排查; 之后程序照常崩溃退出。</summary>
    static void WriteCrashLog(Exception ex)
    {
        try
        {
            Directory.CreateDirectory(AppConfig.DataDirectory);
            File.AppendAllText(Path.Combine(AppConfig.DataDirectory, "crash.log"), $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {ex}\n\n");
        }
        catch (Exception)
        {
            // 日志写不进去就算了
        }
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        // 注销/关机时也要恢复 HidHide 设置
        _hub?.Stop();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _hub?.Stop();
        _showSignal?.Dispose();
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }
}
