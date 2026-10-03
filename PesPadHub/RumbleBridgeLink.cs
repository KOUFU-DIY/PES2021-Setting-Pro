using System.IO;
using System.IO.MemoryMappedFiles;
using System.Reflection;
using System.Text;

namespace PesPadHub;

/// <summary>
/// 和游戏进程里的震动桥 (RumbleBridge/dinput8.cpp) 之间的共享内存。
/// 桥把每个虚拟手柄当前的大/小马达强度写进来, 这边读出来转给实体手柄。布局必须和 dinput8.cpp 里的 SharedBlock 一致。
/// </summary>
sealed class RumbleBridgeLink : IDisposable
{
    const string MapName = @"Local\PesPadHub.RumbleBridge.1";
    const uint Magic = 0x52485050;
    const int HeartbeatOffset = 4, SlotsOffset = 8, SlotSize = 4;
    const int Size = SlotsOffset + SlotSize * PadHub.SlotCount;
    /// <summary>桥每 8 毫秒跳一次心跳; 这么久没跳就当游戏已经退出 (或卡死), 停掉全部震动。</summary>
    const long StaleAfterMs = 400;

    readonly MemoryMappedFile _file;
    readonly MemoryMappedViewAccessor _view;
    uint _lastHeartbeat;
    long _lastBeatTick = long.MinValue / 2;

    /// <summary>游戏进程里的桥当前在线。</summary>
    public bool Alive { get; private set; }

    public RumbleBridgeLink()
    {
        // 游戏可能先于本工具启动并一直开着这块内存, 所以是 "创建或打开"
        _file = MemoryMappedFile.CreateOrOpen(MapName, Size);
        _view = _file.CreateViewAccessor(0, Size);
        _view.Write(0, Magic);
        _lastHeartbeat = _view.ReadUInt32(HeartbeatOffset);
    }

    /// <summary>每轮循环调用一次, 返回在线状态是否发生了变化。</summary>
    public bool Poll(long now)
    {
        uint heartbeat = _view.ReadUInt32(HeartbeatOffset);
        if (heartbeat != _lastHeartbeat)
        {
            _lastHeartbeat = heartbeat;
            _lastBeatTick = now;
        }

        bool alive = now - _lastBeatTick < StaleAfterMs;
        if (alive == Alive) return false;
        Alive = alive;
        // 游戏退出时可能正震到一半, 留下的数值要清掉, 否则下次连上时没被游戏重新写过的手柄会一直震
        if (!alive)
            for (int i = 0; i < SlotSize * PadHub.SlotCount; i++)
                _view.Write(SlotsOffset + i, (byte)0);
        return true;
    }

    public (byte Large, byte Small) Read(int slot)
    {
        if (!Alive) return default;
        int offset = SlotsOffset + slot * SlotSize;
        return (_view.ReadByte(offset), _view.ReadByte(offset + 1));
    }

    public void Dispose()
    {
        _view.Dispose();
        _file.Dispose();
    }
}

/// <summary>把震动桥 (dinput8.dll) 安装到游戏目录 / 从游戏目录移除。DLL 本身内嵌在本程序里。</summary>
static class RumbleBridgeInstaller
{
    public const string FileName = "dinput8.dll";
    const string ResourceName = "PesPadHub.dinput8.dll";
    /// <summary>我们的 DLL 特有的导出函数名, 用来区分游戏目录里已有的 dinput8.dll 是不是别的插件。</summary>
    static readonly byte[] Marker = Encoding.ASCII.GetBytes("PesPadHubRumbleBridgeVersion");

    public static bool IsBundled => Assembly.GetExecutingAssembly().GetManifestResourceInfo(ResourceName) != null;

    public static bool IsOurs(string path) => File.ReadAllBytes(path).AsSpan().IndexOf(Marker) >= 0;

    public static void Install(string gameDirectory)
    {
        using Stream source = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName)
            ?? throw new FileNotFoundException(ResourceName);
        using FileStream target = File.Create(Path.Combine(gameDirectory, FileName));
        source.CopyTo(target);
    }

    public static void Remove(string gameDirectory) => File.Delete(Path.Combine(gameDirectory, FileName));
}
