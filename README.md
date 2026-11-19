# Win Taskbar Tools | Windows 任务栏效率工具合集

A lightweight, dependency-minimal collection of Windows 10 / 11 taskbar enhancement utilities designed to feel native and stay out of your way.

一套轻量、几乎零外部依赖的 Windows 10 / 11 任务栏效率小工具合集，追求原生融合观感与低资源占用。

---

## Included Tools | 包含工具

| Tool | Language / Stack | Highlights | Docs |
|---|---|---|---|
| **WorldClock (`世界时钟`)** | Python 3.9+ / Tkinter / Win32 API | Seamless flush-docked taskbar world clock with live taskbar color & top-highlight sampling, Per-Monitor DPI awareness, event-driven display/theme adaptation, fullscreen auto-hide, multi-timezone scroll switching, and one-click Show Desktop. | [world-clock/README.md](world-clock/README.md) |
| **DockFolder (`任务栏文件夹`)** | C# / WPF (.NET Framework 4.x) | Brings macOS Dock Stacks (Fan & Grid animations) and iOS-style app folders to the Windows taskbar. Compiles directly using Windows' built-in `csc.exe` with zero extra SDKs required. | [dock-folder/README.md](dock-folder/README.md) |

---

## Quick Start | 快速开始

### 1. WorldClock (`world-clock/`)
- **Requirements**: Windows 10 / 11 + Python 3.9+ (with built-in `tkinter`; optional `pip install tzdata` for full IANA timezone support).
- **Run**: Double-click `world-clock/启动世界时钟.bat` (or run `pythonw 世界时钟.pyw`).
- **Restart**: Double-click `world-clock/重启世界时钟.bat` after editing or to cleanly reload.

### 2. DockFolder (`dock-folder/`)
- **Requirements**: Windows 10 / 11 (uses the built-in .NET Framework 4.x C# compiler—no Visual Studio or .NET SDK needed).
- **Build & Run**: Double-click `dock-folder/build.bat` to compile `DockFolder.exe` and open the manager window.
- **Pin to Taskbar**: Create or customize a folder in the manager window, click **Pin to Taskbar**, then pin the generated desktop shortcut to your Windows taskbar.

---

## Repository Layout | 目录结构

```text
win-taskbar-tools/
├── README.md
├── LICENSE
├── .gitignore
├── world-clock/                 # Seamless taskbar world clock (Python / Tkinter)
│   ├── README.md
│   ├── 世界时钟.pyw
│   ├── 启动世界时钟.bat
│   ├── 重启世界时钟.bat
│   ├── worldclock.ico
│   └── worldclock_icon_preview.png
└── dock-folder/                 # macOS Dock-style taskbar folders (C# / WPF)
    ├── README.md
    ├── build.bat
    └── src/
        ├── AppUI.cs
        ├── FolderIcon.cs
        ├── GroupStore.cs
        ├── Host.cs
        ├── JumpLists.cs
        ├── MacMenu.cs
        ├── ManagerWindow.cs
        ├── Native.cs
        ├── PopupWindow.cs
        ├── Program.cs
        ├── Shell.cs
        ├── TaskbarMenu.cs
        ├── app.ico
        └── app.manifest
```

---

## License

MIT License. See [LICENSE](LICENSE) for details.
