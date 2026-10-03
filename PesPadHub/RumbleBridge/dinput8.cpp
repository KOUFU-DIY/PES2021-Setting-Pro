// PesPadHub 震动桥: 放进游戏目录的 dinput8.dll 转接层。
//
// 游戏在 DirectInput 模式下只对 "支持力反馈" 的手柄发震动, 而 PesPadHub 创建的虚拟手柄 (ViGEm) 在
// DirectInput 里不带力反馈。本 DLL 在游戏进程内包住 DirectInput 接口:
//   1. 对 PesPadHub 的虚拟手柄 (VID 0x5045) 声明 "支持力反馈", 并接住游戏创建/播放的力反馈效果;
//   2. 把效果换算成大/小两个马达的强度, 写进共享内存;
//   3. PesPadHub 读共享内存, 让对应的实体手柄震动。
// 其他设备和其他调用一律原样转给系统的 dinput8.dll。删掉本文件即完全恢复原状。

#define WIN32_LEAN_AND_MEAN
#define NOMINMAX
#define DIRECTINPUT_VERSION 0x0800
#include <windows.h>
#include <initguid.h>
#include <dinput.h>
#include <intrin.h>

#include <algorithm>
#include <cmath>
#include <cstdarg>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <string>
#include <vector>

namespace {

const WORD kVendorId = 0x5045;          // PesPadHub 虚拟手柄的 VID, 见 VirtualPads.cs
const int kSlots = 8;
const DWORD kSharedMagic = 0x52485050;  // "PPHR"
const wchar_t kSharedName[] = L"Local\\PesPadHub.RumbleBridge.1";
const double kPi = 3.14159265358979323846;

#pragma pack(push, 1)
struct SharedSlot { BYTE largeMotor, smallMotor, reserved[2]; };
struct SharedBlock {
    DWORD magic;
    DWORD heartbeat;            // 本 DLL 的工作线程每一拍加一; PesPadHub 发现它不动了就停掉全部震动
    SharedSlot slots[kSlots];
};
#pragma pack(pop)

class Device;

std::recursive_mutex g_mutex;           // 保护下面的全部状态以及所有 Device/Effect 对象
std::vector<Device*> g_devices;
SharedBlock* g_shared = nullptr;
bool g_workerStarted = false;

// ───────────────────────── 日志 ─────────────────────────

int g_logLines = 0;
const int kMaxLogLines = 4000;          // 游戏每帧都可能调整效果, 写满就不再记

void Log(const char* format, ...)
{
    static FILE* file = nullptr;
    static bool opened = false;
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    if (!opened) {
        opened = true;
        // 游戏和它的设置程序都会加载本 DLL, 各写各的文件 (按 exe 名区分), 免得互相覆盖
        wchar_t exe[MAX_PATH] = L"";
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        wchar_t* name = wcsrchr(exe, L'\\');
        name = name ? name + 1 : exe;
        if (wchar_t* dot = wcsrchr(name, L'.')) *dot = 0;

        wchar_t path[MAX_PATH];
        DWORD n = GetEnvironmentVariableW(L"APPDATA", path, MAX_PATH);
        if (n > 0 && n + wcslen(name) < MAX_PATH - 40) {
            wcscat_s(path, L"\\PesPadHub");
            CreateDirectoryW(path, nullptr);
            wcscat_s(path, L"\\rumble_bridge_");
            wcscat_s(path, name);
            wcscat_s(path, L".log");
            // 追加写: 多次启动游戏的记录都留着 (上一版每次启动都清空, 结果把要看的那一局冲掉了); 太大了才清空重来
            WIN32_FILE_ATTRIBUTE_DATA attributes;
            bool tooBig = GetFileAttributesExW(path, GetFileExInfoStandard, &attributes) && attributes.nFileSizeLow > 1024 * 1024;
            file = _wfsopen(path, tooBig ? L"w" : L"a", _SH_DENYWR);
            if (file) {
                SYSTEMTIME t;
                GetLocalTime(&t);
                fprintf(file, "\n======== session %04d-%02d-%02d %02d:%02d:%02d ========\n", t.wYear, t.wMonth, t.wDay, t.wHour, t.wMinute, t.wSecond);
            }
        }
    }
    if (!file || g_logLines >= kMaxLogLines) return;
    SYSTEMTIME t;
    GetLocalTime(&t);
    fprintf(file, "%02d:%02d:%02d.%03d  ", t.wHour, t.wMinute, t.wSecond, t.wMilliseconds);
    va_list args;
    va_start(args, format);
    vfprintf(file, format, args);
    va_end(args);
    fputc('\n', file);
    if (++g_logLines == kMaxLogLines) fputs("(log limit reached)\n", file);
    fflush(file);
}

// ───────────────────────── 系统的 dinput8.dll ─────────────────────────

typedef HRESULT (WINAPI *DirectInput8CreateFn)(HINSTANCE, DWORD, REFIID, LPVOID*, LPUNKNOWN);
typedef HRESULT (WINAPI *DllGetClassObjectFn)(REFCLSID, REFIID, LPVOID*);
typedef LPCDIDATAFORMAT (WINAPI *GetdfDIJoystickFn)();

DirectInput8CreateFn g_realCreate = nullptr;
DllGetClassObjectFn g_realGetClassObject = nullptr;
GetdfDIJoystickFn g_realGetdf = nullptr;

void LoadReal()
{
    static std::once_flag once;
    std::call_once(once, [] {
        wchar_t path[MAX_PATH];
        GetSystemDirectoryW(path, MAX_PATH);
        wcscat_s(path, L"\\dinput8.dll");
        HMODULE real = LoadLibraryW(path);
        if (!real) {
            Log("failed to load system dinput8.dll, error %lu", GetLastError());
            return;
        }
        g_realCreate = (DirectInput8CreateFn)GetProcAddress(real, "DirectInput8Create");
        g_realGetClassObject = (DllGetClassObjectFn)GetProcAddress(real, "DllGetClassObject");
        g_realGetdf = (GetdfDIJoystickFn)GetProcAddress(real, "GetdfDIJoystick");

        wchar_t exe[MAX_PATH] = L"";
        GetModuleFileNameW(nullptr, exe, MAX_PATH);
        Log("PesPadHub rumble bridge loaded in %ls", exe);
    });
}

// ───────────────────────── 共享内存 + 工作线程 ─────────────────────────

void TryOpenShared()
{
    HANDLE mapping = OpenFileMappingW(FILE_MAP_READ | FILE_MAP_WRITE, FALSE, kSharedName);
    if (!mapping) return;       // PesPadHub 还没启动, 稍后再试
    auto* block = (SharedBlock*)MapViewOfFile(mapping, FILE_MAP_READ | FILE_MAP_WRITE, 0, 0, sizeof(SharedBlock));
    CloseHandle(mapping);       // 视图会保持映射有效
    if (!block) return;
    if (block->magic != kSharedMagic) {
        UnmapViewOfFile(block);
        return;
    }
    g_shared = block;
    Log("connected to PesPadHub");
}

DWORD WINAPI Worker(LPVOID);

/// 重新计算每个手柄当前的马达强度并写进共享内存。调用方持有 g_mutex。
/// 除了工作线程定时调用, 游戏每次改动效果后也立刻调用一次, 不让震动比游戏的指令慢半拍。
void PublishOutputs();

void EnsureWorker()
{
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    if (g_workerStarted) return;
    g_workerStarted = true;
    // 工作线程会一直跑到进程结束, 所以把本 DLL 钉在进程里, 防止被提前卸载
    HMODULE self;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_PIN | GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS, (LPCWSTR)&Worker, &self);
    HANDLE thread = CreateThread(nullptr, 0, Worker, nullptr, 0, nullptr);
    if (thread) CloseHandle(thread);
}

