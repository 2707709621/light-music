# light music

轻量的 Windows 任务栏音乐播放器，使用 C# / WPF 与 C++ 开发。日常播放、歌曲搜索、收藏和歌单管理均可从任务栏组件的右键菜单进入。

当前版本 **0.15.16**。

## 下载

前往 [Releases](https://github.com/2707709621/light-music/releases/latest) 下载 Windows x64 版本。ZIP 包包含 EXE 与许可文件，运行环境见下文。

## 功能

- 支持 QQ 音乐、网易云音乐、酷狗音乐账号与音乐源切换。
- 读取我喜欢、歌单及平台推荐，支持搜索、收藏、取消收藏和编辑可写歌单。
- 支持音质选择、音量、点击与拖动进度条，以及列表循环、单曲循环、随机播放。
- 退出时保存歌曲、队列和进度，下次启动恢复到暂停状态。
- 任务栏内显示封面、播放按钮和同步歌词，可调整布局、宽度与对齐方式。
- 原有歌词与流光歌词可切换。流光支持随机排布、逐字放大、渐显、旋转、辉光、整句与逐字晃动。
- 菜单支持深浅色、磨砂透明材质、封面莫奈取色和流动光效。
- 可选任务栏材质联动与任务栏上方音频频谱。
- 支持自定义全局快捷键与开机自启动。

单曲循环模式下，点击上一首或下一首会从头重播当前歌曲。

## 运行环境

- Windows 11 x64。任务栏嵌入与外观联动依赖系统窗口实现，不同 Windows 版本和任务栏改装工具可能影响兼容性。
- .NET 10 Desktop Runtime x64。
- Microsoft Edge WebView2 Runtime，用于需要官方网站登录的音乐源。

安装运行时可访问微软官方网站。

- [.NET 10 下载](https://dotnet.microsoft.com/zh-cn/download/dotnet/10.0)
- [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/)

运行程序后，在任务栏歌词、播放组件或通知区域图标上右键展开菜单。选择“音乐源”，再从“账号”完成对应平台登录。

## 默认快捷键

| 操作 | 快捷键 |
| --- | --- |
| 播放 / 暂停 | Ctrl + Alt + 空格 |
| 下一首 | Ctrl + Alt + → |
| 上一首 | Ctrl + Alt + ← |

收藏、音量、静音、显示菜单和切换播放模式可在“设置 → 快捷键设置”中自行绑定。

## 从源码构建

需要 Windows x64、.NET 10 SDK，以及 Visual Studio 2022 或更新版本的“使用 C++ 的桌面开发”工作负载。原生模块使用 MSVC、C++20 和 Windows SDK，开发环境使用 Windows SDK 10.0.26100。

克隆仓库后运行以下命令。

```powershell
git clone https://github.com/2707709621/light-music.git
cd light-music
powershell -ExecutionPolicy Bypass -File .\build.ps1
```

输出位于 `artifacts/publish/`。默认生成依赖 .NET 共享运行时的 x64 单文件 EXE。两个原生 DLL 会从仓库内的 C++ 源码构建并嵌入程序。

也可以直接构建开发版本。

```powershell
dotnet build .\FeatherPlayer\FeatherPlayer.csproj -c Release
```

## 项目结构

```text
FeatherPlayer/          播放器、音乐源、歌词、账号与任务栏组件
  Native/              任务栏材质与音频频谱的 C++ 模块
LightSuite.Windows/    共用 WPF 菜单、图标、主题、光效与窗口材质
  Native/              原生菜单材质模块
licenses/              依赖组件的许可文件
build.ps1              Windows 构建与发布入口
CHANGELOG.md           0.12.6 至 0.15.16 的累计更新说明
```

任务栏播放组件通过 Win32 子窗口挂接。可选的任务栏材质联动通过 Windows XAML Diagnostics 接入 Explorer，关闭联动后可恢复系统外观。频谱使用 WASAPI 进程回环采集本程序的播放音频。

## 本地数据

设置、歌单缓存、播放进度和诊断记录位于 `%LOCALAPPDATA%\FeatherPlayer`。登录凭据使用当前 Windows 用户的 DPAPI 加密。各音乐源分别保存会话，临时播放地址按需获取。

在线服务的登录、会员、音质、版权及地区权限由对应平台决定。社区接口可能发生变动，遇到问题可在 Issues 中提供版本、系统版本、音乐源与错误码，并删除日志中的个人信息后再上传。

## 当前已知问题

- 个别酷狗收藏歌单的摘要曲目数量与歌曲接口实际返回数量存在差异。
- 任务栏材质、嵌入、频谱和歌词动效需要在具体 Windows 环境中确认实际效果。
- 本项目与各音乐平台无隶属关系。

## 开源许可与致谢

本项目使用 GPL-3.0 许可，完整条款见 [LICENSE](LICENSE)。第三方组件保留各自许可与署名，详见 [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md)。

感谢 QQMusicApi、KuGouMusicApi、TaskbarLyrics、TranslucentTB、Folia、wpfsvl、FreqFreak、Microsoft Windows samples 以及相关社区项目提供的协议资料、实现参考和视觉启发。

Copyright (c) 2026 2707709621 and contributors.
