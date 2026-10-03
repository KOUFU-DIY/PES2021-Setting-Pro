using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;

namespace PesPadHub;

/// <summary>GitHub 上的一个新版本。</summary>
sealed record UpdateInfo(Version Version, string Tag, string PageUrl);

/// <summary>
/// 版本号和更新检查。版本号来自 csproj 的 Version; 更新检查读 GitHub 仓库的最新 Release (公开 API, 不用登录),
/// tag 形如 v1.2.3, 比当前版本新就提示用户去 Release 页面下载。
/// </summary>
static class UpdateCheck
{
    public const string Owner = "KOUFU-DIY";
    public const string Repo = "PES2021-Setting-Pro";
    public static readonly string ReleasesUrl = $"https://github.com/{Owner}/{Repo}/releases";

    static readonly Version Current = ParseVersion(
        Assembly.GetEntryAssembly()?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "") ?? new Version(0, 0, 0);

    /// <summary>当前版本, 显示用, 如 "1.0.0"。</summary>
    public static string CurrentText => Current.ToString(3);

    /// <summary>查 GitHub 最新 Release; 有更新返回它, 没有或查不到 (断网、仓库还没发布过) 返回 null。</summary>
    public static async Task<UpdateInfo?> FetchAsync(CancellationToken cancel = default)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(10) };
            http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("PES2021SettingPro", CurrentText));
            http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            using JsonDocument json = JsonDocument.Parse(await http.GetStringAsync($"https://api.github.com/repos/{Owner}/{Repo}/releases/latest", cancel));
            JsonElement root = json.RootElement;
            string tag = root.GetProperty("tag_name").GetString() ?? "";
            string url = root.TryGetProperty("html_url", out JsonElement u) ? u.GetString() ?? ReleasesUrl : ReleasesUrl;
            Version? latest = ParseVersion(tag);
            return latest != null && latest > Current ? new UpdateInfo(latest, tag, url) : null;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or KeyNotFoundException or InvalidOperationException)
        {
            return null;
        }
    }

    /// <summary>"v1.2.3"、"1.2.3"、"1.2.3+abc" 都认; 认不出返回 null。</summary>
    static Version? ParseVersion(string text)
    {
        string s = text.Trim().TrimStart('v', 'V');
        int cut = s.IndexOfAny(['+', '-', ' ']);
        if (cut >= 0) s = s[..cut];
        return Version.TryParse(s, out Version? v) ? new Version(v.Major, v.Minor, Math.Max(v.Build, 0)) : null;
    }
}
