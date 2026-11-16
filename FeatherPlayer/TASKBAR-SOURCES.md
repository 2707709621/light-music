# 任务栏嵌入实现参考

任务栏播放组件独立实现自有 WPF 子窗口的挂接、布局检测及播放控件。可选的任务栏材质联动由 `Native/TaskbarStyle.cpp` 实现，通过 Windows XAML Diagnostics 将原生模块接入 Explorer，参考 TranslucentTB 的相关实现。

参考 ChissaQAQ / TaskbarLyrics（MIT）：

- https://github.com/ChissaQAQ/TaskbarLyrics/blob/main/wpf/TaskbarLyrics/NativeMethods.cs ：`Shell_TrayWnd` + `SetParent` / `WS_CHILD`，只改自有窗口。
- https://github.com/ChissaQAQ/TaskbarLyrics/blob/main/wpf/TaskbarLyrics/TaskbarFreeSpace.cs ：UI Automation 在后台 MTA 线程测量图标占用区间；跳过 Windows 11 的遗留任务栏容器；按静止时间退避扫描。
- https://github.com/ChissaQAQ/TaskbarLyrics/blob/main/LICENSE ：上游 MIT 许可。

本实现只支持主屏横向任务栏；多屏时不会在副屏重复显示。任务栏空位不足、布局结果失效或当前系统阻止跨进程挂接时，歌词条会隐藏，主播放器继续可用。真正挂为任务栏子 HWND 的机制属于兼容性实现，微软未保证 Windows 11 后续版本始终支持。没有悬浮窗回退冒充嵌入。

布局查询采用任务栏 WinEvent 事件触发、150ms 合并、静止时最多 60 秒扫描一次。同步 UIA 调用只在后台执行；如 Explorer 不响应则歌词条在旧快照超过 70 秒后隐藏。后台线程为 background，不会阻止应用退出。

事件处理显式排除自身 HWND 及子项。父任务栏的 `EVENT_OBJECT_REORDER` 只触发复测，不触发隐藏，避免自有窗口显示/隐藏引起的父级重排事件形成循环。任务栏仅改变屏幕位置、客户端尺寸不变时，子窗口随父窗口移动。

`TaskbarWidget.GetDiagnostics()` 只读返回父子句柄、`WS_CHILD` 标志、可见性、屏幕矩形、任务栏客户端尺寸、DPI、布局快照时间及事件计数，可用于验证嵌入和排查兼容性；不包含账户或播放凭据。
