using Microsoft.Win32;
using Nefarius.ViGEm.Client;
using Nefarius.ViGEm.Client.Targets;
using Nefarius.ViGEm.Client.Targets.Xbox360;

namespace PesPadHub;

/// <summary>
/// 一个常驻的虚拟 Xbox 手柄 (驱动层面是 Xbox 360 设备, 设备名按 Xbox One 命名)。
/// 游戏用 DirectInput 时 8 个都可用; 用 XInput 时整个系统最多只有 4 个位置。
/// </summary>
sealed class VirtualPad
{
    // 自定义 VID, 每个虚拟手柄一个独立 PID, 这样系统/游戏里每个手柄可以有不同的名称。
    // 游戏的手柄设置是按设备记的, 改动 PID 会让用户已经配好的设置失效。
    public const ushort VendorId = 0x5045;
    const ushort PidBase = 0x0E00;
    public const int MaxPads = 8;

    const string OemKey = @"System\CurrentControlSet\Control\MediaProperties\PrivateProperties\Joystick\OEM";

    readonly IXbox360Controller _pad;
    int _rumble = -1;

    public int Number { get; }
    public string Name { get; }
    public ushort ProductId { get; }

    public VirtualPad(ViGEmClient client, int number)
    {
        Number = number;
        ProductId = PidFor(number);
        Name = NameFor(number);
        _pad = client.CreateXbox360Controller(VendorId, ProductId);
        _pad.AutoSubmitReport = false;
        _pad.FeedbackReceived += (_, e) => Interlocked.Exchange(ref _rumble, (e.LargeMotor << 8) | e.SmallMotor);
    }

    public static ushort PidFor(int number) => (ushort)(PidBase + number);

    /// <summary>
    /// 设备名必须以游戏认识的名称开头。PES 2021 的设置程序 (Settings_b.dll) 内置了一张
    /// "名称关键字 → 默认键位" 的表, 按设备名里是否含有关键字来选默认键位, 认不出的名称只会套用通用键位
    /// (右摇杆/扳机对不上)。"Controller (Xbox One For Windows" 是表里对应 Xbox 键位的一项。
    /// 序号加在后面, 用来区分 8 个手柄。
    /// </summary>
    public static string NameFor(int number) => $"Controller (Xbox One For Windows) {number}";

    public void Connect()
    {
        // DirectInput 显示的手柄名取自这个注册表项, 必须在设备出现之前写好
        using (var key = Registry.CurrentUser.CreateSubKey($@"{OemKey}\VID_{VendorId:X4}&PID_{ProductId:X4}"))
            key.SetValue("OEMName", Name, RegistryValueKind.String);
        _pad.Connect();
        Submit(default);
    }

    public void Disconnect() => _pad.Disconnect();

    /// <summary>
    /// 游戏走 XInput 时, 震动由驱动回调过来 (回调在驱动线程触发); 这里取出最新一次的值交给主循环转发给实体手柄。
    /// 游戏走 DirectInput 时震动不经过这里, 见 <see cref="RumbleBridgeLink"/>。
    /// </summary>
    public bool TryTakeRumble(out byte large, out byte small)
    {
        int v = Interlocked.Exchange(ref _rumble, -1);
        large = (byte)(v >> 8);
        small = (byte)v;
        return v >= 0;
    }

    public void Submit(in PadState s)
    {
        PadButtons b = s.Buttons;
        _pad.SetButtonState(Xbox360Button.A, b.HasFlag(PadButtons.South));
        _pad.SetButtonState(Xbox360Button.B, b.HasFlag(PadButtons.East));
        _pad.SetButtonState(Xbox360Button.X, b.HasFlag(PadButtons.West));
        _pad.SetButtonState(Xbox360Button.Y, b.HasFlag(PadButtons.North));
        _pad.SetButtonState(Xbox360Button.LeftShoulder, b.HasFlag(PadButtons.LeftShoulder));
        _pad.SetButtonState(Xbox360Button.RightShoulder, b.HasFlag(PadButtons.RightShoulder));
        _pad.SetButtonState(Xbox360Button.Back, b.HasFlag(PadButtons.Back));
        _pad.SetButtonState(Xbox360Button.Start, b.HasFlag(PadButtons.Start));
        _pad.SetButtonState(Xbox360Button.Guide, b.HasFlag(PadButtons.Guide));
        _pad.SetButtonState(Xbox360Button.LeftThumb, b.HasFlag(PadButtons.LeftStick));
        _pad.SetButtonState(Xbox360Button.RightThumb, b.HasFlag(PadButtons.RightStick));
        _pad.SetButtonState(Xbox360Button.Up, b.HasFlag(PadButtons.Up));
        _pad.SetButtonState(Xbox360Button.Down, b.HasFlag(PadButtons.Down));
        _pad.SetButtonState(Xbox360Button.Left, b.HasFlag(PadButtons.Left));
        _pad.SetButtonState(Xbox360Button.Right, b.HasFlag(PadButtons.Right));

        // XInput 的 Y 轴向上为正, 与 SDL 相反
        _pad.SetAxisValue(Xbox360Axis.LeftThumbX, s.LX);
        _pad.SetAxisValue(Xbox360Axis.LeftThumbY, InvertAxis(s.LY));
        _pad.SetAxisValue(Xbox360Axis.RightThumbX, s.RX);
        _pad.SetAxisValue(Xbox360Axis.RightThumbY, InvertAxis(s.RY));
        _pad.SetSliderValue(Xbox360Slider.LeftTrigger, TriggerToByte(s.LT));
        _pad.SetSliderValue(Xbox360Slider.RightTrigger, TriggerToByte(s.RT));
        _pad.SubmitReport();
    }

    static short InvertAxis(short v) => v == short.MinValue ? short.MaxValue : (short)-v;
    static byte TriggerToByte(short v) => (byte)(Math.Max((int)v, 0) >> 7);
}
