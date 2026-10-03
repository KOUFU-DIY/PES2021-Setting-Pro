# PES 2021 Setting Pro

eFootball PES 2021 SEASON UPDATE 的手柄与设置助手（Windows）。

## 功能

- **8 个常驻虚拟手柄**：启动后立刻创建 8 个 Xbox One 型虚拟手柄（`Controller (Xbox One For Windows) 1~8`），游戏里按序号一次设好就不用再改；之后插拔的实体手柄自动映射到这 8 个序号上。
- **手柄映射管理**：主手柄固定序号（图钉）、上下调整顺序、单独关闭某个序号、超出 8 个时的待分配区、震动识别、输入/震动指示灯、总开关、开机自启动（托盘）。
- **一键写入游戏**：把游戏的「控制器 1~8」直接设成虚拟手柄 1~8 + DirectInput，写入 `settings.dat`。
- **游戏设置弹窗**（替代官方设置程序）：
  - 显示：窗口 / 全屏、分辨率、游戏中 / 回放帧率、HDR、Vsync
  - 控制器：DirectInput / XInput，控制器 1~8 分别用哪个设备（含实体手柄），冲突提示，输入指示灯
  - 音频：音频缓冲
  - 在线：P2P 自动、UDP 端口、语音聊天
- **DInput 震动插件**：装进游戏目录的 `dinput8.dll`，让游戏在 DirectInput 模式下也能驱动手柄震动（兼容 sider）。
- 启动时检查 GitHub 上是否有新版本。

## 使用前提

- Windows 10 / 11 x64
- [.NET 9 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0)（x64）
- [ViGEmBus 驱动](https://github.com/nefarius/ViGEmBus/releases)（创建虚拟手柄用，装一次即可）

## 使用

1. 从 [Releases](https://github.com/KOUFU-DIY/PES2021-Setting-Pro/releases) 下载 `PES2021SettingPro-vX.Y.Z.exe`，放到任意位置运行。
2. 先启动本工具，再启动游戏。最小化后在托盘继续运行。
3. 第一次：点「映射手柄写入 setting」（游戏和官方设置程序要先关掉），游戏里的控制器 1~8 就固定对应虚拟手柄 1~8。
4. 需要游戏内震动：点「安装 / 卸载 DInput 震动插件…」，选 `PES2021.exe` 所在文件夹。

配置和日志在 `%APPDATA%\PesPadHub`。每次写入游戏设置前，原文件会备份为 `settings.dat.bak`。

## 从源码构建

```powershell
# 震动插件 (需要 Visual Studio C++ 生成工具)
PesPadHub\RumbleBridge\build.bat
# 单文件 exe → dist\
dotnet publish PesPadHub\PesPadHub.csproj -c Release -o dist
```

发布新版本：改 `PesPadHub\PesPadHub.csproj` 里的 `<Version>`，提交后运行 `release.ps1`（需要 GitHub CLI 并已登录）。
