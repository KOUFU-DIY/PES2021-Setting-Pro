using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Text;
using Nefarius.ViGEm.Client;
using SDL;
using static SDL.SDL3;

namespace PesPadHub;

/// <param name="VirtualName">虚拟手柄的设备名, 和游戏/系统里看到的名称一致。</param>
/// <param name="Enabled">这个槽位没有被单独关闭。</param>
record SlotSnapshot(string VirtualName, string? PadName, string? PadId, bool Connected, bool Pinned, bool Enabled);

/// <summary>已接入但没有槽位可放的实体手柄。</summary>
record WaitingSnapshot(uint Id, string Name, string PadId);

/// <param name="Enabled">总开关的状态。</param>
/// <param name="Ready">虚拟手柄已创建完成并在转发输入。Enabled 为真而 Ready 为假且没有错误, 表示正在启动。</param>
/// <param name="BridgeConnected">游戏进程里的震动插件当前在线。</param>
record HubSnapshot(SlotSnapshot[] Slots, WaitingSnapshot[] Waiting, bool Enabled, bool Ready, string? FatalError, bool BridgeConnected);

/// <summary>
/// 核心: 一条后台线程上常驻 8 个虚拟手柄, 监听实体手柄插拔并把输入转发过去。
/// 所有状态 (包括配置文件) 只在这条线程上读写; 界面通过 Snapshot/事件读取, 通过排队的命令修改。
/// </summary>
sealed unsafe class PadHub
{
    public const int SlotCount = VirtualPad.MaxPads;

    [DllImport("winmm.dll")]
    static extern uint timeBeginPeriod(uint ms);

    [DllImport("winmm.dll")]
    static extern uint timeEndPeriod(uint ms);

    /// <summary>每次下发的震动最多持续 5 秒 (见 PhysicalPad.Rumble), 游戏要求更久时每隔这么久续一次。</summary>
    const long BridgeRefreshMs = 2000;

    readonly Thread _thread;
    readonly ConcurrentQueue<Action> _commands = new();
    readonly AppConfig _config;
    readonly int _demoPads;
    volatile bool _running = true;

    readonly VirtualPad?[] _slots = new VirtualPad?[SlotCount];
    readonly PhysicalPad?[] _mapped = new PhysicalPad?[SlotCount];
    /// <summary>全部已打开的实体手柄, 包括没有槽位可放、在 "待分配" 里等着的。</summary>
    readonly Dictionary<SDL_JoystickID, PhysicalPad> _pads = [];
    readonly Dictionary<string, int> _sessionAffinity = [];
    readonly List<SDL_JoystickID> _demoIds = [];
    readonly bool[] _driverRumble = new bool[SlotCount];
    ViGEmClient? _client;
    RumbleBridgeLink? _bridge;
    bool _ready;
    (string Key, object?[] Args)? _fatal;
    long _neutralizeAt = long.MaxValue;
    volatile HubSnapshot _snapshot;
    int _activityMask, _rumbleMask;
    string[] _activePadIds = [];
    readonly List<string> _activePadsScratch = [];

    /// <summary>映射或开关状态变化时触发 (在后台线程上)。</summary>
    public event Action? Changed;
    /// <summary>事件日志 (在后台线程上)。</summary>
    public event Action<string>? Log;

    public HubSnapshot Snapshot => _snapshot;
    /// <summary>第 N 位为 1 表示第 N 个槽位的实体手柄当前有按键/摇杆输入。</summary>
    public int ActivityMask => Volatile.Read(ref _activityMask);
    /// <summary>第 N 位为 1 表示游戏正在让第 N 个槽位的手柄震动。</summary>
    public int RumbleMask => Volatile.Read(ref _rumbleMask);
    /// <summary>当前有输入的实体手柄 (含待分配的), 以 VID:PID 表示。</summary>
    public string[] ActivePadIds => Volatile.Read(ref _activePadIds);

    public PadHub(AppConfig config, int demoPads = 0)
    {
        _config = config;
        _demoPads = demoPads;
        _snapshot = BuildSnapshot();
        _thread = new Thread(Run) { Name = "PadHub", IsBackground = true };
    }

    public void Start() => _thread.Start();

    public void Stop()
    {
        _running = false;
        if (_thread.IsAlive) _thread.Join(5000);
    }