// ───────────────────────── 力反馈效果 ─────────────────────────

enum class EffectKind { Constant, Ramp, Periodic, Other };

const struct EffectType { const GUID* guid; EffectKind kind; DWORD type; const char* name; bool advertised; } kEffectTypes[] = {
    { &GUID_ConstantForce, EffectKind::Constant, DIEFT_CONSTANTFORCE, "Constant Force", true },
    { &GUID_RampForce,     EffectKind::Ramp,     DIEFT_RAMPFORCE,     "Ramp Force",     true },
    { &GUID_Square,        EffectKind::Periodic, DIEFT_PERIODIC,      "Square Wave",    true },
    { &GUID_Sine,          EffectKind::Periodic, DIEFT_PERIODIC,      "Sine Wave",      true },
    { &GUID_Triangle,      EffectKind::Periodic, DIEFT_PERIODIC,      "Triangle Wave",  true },
    { &GUID_SawtoothUp,    EffectKind::Periodic, DIEFT_PERIODIC,      "Sawtooth Up",    true },
    { &GUID_SawtoothDown,  EffectKind::Periodic, DIEFT_PERIODIC,      "Sawtooth Down",  true },
    // 弹簧/阻尼这类 "条件" 效果震动马达表现不了: 允许创建 (免得游戏报错), 但不输出也不对外声明
    { &GUID_Spring,        EffectKind::Other,    DIEFT_CONDITION,     "Spring",         false },
    { &GUID_Damper,        EffectKind::Other,    DIEFT_CONDITION,     "Damper",         false },
    { &GUID_Inertia,       EffectKind::Other,    DIEFT_CONDITION,     "Inertia",        false },
    { &GUID_Friction,      EffectKind::Other,    DIEFT_CONDITION,     "Friction",       false },
    { &GUID_CustomForce,   EffectKind::Other,    DIEFT_CUSTOMFORCE,   "Custom Force",   false },
};

const EffectType* FindEffectType(REFGUID guid)
{
    for (const EffectType& t : kEffectTypes)
        if (IsEqualGUID(*t.guid, guid)) return &t;
    return nullptr;
}

const DWORD kEffectParams = DIEP_DURATION | DIEP_SAMPLEPERIOD | DIEP_GAIN | DIEP_TRIGGERBUTTON | DIEP_TRIGGERREPEATINTERVAL
                          | DIEP_AXES | DIEP_DIRECTION | DIEP_ENVELOPE | DIEP_TYPESPECIFICPARAMS | DIEP_STARTDELAY;

class Effect final : public IDirectInputEffect {
public:
    Effect(Device* device, const EffectType* type);

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override
    {
        if (!out) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, IID_IDirectInputEffect)) {
            *out = this;
            AddRef();
            return S_OK;
        }
        *out = nullptr;
        return E_NOINTERFACE;
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override;

    STDMETHODIMP Initialize(HINSTANCE, DWORD, REFGUID) override { return DI_OK; }
    STDMETHODIMP GetEffectGuid(LPGUID guid) override
    {
        if (!guid) return E_POINTER;
        *guid = *m_type->guid;
        return DI_OK;
    }
    STDMETHODIMP GetParameters(LPDIEFFECT effect, DWORD flags) override;
    STDMETHODIMP SetParameters(LPCDIEFFECT effect, DWORD flags) override;
    STDMETHODIMP Start(DWORD iterations, DWORD flags) override;
    STDMETHODIMP Stop() override;
    STDMETHODIMP GetEffectStatus(LPDWORD status) override;
    STDMETHODIMP Download() override { return DI_OK; }
    STDMETHODIMP Unload() override { return Stop(); }
    STDMETHODIMP Escape(LPDIEFFESCAPE) override { return DIERR_UNSUPPORTED; }

    // 以下只在持有 g_mutex 时调用
    void ForceStop() { m_playing = false; }
    bool Playing(ULONGLONG now);
    void AddOutput(ULONGLONG now, double motors[2]);

private:
    ~Effect() = default;

    LONG m_ref = 1;
    Device* m_device;
    const EffectType* m_type;

    DWORD m_duration = INFINITE;        // 微秒
    DWORD m_gain = DI_FFNOMINALMAX;
    DWORD m_startDelay = 0;             // 微秒
    DWORD m_samplePeriod = 0;
    DWORD m_trigger = DIEB_NOTRIGGER;
    DWORD m_triggerRepeat = 0;
    DWORD m_coordinates = DIEFF_CARTESIAN;
    DWORD m_axisMode = DIEFF_OBJECTOFFSETS;
    std::vector<DWORD> m_axes;
    std::vector<int> m_axisMotor;       // 每根轴驱动哪个马达: 0 = 大马达, 1 = 小马达, -1 = 认不出
    std::vector<LONG> m_direction;
    std::vector<BYTE> m_typeParams;
    bool m_hasEnvelope = false;
    DIENVELOPE m_envelope = {};

    bool m_playing = false;
    ULONGLONG m_startTick = 0;
    DWORD m_iterations = 1;
    std::string m_lastLogged;
};

// ───────────────────────── 设备包装 ─────────────────────────

class Device final : public IDirectInputDevice8W {
public:
    Device(IDirectInputDevice8W* real, bool unicode, int slot)
        : m_real(real), m_unicode(unicode), m_slot(slot)
    {
        {
            std::lock_guard<std::recursive_mutex> lock(g_mutex);
            g_devices.push_back(this);
        }
        EnsureWorker();
    }

    int Slot() const { return m_slot; }

