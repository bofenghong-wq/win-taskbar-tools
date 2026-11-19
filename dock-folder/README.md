# DockFolder | 任务栏文件夹

Bring macOS Dock **Stacks** (Fan & Grid pop-out animations) and iOS-style app folders to the Windows 10 / 11 taskbar. Built with C# and WPF on .NET Framework 4.x—compiles directly using Windows' built-in compiler with **zero external dependencies**.

把多个软件快捷方式收纳进 Windows 任务栏的一个按钮里，点击即以 macOS Dock 扇形/网格动效展开，点击外部或按 `Esc` 自动收起。纯 C# / WPF 实现，直接调用 Windows 自带的 .NET 编译器即可编译，**无需安装额外 SDK**。

---

## Features | 功能特性

- **Two Pop-Out Styles | 两种展开动效**：
  - **Fan (`扇形`)**: Icons spring out along a curved arc with labels on the side (ideal for ≤ 10 items on a bottom taskbar).
  - **Grid (`网格`)**: Elastic rounded grid panel with mouse-wheel scrolling for larger collections.
  - **Auto (`自动`)**: Uses Fan for ≤ 10 items and Grid otherwise.
- **macOS-Inspired Context Menu | 仿 macOS 堆栈右键菜单**：
  - Right-click the pinned taskbar button or popped-out items to switch **Sort By** (Name / Date Added / Kind / Manual), **Display As** (Fan / Grid / Auto), open in Explorer, or launch the manager.
  - Hold `Shift` + Right-click on the taskbar button to access the native Windows taskbar menu (e.g., to unpin).
- **Clean Manager UI | 统一管理窗口**：
  - Drag and drop `.exe`, shortcuts, or folders directly into any group; drag icons to reorder (automatically switches to Manual sort).
  - Live **Preview Animation** button before pinning.
  - Auto-generates a 3×3 mini-grid folder thumbnail icon whenever group contents change.
- **Multiple Independent Folders | 支持多个独立文件夹**：
  - Each group lives as a normal directory under `Groups/<FolderName>/` and can be pinned independently to the taskbar.

---

## Build & Quick Start | 编译与使用

1. Double-click `build.bat`. It invokes Windows' built-in `csc.exe` (`.NET Framework 4.x`) to compile `DockFolder.exe` and opens the manager window automatically.
2. Add apps or shortcuts into a folder (drag-and-drop or use **Add**).
3. Click **Pin to Taskbar** (`固定到任务栏`) to generate a desktop shortcut with a live grid thumbnail icon.
4. Right-click the desktop shortcut → **Pin to taskbar** (on Windows 11, click *Show more options* first). Afterward, you can delete the desktop shortcut.
5. Enable **Launch at startup** (`开机自动启动`) in the manager window or tray menu so the first click responds instantaneously.

---

## Architecture | 源码结构

- `src/Program.cs` & `src/Host.cs` — Single-instance IPC host, tray icon, and taskbar button hook.
- `src/PopupWindow.cs` — Fan & Grid spring animations and keyboard/mouse interaction.
- `src/ManagerWindow.cs` & `src/AppUI.cs` — Folder manager window, dialogs, and UI theme.
- `src/MacMenu.cs` & `src/TaskbarMenu.cs` — Custom translucent context menus & taskbar right-click interception.
- `src/GroupStore.cs` & `src/FolderIcon.cs` — Group persistence (`Groups/`) and dynamic 3×3 thumbnail icon generation.
- `src/Shell.cs`, `src/JumpLists.cs`, `src/Native.cs` — Win32 P/Invoke, shell icon extraction, and shortcut helpers.
