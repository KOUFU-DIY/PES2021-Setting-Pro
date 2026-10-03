using System.IO;
using System.Text.Json;

namespace PesPadHub;

/// <summary>把某个实体手柄固定到某个槽位 (例如主手柄永远是 1 号)。</summary>
sealed class PinEntry
{
    public string Key { get; set; } = "";
    public int Slot { get; set; }
    public string Name { get; set; } = "";
}

sealed class AppConfig
{
    public static readonly string DataDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PesPadHub");

    static readonly string FilePath = Path.Combine(DataDirectory, "config.json");

    static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    /// <summary>总开关: 关闭时不创建虚拟手柄。</summary>
    public bool Enabled { get; set; } = true;
    /// <summary>界面语言 ("zh" / "en"); 为空表示还没选过, 跟随系统。</summary>
    public string? Language { get; set; }
    public List<PinEntry> Pins { get; set; } = [];
    /// <summary>单独关闭的槽位 (0 起): 不创建该虚拟手柄, 也不往它分配实体手柄。</summary>
    public List<int> DisabledSlots { get; set; } = [];
    /// <summary>上次安装震动插件时选的游戏文件夹 (PES2021.exe 所在目录), 下次打开选择框时直接定位到这里。</summary>
    public string? GameDirectory { get; set; }
    /// <summary>用户点过 "忽略" 的新版本 tag (如 "v1.2.0"), 这个版本不再提示。</summary>
    public string? SkippedUpdate { get; set; }

    public static AppConfig Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var config = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(FilePath)) ?? new AppConfig();
                config.Pins.RemoveAll(p => p.Slot < 0 || p.Slot >= VirtualPad.MaxPads || string.IsNullOrEmpty(p.Key));
                config.DisabledSlots.RemoveAll(s => s < 0 || s >= VirtualPad.MaxPads);
                return config;
            }
        }
        catch (Exception)
        {
            // 配置损坏时按默认配置启动
        }
        return new AppConfig();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(this, JsonOptions));
        }
        catch (Exception)
        {
            // 配置写不进去不影响当前运行
        }
    }
}