    // IUnknown
    STDMETHODIMP QueryInterface(REFIID riid, void** out) override
    {
        if (!out) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, m_unicode ? IID_IDirectInputDevice8W : IID_IDirectInputDevice8A)) {
            *out = this;
            AddRef();
            return S_OK;
        }
        return m_real->QueryInterface(riid, out);
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG ref = InterlockedDecrement(&m_ref);
        if (ref == 0) {
            {
                std::lock_guard<std::recursive_mutex> lock(g_mutex);
                g_devices.erase(std::remove(g_devices.begin(), g_devices.end(), this), g_devices.end());
            }
            Log("slot %d: device released", m_slot + 1);
            m_real->Release();
            delete this;
        }
        return ref;
    }

    // ── 需要改写的方法 ──

    STDMETHODIMP GetCapabilities(LPDIDEVCAPS caps) override
    {
        HRESULT hr = m_real->GetCapabilities(caps);
        if (SUCCEEDED(hr) && caps) {
            caps->dwFlags |= DIDC_FORCEFEEDBACK | DIDC_FFATTACK | DIDC_FFFADE | DIDC_STARTDELAY;
            if (caps->dwSize >= sizeof(DIDEVCAPS)) {
                caps->dwFFSamplePeriod = 1000;
                caps->dwFFMinTimeResolution = 1000;
                caps->dwFFDriverVersion = 1;
            }
        }
        return hr;
    }

    STDMETHODIMP EnumObjects(LPDIENUMDEVICEOBJECTSCALLBACKW callback, LPVOID ref, DWORD flags) override
    {
        if (!callback) return E_POINTER;
        // 真实设备没有力反馈对象, 带着这两个筛选位去问只会得到空结果
        const DWORD ffBits = DIDFT_FFACTUATOR | DIDFT_FFEFFECTTRIGGER;
        if ((flags & DIDFT_FFEFFECTTRIGGER) && !(flags & DIDFT_FFACTUATOR)) return DI_OK;
        EnumObjectsContext context = { this, callback, ref, (flags & DIDFT_FFACTUATOR) != 0 };
        return m_real->EnumObjects(EnumObjectsThunk, &context, flags & ~ffBits);
    }

    STDMETHODIMP GetProperty(REFGUID prop, LPDIPROPHEADER header) override
    {
        ULONG_PTR id = (ULONG_PTR)&prop;    // DIPROP_xxx 不是真的 GUID, 而是伪装成指针的小整数
        if (id == (ULONG_PTR)&DIPROP_AUTOCENTER || id == (ULONG_PTR)&DIPROP_FFGAIN || id == (ULONG_PTR)&DIPROP_FFLOAD) {
            if (!header || header->dwSize < sizeof(DIPROPDWORD)) return DIERR_INVALIDPARAM;
            DWORD& value = ((LPDIPROPDWORD)header)->dwData;
            if (id == (ULONG_PTR)&DIPROP_AUTOCENTER) value = DIPROPAUTOCENTER_OFF;
            else if (id == (ULONG_PTR)&DIPROP_FFGAIN) value = m_gain;
            else value = 0;
            return DI_OK;
        }
        if (id > 0xFFFF || !header || header->dwHow != DIPH_BYID || header->dwSize > sizeof(m_propBuffer))
            return m_real->GetProperty(prop, header);
        // 按对象 ID 查询时, 去掉我们在 EnumObjects 里加上的力反馈标志位, 真实设备不认识它
        std::lock_guard<std::recursive_mutex> lock(g_mutex);
        memcpy(m_propBuffer, header, header->dwSize);
        auto* copy = (LPDIPROPHEADER)m_propBuffer;
        copy->dwObj &= ~(DWORD)(DIDFT_FFACTUATOR | DIDFT_FFEFFECTTRIGGER);
        HRESULT hr = m_real->GetProperty(prop, copy);
        DWORD obj = header->dwObj;
        memcpy(header, m_propBuffer, header->dwSize);
        header->dwObj = obj;
        return hr;
    }

    STDMETHODIMP SetProperty(REFGUID prop, LPCDIPROPHEADER header) override
    {
        ULONG_PTR id = (ULONG_PTR)&prop;
        if (id == (ULONG_PTR)&DIPROP_AUTOCENTER) return DI_OK;
        if (id == (ULONG_PTR)&DIPROP_FFGAIN) {
            if (!header || header->dwSize < sizeof(DIPROPDWORD)) return DIERR_INVALIDPARAM;
            std::lock_guard<std::recursive_mutex> lock(g_mutex);
            m_gain = std::min<DWORD>(((LPCDIPROPDWORD)header)->dwData, DI_FFNOMINALMAX);
            Log("slot %d: device gain = %lu", m_slot + 1, m_gain);
            return DI_OK;
        }
        if (id > 0xFFFF || !header || header->dwHow != DIPH_BYID || header->dwSize > sizeof(m_propBuffer))
            return m_real->SetProperty(prop, header);
        std::lock_guard<std::recursive_mutex> lock(g_mutex);
        memcpy(m_propBuffer, header, header->dwSize);
        auto* copy = (LPDIPROPHEADER)m_propBuffer;
        copy->dwObj &= ~(DWORD)(DIDFT_FFACTUATOR | DIDFT_FFEFFECTTRIGGER);
        return m_real->SetProperty(prop, copy);
    }

    STDMETHODIMP Unacquire() override
    {
        StopAllEffects();   // 游戏失去焦点/放开设备时不能让手柄一直震
        return m_real->Unacquire();
    }

    STDMETHODIMP SetDataFormat(LPCDIDATAFORMAT format) override
    {
        if (!format || !format->rgodf || format->dwNumObjs == 0) return m_real->SetDataFormat(format);
        // 游戏如果照着 EnumObjects 的结果拼数据格式, 会把力反馈标志位带进来;
        // 对真实设备来说那是 "必须是力反馈执行器" 的硬性条件, 会导致轴匹配不上, 所以去掉
        std::vector<DIOBJECTDATAFORMAT> objects(format->rgodf, format->rgodf + format->dwNumObjs);
        for (DIOBJECTDATAFORMAT& o : objects) o.dwType &= ~(DWORD)(DIDFT_FFACTUATOR | DIDFT_FFEFFECTTRIGGER);
        DIDATAFORMAT copy = *format;
        copy.rgodf = objects.data();
        return m_real->SetDataFormat(&copy);
    }

    STDMETHODIMP GetObjectInfo(LPDIDEVICEOBJECTINSTANCEW info, DWORD obj, DWORD how) override
    {
        if (how == DIPH_BYID) obj &= ~(DWORD)(DIDFT_FFACTUATOR | DIDFT_FFEFFECTTRIGGER);
        HRESULT hr = m_real->GetObjectInfo(info, obj, how);
        if (SUCCEEDED(hr) && info && IsForceAxis(info->guidType)) PatchObject(info);
        return hr;
    }

    STDMETHODIMP CreateEffect(REFGUID guid, LPCDIEFFECT params, LPDIRECTINPUTEFFECT* out, LPUNKNOWN outer) override
    {
        if (!out) return E_POINTER;
        *out = nullptr;
        if (outer) return CLASS_E_NOAGGREGATION;
        const EffectType* type = FindEffectType(guid);
        if (!type) {
            Log("slot %d: CreateEffect with unknown GUID {%08lX-...} refused", m_slot + 1, guid.Data1);
            return DIERR_DEVICENOTREG;
        }
        auto* effect = new Effect(this, type);
        Log("slot %d: CreateEffect %s -> %p", m_slot + 1, type->name, effect);
        if (params) {
            HRESULT hr = effect->SetParameters(params, kEffectParams & ~(params->dwSize >= sizeof(DIEFFECT) ? 0 : DIEP_STARTDELAY));
            if (FAILED(hr)) {
                effect->Release();
                return hr;
            }
        }
        *out = effect;
        return DI_OK;
    }

    STDMETHODIMP EnumEffects(LPDIENUMEFFECTSCALLBACKW callback, LPVOID ref, DWORD effType) override
    {
        if (!callback) return E_POINTER;
        for (const EffectType& type : kEffectTypes) {
            if (!type.advertised) continue;
            if (DIEFT_GETTYPE(effType) != DIEFT_ALL && DIEFT_GETTYPE(effType) != type.type) continue;
            BYTE buffer[sizeof(DIEFFECTINFOW)];
            FillEffectInfo(buffer, type);
            if (callback((LPCDIEFFECTINFOW)buffer, ref) == DIENUM_STOP) break;
        }
        return DI_OK;
    }

    STDMETHODIMP GetEffectInfo(LPDIEFFECTINFOW info, REFGUID guid) override
    {
        if (!info) return E_POINTER;
        const EffectType* type = FindEffectType(guid);
        if (!type || !type->advertised) return DIERR_DEVICENOTREG;
        if (info->dwSize != (m_unicode ? sizeof(DIEFFECTINFOW) : sizeof(DIEFFECTINFOA))) return DIERR_INVALIDPARAM;
        FillEffectInfo(info, *type);
        return DI_OK;
    }

    STDMETHODIMP GetForceFeedbackState(LPDWORD state) override
    {
        if (!state) return E_POINTER;
        std::lock_guard<std::recursive_mutex> lock(g_mutex);
        ULONGLONG now = GetTickCount64();
        bool playing = false;
        for (Effect* e : m_effects) playing = playing || e->Playing(now);
        *state = DIGFFS_POWERON
               | (m_actuatorsOn ? DIGFFS_ACTUATORSON : DIGFFS_ACTUATORSOFF)
               | (m_effects.empty() ? DIGFFS_EMPTY : 0)
               | (m_paused ? DIGFFS_PAUSED : 0)
               | (playing ? 0 : DIGFFS_STOPPED);
        return DI_OK;
    }

    STDMETHODIMP SendForceFeedbackCommand(DWORD command) override
    {
        std::lock_guard<std::recursive_mutex> lock(g_mutex);
        Log("slot %d: SendForceFeedbackCommand 0x%lX", m_slot + 1, command);
        switch (command) {
        case DISFFC_RESET:
            StopAllEffects();
            m_paused = false;
            m_actuatorsOn = true;
            break;
        case DISFFC_STOPALL: StopAllEffects(); break;
        case DISFFC_PAUSE: m_paused = true; break;
        case DISFFC_CONTINUE: m_paused = false; break;
        case DISFFC_SETACTUATORSON: m_actuatorsOn = true; break;
        case DISFFC_SETACTUATORSOFF: m_actuatorsOn = false; break;
        default: return DIERR_INVALIDPARAM;
        }
        return DI_OK;
    }

    STDMETHODIMP EnumCreatedEffectObjects(LPDIENUMCREATEDEFFECTOBJECTSCALLBACK callback, LPVOID ref, DWORD) override
    {
        if (!callback) return E_POINTER;
        std::vector<Effect*> effects;
        {
            std::lock_guard<std::recursive_mutex> lock(g_mutex);
            effects = m_effects;
        }
        for (Effect* e : effects)
            if (callback(e, ref) == DIENUM_STOP) break;
        return DI_OK;
    }

    STDMETHODIMP Escape(LPDIEFFESCAPE) override { return DIERR_UNSUPPORTED; }

    // ── 原样转发的方法 ──

    STDMETHODIMP Acquire() override
    {
        HRESULT hr = m_real->Acquire();
        if (FAILED(hr) && hr != m_lastAcquireError) {
            m_lastAcquireError = hr;
            Log("slot %d: Acquire failed with 0x%08lX", m_slot + 1, hr);
        } else if (SUCCEEDED(hr) && m_lastAcquireError != S_OK) {
            m_lastAcquireError = S_OK;
            Log("slot %d: Acquire succeeded", m_slot + 1);
        }
        return hr;
    }

    STDMETHODIMP SetCooperativeLevel(HWND window, DWORD flags) override
    {
        // 游戏对 "支持力反馈" 的手柄会要求独占 + 前台模式 (真实的力反馈设备只有独占时才能播放效果)。
        // 前台模式要求游戏窗口正处于前台, 经 sider 之类的启动器启动时这个条件常常不满足, Acquire 失败,
        // 手柄就完全没反应。我们的力反馈是模拟的, 不需要独占; 改成非独占 + 后台, 和普通手柄一样。
        DWORD adjusted = (flags & ~(DWORD)(DISCL_EXCLUSIVE | DISCL_FOREGROUND)) | DISCL_NONEXCLUSIVE | DISCL_BACKGROUND;
        HRESULT hr = m_real->SetCooperativeLevel(window, adjusted);
        Log("slot %d: SetCooperativeLevel(window %p, flags 0x%lX -> 0x%lX) = 0x%08lX", m_slot + 1, window, flags, adjusted, hr);
        return hr;
    }
    STDMETHODIMP GetDeviceState(DWORD size, LPVOID data) override { return m_real->GetDeviceState(size, data); }
    STDMETHODIMP GetDeviceData(DWORD size, LPDIDEVICEOBJECTDATA data, LPDWORD count, DWORD flags) override { return m_real->GetDeviceData(size, data, count, flags); }
    STDMETHODIMP SetEventNotification(HANDLE event) override { return m_real->SetEventNotification(event); }
    STDMETHODIMP GetDeviceInfo(LPDIDEVICEINSTANCEW info) override { return m_real->GetDeviceInfo(info); }
    STDMETHODIMP RunControlPanel(HWND owner, DWORD flags) override { return m_real->RunControlPanel(owner, flags); }
    STDMETHODIMP Initialize(HINSTANCE instance, DWORD version, REFGUID guid) override { return m_real->Initialize(instance, version, guid); }
    STDMETHODIMP Poll() override { return m_real->Poll(); }
    STDMETHODIMP SendDeviceData(DWORD size, LPCDIDEVICEOBJECTDATA data, LPDWORD count, DWORD flags) override { return m_real->SendDeviceData(size, data, count, flags); }
    STDMETHODIMP EnumEffectsInFile(LPCWSTR, LPDIENUMEFFECTSINFILECALLBACK, LPVOID, DWORD) override { return DIERR_UNSUPPORTED; }
    STDMETHODIMP WriteEffectToFile(LPCWSTR, DWORD, LPDIFILEEFFECT, DWORD) override { return DIERR_UNSUPPORTED; }
    STDMETHODIMP BuildActionMap(LPDIACTIONFORMATW format, LPCWSTR user, DWORD flags) override { return m_real->BuildActionMap(format, user, flags); }
    STDMETHODIMP SetActionMap(LPDIACTIONFORMATW format, LPCWSTR user, DWORD flags) override { return m_real->SetActionMap(format, user, flags); }
    STDMETHODIMP GetImageInfo(LPDIDEVICEIMAGEINFOHEADERW info) override { return m_real->GetImageInfo(info); }

    // ── 供 Effect 和工作线程使用 (调用方持有 g_mutex) ──

    void AddEffect(Effect* e) { m_effects.push_back(e); }
    void RemoveEffect(Effect* e) { m_effects.erase(std::remove(m_effects.begin(), m_effects.end(), e), m_effects.end()); }
    void StopOthers(Effect* keep) { for (Effect* e : m_effects) if (e != keep) e->ForceStop(); }

    /// 一根效果轴驱动哪个马达: X 轴 = 大马达 (0), Y 轴 = 小马达 (1), 认不出返回 -1。
    int MotorOfAxis(DWORD axis, DWORD axisMode)
    {
        // 这里只需要 guidType, 它在 A/W 两种结构里位置相同; 结构大小要和真实接口的字符集对上
        union { DIDEVICEOBJECTINSTANCEW w; DIDEVICEOBJECTINSTANCEA a; } info = {};
        info.w.dwSize = m_unicode ? sizeof(info.w) : sizeof(info.a);
        bool byId = (axisMode & DIEFF_OBJECTIDS) != 0;
        if (byId) axis &= ~(DWORD)(DIDFT_FFACTUATOR | DIDFT_FFEFFECTTRIGGER);
        if (FAILED(m_real->GetObjectInfo(&info.w, axis, byId ? DIPH_BYID : DIPH_BYOFFSET))) return -1;
        if (IsEqualGUID(info.w.guidType, GUID_XAxis)) return 0;
        if (IsEqualGUID(info.w.guidType, GUID_YAxis)) return 1;
        return -1;
    }

    void GetOutput(ULONGLONG now, double motors[2])
    {
        if (m_paused || !m_actuatorsOn) return;
        double sum[2] = { 0, 0 };
        for (Effect* e : m_effects) e->AddOutput(now, sum);
        double gain = m_gain / (double)DI_FFNOMINALMAX;
        motors[0] = std::max(motors[0], std::min(1.0, sum[0] * gain));
        motors[1] = std::max(motors[1], std::min(1.0, sum[1] * gain));
    }

