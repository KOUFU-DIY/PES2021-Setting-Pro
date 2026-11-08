using System.Runtime.InteropServices;

namespace PesPadHub;

/// <summary>
/// 直接用 DirectInput 按实例 GUID 读手柄状态, 只用来判断 "这个设备现在有没有人在按" (控制器页的指示灯)。
/// 认设备的方式和游戏一样, 所以同型号的几个手柄也分得清; 总开关关着时照样能用。只在界面线程上使用。
/// </summary>
sealed unsafe class DirectInputPoller : IDisposable
{
    [DllImport("dinput8.dll")]
    static extern int DirectInput8Create(IntPtr instance, uint version, in Guid riid, out IntPtr di, IntPtr outer);

    /// <summary>系统 dinput8.dll 导出的标准手柄数据格式 c_dfDIJoystick (对应 DIJOYSTATE)。</summary>
    [DllImport("dinput8.dll")]
    static extern IntPtr GetdfDIJoystick();

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    static extern IntPtr GetModuleHandleW(string? name);

    static readonly Guid IID_IDirectInput8W = new("BF798031-483A-4DA2-AA99-5D64ED369700");
    const uint DirectInputVersion = 0x0800;
    const uint DisclNonExclusive = 0x2, DisclBackground = 0x8;

    // DIJOYSTATE: 6 根轴 + 2 个滑块 (各 4 字节), 4 个十字键/苦力帽 (各 4 字节), 32 个按键 (各 1 字节), 共 80 字节
    const int StateSize = 80, AxisCount = 8, PovIndex = 8, PovCount = 4, ButtonOffset = 48, ButtonCount = 32;
    /// <summary>轴的默认量程是 0~65535; 离开起始位置这么多才算在推 (摇杆行程的三分之一左右)。</summary>
    const long AxisThreshold = 12000;

    // COM 虚表里的序号: IDirectInput8W 的 CreateDevice, IDirectInputDevice8W 的各个方法
    const int ReleaseSlot = 2, CreateDeviceSlot = 3, GetPropertySlot = 5, AcquireSlot = 7, UnacquireSlot = 8,
              GetDeviceStateSlot = 9, SetDataFormatSlot = 11, SetCooperativeLevelSlot = 13, PollSlot = 25;

    sealed class Device(IntPtr pointer, string path)
    {
        public IntPtr Pointer { get; } = pointer;
        public string Path { get; } = path;
        /// <summary>打开后第一次读到的轴和十字键数值, 当作 "没人碰" 的样子, 之后跟它比。</summary>
        public int[]? Baseline;
    }

    readonly IntPtr _window;
    readonly IntPtr _format;
    IntPtr _di;
    readonly Dictionary<Guid, Device> _devices = [];

    /// <param name="window">本程序的一个顶层窗口, DirectInput 设置协作方式时要用。</param>
    public DirectInputPoller(IntPtr window)
    {
        _window = window;
        try
        {
            _format = GetdfDIJoystick();
            if (_format == IntPtr.Zero || DirectInput8Create(GetModuleHandleW(null), DirectInputVersion, IID_IDirectInput8W, out _di, IntPtr.Zero) < 0)
                _di = IntPtr.Zero;
        }
        catch (Exception ex) when (ex is DllNotFoundException or EntryPointNotFoundException)
        {
            _di = IntPtr.Zero;      // 系统的 DirectInput 不全: 没有指示灯, 其余功能照常
        }
    }

    static IntPtr* VTable(IntPtr comObject) => *(IntPtr**)comObject;

    /// <summary>要读的设备换成这一批: 新出现的打开, 不再需要的关掉。</summary>
    public void SetDevices(IEnumerable<Guid> instances)
    {
        HashSet<Guid> wanted = instances.ToHashSet();
        foreach (Guid gone in _devices.Keys.Where(g => !wanted.Contains(g)).ToList())
        {
            Close(_devices[gone]);
            _devices.Remove(gone);
        }
        if (_di == IntPtr.Zero) return;
        foreach (Guid instance in wanted)
            if (!_devices.ContainsKey(instance) && Open(instance) is { } device)
                _devices[instance] = device;
    }

    /// <summary>设备的接口路径 (和 SDL 报的是同一种路径), 用来认出它是不是正接在某个序号上的那个手柄; 读不到为空。</summary>
    public string PathOf(Guid instance) => _devices.TryGetValue(instance, out Device? device) ? device.Path : "";