    public void SetEnabled(bool enabled) => _commands.Enqueue(() => DoSetEnabled(enabled));
    public void SetSlotEnabled(int slot, bool enabled) => _commands.Enqueue(() => DoSetSlotEnabled(slot, enabled));
    public void SwapSlots(int a, int b) => _commands.Enqueue(() => DoSwapSlots(a, b));
    public void AssignPad(uint padId, int slot) => _commands.Enqueue(() => DoAssignPad((SDL_JoystickID)padId, slot));
    public void TogglePin(int slot) => _commands.Enqueue(() => DoTogglePin(slot));
    public void Identify(int slot) => _commands.Enqueue(() => _mapped[slot]?.Rumble(200, 200, 500));
    public void IdentifyPad(uint padId) => _commands.Enqueue(() =>
    {
        if (_pads.TryGetValue((SDL_JoystickID)padId, out PhysicalPad? pad)) pad.Rumble(200, 200, 500);
    });

    /// <summary>上次安装震动插件时选的游戏文件夹 (配置归后台线程管, 这里只是记一下)。</summary>
    public string? GameDirectory => _config.GameDirectory;

    public void SetGameDirectory(string directory) => _commands.Enqueue(() =>
    {
        _config.GameDirectory = directory;
        _config.Save();
    });

    public string? SkippedUpdate => _config.SkippedUpdate;

    public void SetSkippedUpdate(string? tag) => _commands.Enqueue(() =>
    {
        _config.SkippedUpdate = tag;
        _config.Save();
    });

    /// <summary>保存语言设置, 并按新语言重新生成快照里的文案。</summary>
    public void SetLanguage(string language) => _commands.Enqueue(() =>
    {
        _config.Language = language;
        _config.Save();
        Publish();
    });

    void Run()
    {
        timeBeginPeriod(1);
        try
        {
            try
            {
                _bridge = new RumbleBridgeLink();
            }
            catch (Exception)
            {
                // 共享内存建不起来只影响游戏内震动, 其余功能照常
            }
            if (_config.Enabled) Activate();
            Publish();

            while (_running)
            {
                try
                {
                    while (_commands.TryDequeue(out Action? command)) command();
                    if (_ready)
                    {
                        NeutralizeIdleSlotsIfDue();
                        PumpSdlEvents();
                        ForwardInput();
                        if (_demoIds.Count > 0) AnimateDemoPads();
                    }
                }
                catch (Exception ex)
                {
                    Deactivate();
                    _fatal = ("FatalRun", [ex.Message]);
                    Publish();
                }
                // 总开关关闭时没有输入要转发, 只需偶尔看一下有没有命令
                Thread.Sleep(_ready ? 1 : 30);
            }
        }
        finally
        {
            Deactivate();
            _bridge?.Dispose();
            _bridge = null;
            timeEndPeriod(1);
            Publish();
        }
    }

    bool SlotEnabled(int slot) => !_config.DisabledSlots.Contains(slot);

    /// <summary>创建全部虚拟手柄并开始监听实体手柄。失败时原因记在 _fatal 里。</summary>
    void Activate()
    {
        _fatal = null;
        try
        {
            _client = new ViGEmClient();
        }
        catch (Exception)
        {
            _fatal = ("FatalNoVigem", []);
            return;
        }

        for (int i = 0; i < SlotCount; i++)
        {
            if (!_running)
            {
                Deactivate();
                return;
            }
            if (!SlotEnabled(i)) continue;
            _slots[i] = new VirtualPad(_client, i + 1);
            _slots[i]!.Connect();
            // 逐个接入并稍作等待, 让系统按 1~8 的顺序枚举
            Thread.Sleep(200);
        }
        WriteLog(Loc.T("LogCreated", _slots.Count(s => s != null)));

        // 本工具通常在后台运行 (前台是游戏), 必须允许后台读取手柄
        SDL_SetHint(SDL_HINT_JOYSTICK_ALLOW_BACKGROUND_EVENTS, "1"u8);
        // 不让 SDL 打开我们自己创建的虚拟手柄, 否则会形成回环
        string ignore = string.Join(',', Enumerable.Range(1, SlotCount).Select(n => $"0x{VirtualPad.VendorId:x4}/0x{VirtualPad.PidFor(n):x4}"));
        SDL_SetHint(SDL_HINT_GAMECONTROLLER_IGNORE_DEVICES, ignore);
        if (!SDL_Init(SDL_InitFlags.SDL_INIT_GAMEPAD))
        {
            string error = SDL_GetError() ?? "";
            Deactivate();
            _fatal = ("FatalSdl", [error]);
            return;
        }

        foreach (VirtualPad? slot in _slots) if (slot != null) Neutralize(slot);
        // 最后创建的几个手柄此时可能还没完全就绪, 稍后对仍然空着的槽位再归中一次
        _neutralizeAt = Environment.TickCount64 + 1500;

        _ready = true;
        AttachDemoPads();
    }