private:
    ~Device() = default;

    struct EnumObjectsContext { Device* device; LPDIENUMDEVICEOBJECTSCALLBACKW callback; LPVOID ref; bool forceOnly; };

    static BOOL CALLBACK EnumObjectsThunk(LPCDIDEVICEOBJECTINSTANCEW object, LPVOID param)
    {
        auto* context = (EnumObjectsContext*)param;
        if (!IsForceAxis(object->guidType))
            return context->forceOnly ? DIENUM_CONTINUE : context->callback(object, context->ref);
        BYTE buffer[sizeof(DIDEVICEOBJECTINSTANCEW)];
        memcpy(buffer, object, std::min<size_t>(object->dwSize, sizeof(buffer)));
        context->device->PatchObject((LPDIDEVICEOBJECTINSTANCEW)buffer);
        return context->callback((LPCDIDEVICEOBJECTINSTANCEW)buffer, context->ref);
    }

    /// 我们把 X、Y 两根轴声明为力反馈执行器: X = 大马达, Y = 小马达 (双马达震动手柄的惯例)。
    static bool IsForceAxis(REFGUID type) { return IsEqualGUID(type, GUID_XAxis) || IsEqualGUID(type, GUID_YAxis); }

    void PatchObject(LPDIDEVICEOBJECTINSTANCEW object)
    {
        object->dwType |= DIDFT_FFACTUATOR;     // dwType/dwFlags 在名称字段之前, A/W 位置相同
        object->dwFlags |= DIDOI_FFACTUATOR;
        if (m_unicode) {
            if (object->dwSize >= sizeof(DIDEVICEOBJECTINSTANCEW)) {
                object->dwFFMaxForce = DI_FFNOMINALMAX;
                object->dwFFForceResolution = 1;
            }
        } else {
            auto* ansi = (LPDIDEVICEOBJECTINSTANCEA)object;
            if (ansi->dwSize >= sizeof(DIDEVICEOBJECTINSTANCEA)) {
                ansi->dwFFMaxForce = DI_FFNOMINALMAX;
                ansi->dwFFForceResolution = 1;
            }
        }
    }

    void FillEffectInfo(void* buffer, const EffectType& type)
    {
        // 名称之前的字段 A/W 位置相同
        auto* info = (LPDIEFFECTINFOW)buffer;
        info->dwSize = m_unicode ? sizeof(DIEFFECTINFOW) : sizeof(DIEFFECTINFOA);
        info->guid = *type.guid;
        info->dwEffType = type.type | DIEFT_FFATTACK | DIEFT_FFFADE | DIEFT_STARTDELAY;
        info->dwStaticParams = kEffectParams;
        info->dwDynamicParams = kEffectParams;
        if (m_unicode) MultiByteToWideChar(CP_ACP, 0, type.name, -1, info->tszName, MAX_PATH);
        else strcpy_s(((LPDIEFFECTINFOA)buffer)->tszName, type.name);
    }

    void StopAllEffects()
    {
        std::lock_guard<std::recursive_mutex> lock(g_mutex);
        for (Effect* e : m_effects) e->ForceStop();
        PublishOutputs();
    }

    LONG m_ref = 1;
    IDirectInputDevice8W* m_real;
    bool m_unicode;                 // 游戏要的是 W 还是 A 接口 (两者的虚表布局相同, 只是结构里的字符串宽度不同)
    int m_slot;                     // 0..7, 对应 PesPadHub 的第 1~8 个虚拟手柄
    DWORD m_gain = DI_FFNOMINALMAX;
    bool m_paused = false;
    bool m_actuatorsOn = true;
    HRESULT m_lastAcquireError = S_OK;
    std::vector<Effect*> m_effects;
    BYTE m_propBuffer[1024];
};

