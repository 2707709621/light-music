# Light 系列 Windows 基础库

供 light music 和 light drive 共同引用。集中维护 WPF 菜单、图标、动画、关闭与焦点处理、HCT 配色、光效、C++ 窗口材质及本机视觉状态通信。应用的账号、业务缓存、播放与下载留在各自项目。两个 EXE 独立运行和发布。

FluentMenu.xaml 使用程序集 pack URI，不能改回宿主相对 URI。UI 类型需从宿主 WPF Dispatcher 调用。C++ DLL 为 x64 /MT，嵌入后按内容哈希提取。

代码从本地两项目提取，保留上游许可要求。light drive 分发继续遵守其 GPL-3.0 与 THIRD-PARTY-NOTICES。