    /// <summary>
    /// 让一个空闲的虚拟手柄精确归中。驱动给新设备的初始状态摇杆略偏离中心, 而且只在报告内容有变化时才更新,
    /// 所以先提交一个略有不同的状态, 再提交归中状态。
    /// </summary>
    static void Neutralize(VirtualPad pad)
    {
        pad.Submit(new PadState(PadButtons.None, 256, 0, 0, 0, 0, 0));
        pad.Submit(default);
    }

    void NeutralizeIdleSlotsIfDue()
    {
        if (Environment.TickCount64 < _neutralizeAt) return;
        _neutralizeAt = long.MaxValue;
        for (int i = 0; i < SlotCount; i++)
            if (_mapped[i] == null && _slots[i] is { } slot)
                Neutralize(slot);
    }

    /// <summary>移除全部虚拟手柄, 放开实体手柄。可重复调用。</summary>
    void Deactivate()
    {
        _ready = false;
        _neutralizeAt = long.MaxValue;
        Volatile.Write(ref _activityMask, 0);
        Volatile.Write(ref _rumbleMask, 0);
        Array.Clear(_driverRumble);

        foreach (PhysicalPad pad in _pads.Values)
        {
            pad.Rumble(0, 0);
            pad.Close();
        }
        _pads.Clear();
        _demoIds.Clear();
        Array.Clear(_mapped);
        SDL_Quit();

        for (int i = 0; i < SlotCount; i++)
        {
            try { _slots[i]?.Disconnect(); } catch (Exception) { /* 驱动已断开时忽略 */ }
            _slots[i] = null;
        }
        _client?.Dispose();
        _client = null;
    }

    void DoSetEnabled(bool enabled)
    {
        _config.Enabled = enabled;
        _config.Save();

        if (enabled)
        {
            if (_ready) return;
            // 先让界面显示 "正在创建", 创建 8 个手柄要一两秒
            _fatal = null;
            Publish();
            Activate();
        }
        else
        {
            bool wasActive = _ready;
            Deactivate();
            _fatal = null;
            if (wasActive) WriteLog(Loc.T("LogSwitchedOff"));
        }
        Publish();
    }

    /// <summary>单独开关一个槽位: 关掉时移除该虚拟手柄, 上面的实体手柄退到待分配; 打开时重新创建。</summary>
    void DoSetSlotEnabled(int slot, bool enabled)
    {
        if (slot < 0 || slot >= SlotCount || SlotEnabled(slot) == enabled) return;
        if (enabled) _config.DisabledSlots.Remove(slot);
        else _config.DisabledSlots.Add(slot);
        _config.Save();

        if (_ready && _client != null)
        {
            if (enabled)
            {
                _slots[slot] = new VirtualPad(_client, slot + 1);
                _slots[slot]!.Connect();
                _neutralizeAt = Environment.TickCount64 + 1500;
                WriteLog(Loc.T("LogSlotEnabled", slot + 1));
                FillFreeSlotsFromWaiting();
            }
            else
            {
                if (_mapped[slot] is { } pad) MoveToWaiting(pad);
                try { _slots[slot]?.Disconnect(); } catch (Exception) { /* 驱动已断开时忽略 */ }
                _slots[slot] = null;
                WriteLog(Loc.T("LogSlotDisabled", slot + 1));
            }
        }
        Publish();
    }

    void PumpSdlEvents()
    {
        SDL_Event e;
        while (SDL_PollEvent(&e))
        {
            switch ((SDL_EventType)e.type)
            {
                case SDL_EventType.SDL_EVENT_GAMEPAD_ADDED: OnPadAdded(e.gdevice.which); break;
                case SDL_EventType.SDL_EVENT_GAMEPAD_REMOVED: OnPadRemoved(e.gdevice.which); break;
            }
        }
    }