// ───────────────────────── Effect 的实现 ─────────────────────────

Effect::Effect(Device* device, const EffectType* type) : m_device(device), m_type(type)
{
    m_device->AddRef();     // 效果活着的时候设备不能被销毁
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    m_device->AddEffect(this);
}

STDMETHODIMP_(ULONG) Effect::Release()
{
    LONG ref = InterlockedDecrement(&m_ref);
    if (ref == 0) {
        {
            std::lock_guard<std::recursive_mutex> lock(g_mutex);
            m_device->RemoveEffect(this);
            PublishOutputs();
        }
        Device* device = m_device;
        delete this;
        device->Release();
    }
    return ref;
}

STDMETHODIMP Effect::SetParameters(LPCDIEFFECT effect, DWORD flags)
{
    if (!effect) return E_POINTER;
    std::lock_guard<std::recursive_mutex> lock(g_mutex);

    if (flags & DIEP_DURATION) m_duration = effect->dwDuration;
    if (flags & DIEP_SAMPLEPERIOD) m_samplePeriod = effect->dwSamplePeriod;
    if (flags & DIEP_GAIN) m_gain = std::min<DWORD>(effect->dwGain, DI_FFNOMINALMAX);
    if (flags & DIEP_TRIGGERBUTTON) m_trigger = effect->dwTriggerButton;
    if (flags & DIEP_TRIGGERREPEATINTERVAL) m_triggerRepeat = effect->dwTriggerRepeatInterval;
    if ((flags & DIEP_STARTDELAY) && effect->dwSize >= sizeof(DIEFFECT)) m_startDelay = effect->dwStartDelay;

    if (flags & DIEP_AXES) {
        if (effect->cAxes > 32 || (effect->cAxes > 0 && !effect->rgdwAxes)) return DIERR_INVALIDPARAM;
        m_axisMode = effect->dwFlags & (DIEFF_OBJECTIDS | DIEFF_OBJECTOFFSETS);
        m_axes.assign(effect->rgdwAxes, effect->rgdwAxes + effect->cAxes);
        m_axisMotor.clear();
        for (DWORD axis : m_axes) m_axisMotor.push_back(m_device->MotorOfAxis(axis, m_axisMode));
    }
    if (flags & DIEP_DIRECTION) {
        if (effect->cAxes > 32) return DIERR_INVALIDPARAM;
        m_coordinates = effect->dwFlags & (DIEFF_CARTESIAN | DIEFF_POLAR | DIEFF_SPHERICAL);
        if (effect->rglDirection) m_direction.assign(effect->rglDirection, effect->rglDirection + effect->cAxes);
        else m_direction.clear();
    }
    if (flags & DIEP_ENVELOPE) {
        m_hasEnvelope = effect->lpEnvelope != nullptr;
        if (m_hasEnvelope) m_envelope = *effect->lpEnvelope;
    }
    if (flags & DIEP_TYPESPECIFICPARAMS) {
        if (effect->lpvTypeSpecificParams && effect->cbTypeSpecificParams > 0 && effect->cbTypeSpecificParams <= 4096) {
            auto* bytes = (const BYTE*)effect->lpvTypeSpecificParams;
            m_typeParams.assign(bytes, bytes + effect->cbTypeSpecificParams);
        } else {
            m_typeParams.clear();
        }
    }

    // 记下游戏实际给的参数, 用于核对换算是否符合游戏的用法。游戏每帧都会重设一次, 所以只在内容有变化时记
    LONG magnitude = m_typeParams.size() >= sizeof(LONG) ? *(const LONG*)m_typeParams.data() : 0;
    char line[256];
    snprintf(line, sizeof(line), "duration=%lu gain=%lu coord=0x%lX axes=%zu [0x%lX,0x%lX] motor [%d,%d] dir [%ld,%ld] magnitude=%ld",
        m_duration, m_gain, m_coordinates, m_axes.size(),
        m_axes.size() > 0 ? m_axes[0] : 0UL, m_axes.size() > 1 ? m_axes[1] : 0UL,
        m_axisMotor.size() > 0 ? m_axisMotor[0] : -1, m_axisMotor.size() > 1 ? m_axisMotor[1] : -1,
        m_direction.size() > 0 ? m_direction[0] : 0L, m_direction.size() > 1 ? m_direction[1] : 0L, magnitude);
    if (m_lastLogged != line) {
        m_lastLogged = line;
        Log("slot %d: effect %p SetParameters flags=0x%lX %s", m_device->Slot() + 1, this, flags, line);
    }

    if (flags & DIEP_START) return Start(1, 0);
    PublishOutputs();
    return DI_OK;
}

