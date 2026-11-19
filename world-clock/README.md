# WorldClock | 世界时钟

A borderless, flush-docked world clock for the Windows 10 / 11 taskbar written in Python (`tkinter` + Win32 APIs).

一个平嵌停靠在 Windows 10 / 11 系统任务栏上的多时区世界时钟，使用 Python（`tkinter` + Win32 API）编写。

---

## Features | 功能特性

- **Seamless Taskbar Blending | 任务栏无色差融合**：
  - Multi-point median color sampling around the clock area with lock-screen / hover-highlight rejection.
  - Row-by-row replication of the taskbar's top highlight/gradient border.
- **Per-Monitor DPI & Event-Driven Adaptation | 每显示器 DPI 感知与事件驱动**：
  - Listens to Windows display/DPI/theme/wallpaper/Explorer-restart broadcasts rather than heavy polling.
  - Automatically adapts font size, layout, and color on resolution or scaling changes (e.g., 150% ↔ 100%).
- **Two Modes | 双模式**：
  - **Docked Mode (default)**: Sits flush over the system clock without covering tray icons.
  - **Floating Mode**: Drag off the taskbar to turn into a rounded floating card; double-click or use the menu to dock back.
- **Fullscreen Auto-Hide | 全屏自动隐藏**：
  - Automatically hides during fullscreen video playback, presentations, or games without false triggers on normal maximized windows or desktop clicks.
- **Custom Win11-Style Context Menu | 自绘 Win11 风格右键菜单**：
  - Rounded corners, dark/light theme matching, keyboard navigation (`↑`/`↓`/`Enter`/`Esc`), timezone presets & fixed UTC offsets, 12/24h toggle, seconds display, autostart toggle, copy time, and one-click **Dock to Taskbar / Re-adapt Display**.

---

## Controls | 交互操作

| Action | Behavior |
|---|---|
| **Left Click (Single)** | Show Desktop (`Win + D`, click again to restore) |
| **Left Drag** | Detach from taskbar into floating card mode |
| **Double Click** | Temporarily turn transparent to view the underlying system clock |
| **Triple Click** | Open Windows system calendar |
| **Mouse Wheel** | Cycle through configured timezones (shows city toast briefly) |
| **Ctrl + Mouse Wheel** | Resize the clock |
| **Right Click** | Open custom Win11-style context menu |

---

## Usage | 使用方法

1. Install **Python 3.9+** (standard installer includes `tkinter`; optionally run `pip install tzdata` for full timezone database support).
2. Double-click `启动世界时钟.bat` to launch (single-instance enforced).
3. Double-click `重启世界时钟.bat` to gracefully stop any running instance and restart.
4. *(Optional)* Create an empty file named `diag_on` in the script directory to enable diagnostic logging to `worldclock_diag.log` (capped at 200 KB); delete `diag_on` to disable.

Settings are stored locally in `worldclock_config.json` (auto-created on first run; delete it to reset defaults).
