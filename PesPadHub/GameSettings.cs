using System.IO;
using System.Runtime.InteropServices;

namespace PesPadHub;

/// <summary>
/// PES 2021 的设置文件 (Documents\KONAMI\eFootball PES 2021 SEASON UPDATE\settings.dat), 共 612 字节。
/// 布局由实际文件和游戏设置程序逐项切换对比得出 (2026-10-03):
///   0x00  "WECF"
///   0x04  未知 (=107, 大概是版本号)
///   0x08  标志位 DWORD: 0x001 全屏 (否则窗口), 0x004 游戏中 60fps (否则 30), 0x008 回放 60fps (否则 30),
///         0x010 XInput (否则 DirectInput), 0x020 在线 P2P 自动, 0x040 UDP 端口自动, 0x080 语音聊天,
///         0x100 HDR, 0x200/0x400/0x800 = Vsync 禁用/启用1/启用2; 其余位 (0x002 等) 含义未知, 原样保留
///   0x0C  分辨率宽 DWORD, 0x10 高 DWORD
///   0x14  未知 (=2)
///   0x18  音频缓冲 DWORD, 存档位减 1 (界面 1~8 → 0~7)
///   0x1C  UDP 预设端口 1 DWORD, 0x20 端口 2 DWORD
///   0x24  16 字节未知 (全 0)
///   0x34  键盘按键表 24 项 × [0x10, DIK 扫描码]
///   0x64  8 个游戏内玩家手柄位, 每个 64 字节 = 16 字节 DirectInput 实例 GUID + 24 项按键映射 (每项 [类型, 序号])
/// 24 个槽位顺序同游戏: □ × ○ △ L2 R2 L1 R1 Start Select L3 R3 十字键↑↓←→ 左摇杆↑↓←→ 右摇杆↑↓←→。
/// 没解码的字节一律保留原值, 只改动有把握的字段。
/// </summary>
sealed class GameSettingsFile
{
    public const string FileName = "settings.dat";
    public const int PlayerCount = 8;
    public const int AudioBufferMin = 1, AudioBufferMax = 8;
    const int FileSize = 612;
    const int FlagsOffset = 0x08, WidthOffset = 0x0C, HeightOffset = 0x10, AudioBufferOffset = 0x18, UdpPort1Offset = 0x1C, UdpPort2Offset = 0x20;
    const int EntriesOffset = 0x64, EntrySize = 64, MappingSize = 48;

    const uint FullscreenFlag = 0x001, GameFps60Flag = 0x004, ReplayFps60Flag = 0x008, XInputFlag = 0x010, HdrFlag = 0x100;
    const uint P2PAutoFlag = 0x020, UdpAutoFlag = 0x040, VoiceChatFlag = 0x080;
    const uint VsyncOffFlag = 0x200, Vsync1Flag = 0x400, Vsync2Flag = 0x800, VsyncMask = VsyncOffFlag | Vsync1Flag | Vsync2Flag;

    public enum VsyncMode { Off, Mode1, Mode2 }

    /// <summary>设置程序为各类手柄套用的默认按键表 (取自 Settings_b.dll 里的名称关键字表)。</summary>
    public static readonly byte[] XboxMapping =
    [
        0x00, 0x02, 0x00, 0x00, 0x00, 0x01, 0x00, 0x03, 0x04, 0x04, 0x06, 0x04, 0x00, 0x04, 0x00, 0x05,
        0x00, 0x07, 0x00, 0x06, 0x00, 0x08, 0x00, 0x09, 0x0C, 0x00, 0x0D, 0x00, 0x0E, 0x00, 0x0F, 0x00,
        0x06, 0x01, 0x04, 0x01, 0x06, 0x00, 0x04, 0x00, 0x06, 0x03, 0x04, 0x03, 0x06, 0x02, 0x04, 0x02,
    ];
    public static readonly byte[] DualShockMapping =
    [
        0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x06, 0x00, 0x07, 0x00, 0x04, 0x00, 0x05,
        0x00, 0x09, 0x00, 0x08, 0x00, 0x0A, 0x00, 0x0B, 0x0C, 0x00, 0x0D, 0x00, 0x0E, 0x00, 0x0F, 0x00,
        0x06, 0x01, 0x04, 0x01, 0x06, 0x00, 0x04, 0x00, 0x06, 0x05, 0x04, 0x05, 0x06, 0x04, 0x04, 0x04,
    ];
    public static readonly byte[] GenericMapping =
    [
        0x00, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00, 0x03, 0x00, 0x06, 0x00, 0x07, 0x00, 0x04, 0x00, 0x05,
        0x00, 0x09, 0x00, 0x08, 0x00, 0x0A, 0x00, 0x0B, 0x0C, 0x00, 0x0D, 0x00, 0x0E, 0x00, 0x0F, 0x00,
        0x06, 0x01, 0x04, 0x01, 0x06, 0x00, 0x04, 0x00, 0x06, 0x03, 0x04, 0x03, 0x06, 0x02, 0x04, 0x02,
    ];

