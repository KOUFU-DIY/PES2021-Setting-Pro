# 发布一个新版本到 GitHub:
#   1. 先在 PesPadHub\PesPadHub.csproj 里把 <Version> 改成新版本号 (如 1.1.0), 提交
#   2. 运行:  .\release.ps1
#      (或  .\release.ps1 -Notes "这次改了什么")
# 脚本会: 编译震动插件 → 发布单文件 exe 到 dist → 打 tag vX.Y.Z 推到 GitHub → 创建 Release 并上传 exe。
# 程序启动时会拿自己的版本号和这里的最新 Release 比较, 有新版本就提示用户去 Release 页面下载。
param([string]$Notes = "")

$ErrorActionPreference = 'Stop'
Set-Location $PSScriptRoot

if (Get-Process PES2021, Settings -ErrorAction SilentlyContinue) { throw "游戏或它的设置程序正在运行, 先关掉再发布。" }
if (-not (Get-Command gh -ErrorAction SilentlyContinue)) { throw "没找到 GitHub CLI (gh), 先安装并 gh auth login。" }

[xml]$proj = Get-Content PesPadHub\PesPadHub.csproj
$version = $proj.Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "csproj 里没有 <Version>。" }
$tag = "v$version"
"版本: $tag"

if (git status --porcelain) { throw "还有未提交的改动, 先提交再发布。" }
if (git tag -l $tag) { throw "tag $tag 已存在; 先改 csproj 里的 <Version>。" }

# 震动插件 (C++), 没有 VS 编译环境时跳过, exe 里就不带插件
if (Test-Path PesPadHub\RumbleBridge\build.bat) {
    Push-Location PesPadHub\RumbleBridge
    try { cmd /c build.bat | Out-Host } finally { Pop-Location }
}

Get-Process 'PES 2021 Setting Pro' -ErrorAction SilentlyContinue | Stop-Process -Confirm:$false
Start-Sleep 2
dotnet publish PesPadHub\PesPadHub.csproj -c Release -o dist -nologo -v q
$exe = Get-Item 'dist\PES 2021 Setting Pro.exe'
"已发布: $($exe.FullName) ($([math]::Round($exe.Length / 1MB, 1)) MB)"

# 上传的文件名不带空格, 方便下载
$asset = "dist\PES2021SettingPro-$tag.exe"
Copy-Item $exe.FullName $asset -Force

git tag -a $tag -m "Release $tag"
git push origin HEAD
git push origin $tag

if (-not $Notes) { $Notes = "PES 2021 Setting Pro $tag" }
gh release create $tag $asset --title "PES 2021 Setting Pro $tag" --notes $Notes
Remove-Item $asset -Force
"完成: https://github.com/KOUFU-DIY/PES2021-Setting-Pro/releases/tag/$tag"