    void OnPadAdded(SDL_JoystickID id)
    {
        if (_pads.ContainsKey(id)) return;
        if (SDL_GetGamepadVendorForID(id) == VirtualPad.VendorId) return;

        PhysicalPad? pad = PhysicalPad.Open(id);
        if (pad == null)
        {
            WriteLog(Loc.T("LogOpenFailed", SDL_GetError()));
            return;
        }
        _pads[id] = pad;

        int slot = PlacePad(pad);
        if (slot < 0)
        {
            // 没有空位: 留在待分配区, 等有人让位或用户手动指定
            WriteLog(Loc.T("LogWaiting", pad.Name));
        }
        else
        {
            Attach(pad, slot);
            WriteLog(Loc.T("LogAttached", pad.Name, slot + 1));
        }
        Publish();
    }

    /// <summary>
    /// 决定新接入的手柄放到哪个槽位: 被固定的手柄回到固定槽位 (必要时把占位的手柄挪走),
    /// 其余手柄优先回到本次运行中用过的槽位, 否则占用序号最小的空槽; 没有空槽返回 -1。
    /// </summary>
    int PlacePad(PhysicalPad pad)
    {
        PinEntry? pin = _config.Pins.Find(p => p.Key == pad.AffinityKey);
        if (pin != null && SlotEnabled(pin.Slot))
        {
            if (_mapped[pin.Slot] is { } occupant)
            {
                Detach(pin.Slot);
                int other = FreeSlot();
                if (other >= 0)
                {
                    Attach(occupant, other);
                    WriteLog(Loc.T("LogEvicted", occupant.Name, other + 1));
                }
                else
                {
                    WriteLog(Loc.T("LogWaiting", occupant.Name));
                }
            }
            return pin.Slot;
        }

        if (_sessionAffinity.TryGetValue(pad.AffinityKey, out int previous) && SlotEnabled(previous) && _mapped[previous] == null && PinAt(previous) == null)
            return previous;
        return FreeSlot();
    }

    /// <summary>序号最小的空槽; 尽量不占用为 "已固定但暂未连接" 的手柄预留的槽位。</summary>
    int FreeSlot()
    {
        for (int i = 0; i < SlotCount; i++)
            if (SlotEnabled(i) && _mapped[i] == null && PinAt(i) == null)
                return i;
        for (int i = 0; i < SlotCount; i++)
            if (SlotEnabled(i) && _mapped[i] == null)
                return i;
        return -1;
    }

    PinEntry? PinAt(int slot) => _config.Pins.Find(p => p.Slot == slot);

    IEnumerable<PhysicalPad> WaitingPads => _pads.Values.Where(p => p.Slot < 0);

    void Attach(PhysicalPad pad, int slot)
    {
        _mapped[slot] = pad;
        pad.Slot = slot;
        pad.ForceSubmit = true;
        pad.BridgeRumble = 0;
        _sessionAffinity[pad.AffinityKey] = slot;
    }

    void Detach(int slot)
    {
        if (_mapped[slot] is { } pad) pad.Slot = -1;
        _mapped[slot] = null;
        // 虚拟手柄保持连接, 只是回到无输入状态, 游戏侧感知不到拔出
        _slots[slot]?.Submit(default);
    }

    void MoveToWaiting(PhysicalPad pad)
    {
        if (pad.Slot >= 0) Detach(pad.Slot);
        pad.Rumble(0, 0);
    }

    /// <summary>有空位了就把待分配区里的手柄 (按接入先后) 放进去。</summary>
    void FillFreeSlotsFromWaiting()
    {
        foreach (PhysicalPad pad in WaitingPads.OrderBy(p => p.Id).ToList())
        {
            int slot = PlacePad(pad);
            if (slot < 0) break;
            Attach(pad, slot);
            WriteLog(Loc.T("LogAttached", pad.Name, slot + 1));
        }
    }

    void OnPadRemoved(SDL_JoystickID id)
    {
        if (!_pads.Remove(id, out PhysicalPad? pad)) return;
        if (pad.Slot >= 0)
        {
            int slot = pad.Slot;
            Detach(slot);
            WriteLog(Loc.T("LogRemoved", pad.Name, slot + 1));
            pad.Close();
            FillFreeSlotsFromWaiting();
        }
        else
        {
            WriteLog(Loc.T("LogRemovedWaiting", pad.Name));
            pad.Close();
        }
        Publish();
    }

