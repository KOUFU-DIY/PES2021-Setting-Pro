using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace PesPadHub;

/// <summary>主窗口里一行槽位的显示状态。</summary>
sealed class SlotViewModel(int index) : INotifyPropertyChanged
{
    string _padText = "", _virtualName = "";
    bool _connected, _pinned, _active, _rumbling, _enabled = true, _ready;

    public int Index { get; } = index;
    public int Number => Index + 1;
    public string VirtualName { get => _virtualName; private set => Set(ref _virtualName, value); }
    public bool CanMoveUp => Index > 0 && HasContent;
    public bool CanMoveDown => Index < PadHub.SlotCount - 1 && HasContent;
    /// <summary>可以操作: 总开关开着, 槽位没被单独关闭, 且有已连接的手柄或为未连接手柄保留的固定设置。</summary>
    public bool HasContent => _ready && Enabled && (Connected || Pinned);
    public bool CanRumble => _ready && Enabled && Connected;
    /// <summary>总开关开着时才能单独开关槽位。</summary>
    public bool CanToggle => _ready;

    public string PadText { get => _padText; private set => Set(ref _padText, value); }
    public bool Connected { get => _connected; private set => Set(ref _connected, value); }
    public bool Pinned { get => _pinned; private set => Set(ref _pinned, value); }
    public bool Enabled { get => _enabled; private set => Set(ref _enabled, value); }
    public bool Active { get => _active; set => Set(ref _active, value); }
    public bool Rumbling { get => _rumbling; set => Set(ref _rumbling, value); }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void Apply(SlotSnapshot s, bool ready)
    {
        _ready = ready;
        VirtualName = s.VirtualName;
        Enabled = s.Enabled;
        Connected = s.Connected;
        Pinned = s.Pinned;
        PadText = s switch
        {
            { Enabled: false } => Loc.T("SlotDisabled"),
            { Connected: true } => $"{s.PadName}  [{s.PadId}]",
            { Pinned: true } => Loc.T("PinnedAbsent", s.PadName),
            _ => Loc.T("NotConnected"),
        };
        Raise(nameof(HasContent));
        Raise(nameof(CanMoveUp));
        Raise(nameof(CanMoveDown));
        Raise(nameof(CanRumble));
        Raise(nameof(CanToggle));
    }

    void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        Raise(name);
    }

    void Raise(string? name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>待分配区里的一个实体手柄。</summary>
sealed record WaitingViewModel(uint Id, string Text);