    public static string DefaultPath => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "KONAMI", "eFootball PES 2021 SEASON UPDATE", FileName);

    readonly byte[] _data;

    public string Path { get; }

    GameSettingsFile(string path, byte[] data)
    {
        Path = path;
        _data = data;
    }

    public static GameSettingsFile Load(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        if (data.Length != FileSize || data[0] != (byte)'W' || data[1] != (byte)'E' || data[2] != (byte)'C' || data[3] != (byte)'F')
            throw new InvalidDataException(FileName);
        return new GameSettingsFile(path, data);
    }

    /// <summary>写回文件; 先把原文件备份为 settings.dat.bak。</summary>
    public void Save()
    {
        File.Copy(Path, Path + ".bak", overwrite: true);
        File.WriteAllBytes(Path, _data);
    }

    uint Flags
    {
        get => BitConverter.ToUInt32(_data, FlagsOffset);
        set => BitConverter.GetBytes(value).CopyTo(_data, FlagsOffset);
    }

    bool GetFlag(uint flag) => (Flags & flag) != 0;
    void SetFlag(uint flag, bool on) => Flags = on ? Flags | flag : Flags & ~flag;

    public bool Fullscreen { get => GetFlag(FullscreenFlag); set => SetFlag(FullscreenFlag, value); }
    public bool GameFps60 { get => GetFlag(GameFps60Flag); set => SetFlag(GameFps60Flag, value); }
    public bool ReplayFps60 { get => GetFlag(ReplayFps60Flag); set => SetFlag(ReplayFps60Flag, value); }
    public bool XInput { get => GetFlag(XInputFlag); set => SetFlag(XInputFlag, value); }
    public bool Hdr { get => GetFlag(HdrFlag); set => SetFlag(HdrFlag, value); }

    public VsyncMode Vsync
    {
        get => GetFlag(Vsync2Flag) ? VsyncMode.Mode2 : GetFlag(VsyncOffFlag) ? VsyncMode.Off : VsyncMode.Mode1;
        set => Flags = (Flags & ~VsyncMask) | value switch { VsyncMode.Off => VsyncOffFlag, VsyncMode.Mode2 => Vsync2Flag, _ => Vsync1Flag };
    }

    int GetInt(int offset) => (int)BitConverter.ToUInt32(_data, offset);
    void SetInt(int offset, int value) => BitConverter.GetBytes((uint)value).CopyTo(_data, offset);

    public int Width { get => GetInt(WidthOffset); set => SetInt(WidthOffset, value); }
    public int Height { get => GetInt(HeightOffset); set => SetInt(HeightOffset, value); }

    // ── 音频 / 在线 ──

    /// <summary>音频缓冲档位 1~8。</summary>
    public int AudioBuffer
    {
        get => Math.Clamp(GetInt(AudioBufferOffset) + 1, AudioBufferMin, AudioBufferMax);
        set => SetInt(AudioBufferOffset, Math.Clamp(value, AudioBufferMin, AudioBufferMax) - 1);
    }

    public bool P2PAuto { get => GetFlag(P2PAutoFlag); set => SetFlag(P2PAutoFlag, value); }
    public bool UdpAuto { get => GetFlag(UdpAutoFlag); set => SetFlag(UdpAutoFlag, value); }
    public bool VoiceChat { get => GetFlag(VoiceChatFlag); set => SetFlag(VoiceChatFlag, value); }
    public int UdpPort1 { get => GetInt(UdpPort1Offset); set => SetInt(UdpPort1Offset, value); }
    public int UdpPort2 { get => GetInt(UdpPort2Offset); set => SetInt(UdpPort2Offset, value); }

    public Guid PlayerDevice(int player) => new(_data.AsSpan(EntriesOffset + player * EntrySize, 16));

    /// <summary>给一个玩家位指定手柄; 换了设备才换按键表, 没换就保留用户在游戏里调过的按键。</summary>
    public void SetPlayerDevice(int player, Guid device, byte[] mappingForNewDevice)
    {
        int at = EntriesOffset + player * EntrySize;
        if (PlayerDevice(player) == device) return;
        device.ToByteArray().CopyTo(_data, at);
        mappingForNewDevice.CopyTo(_data, at + 16);
    }

    public bool PlayerMappingIs(int player, byte[] mapping) =>
        _data.AsSpan(EntriesOffset + player * EntrySize + 16, MappingSize).SequenceEqual(mapping);

    /// <summary>按设备名选默认按键表, 和游戏设置程序的规则一致 (名称里含关键字)。</summary>
    public static byte[] MappingForName(string productName)
    {
        if (productName.Contains("Xbox", StringComparison.OrdinalIgnoreCase) || productName.Contains("XBOX")) return XboxMapping;
        if (productName.Contains("Wireless Controller")) return DualShockMapping;
        return GenericMapping;
    }
}