    void ForwardInput()
    {
        long now = Environment.TickCount64;
        if (_bridge != null && _bridge.Poll(now)) Publish();

        int activity = 0, rumbling = 0;
        for (int i = 0; i < SlotCount; i++)
        {
            VirtualPad? slot = _slots[i];
            if (slot == null) continue;

            // 震动有两个来源: 驱动回调 (游戏走 XInput 时) 和游戏进程里的震动插件 (游戏走 DirectInput 时)
            bool hasRumble = slot.TryTakeRumble(out byte large, out byte small);
            if (hasRumble) _driverRumble[i] = large != 0 || small != 0;
            (byte bridgeLarge, byte bridgeSmall) = _bridge?.Read(i) ?? default;
            ushort bridge = (ushort)((bridgeLarge << 8) | bridgeSmall);
            if (_driverRumble[i] || bridge != 0) rumbling |= 1 << i;

            PhysicalPad? pad = _mapped[i];
            if (pad == null) continue;

            PadState state = pad.Read();
            if (state != pad.LastState || pad.ForceSubmit)
            {
                slot.Submit(state);
                pad.LastState = state;
                pad.ForceSubmit = false;
            }
            if (hasRumble) pad.Rumble(large, small);
            if (bridge != pad.BridgeRumble || (bridge != 0 && now - pad.BridgeRumbleTick > BridgeRefreshMs))
            {
                pad.Rumble(bridgeLarge, bridgeSmall);
                pad.BridgeRumble = bridge;
                pad.BridgeRumbleTick = now;
            }
            if (IsActive(state))
            {
                activity |= 1 << i;
                _activePadsScratch.Add($"{pad.Vid:X4}:{pad.Pid:X4}");
            }
        }
        // 待分配的手柄没有槽位, 也读一下, 控制器页里选了它们时才有输入指示
        foreach (PhysicalPad pad in _pads.Values)
            if (pad.Slot < 0 && IsActive(pad.Read()))
                _activePadsScratch.Add($"{pad.Vid:X4}:{pad.Pid:X4}");

        Volatile.Write(ref _activityMask, activity);
        Volatile.Write(ref _rumbleMask, rumbling);
        if (_activePadsScratch.Count > 0 || _activePadIds.Length > 0)
            Volatile.Write(ref _activePadIds, _activePadsScratch.ToArray());
        _activePadsScratch.Clear();
    }

    static bool IsActive(in PadState s)
    {
        const int stick = 12000, trigger = 6000;
        return s.Buttons != PadButtons.None
            || Math.Abs((int)s.LX) > stick || Math.Abs((int)s.LY) > stick
            || Math.Abs((int)s.RX) > stick || Math.Abs((int)s.RY) > stick
            || s.LT > trigger || s.RT > trigger;
    }

    /// <summary>交换两个槽位的内容 (已连接的手柄和固定设置一起换)。</summary>
    void DoSwapSlots(int a, int b)
    {
        if (!_ready || a == b || a < 0 || b < 0 || a >= SlotCount || b >= SlotCount) return;
        if (!SlotEnabled(a) || !SlotEnabled(b)) return;

        PhysicalPad? padA = _mapped[a], padB = _mapped[b];
        Detach(a);
        Detach(b);
        if (padA != null) Attach(padA, b);
        if (padB != null) Attach(padB, a);

        bool pinsChanged = false;
        foreach (PinEntry pin in _config.Pins)
        {
            if (pin.Slot == a) { pin.Slot = b; pinsChanged = true; }
            else if (pin.Slot == b) { pin.Slot = a; pinsChanged = true; }
        }
        if (pinsChanged) _config.Save();
        Publish();
    }

    /// <summary>把一个手柄 (待分配区里的, 或已在别的槽位上的) 放到指定槽位; 原来占着这个槽位的手柄退到待分配区。</summary>
    void DoAssignPad(SDL_JoystickID id, int slot)
    {
        if (!_ready || slot < 0 || slot >= SlotCount || !SlotEnabled(slot)) return;
        if (!_pads.TryGetValue(id, out PhysicalPad? pad) || pad.Slot == slot) return;

        if (pad.Slot >= 0)
        {
            DoSwapSlots(pad.Slot, slot);
            return;
        }
        if (_mapped[slot] is { } occupant)
        {
            MoveToWaiting(occupant);
            WriteLog(Loc.T("LogWaiting", occupant.Name));
        }
        Attach(pad, slot);
        WriteLog(Loc.T("LogAttached", pad.Name, slot + 1));
        Publish();
    }