    /// <summary>把所有设备读一遍, 返回现在有人在按的: 有按键按着、十字键按着, 或者某根轴离开了起始位置。</summary>
    public HashSet<Guid> ActiveDevices()
    {
        var active = new HashSet<Guid>();
        int* state = stackalloc int[StateSize / sizeof(int)];
        foreach ((Guid instance, Device device) in _devices)
        {
            IntPtr* vt = VTable(device.Pointer);
            ((delegate* unmanaged[Stdcall]<IntPtr, int>)vt[PollSlot])(device.Pointer);     // 不需要轮询的设备会直接返回
            int hr = ((delegate* unmanaged[Stdcall]<IntPtr, uint, void*, int>)vt[GetDeviceStateSlot])(device.Pointer, StateSize, state);
            if (hr < 0)
            {
                // 还没获取到, 或者中途丢了 (比如被别的程序独占过): 重新获取, 下一轮再读
                ((delegate* unmanaged[Stdcall]<IntPtr, int>)vt[AcquireSlot])(device.Pointer);
                continue;
            }
            if (device.Baseline == null)
            {
                device.Baseline = new int[AxisCount + PovCount];
                for (int i = 0; i < AxisCount + PovCount; i++) device.Baseline[i] = state[i];
                continue;
            }
            if (Differs(state, device.Baseline)) active.Add(instance);
        }
        return active;
    }

    static bool Differs(int* state, int[] baseline)
    {
        for (int i = 0; i < AxisCount; i++)
            if (Math.Abs((long)state[i] - baseline[i]) > AxisThreshold) return true;
        // 十字键也跟起始值比: 设备没有这个部件时读到的是固定值, 不会误亮
        for (int i = PovIndex; i < PovIndex + PovCount; i++)
            if (state[i] != baseline[i]) return true;
        byte* buttons = (byte*)state + ButtonOffset;
        for (int i = 0; i < ButtonCount; i++)
            if ((buttons[i] & 0x80) != 0) return true;
        return false;
    }

    Device? Open(Guid instance)
    {
        IntPtr pointer;
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, Guid*, IntPtr*, IntPtr, int>)VTable(_di)[CreateDeviceSlot])(_di, &instance, &pointer, IntPtr.Zero);
        if (hr < 0 || pointer == IntPtr.Zero) return null;
        IntPtr* vt = VTable(pointer);
        hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, int>)vt[SetDataFormatSlot])(pointer, _format);
        if (hr >= 0)
            hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, uint, int>)vt[SetCooperativeLevelSlot])(pointer, _window, DisclBackground | DisclNonExclusive);
        if (hr < 0)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vt[ReleaseSlot])(pointer);
            return null;
        }
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)vt[AcquireSlot])(pointer);    // 失败的话读的时候再试
        return new Device(pointer, ReadPath(pointer));
    }

    /// <summary>读设备的接口路径 (DIPROP_GUIDANDPATH)。</summary>
    static string ReadPath(IntPtr pointer)
    {
        // DIPROPGUIDANDPATH: DIPROPHEADER (dwSize, dwHeaderSize, dwObj, dwHow 各 4 字节) + guidClass (16 字节) + wszPath[MAX_PATH]
        const int HeaderSize = 16, PathOffset = HeaderSize + 16, Size = PathOffset + 260 * sizeof(char);
        byte* buffer = stackalloc byte[Size];
        new Span<byte>(buffer, Size).Clear();
        ((uint*)buffer)[0] = Size;
        ((uint*)buffer)[1] = HeaderSize;        // dwObj = 0, dwHow = DIPH_DEVICE (0): 问的是整个设备
        IntPtr property = 12;                   // DIPROP_GUIDANDPATH = MAKEDIPROP(12), 这类属性的 "GUID" 其实是个小整数
        int hr = ((delegate* unmanaged[Stdcall]<IntPtr, IntPtr, void*, int>)VTable(pointer)[GetPropertySlot])(pointer, property, buffer);
        return hr < 0 ? "" : new string((char*)(buffer + PathOffset));
    }

    static void Close(Device device)
    {
        IntPtr* vt = VTable(device.Pointer);
        ((delegate* unmanaged[Stdcall]<IntPtr, int>)vt[UnacquireSlot])(device.Pointer);
        ((delegate* unmanaged[Stdcall]<IntPtr, uint>)vt[ReleaseSlot])(device.Pointer);
    }

    public void Dispose()
    {
        foreach (Device device in _devices.Values) Close(device);
        _devices.Clear();
        if (_di != IntPtr.Zero)
        {
            ((delegate* unmanaged[Stdcall]<IntPtr, uint>)VTable(_di)[ReleaseSlot])(_di);
            _di = IntPtr.Zero;
        }
    }
}
