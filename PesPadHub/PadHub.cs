using System.Collections.Concurrent;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using Nefarius.ViGEm.Client;
using SDL;
using static SDL.SDL3;

namespace PesPadHub;

/// <param name="VirtualName">虚拟手柄的设备名, 和游戏/系统里看到的名称一致。</param>
/// <param name="Enabled">这个槽位没有被单独关闭。</param>
/// <param name="RumbleOn">这个槽位的震动开着 (游戏让它震动时实体手柄跟着震)。</param>
/// <param name="PadPath">接在这个槽位上的实体手柄的设备路径 (SDL 报的), 控制器页用它和 DirectInput 设备对上号。</param>
record SlotSnapshot(string VirtualName, string? PadName, string? PadId, bool Connected, bool Pinned, bool Enabled, bool RumbleOn, string? PadPath);

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
    volatile int _triggerRumble;

    readonly VirtualPad?[] _slots = new VirtualPad?[SlotCount];
    readonly PhysicalPad?[] _mapped = new PhysicalPad?[SlotCount];
    /// <summary>全部已打开的实体手柄, 包括没有槽位可放、在 "待分配" 里等着的。</summary>
    readonly Dictionary<SDL_JoystickID, PhysicalPad> _pads = [];
    readonly Dictionary<string, int> _sessionAffinity = [];
    readonly List<SDL_JoystickID> _demoIds = [];
    readonly bool[] _driverRumble = new bool[SlotCount];
    /// <summary>单独关掉了震动的槽位 (配置里 RumbleOffSlots 的副本; 转发循环每毫秒都要查, 所以用数组)。</summary>
    readonly bool[] _rumbleOff = new bool[SlotCount];
    ViGEmClient? _client;
    RumbleBridgeLink? _bridge;
    bool _ready;
    (string Key, object?[] Args)? _fatal;
    long _neutralizeAt = long.MaxValue;
    volatile HubSnapshot _snapshot;
    int _activityMask, _rumbleMask;

    /// <summary>映射或开关状态变化时触发 (在后台线程上)。</summary>
    public event Action? Changed;
    /// <summary>事件日志 (在后台线程上)。</summary>
    public event Action<string>? Log;

    public HubSnapshot Snapshot => _snapshot;
    /// <summary>第 N 位为 1 表示第 N 个槽位的实体手柄当前有按键/摇杆输入。</summary>
    public int ActivityMask => Volatile.Read(ref _activityMask);
    /// <summary>第 N 位为 1 表示游戏正在让第 N 个槽位的手柄震动。</summary>
    public int RumbleMask => Volatile.Read(ref _rumbleMask);

    public PadHub(AppConfig config, int demoPads = 0)
    {
        _config = config;
        _demoPads = demoPads;
        foreach (int slot in config.RumbleOffSlots)
            if (slot >= 0 && slot < SlotCount) _rumbleOff[slot] = true;
        _triggerRumble = NearestTriggerRumbleLevel(config.TriggerRumblePercent);
        PhysicalPad.TriggerRumblePercent = TriggerRumbleLevels[_triggerRumble];
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
    public void SetSlotRumble(int slot, bool on) => _commands.Enqueue(() => DoSetSlotRumble(slot, on));
    public void SwapSlots(int a, int b) => _commands.Enqueue(() => DoSwapSlots(a, b));
    public void AssignPad(uint padId, int slot) => _commands.Enqueue(() => DoAssignPad((SDL_JoystickID)padId, slot));
    public void TogglePin(int slot) => _commands.Enqueue(() => DoTogglePin(slot));
    public void Identify(int slot) => _commands.Enqueue(() =>
    {
        if (_mapped[slot] is { } pad) DoIdentify(pad);
    });
    public void IdentifyPad(uint padId) => _commands.Enqueue(() =>
    {
        if (_pads.TryGetValue((SDL_JoystickID)padId, out PhysicalPad? pad)) DoIdentify(pad);
    });

    /// <summary>让一个实体手柄震一下。开着扳机震动时顺便在日志里说明这个手柄支不支持, 方便用户试。</summary>
    void DoIdentify(PhysicalPad pad)
    {
        pad.Rumble(200, 200, 500);
        if (PhysicalPad.TriggerRumblePercent > 0)
            WriteLog(Loc.T(pad.TriggerRumbleWorked ? "LogTriggerRumbleOk" : "LogTriggerRumbleNo", pad.Name));
    }

    /// <summary>上次安装震动插件时选的游戏文件夹 (配置归后台线程管, 这里只是记一下)。</summary>
    public string? GameDirectory => _config.GameDirectory;

    public void SetGameDirectory(string directory) => _commands.Enqueue(() =>
    {
        _config.GameDirectory = directory;
        _config.Save();
    });

    /// <summary>
    /// 扳机震动各档位对应的强度百分比 (下标 = 界面上的档位: 关闭 / 极弱 / 弱 / 中 / 强)。
    /// 配置里存的是百分比本身, 以后增减档位不会让已存的设置变成别的档。
    /// </summary>
    static readonly int[] TriggerRumbleLevels = [0, 20, 35, 65, 100];

    /// <summary>当前档位 (TriggerRumbleLevels 的下标)。</summary>
    public int TriggerRumble => _triggerRumble;

    public void SetTriggerRumble(int level)
    {
        level = Math.Clamp(level, 0, TriggerRumbleLevels.Length - 1);
        int percent = TriggerRumbleLevels[level];
        _triggerRumble = level;
        PhysicalPad.TriggerRumblePercent = percent;
        _commands.Enqueue(() =>
        {
            _config.TriggerRumblePercent = percent;
            _config.Save();
        });
    }

    /// <summary>离给定百分比最近的档位。</summary>
    static int NearestTriggerRumbleLevel(int percent)
    {
        int best = 0;
        for (int i = 1; i < TriggerRumbleLevels.Length; i++)
            if (Math.Abs(TriggerRumbleLevels[i] - percent) < Math.Abs(TriggerRumbleLevels[best] - percent)) best = i;
        return best;
    }

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

    /// <summary>
    /// 单独开关一个槽位的震动: 关掉后游戏让这个虚拟手柄震动时, 实体手柄 (连同扳机) 不跟着震。
    /// 设置跟着序号走, 不跟着手柄走; "震动" 辨认按钮不受影响。
    /// </summary>
    void DoSetSlotRumble(int slot, bool on)
    {
        if (slot < 0 || slot >= SlotCount || _rumbleOff[slot] == !on) return;
        _rumbleOff[slot] = !on;
        _config.RumbleOffSlots.Remove(slot);
        if (!on) _config.RumbleOffSlots.Add(slot);
        _config.Save();
        if (!on && _mapped[slot] is { } pad)
        {
            pad.Rumble(0, 0);       // 正在震的马上停下
            pad.BridgeRumble = 0;
        }
        WriteLog(Loc.T(on ? "LogRumbleOn" : "LogRumbleOff", slot + 1));
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
        // 设备细节只写进日志文件: 排查 "某台电脑上手柄对不上号" 时要看系统给的是哪种路径、有没有序列号
        WriteLogFile($"  设备：{pad.Name} [{pad.Vid:X4}:{pad.Pid:X4}] 序列号：{(pad.HasSerial ? "有" : "无")} 路径：{pad.Path}");
        if (_pads.Values.Any(p => p != pad && p.AffinityKey == pad.AffinityKey))
            WriteLogFile($"  注意：{pad.Name} 和另一个已接入的手柄分不出来 (同型号, 系统没给序列号也没给不同的路径)");

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
        // 固定的序号上已经接着一个和它分不出来的手柄 (同型号、系统不给序列号或路径) 时不去抢, 免得两个手柄来回换位
        if (pin != null && SlotEnabled(pin.Slot) && _mapped[pin.Slot]?.AffinityKey != pad.AffinityKey)
        {
            if (_mapped[pin.Slot] is { } occupant)
            {
                Detach(pin.Slot);
                // 给让位的手柄另找空位时不能算上刚腾出来的这个序号, 否则两个手柄会叠在同一个序号上
                int other = FreeSlot(except: pin.Slot);
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

    /// <summary>序号最小的空槽 (except 这个序号除外); 尽量不占用为 "已固定但暂未连接" 的手柄预留的槽位。</summary>
    int FreeSlot(int except = -1)
    {
        for (int i = 0; i < SlotCount; i++)
            if (i != except && SlotEnabled(i) && _mapped[i] == null && PinAt(i) == null)
                return i;
        for (int i = 0; i < SlotCount; i++)
            if (i != except && SlotEnabled(i) && _mapped[i] == null)
                return i;
        return -1;
    }

    PinEntry? PinAt(int slot) => _config.Pins.Find(p => p.Slot == slot);

    IEnumerable<PhysicalPad> WaitingPads => _pads.Values.Where(p => p.Slot < 0);

    void Attach(PhysicalPad pad, int slot)
    {
        // 一个序号只接一个手柄, 一个手柄只占一个序号: 就算调用方没先腾好位置, 也不会叠在一起
        if (pad.Slot >= 0 && pad.Slot != slot) Release(pad);
        if (_mapped[slot] is { } occupant && occupant != pad)
        {
            occupant.Slot = -1;
            WriteLog(Loc.T("LogWaiting", occupant.Name));
        }
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

    /// <summary>让手柄离开它所在的序号; 只有那个序号上接的确实是它时才清空, 不会误伤别的手柄。</summary>
    void Release(PhysicalPad pad)
    {
        if (pad.Slot >= 0 && _mapped[pad.Slot] == pad) Detach(pad.Slot);
        pad.Slot = -1;
    }

    void MoveToWaiting(PhysicalPad pad)
    {
        Release(pad);
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
            Release(pad);
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

            // 震动有两个来源: 驱动回调 (游戏走 XInput 时) 和游戏进程里的震动插件 (游戏走 DirectInput 时)。
            // 这个槽位关了震动时, 驱动回调照样取走 (不然会积着), 只是不转给实体手柄, 指示灯也不亮
            bool rumbleOn = !_rumbleOff[i];
            bool hasRumble = slot.TryTakeRumble(out byte large, out byte small) && rumbleOn;
            if (hasRumble) _driverRumble[i] = large != 0 || small != 0;
            else if (!rumbleOn) _driverRumble[i] = false;
            (byte bridgeLarge, byte bridgeSmall) = rumbleOn && _bridge != null ? _bridge.Read(i) : default;
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
            if (IsActive(state)) activity |= 1 << i;
        }

        Volatile.Write(ref _activityMask, activity);
        Volatile.Write(ref _rumbleMask, rumbling);
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
                SlotEnabled(i),
                !_rumbleOff[i],
                pad?.Path);
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

    // 界面上的日志同时追加到 %APPDATA%\PesPadHub\hub.log, 别的电脑上出问题时可以把这个文件发过来
    const string LogFileName = "hub.log";
    const long MaxLogFileBytes = 1024 * 1024;
    readonly object _logFileLock = new();
    StreamWriter? _logFile;
    bool _logFileFailed;

    void WriteLog(string message)
    {
        string line = $"{DateTime.Now:HH:mm:ss}  {message}";
        AppendLogFile(line);
        Log?.Invoke(line);
    }

    /// <summary>只写进日志文件、界面上不显示的细节。</summary>
    void WriteLogFile(string message) => AppendLogFile($"{DateTime.Now:HH:mm:ss}  {message}");

    void AppendLogFile(string line)
    {
        lock (_logFileLock)
        {
            if (_logFileFailed) return;
            try
            {
                if (_logFile == null)
                {
                    Directory.CreateDirectory(AppConfig.DataDirectory);
                    string path = Path.Combine(AppConfig.DataDirectory, LogFileName);
                    bool tooBig = File.Exists(path) && new FileInfo(path).Length > MaxLogFileBytes;
                    _logFile = new StreamWriter(path, append: !tooBig) { AutoFlush = true };
                    _logFile.WriteLine();
                    _logFile.WriteLine($"======== {DateTime.Now:yyyy-MM-dd HH:mm:ss}  PES 2021 Setting Pro v{UpdateCheck.CurrentText} ========");
                }
                _logFile.WriteLine(line);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                _logFileFailed = true;     // 写不了就算了, 不影响手柄转发
            }
        }
    }

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
