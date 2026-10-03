using SDL;
using static SDL.SDL3;

namespace PesPadHub;

/// <summary>一个已打开的实体手柄 (通过 SDL 读取, 支持 Xbox / PS / Switch / 通用 DirectInput 手柄)。</summary>
sealed unsafe class PhysicalPad
{
    static readonly (SDL_GamepadButton Sdl, PadButtons Pad)[] ButtonMap =
    [
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, PadButtons.South),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_EAST, PadButtons.East),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_WEST, PadButtons.West),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_NORTH, PadButtons.North),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_BACK, PadButtons.Back),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_GUIDE, PadButtons.Guide),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_START, PadButtons.Start),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_STICK, PadButtons.LeftStick),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_STICK, PadButtons.RightStick),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_LEFT_SHOULDER, PadButtons.LeftShoulder),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_RIGHT_SHOULDER, PadButtons.RightShoulder),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_UP, PadButtons.Up),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_DOWN, PadButtons.Down),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_LEFT, PadButtons.Left),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_DPAD_RIGHT, PadButtons.Right),
        (SDL_GamepadButton.SDL_GAMEPAD_BUTTON_TOUCHPAD, PadButtons.Touchpad),
    ];

    readonly SDL_Gamepad* _handle;

    public SDL_JoystickID Id { get; }
    public string Name { get; }
    public string Path { get; }
    public ushort Vid { get; }
    public ushort Pid { get; }
    /// <summary>同一个实体手柄重插后用它找回原来的槽位。</summary>
    public string AffinityKey { get; }
    public int Slot { get; set; } = -1;
    public PadState LastState { get; set; }
    /// <summary>换槽位后即使输入没变也要把当前状态重新提交一次。</summary>
    public bool ForceSubmit { get; set; }
    /// <summary>最近一次按震动桥的数值下发给这个手柄的震动 (大马达在高 8 位) 以及下发时间。</summary>
    public ushort BridgeRumble { get; set; }
    public long BridgeRumbleTick { get; set; }

    PhysicalPad(SDL_JoystickID id, SDL_Gamepad* handle)
    {
        Id = id;
        _handle = handle;
        Name = SDL_GetGamepadName(handle) ?? "未知手柄";
        Path = SDL_GetGamepadPath(handle) ?? "";
        Vid = SDL_GetGamepadVendor(handle);
        Pid = SDL_GetGamepadProduct(handle);
        string? serial = SDL_GetGamepadSerial(handle);
        AffinityKey = !string.IsNullOrEmpty(serial) ? $"{Vid:X4}:{Pid:X4}:{serial}" : $"{Vid:X4}:{Pid:X4}:{Path}";
    }

    public static PhysicalPad? Open(SDL_JoystickID id)
    {
        SDL_Gamepad* handle = SDL_OpenGamepad(id);
        return handle == null ? null : new PhysicalPad(id, handle);
    }

    public PadState Read()
    {
        PadButtons b = PadButtons.None;
        foreach (var (sdl, pad) in ButtonMap)
            if (SDL_GetGamepadButton(_handle, sdl))
                b |= pad;

        return new PadState(
            b,
            SDL_GetGamepadAxis(_handle, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX),
            SDL_GetGamepadAxis(_handle, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY),
            SDL_GetGamepadAxis(_handle, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTX),
            SDL_GetGamepadAxis(_handle, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHTY),
            SDL_GetGamepadAxis(_handle, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFT_TRIGGER),
            SDL_GetGamepadAxis(_handle, SDL_GamepadAxis.SDL_GAMEPAD_AXIS_RIGHT_TRIGGER));
    }

    // 游戏不主动停止时最多震 5 秒, 避免工具或游戏异常退出后手柄一直震
    public void Rumble(byte large, byte small, uint durationMs = 5000)
    {
        SDL_RumbleGamepad(_handle, (ushort)(large * 257), (ushort)(small * 257), durationMs);
    }

    public void Close() => SDL_CloseGamepad(_handle);
}