    void DoTogglePin(int slot)
    {
        if (!_ready || slot < 0 || slot >= SlotCount) return;

        if (PinAt(slot) is { } pin)
        {
            _config.Pins.Remove(pin);
            WriteLog(Loc.T("LogUnpinned", pin.Name, slot + 1));
        }
        else if (_mapped[slot] is { } pad)
        {
            _config.Pins.RemoveAll(p => p.Key == pad.AffinityKey);
            _config.Pins.Add(new PinEntry { Key = pad.AffinityKey, Slot = slot, Name = pad.Name });
            WriteLog(Loc.T("LogPinned", pad.Name, slot + 1));
        }
        else
        {
            return;
        }
        _config.Save();
        Publish();
    }

    HubSnapshot BuildSnapshot()
    {
        var slots = new SlotSnapshot[SlotCount];
        for (int i = 0; i < SlotCount; i++)
        {
            PhysicalPad? pad = _mapped[i];
            PinEntry? pin = PinAt(i);
            slots[i] = new SlotSnapshot(
                VirtualPad.NameFor(i + 1),
                pad?.Name ?? pin?.Name,
                pad == null ? null : $"{pad.Vid:X4}:{pad.Pid:X4}",
                pad != null,
                pin != null,
                SlotEnabled(i));
        }
        WaitingSnapshot[] waiting = WaitingPads.OrderBy(p => p.Id)
            .Select(p => new WaitingSnapshot((uint)p.Id, p.Name, $"{p.Vid:X4}:{p.Pid:X4}")).ToArray();
        string? fatal = _fatal is { } f ? Loc.T(f.Key, f.Args) : null;
        return new HubSnapshot(slots, waiting, _config.Enabled, _ready, fatal, _ready && _bridge is { Alive: true });
    }

    void Publish()
    {
        _snapshot = BuildSnapshot();
        Changed?.Invoke();
    }

    void WriteLog(string message) => Log?.Invoke($"{DateTime.Now:HH:mm:ss}  {message}");

    // ── 演示模式 (--demo N): 没有实体手柄时用 SDL 的虚拟摇杆模拟 N 个实体手柄, 用于检查界面和转发链路 ──

    void AttachDemoPads()
    {
        for (int i = 0; i < _demoPads; i++)
        {
            byte[] name = Encoding.UTF8.GetBytes(Loc.T("DemoPadName", i + 1) + "\0");
            fixed (byte* namePtr = name)
            {
                SDL_VirtualJoystickDesc desc = default;
                desc.version = (uint)sizeof(SDL_VirtualJoystickDesc);
                desc.type = (ushort)SDL_JoystickType.SDL_JOYSTICK_TYPE_GAMEPAD;
                desc.vendor_id = 0x1234;
                desc.product_id = (ushort)(0x0001 + i);
                desc.naxes = (ushort)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_COUNT;
                desc.nbuttons = 15;
                desc.name = namePtr;
                SDL_JoystickID id = SDL_AttachVirtualJoystick(&desc);
                if (id != 0) _demoIds.Add(id);
                else WriteLog(Loc.T("LogDemoFailed", SDL_GetError()));
            }
        }
    }

    /// <summary>第 1 个演示手柄: 每秒按一下 South 键, 左摇杆缓慢画圈; 其余演示手柄保持静止。</summary>
    void AnimateDemoPads()
    {
        SDL_Joystick* joystick = SDL_GetJoystickFromID(_demoIds[0]);
        if (joystick == null) return;
        double t = Environment.TickCount64 / 1000.0;
        SDL_SetJoystickVirtualButton(joystick, (int)SDL_GamepadButton.SDL_GAMEPAD_BUTTON_SOUTH, t % 2.0 < 1.0);
        SDL_SetJoystickVirtualAxis(joystick, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTX, (short)(Math.Cos(t) * 30000));
        SDL_SetJoystickVirtualAxis(joystick, (int)SDL_GamepadAxis.SDL_GAMEPAD_AXIS_LEFTY, (short)(Math.Sin(t) * 30000));
    }
}
