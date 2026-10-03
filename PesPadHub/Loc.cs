using System.Globalization;
using System.Windows;

namespace PesPadHub;

/// <summary>
/// 中英文界面文案。XAML 里用 {DynamicResource S.键名} 引用, 代码里用 Loc.T("键名")。
/// </summary>
static class Loc
{
    public const string Chinese = "zh";
    public const string English = "en";

    static readonly Dictionary<string, (string Zh, string En)> Strings = new()
    {
        ["AppTitle"] = ("PES 2021 Setting Pro", "PES 2021 Setting Pro"),
        ["TipLanguage"] = ("界面语言", "Display language"),
        ["TabPlayers"] = ("控制器", "Controllers"),
        ["TabDisplay"] = ("显示", "Display"),
        ["TabAudio"] = ("音频", "Audio"),
        ["TabOnline"] = ("在线", "Online"),
        ["GameSettings"] = ("游戏设置", "Game settings"),
        ["UpdateAvailable"] = ("发现新版本 {0}（当前 v{1}）", "New version {0} available (current v{1})"),
        ["UpdateDownload"] = ("打开下载页面", "Open download page"),
        ["UpdateSkip"] = ("忽略此版本", "Skip this version"),
        ["OpenSettings"] = ("显示 / 控制器 设置…", "Display / controller settings…"),
        ["TipOpenSettings"] = ("打开游戏设置窗口：显示（窗口 / 全屏、分辨率、帧率、HDR、Vsync、设置文件备份）和控制器（DirectInput / XInput、控制器 1~8 用哪个设备）",
                              "Opens the game settings window: Display (window / fullscreen, resolution, fps, HDR, Vsync, settings backup) and Controllers (DirectInput / XInput, which device each controller 1–8 uses)"),

        // 总览
        ["Master"] = ("虚拟手柄总开关", "Virtual controller master switch"),
        ["TipMaster"] = ("关闭后移除全部虚拟手柄，实体手柄恢复直连；再打开会重新创建。不影响“控制器”页的设置。",
                         "Turning this off removes all virtual controllers, so physical controllers work directly again. Turning it on creates them again."),
        ["StatusOff"] = ("已关闭 · 虚拟手柄已移除", "Off · virtual controllers removed"),
        ["StatusStarting"] = ("正在创建虚拟手柄…", "Creating virtual controllers…"),
        ["StatusRunning"] = ("{0} 个虚拟手柄运行中 · 已接入 {1} 个实体手柄", "{0} virtual controllers running · {1} physical connected"),
        ["StatusFailed"] = ("未运行", "Not running"),
        ["AutoStart"] = ("开机自启动（最小化到托盘）", "Start with Windows (minimized to tray)"),
        ["AutoStartFailed"] = ("设置开机自启动失败：{0}", "Could not change the startup setting: {0}"),
        ["TrayNote"] = ("先启动本工具再启动游戏。最小化后在托盘继续运行，关闭窗口即退出。",
                        "Start this tool before the game. Minimizing keeps it running in the tray; closing the window exits."),
        ["FileOpFailed"] = ("操作失败：{0}", "The operation failed: {0}"),
        ["GameRunning"] = ("游戏或它的设置程序正在运行，请先关闭再操作。", "The game or its settings program is running; close it first."),
        ["GameFileMissing"] = ("未找到游戏设置文件：\n{0}\n\n请先运行一次游戏的设置程序。", "The game's settings file was not found:\n{0}\n\nRun the game's settings program once first."),
        ["GameFileInvalid"] = ("settings.dat 的格式不是预期的，为安全起见不做修改。", "settings.dat does not have the expected format, so it is left untouched."),

        // 手柄映射
        ["NotConnected"] = ("未连接", "Not connected"),
        ["PinnedAbsent"] = ("已固定：{0}（未连接）", "Pinned: {0} (not connected)"),
        ["SlotDisabled"] = ("已关闭", "Off"),
        ["TipSlotEnable"] = ("单独开关这个序号：关掉后该虚拟手柄从系统中移除", "Enable or disable this number individually; when off, the virtual controller is removed from the system"),
        ["TipActivity"] = ("输入指示：按下该手柄任意键时亮起", "Input indicator: lights up while this controller is being used"),
        ["TipRumbleIndicator"] = ("震动指示：游戏让这个手柄震动时亮起", "Rumble indicator: lights up while the game is vibrating this controller"),
        ["Pin"] = ("固定", "Pin"),
        ["TipPin"] = ("固定：这个实体手柄以后始终对应这个序号（再点一次取消）", "Pin: always map this controller to this number (click again to unpin)"),
        ["MoveUp"] = ("上移", "Move up"),
        ["TipMoveUp"] = ("上移一位", "Move up one slot"),
        ["MoveDown"] = ("下移", "Move down"),
        ["TipMoveDown"] = ("下移一位", "Move down one slot"),
        ["Rumble"] = ("震动", "Rumble"),
        ["TipRumble"] = ("让这个实体手柄震动一下，方便辨认", "Make this controller vibrate so you can tell which one it is"),
        ["WaitingHeader"] = ("待分配（没有空位的手柄）", "Waiting (controllers without a free number)"),
        ["AssignTo"] = ("放到…", "Put at…"),
        ["TipAssign"] = ("把这个手柄放到指定序号，原来占着该序号的手柄退到待分配", "Put this controller at the chosen number; the one currently there moves to the waiting list"),
        ["BridgeInstall"] = ("安装 / 卸载 DInput 震动插件…", "Install / remove DInput rumble plugin…"),
        ["TipBridge"] = ("游戏在 DirectInput 模式下不会让虚拟手柄震动。这个插件是放进游戏文件夹的一个 dinput8.dll，它把游戏的震动指令转给本工具，再由本工具让实体手柄震动。\n选择游戏文件夹后：没装过就安装；已装过可以选择更新或卸载。",
                         "In DirectInput mode the game does not vibrate virtual controllers. This plugin is a dinput8.dll placed in the game folder; it passes the game's rumble commands to this tool, which then vibrates the physical controller.\nPick the game folder: the plugin is installed if absent; if present you can update or remove it."),
        ["BridgeConnected"] = ("震动插件：已连接", "Rumble plugin: connected"),
        ["BridgeIdle"] = ("震动插件：未连接", "Rumble plugin: not connected"),
        ["BridgePickFolder"] = ("选择 PES2021.exe 所在的文件夹", "Select the folder containing PES2021.exe"),
        ["BridgeNoGame"] = ("这个文件夹里没有 PES2021.exe：\n{0}\n\n仍然安装到这里吗？", "This folder does not contain PES2021.exe:\n{0}\n\nInstall here anyway?"),
        ["BridgeForeign"] = ("这个文件夹里已经有一个别的程序的 dinput8.dll。为了不弄坏它，没有安装。\n\n如果确定不需要它，请先手动把它改名或移走，再来安装。",
                             "This folder already contains a dinput8.dll from another program, so nothing was installed to avoid breaking it.\n\nIf you are sure you do not need it, rename or move it first, then install again."),
        ["BridgeExists"] = ("这个文件夹里已经装有震动插件。\n\n“是” = 更新为当前版本\n“否” = 卸载插件", "The rumble plugin is already installed in this folder.\n\nYes = update it to the current version\nNo = remove it"),
        ["BridgeInstalled"] = ("已安装到：\n{0}\n\n重新启动游戏后生效。游戏需使用 DirectInput。", "Installed to:\n{0}\n\nRestart the game for it to take effect. The game must use DirectInput."),
        ["BridgeRemoved"] = ("已从这个文件夹卸载震动插件：\n{0}", "The rumble plugin was removed from:\n{0}"),
        ["BridgeFailed"] = ("操作失败：{0}\n\n如果游戏或它的设置程序正在运行，请先关闭再试。", "The operation failed: {0}\n\nIf the game or its settings program is running, close it and try again."),
        ["BridgeNotBundled"] = ("这个版本的程序里没有包含震动插件文件。", "This build does not include the rumble plugin file."),

        // 玩家手柄
        ["ModeDInput"] = ("DirectInput（可用 8 个）", "DirectInput (up to 8)"),
        ["ModeXInput"] = ("XInput（最多 4 个，下面的分配无效）", "XInput (at most 4; assignments below do not apply)"),
        ["PlayerN"] = ("控制器 {0}", "Controller {0}"),
        ["DeviceNone"] = ("（未设置）", "(not set)"),
        ["DeviceVirtual"] = ("虚拟手柄 {0}", "Virtual controller {0}"),
        ["DeviceVirtualOff"] = ("虚拟手柄 {0}（未运行）", "Virtual controller {0} (not running)"),
        ["DeviceUnknown"] = ("（未接入的设备 {0}）", "(device not connected {0})"),
        ["OneClickApply"] = ("映射手柄写入 setting", "Write mapped controllers to settings"),
        ["TipOneClickApply"] = ("把控制器 1~8 依次设为虚拟手柄 1~8 并选中 DirectInput，直接写入游戏的 settings.dat（原文件备份为 settings.dat.bak）", "Sets controllers 1–8 to virtual controllers 1–8 with DirectInput and writes the game's settings.dat (original kept as settings.dat.bak)"),
        ["OneClick"] = ("一键：控制器 1~8 = 虚拟手柄 1~8", "One click: controllers 1–8 = virtual 1–8"),
        ["SaveToGame"] = ("保存到游戏", "Save to game"),
        ["Reload"] = ("重新读取", "Reload"),
        ["NoConflict"] = ("没有冲突", "No conflicts"),
        ["ConflictDuplicate"] = ("控制器 {0} 和控制器 {1} 选了同一个手柄", "Controllers {0} and {1} use the same device"),
        ["ConflictMapped"] = ("控制器 {0} 选的实体手柄「{1}」已经通过映射对应到虚拟手柄 {2}（控制器 {3}），两个控制器会收到同一个手柄的输入",
                              "Controller {0}'s physical device \"{1}\" is also mapped to virtual controller {2} (controller {3}); both would receive the same input"),
        ["ConflictSaveAnyway"] = ("存在冲突：\n{0}\n\n仍然保存吗？", "There are conflicts:\n{0}\n\nSave anyway?"),
        ["PlayersSaved"] = ("已保存。下次启动游戏生效。", "Saved. It takes effect the next time the game starts."),
        ["NoVirtualPads"] = ("当前没有虚拟手柄（总开关已关闭或还在创建中），没有可写入的映射。", "No virtual controllers are present right now (master switch off or still starting), so there is nothing to write."),

        // 官方设置
        ["ScreenMode"] = ("画面模式", "Screen mode"),
        ["Windowed"] = ("窗口模式", "Windowed"),
        ["Fullscreen"] = ("全屏幕", "Full screen"),
        ["Resolution"] = ("分辨率", "Resolution"),
        ["ResolutionNote"] = ("显示器 {0}×{1}", "Monitor {0}×{1}"),
        ["FrameRate"] = ("帧速率", "Frame rate"),
        ["FpsGame"] = ("游戏中", "In game"),
        ["FpsReplay"] = ("比赛回放和演示", "Replays and demos"),
        ["Hdr"] = ("HDR", "HDR"),
        ["Enable"] = ("启用", "Enable"),
        ["VsyncLabel"] = ("Vsync", "Vsync"),
        ["VsyncOff"] = ("禁用", "Off"),
        ["Vsync1"] = ("启用1", "Mode 1"),
        ["Vsync2"] = ("启用2", "Mode 2"),
        ["AudioBuffer"] = ("音频缓冲", "Audio buffer"),
        ["P2P"] = ("P2P", "P2P"),
        ["Auto"] = ("自动", "Automatic"),
        ["UdpPort"] = ("UDP端口（预设：5739，5740）", "UDP port (default: 5739, 5740)"),
        ["UdpPortNote"] = ("如果你想要手动更改UDP端口号，请为每个端口使用不同的值。", "To set the UDP ports manually, use a different value for each port."),
        ["VoiceChat"] = ("语音聊天设定", "Voice chat"),
        ["VoiceChatEnabled"] = ("语音聊天已启用", "Voice chat enabled"),
        ["SoundProperties"] = ("声音属性", "Sound properties"),
        ["PortInvalid"] = ("端口要填 1~65535 的数字。", "Ports must be numbers from 1 to 65535."),
        ["PortsSame"] = ("两个端口要用不同的值。", "The two ports must be different."),
        ["TipSave"] = ("写入游戏的 settings.dat，下次启动游戏生效；原文件备份为 settings.dat.bak", "Writes the game's settings.dat; takes effect at the next game start. The original is kept as settings.dat.bak"),
        ["Saved"] = ("已保存。下次启动游戏生效。", "Saved. It takes effect the next time the game starts."),

        // 托盘
        ["TrayShow"] = ("显示主窗口", "Show window"),
        ["TrayExit"] = ("退出", "Exit"),

        // 错误
        ["FatalNoVigem"] = ("未检测到 ViGEmBus 驱动，无法创建虚拟手柄。请先安装 ViGEmBus，然后把总开关重新打开。",
                            "The ViGEmBus driver was not found, so virtual controllers cannot be created. Install ViGEmBus, then turn the master switch on again."),
        ["FatalSdl"] = ("手柄输入模块 (SDL) 初始化失败：{0}", "The controller input module (SDL) failed to start: {0}"),
        ["FatalRun"] = ("运行出错，已停止：{0}。可以把总开关关掉再打开来重试。", "Stopped because of an error: {0}. Turn the master switch off and on to retry."),

        // 日志
        ["LogCreated"] = ("已创建 {0} 个虚拟 Xbox One 手柄", "Created {0} virtual Xbox One controllers"),
        ["LogSwitchedOff"] = ("总开关已关闭：全部虚拟手柄已移除", "Master switch off: all virtual controllers removed"),
        ["LogOpenFailed"] = ("打开手柄失败：{0}", "Failed to open a controller: {0}"),
        ["LogWaiting"] = ("待分配：{0}（没有空位）", "Waiting: {0} (no free number)"),
        ["LogRemovedWaiting"] = ("拔出：{0}（待分配中）", "Disconnected: {0} (was waiting)"),
        ["LogSlotEnabled"] = ("已打开 {0} 号：虚拟手柄已创建", "No. {0} turned on: virtual controller created"),
        ["LogSlotDisabled"] = ("已关闭 {0} 号：虚拟手柄已移除", "No. {0} turned off: virtual controller removed"),
        ["LogAttached"] = ("接入：{0} → {1} 号", "Connected: {0} → No. {1}"),
        ["LogEvicted"] = ("{0} 让位，改为 → {1} 号", "{0} moved aside → No. {1}"),
        ["LogRemoved"] = ("拔出：{0}（{1} 号空出）", "Disconnected: {0} (No. {1} is free)"),
        ["LogPinned"] = ("已固定：{0} 以后始终对应 {1} 号", "Pinned: {0} will always map to No. {1}"),
        ["LogUnpinned"] = ("已取消固定：{0}（{1} 号）", "Unpinned: {0} (No. {1})"),
        ["LogDemoFailed"] = ("创建演示手柄失败：{0}", "Failed to create a demo controller: {0}"),
        ["DemoPadName"] = ("演示手柄 {0}", "Demo pad {0}"),
    };

    static volatile string _language = Chinese;

    public static string Language => _language;

    /// <summary>语言切换后触发 (在界面线程上)。</summary>
    public static event Action? Changed;

    /// <summary>没有保存过语言设置时: 系统是中文就用中文, 否则用英文。</summary>
    public static string DefaultLanguage =>
        CultureInfo.CurrentUICulture.Name.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? Chinese : English;

    public static string T(string key) =>
        Strings.TryGetValue(key, out var s) ? (_language == English ? s.En : s.Zh) : key;

    public static string T(string key, params object?[] args) => string.Format(T(key), args);

    /// <summary>切换语言并刷新 XAML 里引用的全部文案, 必须在界面线程调用。</summary>
    public static void SetLanguage(string language)
    {
        _language = language == English ? English : Chinese;
        ResourceDictionary resources = Application.Current.Resources;
        foreach (string key in Strings.Keys)
            resources["S." + key] = T(key);
        Changed?.Invoke();
    }
}
