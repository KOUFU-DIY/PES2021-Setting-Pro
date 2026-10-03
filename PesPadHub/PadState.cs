namespace PesPadHub;

[Flags]
enum PadButtons : uint
{
    None = 0,
    South = 1 << 0,
    East = 1 << 1,
    West = 1 << 2,
    North = 1 << 3,
    Back = 1 << 4,
    Guide = 1 << 5,
    Start = 1 << 6,
    LeftStick = 1 << 7,
    RightStick = 1 << 8,
    LeftShoulder = 1 << 9,
    RightShoulder = 1 << 10,
    Up = 1 << 11,
    Down = 1 << 12,
    Left = 1 << 13,
    Right = 1 << 14,
    Touchpad = 1 << 15,
}

/// <summary>
/// 一帧手柄状态, 数值约定与 SDL 一致: 摇杆 -32768..32767 (Y 轴向下为正), 扳机 0..32767。
/// </summary>
record struct PadState(PadButtons Buttons, short LX, short LY, short RX, short RY, short LT, short RT);