STDMETHODIMP Effect::GetParameters(LPDIEFFECT effect, DWORD flags)
{
    if (!effect) return E_POINTER;
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    HRESULT result = DI_OK;

    if (flags & DIEP_DURATION) effect->dwDuration = m_duration;
    if (flags & DIEP_SAMPLEPERIOD) effect->dwSamplePeriod = m_samplePeriod;
    if (flags & DIEP_GAIN) effect->dwGain = m_gain;
    if (flags & DIEP_TRIGGERBUTTON) effect->dwTriggerButton = m_trigger;
    if (flags & DIEP_TRIGGERREPEATINTERVAL) effect->dwTriggerRepeatInterval = m_triggerRepeat;
    if ((flags & DIEP_STARTDELAY) && effect->dwSize >= sizeof(DIEFFECT)) effect->dwStartDelay = m_startDelay;

    if (flags & (DIEP_AXES | DIEP_DIRECTION)) {
        DWORD count = (DWORD)m_axes.size();
        if (effect->cAxes < count) {
            result = DIERR_MOREDATA;
        } else {
            if ((flags & DIEP_AXES) && effect->rgdwAxes) std::copy(m_axes.begin(), m_axes.end(), effect->rgdwAxes);
            if ((flags & DIEP_DIRECTION) && effect->rglDirection)
                for (size_t i = 0; i < m_direction.size() && i < count; i++) effect->rglDirection[i] = m_direction[i];
            effect->dwFlags = (effect->dwFlags & ~(DWORD)(DIEFF_OBJECTIDS | DIEFF_OBJECTOFFSETS | DIEFF_CARTESIAN | DIEFF_POLAR | DIEFF_SPHERICAL))
                            | m_axisMode | m_coordinates;
        }
        effect->cAxes = count;
    }
    if (flags & DIEP_ENVELOPE) {
        if (m_hasEnvelope && effect->lpEnvelope) *effect->lpEnvelope = m_envelope;
        else effect->lpEnvelope = nullptr;
    }
    if (flags & DIEP_TYPESPECIFICPARAMS) {
        DWORD size = (DWORD)m_typeParams.size();
        if (effect->cbTypeSpecificParams < size) result = DIERR_MOREDATA;
        else if (effect->lpvTypeSpecificParams && size > 0) memcpy(effect->lpvTypeSpecificParams, m_typeParams.data(), size);
        effect->cbTypeSpecificParams = size;
    }
    return result;
}

STDMETHODIMP Effect::Start(DWORD iterations, DWORD flags)
{
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    if (flags & DIES_SOLO) m_device->StopOthers(this);
    m_iterations = iterations == 0 ? 1 : iterations;
    m_startTick = GetTickCount64();
    m_playing = true;
    Log("slot %d: effect %p Start iterations=%lu flags=0x%lX", m_device->Slot() + 1, this, iterations, flags);
    PublishOutputs();
    return DI_OK;
}

STDMETHODIMP Effect::Stop()
{
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    if (m_playing) Log("slot %d: effect %p Stop", m_device->Slot() + 1, this);
    m_playing = false;
    PublishOutputs();
    return DI_OK;
}

STDMETHODIMP Effect::GetEffectStatus(LPDWORD status)
{
    if (!status) return E_POINTER;
    std::lock_guard<std::recursive_mutex> lock(g_mutex);
    *status = Playing(GetTickCount64()) ? DIEGES_PLAYING : 0;
    return DI_OK;
}

bool Effect::Playing(ULONGLONG now)
{
    if (!m_playing) return false;
    if (m_duration != INFINITE && m_iterations != INFINITE) {
        double totalMs = (m_startDelay + (double)m_duration * m_iterations) / 1000.0;
        if ((double)(now - m_startTick) >= totalMs) m_playing = false;
    }
    return m_playing;
}

