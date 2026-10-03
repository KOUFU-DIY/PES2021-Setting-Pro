using System.IO;
using System.Windows;

namespace PesPadHub;

/// <summary>主窗口里和游戏 settings.dat 打交道的部分: 打开游戏设置弹窗、一键把映射写进游戏。</summary>
public partial class MainWindow
{
    SettingsWindow? _settings;

    static string GamePath => GameSettingsFile.DefaultPath;

    /// <summary>打开 (或切到前台) 游戏设置弹窗: "显示" 和 "控制器" 两页。</summary>
    void OpenSettings_Click(object sender, RoutedEventArgs e)
    {
        if (_settings == null)
        {
            _settings = new SettingsWindow(_hub) { Owner = this };
            _settings.Closed += (_, _) => _settings = null;
            _settings.Show();
            return;
        }
        _settings.Reload();     // 文件可能在外面被改过
        if (_settings.WindowState == WindowState.Minimized) _settings.WindowState = WindowState.Normal;
        _settings.Activate();
    }

    /// <summary>"映射手柄写入 setting": 控制器 1~8 = 虚拟手柄 1~8 + DirectInput, 直接写入游戏; 没接着的序号保持原设置。</summary>
    void OneClickApply_Click(object sender, RoutedEventArgs e)
    {
        if (GameIsRunning())
        {
            Warn("GameRunning");
            return;
        }
        if (!File.Exists(GamePath))
        {
            Warn("GameFileMissing", GamePath);
            return;
        }
        GameSettingsFile game;
        try
        {
            game = GameSettingsFile.Load(GamePath);     // 以磁盘上的最新内容为准, 只改控制器相关字段
        }
        catch (Exception)
        {
            Warn("GameFileInvalid");
            return;
        }

        var virtuals = _hub.Snapshot.Ready
            ? DirectInputDevices.Enumerate().Where(d => d.VirtualNumber > 0).ToDictionary(d => d.VirtualNumber)
            : [];
        if (virtuals.Count == 0)
        {
            Warn("NoVirtualPads");
            return;
        }

        try
        {
            game.XInput = false;
            for (int i = 0; i < GameSettingsFile.PlayerCount; i++)
                if (virtuals.TryGetValue(i + 1, out DirectInputDevice? d)) game.SetPlayerDevice(i, d.Instance, GameSettingsFile.XboxMapping);
            game.Save();
            Info("PlayersSaved");
            _settings?.Reload();
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            MessageBox.Show(this, Loc.T("FileOpFailed", ex.Message), Loc.T("AppTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }
}