/// <summary>DirectInput 枚举到的一个游戏手柄。</summary>
sealed record DirectInputDevice(Guid Instance, ushort Vid, ushort Pid, string Name)
{
    /// <summary>是本工具的第几个虚拟手柄 (1~8), 不是则为 0。</summary>
    public int VirtualNumber =>
        Vid == VirtualPad.VendorId ? Enumerable.Range(1, PadHub.SlotCount).FirstOrDefault(n => VirtualPad.PidFor(n) == Pid) : 0;
}

/// <summary>
/// 用 DirectInput 枚举当前接着的手柄。游戏设置文件里就是靠实例 GUID 认手柄的。
/// 直接调 dinput8.dll 的 COM 接口, 只用到 EnumDevices 一个方法。
/// </summary>
static unsafe class DirectInputDevices
{
    [DllImport("dinput8.dll")]
    static extern int DirectInput8Create(IntPtr instance, uint version, in Guid riid, out IntPtr di, IntPtr outer);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);

    static readonly Guid IID_IDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");
    const uint DirectInputVersion = 0x0800;
    const uint DI8DEVCLASS_GAMECTRL = 4;
    const uint DIEDFL_ATTACHEDONLY = 1;

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    delegate int EnumDevicesCallback(IntPtr instance, IntPtr context);

    public static List<DirectInputDevice> Enumerate()
    {
        var result = new List<DirectInputDevice>();
        if (DirectInput8Create(GetModuleHandleW(null), DirectInputVersion, IID_IDirectInput8W, out IntPtr di, IntPtr.Zero) < 0)
            return result;
        try
        {
            // DIDEVICEINSTANCEW: dwSize, guidInstance (偏移 4), guidProduct (偏移 20), dwDevType, tszInstanceName[260] (偏移 40), tszProductName[260] (偏移 560)
            EnumDevicesCallback callback = (instance, _) =>
            {
                var product = Marshal.PtrToStructure<Guid>(instance + 20);
                byte[] p = product.ToByteArray();     // Data1 的低 16 位是 VID, 高 16 位是 PID
                ushort vid = (ushort)(p[0] | (p[1] << 8)), pid = (ushort)(p[2] | (p[3] << 8));
                string name = Marshal.PtrToStringUni(instance + 560) ?? "";
                result.Add(new DirectInputDevice(Marshal.PtrToStructure<Guid>(instance + 4), vid, pid, name));
                return 1;   // DIENUM_CONTINUE
            };
            // 虚表: 0 QueryInterface, 1 AddRef, 2 Release, 3 CreateDevice, 4 EnumDevices
            IntPtr* vtable = *(IntPtr**)di;
            var enumDevices = (delegate* unmanaged[Stdcall]<IntPtr, uint, IntPtr, IntPtr, uint, int>)vtable[4];
            enumDevices(di, DI8DEVCLASS_GAMECTRL, Marshal.GetFunctionPointerForDelegate(callback), IntPtr.Zero, DIEDFL_ATTACHEDONLY);
            GC.KeepAlive(callback);
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vtable[2])(di);
        }
        catch (Exception)
        {
            // 枚举失败就当没查到
        }
        return result;
    }

    /// <summary>
    /// DirectInput 给每个 VID/PID 分配过的实例 GUID 记在注册表里, 设备不在时也能查到;
    /// 用来认出设置文件里记的是哪个虚拟手柄 (总开关关着时它们都不在)。查不到返回 null。
    /// </summary>
    public static Guid? RememberedInstance(ushort vid, ushort pid)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                $@"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\DirectInput\VID_{vid:X4}&PID_{pid:X4}\Calibration\0");
            return key?.GetValue("GUID") is byte[] { Length: 16 } bytes ? new Guid(bytes) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}

/// <summary>开机自启动: 写在当前用户的 Run 注册表项里, 以最小化到托盘的方式启动。</summary>
static class AutoStart
{
    const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    const string ValueName = "PesPadHub";

    public static bool IsEnabled
    {
        get
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return key?.GetValue(ValueName) is string command && command.Contains(Environment.ProcessPath ?? "\0", StringComparison.OrdinalIgnoreCase);
        }
    }

    public static void SetEnabled(bool enabled)
    {
        using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
        if (enabled) key.SetValue(ValueName, $"\"{Environment.ProcessPath}\" --minimized");
        else key.DeleteValue(ValueName, throwOnMissingValue: false);
    }

    /// <summary>程序改名或挪了位置后, 自启动项里记的还是旧路径; 启动时若旧路径已不存在就改成现在的。</summary>
    public static void RepairPath()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            if (key?.GetValue(ValueName) is not string command || Environment.ProcessPath is not { } current) return;
            string old = command.Trim().Split('"', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "";
            if (old.Length == 0 || string.Equals(old, current, StringComparison.OrdinalIgnoreCase) || System.IO.File.Exists(old)) return;
            SetEnabled(true);
        }
        catch (Exception) { /* 注册表读写不了就算了, 用户可以在界面上重新勾选 */ }
    }
}