void Effect::AddOutput(ULONGLONG now, double motors[2])
{
    if (!Playing(now)) return;
    double elapsedUs = (double)(now - m_startTick) * 1000.0 - m_startDelay;
    if (elapsedUs < 0) return;      // 还在起始延迟里

    double position = 0;            // 在单次播放中的进度 0..1, 只有渐变效果用得到
    if (m_duration != INFINITE && m_duration > 0) position = std::fmod(elapsedUs, (double)m_duration) / m_duration;

    double level = 0;
    bool smallMotorOnly = false;
    switch (m_type->kind) {
    case EffectKind::Constant:
        if (m_typeParams.size() >= sizeof(DICONSTANTFORCE)) {
            level = ((const DICONSTANTFORCE*)m_typeParams.data())->lMagnitude;
            // PES 2021 校准 (2026-10-03, 用同一局游戏在 XInput 模式下的记录对照):
            // 游戏在 DirectInput 下只给一个强度, 不区分马达。XInput 下它给大马达的只有 10/40/90/100%,
            // 给小马达的只有 30/40/60/100%, 所以 30% 和 60% 必定是小马达的事件;
            // 40% 和 100% 两种马达都有可能, 按出现得多的大马达处理; 其余按大马达。
            long magnitude = std::labs((long)level);
            smallMotorOnly = magnitude == 3000 || magnitude == 6000;
        }
        break;
    case EffectKind::Ramp:
        if (m_typeParams.size() >= sizeof(DIRAMPFORCE)) {
            auto* ramp = (const DIRAMPFORCE*)m_typeParams.data();
            level = ramp->lStart + (ramp->lEnd - ramp->lStart) * position;
        }
        break;
    case EffectKind::Periodic:
        // 马达跟不上波形本身, 按振幅当作持续震动
        if (m_typeParams.size() >= sizeof(DIPERIODIC)) level = ((const DIPERIODIC*)m_typeParams.data())->dwMagnitude;
        break;
    case EffectKind::Other:
        return;
    }
    level = std::min(1.0, std::fabs(level) / DI_FFNOMINALMAX) * (m_gain / (double)DI_FFNOMINALMAX);
    if (level <= 0) return;

    // 把力分到两个马达上。单轴效果: 那根轴对应的马达; 双轴效果: 按方向在两根轴上的分量;
    // 认不出轴或方向为零时两个马达一起震。
    double weight[2] = { 1, 1 };
    if (smallMotorOnly) {
        weight[0] = 0;
    } else if (m_axes.size() == 1) {
        if (m_axisMotor[0] == 0) weight[1] = 0;
        else if (m_axisMotor[0] == 1) weight[0] = 0;
    } else if (m_axes.size() >= 2 && !m_direction.empty()) {
        double c0, c1;      // 方向在第 1、第 2 根轴上的分量
        if (m_coordinates & DIEFF_POLAR) {
            double angle = m_direction[0] / 100.0 * kPi / 180.0;    // 0 = 正北 (第 2 根轴的负方向), 顺时针
            c0 = std::sin(angle);
            c1 = -std::cos(angle);
        } else if (m_coordinates & DIEFF_SPHERICAL) {
            double angle = m_direction[0] / 100.0 * kPi / 180.0;    // 从第 1 根轴转向第 2 根轴
            c0 = std::cos(angle);
            c1 = std::sin(angle);
        } else {
            c0 = m_direction[0];
            c1 = m_direction.size() > 1 ? m_direction[1] : 0;
        }
        double norm = std::max(std::fabs(c0), std::fabs(c1));
        if (norm > 1e-9) {
            double share0 = std::fabs(c0) / norm, share1 = std::fabs(c1) / norm;
            if (share0 < 1e-6) share0 = 0;
            if (share1 < 1e-6) share1 = 0;
            int motor0 = m_axisMotor[0] >= 0 ? m_axisMotor[0] : 0;
            int motor1 = m_axisMotor[1] >= 0 ? m_axisMotor[1] : 1 - motor0;
            weight[0] = weight[1] = 0;
            weight[motor0] = std::max(weight[motor0], share0);
            weight[motor1] = std::max(weight[motor1], share1);
        }
    }
    motors[0] += level * weight[0];
    motors[1] += level * weight[1];
}

// ───────────────────────── 工作线程 ─────────────────────────

void PublishOutputs()
{
    static SharedSlot written[kSlots] = {};
    static bool wasPresent[kSlots] = {};

    ULONGLONG now = GetTickCount64();
    double motors[kSlots][2] = {};
    bool present[kSlots] = {};
    for (Device* device : g_devices) {
        present[device->Slot()] = true;
        device->GetOutput(now, motors[device->Slot()]);
    }

    for (int i = 0; i < kSlots; i++) {
        // 只管本进程里打开着的手柄; 设备刚被释放时补写一次 0
        if (!present[i] && !wasPresent[i]) continue;
        wasPresent[i] = present[i];
        SharedSlot value = { (BYTE)std::lround(motors[i][0] * 255), (BYTE)std::lround(motors[i][1] * 255), { 0, 0 } };
        if (value.largeMotor != written[i].largeMotor || value.smallMotor != written[i].smallMotor) {
            Log("slot %d: output large=%u small=%u%s", i + 1, value.largeMotor, value.smallMotor, g_shared ? "" : " (PesPadHub not connected)");
            written[i] = value;
        }
        if (g_shared) g_shared->slots[i] = value;
    }
}

/// 每 8 毫秒重算一次: 效果有时长, 到点要自己停; 同时跳一下心跳, 让 PesPadHub 知道游戏还活着。
DWORD WINAPI Worker(LPVOID)
{
    ULONGLONG lastOpenAttempt = 0;
    for (;;) {
        Sleep(8);
        ULONGLONG now = GetTickCount64();
        std::lock_guard<std::recursive_mutex> lock(g_mutex);
        if (!g_shared && now - lastOpenAttempt >= 1000) {
            lastOpenAttempt = now;
            TryOpenShared();
        }
        PublishOutputs();
        if (g_shared) g_shared->heartbeat++;
    }
}

// ───────────────────────── IDirectInput8 包装 ─────────────────────────

/// 设备的产品 GUID 形如 {PIDVID-0000-0000-0000-504944564944}: Data1 的低 16 位是 VID, 高 16 位是 PID。
int SlotOfProduct(REFGUID product)
{
    if (LOWORD(product.Data1) != kVendorId || memcmp(product.Data4 + 2, "PIDVID", 6) != 0) return -1;
    int number = HIWORD(product.Data1) & 0xFF;      // PID 0x0D01~0x0D08 (DS4) 或 0x0E01~0x0E08 (Xbox)
    return number >= 1 && number <= kSlots ? number - 1 : -1;
}

class DirectInput final : public IDirectInput8W {
public:
    DirectInput(IDirectInput8W* real, bool unicode) : m_real(real), m_unicode(unicode) {}

    STDMETHODIMP QueryInterface(REFIID riid, void** out) override
    {
        if (!out) return E_POINTER;
        if (IsEqualIID(riid, IID_IUnknown) || IsEqualIID(riid, m_unicode ? IID_IDirectInput8W : IID_IDirectInput8A)) {
            *out = this;
            AddRef();
            return S_OK;
        }
        return m_real->QueryInterface(riid, out);
    }
    STDMETHODIMP_(ULONG) AddRef() override { return InterlockedIncrement(&m_ref); }
    STDMETHODIMP_(ULONG) Release() override
    {
        LONG ref = InterlockedDecrement(&m_ref);
        if (ref == 0) {
            m_real->Release();
            delete this;
        }
        return ref;
    }

    STDMETHODIMP CreateDevice(REFGUID guid, LPDIRECTINPUTDEVICE8W* out, LPUNKNOWN outer) override
    {
        HRESULT hr = m_real->CreateDevice(guid, out, outer);
        if (FAILED(hr) || !out || !*out || outer) return hr;

        // 只需要产品 GUID, 它在 A/W 两种结构里位置相同
        union { DIDEVICEINSTANCEW w; DIDEVICEINSTANCEA a; } info = {};
        info.w.dwSize = m_unicode ? sizeof(info.w) : sizeof(info.a);
        if (FAILED((*out)->GetDeviceInfo(&info.w))) return hr;
        int slot = SlotOfProduct(info.w.guidProduct);
        if (slot < 0) return hr;    // 不是 PesPadHub 的虚拟手柄: 不包装, 游戏直接用系统的对象

        Log("slot %d: device created (product %08lX, %s interface)", slot + 1, info.w.guidProduct.Data1, m_unicode ? "W" : "A");
        *out = new Device(*out, m_unicode, slot);
        return hr;
    }

    STDMETHODIMP EnumDevices(DWORD type, LPDIENUMDEVICESCALLBACKW callback, LPVOID ref, DWORD flags) override
    {
        if (!(flags & DIEDFL_FORCEFEEDBACK) || !callback) return m_real->EnumDevices(type, callback, ref, flags);

        // 游戏只要 "支持力反馈的设备": 先报真正支持的, 再补上我们的虚拟手柄
        EnumDevicesContext context = { callback, ref, false, false };
        HRESULT hr = m_real->EnumDevices(type, EnumDevicesThunk, &context, flags);
        if (FAILED(hr) || context.stopped) return hr;
        context.oursOnly = true;
        return m_real->EnumDevices(type, EnumDevicesThunk, &context, flags & ~(DWORD)DIEDFL_FORCEFEEDBACK);
    }

    STDMETHODIMP GetDeviceStatus(REFGUID guid) override { return m_real->GetDeviceStatus(guid); }
    STDMETHODIMP RunControlPanel(HWND owner, DWORD flags) override { return m_real->RunControlPanel(owner, flags); }
    STDMETHODIMP Initialize(HINSTANCE instance, DWORD version) override { return m_real->Initialize(instance, version); }
    STDMETHODIMP FindDevice(REFGUID guid, LPCWSTR name, LPGUID instance) override { return m_real->FindDevice(guid, name, instance); }
    STDMETHODIMP EnumDevicesBySemantics(LPCWSTR user, LPDIACTIONFORMATW format, LPDIENUMDEVICESBYSEMANTICSCBW callback, LPVOID ref, DWORD flags) override
    {
        return m_real->EnumDevicesBySemantics(user, format, callback, ref, flags);
    }
    STDMETHODIMP ConfigureDevices(LPDICONFIGUREDEVICESCALLBACK callback, LPDICONFIGUREDEVICESPARAMSW params, DWORD flags, LPVOID ref) override
    {
        return m_real->ConfigureDevices(callback, params, flags, ref);
    }

private:
    ~DirectInput() = default;

    struct EnumDevicesContext { LPDIENUMDEVICESCALLBACKW callback; LPVOID ref; bool oursOnly; bool stopped; };

    static BOOL CALLBACK EnumDevicesThunk(LPCDIDEVICEINSTANCEW instance, LPVOID param)
    {
        auto* context = (EnumDevicesContext*)param;
        if (context->oursOnly && SlotOfProduct(instance->guidProduct) < 0) return DIENUM_CONTINUE;
        if (context->callback(instance, context->ref) == DIENUM_STOP) {
            context->stopped = true;
            return DIENUM_STOP;
        }
        return DIENUM_CONTINUE;
    }

    LONG m_ref = 1;
    IDirectInput8W* m_real;
    bool m_unicode;
};

} // namespace

// ───────────────────────── 导出函数 ─────────────────────────

extern "C" HRESULT WINAPI DirectInput8Create(HINSTANCE instance, DWORD version, REFIID riid, LPVOID* out, LPUNKNOWN outer)
{
    LoadReal();
    if (!g_realCreate) return E_FAIL;
    HRESULT hr = g_realCreate(instance, version, riid, out, outer);
    if (FAILED(hr) || !out || !*out || outer) return hr;

    bool unicode = IsEqualIID(riid, IID_IDirectInput8W) != 0;
    if (!unicode && !IsEqualIID(riid, IID_IDirectInput8A)) return hr;

    // 只包装游戏本体拿到的接口。sider 这类注入游戏进程的模块也会调用本函数, 并且会在拿到的对象上改写虚表、
    // 只保存一份 "原始函数": 如果它拿到的是我们的包装对象, 而游戏又用到了未包装的真实对象 (键盘、鼠标),
    // 两类对象的调用就会被串到一起, 键盘和手柄一起失灵。让它们拿到系统原始接口, 它们的挂钩就只会碰到真实对象。
    HMODULE game = GetModuleHandleW(nullptr);
    HMODULE caller = nullptr;
    GetModuleHandleExW(GET_MODULE_HANDLE_EX_FLAG_FROM_ADDRESS | GET_MODULE_HANDLE_EX_FLAG_UNCHANGED_REFCOUNT, (LPCWSTR)_ReturnAddress(), &caller);
    if (instance != game && caller != game) {
        wchar_t module[MAX_PATH] = L"?";
        if (caller) GetModuleFileNameW(caller, module, MAX_PATH);
        Log("DirectInput8Create from %ls: not the game itself, passing the system interface through", wcsrchr(module, L'\\') ? wcsrchr(module, L'\\') + 1 : module);
        return hr;
    }

    Log("DirectInput8Create (%s interface, version 0x%lX)", unicode ? "W" : "A", version);
    *out = new DirectInput((IDirectInput8W*)*out, unicode);
    return hr;
}

BOOL WINAPI DllMain(HINSTANCE module, DWORD reason, LPVOID)
{
    if (reason == DLL_PROCESS_ATTACH) DisableThreadLibraryCalls(module);
    return TRUE;
}

extern "C" HRESULT WINAPI DllCanUnloadNow()
{
    return S_FALSE;
}

extern "C" HRESULT WINAPI DllGetClassObject(REFCLSID clsid, REFIID riid, LPVOID* out)
{
    LoadReal();
    return g_realGetClassObject ? g_realGetClassObject(clsid, riid, out) : CLASS_E_CLASSNOTAVAILABLE;
}

extern "C" HRESULT WINAPI DllRegisterServer()
{
    return E_NOTIMPL;
}

extern "C" HRESULT WINAPI DllUnregisterServer()
{
    return E_NOTIMPL;
}

extern "C" LPCDIDATAFORMAT WINAPI GetdfDIJoystick()
{
    LoadReal();
    return g_realGetdf ? g_realGetdf() : nullptr;
}

/// PesPadHub 靠这个导出名认出游戏目录里的 dinput8.dll 是不是自己的, 以免覆盖/删除别的插件。
extern "C" DWORD WINAPI PesPadHubRumbleBridgeVersion()
{
    return 1;
}
