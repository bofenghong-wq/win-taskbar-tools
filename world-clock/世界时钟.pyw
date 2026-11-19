#!/usr/bin/env python3
# -*- coding: utf-8 -*-
"""
世界时钟 WorldClock —— 停靠在系统任务栏上的时钟（可脱离为悬浮卡片）

模式:
  · 停靠模式（默认）: 平嵌在任务栏时钟位置, 无边框融入任务栏,
    自动跟随任务栏位置/尺寸/主题(浅色深色)/自动隐藏;
    界面只显示时间与日期。
  · 悬浮模式: 拖动脱离停靠, 变为 iOS 卡片自由悬浮; 双击回到停靠。

功能:
  · 全屏自动隐藏: 放映 PPT / 全屏播放视频（含浏览器流媒体网页全屏）时自动隐藏, 退出后恢复
  · 时区选择、位置、模式等自动保存, 下次启动恢复
  · 单实例保护; 开机自启动（右键菜单开关）

操作:
  单击          显示桌面（等同 Win+D, 再点一次恢复窗口）
  左键拖动      脱离停靠并移动
  双击          原地变全透明（位置不变, 直接看到系统时钟; 光标移开或超时自动恢复）
  三击          弹出系统日历（Win11 通知中心/日历）
  滚轮          切换时区（切换瞬间提示城市名）
  Ctrl+滚轮     调整窗口大小
  右键          打开菜单（Win11 风格自绘菜单, 跟随任务栏深浅色）

运行: 双击 启动世界时钟.bat 或桌面快捷方式「世界时钟」
"""
import ctypes
import json
import math
import os
import shutil
import subprocess
import sys
import time as _time
from datetime import datetime, timedelta, timezone

try:
    from zoneinfo import ZoneInfo
except ImportError:
    ZoneInfo = None

import tkinter as tk
from tkinter import font as tkfont
from tkinter import messagebox

def _set_dpi_awareness():
    """按「每显示器 DPI 感知 V2」运行: 所有坐标都是真实像素。
    旧版用「系统 DPI 感知」, 运行中修改显示缩放(如 150%→100%)后坐标被系统换算,
    取色点落到错误位置, 时钟变成黑块。"""
    try:
        if ctypes.windll.user32.SetProcessDpiAwarenessContext(ctypes.c_void_p(-4)):
            return
    except Exception:
        pass
    for v in (2, 1):
        try:
            ctypes.windll.shcore.SetProcessDpiAwareness(v)
            return
        except Exception:
            pass


_set_dpi_awareness()
try:
    import ctypes.wintypes
except Exception:
    ctypes.wintypes = None

APP_DIR = os.path.dirname(os.path.abspath(__file__))
CONFIG_PATH = os.path.join(APP_DIR, "worldclock_config.json")
RUN_KEY = r"HKCU\Software\Microsoft\Windows\CurrentVersion\Run"
RUN_NAME = "WorldClock"

# 时区列表: (显示名, 时区)。None=系统本地时间, 字符串=IANA 时区, 整数=固定 UTC 偏移小时
ZONES = [
    ("本地时间", None),
    ("北京/上海", "Asia/Shanghai"),
    ("香港", "Asia/Hong_Kong"),
    ("新加坡", "Asia/Singapore"),
    ("东京", "Asia/Tokyo"),
    ("首尔", "Asia/Seoul"),
    ("曼谷", "Asia/Bangkok"),
    ("迪拜", "Asia/Dubai"),
    ("莫斯科", "Europe/Moscow"),
    ("柏林", "Europe/Berlin"),
    ("巴黎", "Europe/Paris"),
    ("伦敦", "Europe/London"),
    ("纽约", "America/New_York"),
    ("芝加哥", "America/Chicago"),
    ("丹佛", "America/Denver"),
    ("洛杉矶", "America/Los_Angeles"),
    ("悉尼", "Australia/Sydney"),
    ("奥克兰", "Pacific/Auckland"),
    ("夏威夷", "Pacific/Honolulu"),
    ("UTC", "UTC"),
]
ZONES += [(f"UTC{h:+d}", h) for h in range(-12, 15) if h != 0]

WEEK_CN = ["周一", "周二", "周三", "周四", "周五", "周六", "周日"]

# 未安装 tzdata 时的兜底偏移（标准时, 不含夏令时）
FALLBACK_OFFSET = {
    "Asia/Shanghai": 8, "Asia/Hong_Kong": 8, "Asia/Singapore": 8, "Asia/Tokyo": 9,
    "Asia/Seoul": 9, "Asia/Bangkok": 7, "Asia/Dubai": 4, "Europe/Moscow": 3,
    "Europe/Berlin": 1, "Europe/Paris": 1, "Europe/London": 0, "America/New_York": -5,
    "America/Chicago": -6, "America/Denver": -7, "America/Los_Angeles": -8,
    "Australia/Sydney": 10, "Pacific/Auckland": 12, "Pacific/Honolulu": -10, "UTC": 0,
}

# ------- 配色 -------
KEY = "#010203"          # 透明色键（悬浮卡片圆角以外区域完全透明）
BG_TOP = "#222226"
BG_BOT = "#17171b"
C_BORDER = "#44444c"
C_TIME = "#f5f5f7"
C_SEC = "#8e8e93"
C_DATE = "#98989f"
C_TOAST = "#c7c7cc"
PAD = 6

# 停靠模式不透明度。取色准确后用完全不透明: 0.95 会让下面的系统时钟文字和
# 底色混进来, 造成发灰/发白的色块
DOCK_ALPHA = 1.0
# 底色变化小于该值(各通道)时不重绘, 避免亚克力噪点导致闪烁
COLOR_EPS = 3
# 交互宽限期: 用户操作后 N 秒内不执行全屏隐藏
GRACE = 8.0
# 连击: 位移超过该像素才算拖动（连击时的手抖不触发"脱离停靠"）
DRAG_SLOP = 4
# 双击全透明的最长保持秒数, 到时自动恢复（透明期间鼠标可穿过窗口）
TRANSPARENT_HOLD = 6.0

# 系统外壳类窗口: 桌面/任务栏/悬浮层, 即使占满屏幕也不算全屏
EXCLUDE_CLASSES = ("Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd", "CEF-OSC-WIDGET")
# 系统外壳进程: 开始菜单/搜索/输入法/资源管理器等, 不算全屏应用
SHELL_PROCS = {
    "explorer.exe", "searchhost.exe", "startmenuexperiencehost.exe",
    "shellexperiencehost.exe", "textinputhost.exe", "shellruntime.exe",
    "widgets.exe", "widgetservice.exe", "nvidia share.exe", "nvidiaspawns.exe",
}
# 浏览器主窗口: 网页视频全屏常用 HTML5 Fullscreen, 窗口往往仍带 WS_MAXIMIZE
BROWSER_CLASSES = {
    "chrome_widgetwin_0", "chrome_widgetwin_1", "chrome_widgetwin_2",
    "mozillawindowclass", "ieframe",
}


def mix(c1, c2, t):
    """两种 #rrggbb 颜色按 t(0~1) 线性插值"""
    t = max(0.0, min(1.0, t))
    r = int(int(c1[1:3], 16) + (int(c2[1:3], 16) - int(c1[1:3], 16)) * t)
    g = int(int(c1[3:5], 16) + (int(c2[3:5], 16) - int(c1[3:5], 16)) * t)
    b = int(int(c1[5:7], 16) + (int(c2[5:7], 16) - int(c1[5:7], 16)) * t)
    return f"#{r:02x}{g:02x}{b:02x}"


def rgb_str(c):
    return f"#{c[0]:02x}{c[1]:02x}{c[2]:02x}"


def rrect_pts(x0, y0, x1, y1, r):
    """圆角矩形的多边形顶点（顺时针）"""
    r = max(0.0, min(r, (x1 - x0) / 2, (y1 - y0) / 2))
    n = 10
    pts = []

    def arc(cx, cy, a0, a1):
        for i in range(n + 1):
            a = math.radians(a0 + (a1 - a0) * i / n)
            pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))

    pts.append((x0 + r, y0))
    arc(x1 - r, y0 + r, -90, 0)
    pts.append((x1, y1 - r))
    arc(x1 - r, y1 - r, 0, 90)
    pts.append((x0 + r, y1))
    arc(x0 + r, y1 - r, 90, 180)
    pts.append((x0, y0 + r))
    arc(x0 + r, y0 + r, 180, 270)
    return pts


def cutout_pts(cx, cy, r, a0, a1, px, py):
    """圆角矩形角落的裁切多边形（填充透明色, 把渐变矩形切成圆角）"""
    pts = [(px, py)]
    n = 10
    for i in range(n + 1):
        a = math.radians(a0 + (a1 - a0) * i / n)
        pts.append((cx + r * math.cos(a), cy + r * math.sin(a)))
    pts.append((px, py))
    return pts


class MONITORINFO(ctypes.Structure):
    _fields_ = [("cbSize", ctypes.wintypes.DWORD),
                ("rcMonitor", ctypes.wintypes.RECT),
                ("rcWork", ctypes.wintypes.RECT),
                ("dwFlags", ctypes.wintypes.DWORD)]


# ======================================================================
#  自绘右键菜单（Win11 Fluent 风格）
# ======================================================================
# Segoe Fluent Icons(Win11) / Segoe MDL2 Assets(Win10) 共用码位
GL = {
    "desktop": "", "calendar": "", "eye": "", "pin": "",
    "globe": "", "offset": "", "sec": "", "clock": "",
    "power": "", "copy": "", "settings": "", "help": "",
    "exit": "", "reset": "", "zoom": "", "check": "", "chev": "",
}

MENU_THEMES = {
    "dark": dict(bg="#2c2c2c", border="#454545", hover="#3b3b3b", press="#353535",
                 text="#ffffff", sub="#a0a0a0", dis="#6e6e6e", sep="#404040"),
    "light": dict(bg="#f9f9f9", border="#d6d6d6", hover="#ececec", press="#e3e3e3",
                  text="#1b1b1b", sub="#6b6b6b", dis="#a8a8a8", sep="#e3e3e3"),
}


def accent_color(dark):
    """系统强调色（HKCU\\...\\DWM\\AccentColor, ABGR）; 深色主题下提亮以保证对比度"""
    c = None
    try:
        import winreg
        k = winreg.OpenKey(winreg.HKEY_CURRENT_USER, r"Software\Microsoft\Windows\DWM")
        v, _ = winreg.QueryValueEx(k, "AccentColor")
        winreg.CloseKey(k)
        c = f"#{v & 0xFF:02x}{(v >> 8) & 0xFF:02x}{(v >> 16) & 0xFF:02x}"
    except Exception:
        pass
    if c is None:
        return "#60cdff" if dark else "#005fb8"
    return mix(c, "#ffffff", 0.45) if dark else mix(c, "#000000", 0.15)


def monitor_work_area(x, y):
    """(x,y) 所在显示器的工作区 (l, t, r, b); 取不到时退回整屏"""
    try:
        pt = ctypes.wintypes.POINT(int(x), int(y))
        mon = ctypes.windll.user32.MonitorFromPoint(pt, 2)
        info = MONITORINFO()
        info.cbSize = ctypes.sizeof(MONITORINFO)
        if ctypes.windll.user32.GetMonitorInfoW(mon, ctypes.byref(info)):
            w = info.rcWork
            return w.left, w.top, w.right, w.bottom
    except Exception:
        pass
    return None


def monitor_rect_of(r):
    """矩形 r 所在(最近)显示器的整屏矩形 (l, t, r, b)"""
    try:
        mon = ctypes.windll.user32.MonitorFromRect(ctypes.byref(r), 2)   # MONITOR_DEFAULTTONEAREST
        info = MONITORINFO()
        info.cbSize = ctypes.sizeof(MONITORINFO)
        if mon and ctypes.windll.user32.GetMonitorInfoW(mon, ctypes.byref(info)):
            m = info.rcMonitor
            return m.left, m.top, m.right, m.bottom
    except Exception:
        pass
    return None


def virtual_screen():
    """所有显示器拼成的虚拟屏幕 (l, t, r, b), 实时读取"""
    try:
        g = ctypes.windll.user32.GetSystemMetrics
        x, y, w, h = g(76), g(77), g(78), g(79)
        if w > 0 and h > 0:
            return x, y, x + w, y + h
    except Exception:
        pass
    return None


class FluentMenu:
    """Win11 风格弹出菜单: 圆角、跟随深浅色、图标列、悬停圆角高亮、
    勾选/单选强调色、级联子菜单、键盘导航、淡入动画。整块用一个 Canvas 自绘。

    items 中每项为 dict:
      kind   "cmd" | "check" | "radio" | "sub" | "sep" | "header"
      text   主文字;  sub(header 第二行) / hint(右侧灰字)
      icon   GL 中的字形;  checked: bool;  enabled: bool (默认 True)
      command 回调;  items: 子菜单项（kind="sub"）
    """

    def __init__(self, owner, items, x, y, dark, parent=None, anchor=None):
        self.owner = owner
        self.root = owner.root
        self.items = items
        self.dark = dark
        self.parent = parent
        self.child = None
        self.child_idx = None
        self.hover = None
        self.pressed = None
        self.closed = False
        self.scroll = 0
        self._sub_job = None
        self._fade_job = None
        self.th = MENU_THEMES["dark" if dark else "light"]
        self.accent = accent_color(dark)

        try:
            s = max(1.0, (owner._dpi or self.root.winfo_fpixels("1i") * 1.0) / 96.0)
        except Exception:
            s = 1.0
        self.s = s
        fam = set(tkfont.families(self.root))
        ui = next((f for f in ("Segoe UI Variable Text", "Microsoft YaHei UI", "Segoe UI",
                               "Microsoft YaHei", "TkDefaultFont") if f in fam), "TkDefaultFont")
        ic = next((f for f in ("Segoe Fluent Icons", "Segoe MDL2 Assets") if f in fam), None)
        cjk = owner.f_cjk
        px = owner._px
        self.f_text = tkfont.Font(root=self.root, family=cjk if cjk != "TkDefaultFont" else ui, size=px(9))
        self.f_hint = tkfont.Font(root=self.root, family=cjk if cjk != "TkDefaultFont" else ui, size=px(8))
        self.f_head = tkfont.Font(root=self.root, family=cjk if cjk != "TkDefaultFont" else ui,
                                  size=px(11), weight="bold")
        self.f_icon = tkfont.Font(root=self.root, family=ic, size=px(10)) if ic else None
        self.f_icon_s = tkfont.Font(root=self.root, family=ic, size=px(8)) if ic else None

        self._layout()

        t = self.top = tk.Toplevel(self.root)
        t.withdraw()
        t.overrideredirect(True)
        t.attributes("-topmost", True)
        try:
            t.attributes("-alpha", 0.0)
        except Exception:
            pass
        t.configure(bg=self.th["bg"])
        self.cv = tk.Canvas(t, width=self.W, height=self.vh, bg=self.th["bg"],
                            highlightthickness=0, bd=0)
        self.cv.pack(fill="both", expand=True)

        # 位置: 默认在光标上方/右侧（任务栏在底部时菜单向上展开）, 夹在显示器工作区内
        wa = monitor_work_area(x, y) or (0, 0, self.root.winfo_screenwidth(), self.root.winfo_screenheight())
        if anchor is not None:                       # 子菜单: 贴着父菜单项右边展开
            ax0, ay0, ax1 = anchor
            px = ax1 - int(2 * s)
            if px + self.W > wa[2]:
                px = ax0 - self.W + int(2 * s)
            py = ay0 - self.pad
        else:
            px = x
            py = y - self.vh if y - self.vh >= wa[1] else y
            if px + self.W > wa[2]:
                px = x - self.W
        px = max(wa[0], min(px, wa[2] - self.W))
        py = max(wa[1], min(py, wa[3] - self.vh))
        self.x, self.y = px, py
        t.geometry(f"{self.W}x{self.vh}+{px}+{py}")

        self.draw()
        cv = self.cv
        cv.bind("<Motion>", self._on_motion)
        cv.bind("<Leave>", self._on_leave)
        cv.bind("<ButtonPress-1>", self._on_press)
        cv.bind("<ButtonRelease-1>", self._on_release)
        cv.bind("<MouseWheel>", self._on_wheel)
        t.bind("<Escape>", lambda e: self.close_all())
        t.bind("<Up>", lambda e: self._key_move(-1))
        t.bind("<Down>", lambda e: self._key_move(1))
        t.bind("<Return>", lambda e: self._activate(self.hover))
        t.bind("<Right>", lambda e: self._key_right())
        t.bind("<Left>", lambda e: self._key_left())

        t.deiconify()
        t.lift()
        self._win11_round()
        try:
            t.focus_force()
        except Exception:
            pass
        self._fade(0)

    # ---------- 布局 ----------
    def _layout(self):
        s = self.s
        self.pad = int(4 * s)
        self.row_h = int(30 * s)
        self.sep_h = int(9 * s)
        self.head_h = int(50 * s)
        self.icon_w = int(36 * s) if self.f_icon else int(12 * s)
        text_w = hint_w = 0
        has_right = False
        for it in self.items:
            k = it.get("kind", "cmd")
            if k == "header":
                text_w = max(text_w, self.f_head.measure(it["text"]) - self.icon_w + int(14 * s),
                              self.f_hint.measure(it.get("sub", "")) - self.icon_w + int(14 * s))
            elif k != "sep":
                text_w = max(text_w, self.f_text.measure(it["text"]))
                if it.get("hint"):
                    hint_w = max(hint_w, self.f_hint.measure(it["hint"]))
                if k == "sub":
                    has_right = True
        right_w = (hint_w + int(16 * s) if hint_w else 0) + (int(20 * s) if has_right else 0)
        self.W = int(self.pad * 2 + self.icon_w + text_w + int(28 * s) + right_w)
        self.W = max(self.W, int(200 * s))
        self.rows = []
        y = self.pad
        for it in self.items:
            k = it.get("kind", "cmd")
            h = self.sep_h if k == "sep" else self.head_h if k == "header" else self.row_h
            self.rows.append((y, y + h))
            y += h
        self.full_h = y + self.pad
        # 太长（如 UTC 偏移子菜单）时限高, 用滚轮滚动
        try:
            sh = self.root.winfo_screenheight()
        except Exception:
            sh = 1080
        self.vh = min(self.full_h, int(sh * 0.8))

    def _win11_round(self):
        """Win11: DWM 圆角 + 边框色; Win10 上调用失败则保留自绘直角边框"""
        self.rounded = False
        try:
            hwnd = ctypes.windll.user32.GetParent(self.top.winfo_id()) or self.top.winfo_id()
            pref = ctypes.c_int(2)                                   # DWMWCP_ROUND
            hr = ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 33, ctypes.byref(pref), 4)
            if hr == 0:
                b = self.th["border"]
                col = ctypes.c_uint32(int(b[1:3], 16) | (int(b[3:5], 16) << 8) | (int(b[5:7], 16) << 16))
                ctypes.windll.dwmapi.DwmSetWindowAttribute(hwnd, 34, ctypes.byref(col), 4)
                self.rounded = True
                self.draw()
        except Exception:
            pass

    def _fade(self, step):
        if self.closed:
            return
        a = min(1.0, (step + 1) / 6.0)
        try:
            self.top.attributes("-alpha", a)
        except Exception:
            return
        if a < 1.0:
            self._fade_job = self.root.after(14, lambda: self._fade(step + 1))

    # ---------- 绘制 ----------
    def draw(self):
        cv, th, s = self.cv, self.th, self.s
        cv.delete("all")
        if not getattr(self, "rounded", False):
            cv.create_rectangle(0, 0, self.W - 1, self.vh - 1, outline=th["border"], fill=th["bg"])
        x_icon = self.pad + self.icon_w // 2
        x_text = self.pad + self.icon_w
        x_right = self.W - self.pad - int(10 * s)
        for i, it in enumerate(self.items):
            y0, y1 = self.rows[i]
            y0 -= self.scroll
            y1 -= self.scroll
            if y1 < 0 or y0 > self.vh:
                continue
            k = it.get("kind", "cmd")
            ym = (y0 + y1) / 2
            if k == "sep":
                cv.create_line(self.pad + int(8 * s), ym, self.W - self.pad - int(8 * s), ym, fill=th["sep"])
                continue
            if k == "header":
                cv.create_text(self.pad + int(14 * s), y0 + int(8 * s), text=it["text"], anchor="nw",
                               font=self.f_head, fill=th["text"])
                cv.create_text(self.pad + int(14 * s), y1 - int(8 * s), text=it.get("sub", ""), anchor="sw",
                               font=self.f_hint, fill=th["sub"])
                continue
            en = it.get("enabled", True)
            hot = (i == self.hover or i == self.child_idx) and en
            if hot:
                fill = th["press"] if i == self.pressed else th["hover"]
                cv.create_polygon(rrect_pts(self.pad, y0 + 1, self.W - self.pad, y1 - 1, 4 * s),
                                  fill=fill, outline="", smooth=True)
            fg = th["text"] if en else th["dis"]
            checked = it.get("checked") and k in ("check", "radio")
            if checked:
                # Win11 选中指示: 左侧强调色短竖条 + 对勾
                cv.create_polygon(rrect_pts(self.pad + 1, ym - 8 * s, self.pad + 1 + 3 * s, ym + 8 * s, 1.5 * s),
                                  fill=self.accent, outline="", smooth=True)
            glyph = GL["check"] if checked else it.get("icon")
            if glyph and self.f_icon:
                cv.create_text(x_icon, ym, text=glyph, font=self.f_icon,
                               fill=self.accent if checked and en else fg)
            cv.create_text(x_text, ym, text=it["text"], anchor="w", font=self.f_text, fill=fg)
            rx = x_right
            if k == "sub":
                if self.f_icon_s:
                    cv.create_text(rx, ym, text=GL["chev"], font=self.f_icon_s, fill=th["sub"], anchor="e")
                else:
                    cv.create_text(rx, ym, text="›", font=self.f_text, fill=th["sub"], anchor="e")
                rx -= int(20 * s)
            if it.get("hint"):
                cv.create_text(rx, ym, text=it["hint"], anchor="e", font=self.f_hint,
                               fill=th["sub"] if en else th["dis"])
        if self.full_h > self.vh:                    # 滚动提示条
            frac0 = self.scroll / self.full_h
            frac1 = (self.scroll + self.vh) / self.full_h
            cv.create_line(self.W - 3 * s, self.vh * frac0 + 4 * s, self.W - 3 * s, self.vh * frac1 - 4 * s,
                           fill=th["sub"], width=max(2, int(2 * s)), capstyle="round")

    # ---------- 交互 ----------
    def _row_at(self, y):
        y += self.scroll
        for i, (y0, y1) in enumerate(self.rows):
            if y0 <= y < y1:
                k = self.items[i].get("kind", "cmd")
                if k in ("sep", "header") or not self.items[i].get("enabled", True):
                    return None
                return i
        return None

    def _set_hover(self, i):
        if i == self.hover:
            return
        self.hover = i
        self.draw()
        if self._sub_job:
            self.root.after_cancel(self._sub_job)
            self._sub_job = None
        if i is not None and self.items[i].get("kind") == "sub":
            if self.child_idx != i:
                self._sub_job = self.root.after(220, lambda: self._open_child(i))
        elif self.child is not None and i is not None:
            self._sub_job = self.root.after(260, self._close_child)

    def _on_motion(self, e):
        self.owner._interact_at = _time.monotonic()
        self._set_hover(self._row_at(e.y))

    def _on_leave(self, e):
        if self.child is None:
            self.pressed = None
            self._set_hover(None)

    def _on_press(self, e):
        self.pressed = self._row_at(e.y)
        self.draw()

    def _on_release(self, e):
        i = self._row_at(e.y)
        p, self.pressed = self.pressed, None
        if i is not None and i == p:
            self._activate(i)
        else:
            self.draw()

    def _on_wheel(self, e):
        if self.full_h <= self.vh:
            return
        step = int(self.row_h * (-1 if e.delta > 0 else 1) * 2)
        self.scroll = max(0, min(self.full_h - self.vh, self.scroll + step))
        self.hover = self._row_at(e.y)
        self.draw()

    def _activate(self, i):
        if i is None:
            return
        it = self.items[i]
        if not it.get("enabled", True):
            return
        if it.get("kind") == "sub":
            self._open_child(i)
            return
        cmd = it.get("command")
        self.close_all()
        if cmd:
            self.root.after(10, cmd)

    def _key_move(self, d):
        idx = [i for i, it in enumerate(self.items)
               if it.get("kind", "cmd") not in ("sep", "header") and it.get("enabled", True)]
        if not idx:
            return
        if self.hover in idx:
            n = idx[(idx.index(self.hover) + d) % len(idx)]
        else:
            n = idx[0] if d > 0 else idx[-1]
        self.hover = n
        y0, y1 = self.rows[n]
        if y0 < self.scroll:
            self.scroll = y0 - self.pad
        elif y1 > self.scroll + self.vh:
            self.scroll = y1 - self.vh + self.pad
        self.scroll = max(0, min(self.full_h - self.vh, self.scroll))
        self.draw()

    def _key_right(self):
        if self.hover is not None and self.items[self.hover].get("kind") == "sub":
            self._open_child(self.hover)
            if self.child:
                self.child._key_move(1)

    def _key_left(self):
        if self.parent is not None:
            p = self.parent
            p._close_child()
            try:
                p.top.focus_force()
            except Exception:
                pass

    # ---------- 子菜单 ----------
    def _open_child(self, i):
        self._sub_job = None
        if self.closed:
            return
        if self.child is not None and self.child_idx == i:
            return
        self._close_child()
        y0, y1 = self.rows[i]
        ay = self.y + y0 - self.scroll
        self.child_idx = i
        self.child = FluentMenu(self.owner, self.items[i]["items"], 0, ay, self.dark,
                                parent=self, anchor=(self.x, ay, self.x + self.W))
        self.draw()

    def _close_child(self):
        self._sub_job = None
        if self.child is not None:
            self.child.close()
        self.child = None
        self.child_idx = None
        if not self.closed:
            self.draw()

    # ---------- 关闭 ----------
    def close(self):
        if self.closed:
            return
        if self.child is not None:
            self.child.close()
            self.child = None
        self.closed = True
        for j in (self._sub_job, self._fade_job):
            if j:
                try:
                    self.root.after_cancel(j)
                except Exception:
                    pass
        try:
            self.top.destroy()
        except Exception:
            pass

    def close_all(self):
        m = self
        while m.parent is not None:
            m = m.parent
        m.close()
        if self.owner.menu is m:
            self.owner.menu = None

    def contains(self, x, y):
        """屏幕坐标是否落在本菜单或其子菜单上"""
        if self.closed:
            return False
        if self.x <= x < self.x + self.W and self.y <= y < self.y + self.vh:
            return True
        return self.child is not None and self.child.contains(x, y)


class WorldClock:
    def __init__(self, root, test=False):
        self.root = root
        self.test = test
        self.cfg = self.load_cfg()
        self.tz_idx = self.cfg.get("tz_idx", 0)
        if not 0 <= self.tz_idx < len(ZONES):
            self.tz_idx = 0
        self.h24 = bool(self.cfg.get("h24", True))
        self.show_sec = bool(self.cfg.get("sec", False))
        self.scale = min(1.6, max(0.6, float(self.cfg.get("scale", 1.0))))
        self.docked = bool(self.cfg.get("docked", True))
        self.tray_h = None
        self.tray_rect = None
        self.tray_visible = False
        self.taskbar_color = None
        self.edge_profile = {}        # 停靠时上下边缘逐行复刻的任务栏颜色 {行号: rgb}
        self.time_color = C_TIME
        self.date_color = C_DATE
        self.last_sec = -1
        self.drag = None
        self.toast = None
        self.toast_job = None
        self.hidden = False
        self._interact_at = 0.0
        self._dock_last = 0.0
        self._theme_last = 0.0
        self.transparent = False      # 双击进入的全透明状态
        self._transparent_job = None
        self._transparent_at = 0.0
        self._click_seq = 0           # 连击计数
        self._last_click = 0.0
        self._click_job = None
        self._press_xy = None
        self._moved = False

        root.overrideredirect(True)
        root.attributes("-topmost", True)
        root.attributes("-transparentcolor", KEY)
        root.attributes("-alpha", DOCK_ALPHA if self.docked else 1.0)
        root.configure(bg=KEY)

        self.cv = tk.Canvas(root, bg=KEY, highlightthickness=0, bd=0, cursor="fleur")
        self.cv.pack(fill="both", expand=True)

        self._dpi = None
        self._last_sig = None
        self._pending_display = False   # 收到 WM_DISPLAYCHANGE 等消息后置位, 由 tick 处理
        self._pending_color = True
        self._recheck_at = []
        self._sig_last = 0.0
        self._hooked = False
        self._old_wndproc = None
        self._color_retry_at = None
        self._color_fail = 0
        self._sample_agree = 1.0
        self._msg_taskbar_created = 0
        self._check_dpi(initial=True)      # 按当前实际缩放设置字号（而非登录时的缩放）

        fam = set(tkfont.families(root))
        self.f_digits = next((f for f in ("Bahnschrift", "Consolas", "Segoe UI", "TkFixedFont") if f in fam), "TkFixedFont")
        self.f_cjk = next((f for f in ("Microsoft YaHei UI", "Microsoft YaHei", "SimHei", "TkDefaultFont") if f in fam), "TkDefaultFont")

        self.cv.bind("<Button-1>", self.on_press)
        self.cv.bind("<B1-Motion>", self.on_move)
        self.cv.bind("<ButtonRelease-1>", self.on_release)
        self.cv.bind("<Button-3>", self.on_menu)
        root.bind("<MouseWheel>", self.on_wheel)

        if self.docked:
            tr = self._tray_rect()
            if tr is not None:
                self.tray_h = tr.bottom - tr.top
        self.build_layout()
        if test:
            root.geometry(f"{self.W}x{self.H}-3000-3000")
        else:
            x, y = self._initial_pos()
            root.geometry(f"{self.W}x{self.H}+{x}+{y}")

        self.menu = None
        self._menu_armed = False
        self.redraw()
        if not test:
            self._hook_messages()
            self._open_quit_event()
        try:
            self.root.update_idletasks()               # 先让窗口就位, 首次取色才准确
        except Exception:
            pass
        self.tick()

    # ---------- 任务栏 ----------
    def _tray_rect(self):
        """主任务栏矩形; 任务栏隐藏(自动隐藏滑出屏幕)时返回 None。
        用任务栏所在显示器的实时矩形判断, 不用 Tk 缓存的屏幕尺寸 ——
        分辨率来回切换后 Tk 的尺寸可能过期, 会把任务栏误判为隐藏, 时钟就回不来。"""
        if ctypes.wintypes is None:
            return None
        try:
            u = ctypes.windll.user32
            hwnd = u.FindWindowW("Shell_TrayWnd", None)
            if not hwnd or not u.IsWindowVisible(hwnd):
                return None
            r = ctypes.wintypes.RECT()
            if not u.GetWindowRect(hwnd, ctypes.byref(r)):
                return None
            w, h = r.right - r.left, r.bottom - r.top
            if w <= 0 or h <= 0:
                return None
            mon = monitor_rect_of(r)
            if mon is None:
                return None
            # 与显示器的重叠部分不足一半 → 自动隐藏已滑出屏幕
            ix = min(r.right, mon[2]) - max(r.left, mon[0])
            iy = min(r.bottom, mon[3]) - max(r.top, mon[1])
            if ix <= 0 or iy <= 0 or ix * iy < 0.5 * w * h:
                return None
            return r
        except Exception:
            return None

    def _win_pos(self):
        """时钟窗口应在的位置。停靠时按任务栏直接算出, 不读 winfo_x/y:
        刚启动、窗口还没摆放好时 winfo_x/y 是 (0,0), 会把取色区算到整条任务栏上。"""
        tr = self.tray_rect
        if self.docked and tr is not None:
            if tr.right - tr.left > tr.bottom - tr.top:
                return (tr.right - self.W - 6,
                        tr.top + max(0, (tr.bottom - tr.top - self.H) // 2))
            return tr.left, tr.bottom - self.H - 6
        return self.root.winfo_x(), self.root.winfo_y()

    def _sample_points(self):
        """任务栏取色点: 时钟窗口旁边、图标上下留白处的两排点。
        旧版只取任务栏最右缘 1 个点（Win11 是「显示桌面」细条, 颜色偏亮）,
        且 GetPixel 失败返回的 CLR_INVALID 被当成白色, 是色差的根源。"""
        tr = self.tray_rect
        if tr is None:
            return []
        try:
            wx, wy = self._win_pos()
        except Exception:
            return []
        W, H = self.W, self.H
        pts = []
        if tr.right - tr.left > tr.bottom - tr.top:      # 水平任务栏
            th = tr.bottom - tr.top
            m = max(3, int(th * 0.12))                   # 图标垂直居中, 上下边缘是纯底色
            rows = (tr.top + m, tr.bottom - 1 - m)
            # 左侧: 托盘区上下留白（跨度约 240px, 中位数抗图标/悬停高亮干扰）
            xs = [wx - 4 - i * 10 for i in range(24)]
            # 右侧: 时钟与屏幕边缘之间（避开最右 12px 的「显示桌面」细条）
            xs += [x for x in range(wx + W + 2, tr.right - 12, 4)][:6]
            for y in rows:
                pts += [(x, y) for x in xs if tr.left <= x < tr.right]
        else:                                            # 垂直任务栏
            tw = tr.right - tr.left
            m = max(3, int(tw * 0.12))
            cols = (tr.left + m, tr.right - 1 - m)
            ys = [wy - 4 - i * 10 for i in range(24)]
            for x in cols:
                pts += [(x, y) for y in ys if tr.top <= y < tr.bottom]
        return pts[:64]                                  # 上限: 逐点读屏很慢

    def _grab(self, x, y, w, h):
        """BitBlt 截取屏幕一块区域, 返回 BGRA 字节缓冲（失败返回 None）。
        句柄在 64 位下必须声明为指针类型, 否则会被截断。"""
        u, g = ctypes.windll.user32, ctypes.windll.gdi32
        vp = ctypes.c_void_p
        if not getattr(self, "_gdi_ready", False):
            u.GetDC.restype = vp
            u.GetDC.argtypes = [vp]
            u.ReleaseDC.argtypes = [vp, vp]
            g.CreateCompatibleDC.restype = vp
            g.CreateCompatibleDC.argtypes = [vp]
            g.CreateCompatibleBitmap.restype = vp
            g.CreateCompatibleBitmap.argtypes = [vp, ctypes.c_int, ctypes.c_int]
            g.SelectObject.restype = vp
            g.SelectObject.argtypes = [vp, vp]
            g.BitBlt.argtypes = [vp, ctypes.c_int, ctypes.c_int, ctypes.c_int, ctypes.c_int,
                                 vp, ctypes.c_int, ctypes.c_int, ctypes.c_uint32]
            g.GetDIBits.argtypes = [vp, vp, ctypes.c_uint, ctypes.c_uint, vp, vp, ctypes.c_uint]
            g.DeleteObject.argtypes = [vp]
            g.DeleteDC.argtypes = [vp]
            self._gdi_ready = True
        if w <= 0 or h <= 0:
            return None
        hdc = u.GetDC(None)
        if not hdc:
            return None
        mdc = bmp = old = None
        try:
            mdc = g.CreateCompatibleDC(hdc)
            bmp = g.CreateCompatibleBitmap(hdc, w, h)
            if not mdc or not bmp:
                return None
            old = g.SelectObject(mdc, bmp)
            if not g.BitBlt(mdc, 0, 0, w, h, hdc, x, y, 0x00CC0020):    # SRCCOPY
                return None
            bih = (ctypes.c_uint32 * 10)()                           # BITMAPINFOHEADER(40 字节)
            bih[0] = 40
            bih[1] = w
            bih[2] = ctypes.c_uint32(-h).value                       # 负高度 = 自上而下
            bih[3] = 1 | (32 << 16)                                  # biPlanes=1, biBitCount=32
            buf = (ctypes.c_ubyte * (w * h * 4))()
            if not g.GetDIBits(mdc, bmp, 0, h, buf, bih, 0):
                return None
            return buf
        except Exception:
            return None
        finally:
            if mdc and old:
                g.SelectObject(mdc, old)
            if bmp:
                g.DeleteObject(bmp)
            if mdc:
                g.DeleteDC(mdc)
            u.ReleaseDC(None, hdc)

    def _sample_taskbar_profile(self):
        """截取时钟左侧一条任务栏, 逐行取中位数, 得到任务栏的纵向颜色剖面。
        返回 (底色, {窗口内行号: 颜色}) —— 只在上下边缘带逐行复刻(顶部高光线/渐变),
        中间图标所在行统一用底色。仅水平任务栏; 失败返回 None。"""
        tr = self.tray_rect
        if tr is None or tr.right - tr.left <= tr.bottom - tr.top:
            return None
        try:
            wx, wy = self._win_pos()
        except Exception:
            return None
        th = tr.bottom - tr.top
        gw = 240
        gx = max(tr.left, wx - 4 - gw)
        gw = wx - 4 - gx
        if gw < 40:
            return None
        buf = self._grab(gx, tr.top, gw, th)
        if buf is None:
            return None
        med = lambda a: sorted(a)[len(a) // 2]
        cols = range(0, gw, 6)
        rows = []
        for y in range(th):
            base = y * gw * 4
            rs = [buf[base + x * 4 + 2] for x in cols]
            gs = [buf[base + x * 4 + 1] for x in cols]
            bs = [buf[base + x * 4] for x in cols]
            rows.append((med(rs), med(gs), med(bs)))
        m = max(3, int(th * 0.12))                       # 上下边缘带（图标不会伸进去）
        band = [rows[m], rows[th - 1 - m]]
        # 一致性: 边缘带里与中位数接近的列占比。悬停高亮/弹出面板/提示框盖住取色区时会明显变低
        ok = tot = 0
        for y in (m, th - 1 - m):
            ref = rows[y]
            base = y * gw * 4
            for x in cols:
                c = (buf[base + x * 4 + 2], buf[base + x * 4 + 1], buf[base + x * 4])
                tot += 1
                if max(abs(a - b) for a, b in zip(c, ref)) <= 8:
                    ok += 1
        self._sample_agree = ok / max(1, tot)
        basec = tuple(med([c[i] for c in band]) for i in range(3))
        # 边缘带中与底色明显不同的行 = 任务栏的顶部高光线/渐变 → 原样复刻
        prof = {}
        for y in list(range(0, m + 1)) + list(range(th - 1 - m, th)):
            c = rows[y]
            wy_row = tr.top + y - wy                     # 换算到时钟窗口内的行号
            if 0 <= wy_row < self.H and max(abs(a - b) for a, b in zip(c, basec)) >= 2:
                prof[wy_row] = c
        return basec, prof

    def _sample_taskbar_color(self):
        """从任务栏空余区域多点采样, 取各通道中位数作为表面颜色（用于停靠模式融合）"""
        pts = self._sample_points()
        if not pts:
            return None
        try:
            u, g = ctypes.windll.user32, ctypes.windll.gdi32
            g.GetPixel.restype = ctypes.c_uint32          # 否则 CLR_INVALID 会变成 -1 → 白色
            g.GetPixel.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_int]
            hdc = u.GetDC(0)
            if not hdc:
                return None
            rs, gs, bs = [], [], []
            try:
                for x, y in pts:
                    c = g.GetPixel(hdc, int(x), int(y))
                    if c == 0xFFFFFFFF:                 # CLR_INVALID: 点不在屏幕上
                        continue
                    rs.append(c & 0xFF)
                    gs.append((c >> 8) & 0xFF)
                    bs.append((c >> 16) & 0xFF)
            finally:
                u.ReleaseDC(0, hdc)
            if len(rs) < 4:
                return None
            med = lambda a: sorted(a)[len(a) // 2]
            return (med(rs), med(gs), med(bs))
        except Exception:
            return None

    def _theme_is_dark(self):
        try:
            import winreg
            k = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                               r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
            # 任务栏跟随「Windows 模式」(SystemUsesLightTheme), 不是「应用模式」
            v, _ = winreg.QueryValueEx(k, "SystemUsesLightTheme")
            winreg.CloseKey(k)
            return v == 0
        except Exception:
            return True

    def _screen_locked(self):
        """锁屏/安全桌面(UAC)时截屏得到的是黑色或锁屏画面, 不能拿来取色"""
        try:
            u = ctypes.windll.user32
            u.OpenInputDesktop.restype = ctypes.c_void_p
            h = u.OpenInputDesktop(0, False, 0x0001)          # DESKTOP_READOBJECTS
            if not h:
                return True
            u.CloseDesktop.argtypes = [ctypes.c_void_p]
            u.CloseDesktop(h)
            return False
        except Exception:
            return False

    def _accent_on_taskbar(self):
        """「在开始菜单和任务栏上显示强调色」开启时任务栏是强调色, 不按深浅主题校验"""
        try:
            import winreg
            k = winreg.OpenKey(winreg.HKEY_CURRENT_USER,
                               r"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize")
            v, _ = winreg.QueryValueEx(k, "ColorPrevalence")
            winreg.CloseKey(k)
            return v == 1
        except Exception:
            return False

    def _plausible(self, c):
        """样本是否可信: 取色区域颜色一致, 且亮度与系统深浅主题相符"""
        if getattr(self, "_sample_agree", 1.0) < 0.8:
            return False, f"agree={self._sample_agree:.2f}"
        if not self._accent_on_taskbar():
            lum = 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2]
            dark = self._theme_is_dark()
            if (dark and lum > 170) or (not dark and lum < 110):
                return False, f"lum={lum:.0f} dark={dark}"
        return True, ""

    def _retry_color(self, why):
        """坏样本: 不采用, 3 秒后重取(最多连续 20 次, 之后回到常规节奏)"""
        self._diag(f"color rejected: {why}")
        self._color_fail += 1
        if self._color_fail <= 20:
            self._color_retry_at = _time.monotonic() + 3.0
        if self.taskbar_color is None:                     # 还没有可用颜色: 先用主题默认色
            self._apply_color((32, 32, 32) if self._theme_is_dark() else (238, 238, 238), {})

    def refresh_theme(self):
        """停靠模式: 按任务栏表面颜色调整背景与文字色"""
        if not self.docked:
            return
        self._color_retry_at = None
        if self._screen_locked():
            self._retry_color("locked")
            return
        prof = {}
        self._sample_agree = 1.0
        res = self._sample_taskbar_profile()
        if res is not None:
            c, prof = res
        else:
            c = self._sample_taskbar_color()             # 退回旧的逐点取色
        if c is None:
            self._retry_color("capture failed")
            return
        ok, why = self._plausible(c)
        if not ok:
            self._retry_color(why)
            return
        self._color_fail = 0
        self._apply_color(c, prof)

    def _apply_color(self, c, prof):
        old = self.taskbar_color
        same_prof = (set(prof) == set(self.edge_profile) and all(
            max(abs(a - b) for a, b in zip(prof[k], self.edge_profile[k])) < COLOR_EPS for k in prof))
        if old is not None and same_prof and max(abs(a - b) for a, b in zip(old, c)) < COLOR_EPS:
            return
        self._diag(f"color applied: {c} edge_rows={len(prof)}")
        self.taskbar_color = c
        self.edge_profile = prof
        lum = 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2]
        if lum > 150:
            self.time_color = "#1c1c1e"
            self.date_color = "#6e6e73"
        else:
            self.time_color = "#f5f5f7"
            self.date_color = "#98989f"
        self.redraw()

    def update_dock(self):
        """停靠模式: 跟随任务栏位置/尺寸/可见性"""
        if not self.docked:
            return
        rect = self._tray_rect()
        if rect is not None and not self.tray_visible:
            self._pending_color = True            # 任务栏刚出现(自动隐藏滑出/资源管理器重启后)
        self.tray_visible = rect is not None
        if rect is not None:
            self.tray_rect = rect
            new_h = rect.bottom - rect.top
            try:
                dpi = int(ctypes.windll.user32.GetDpiForWindow(self.root.winfo_id()))
            except Exception:
                dpi = 0
            if new_h != self.tray_h or (dpi and dpi != self._dpi):
                # 缩放/任务栏尺寸变了但没收到系统消息: 完整重新适配(字号、位置、颜色)
                self._diag(f"dock check: tray_h {self.tray_h}->{new_h} dpi {self._dpi}->{dpi}")
                self.readapt(manual=False)
                return
            if rect.right - rect.left > rect.bottom - rect.top:  # 水平任务栏
                # 垂直居中于任务栏（高度比任务栏少 3px）
                x = rect.right - self.W - 6
                y = rect.top + max(0, (rect.bottom - rect.top - self.H) // 2)
            else:                                                # 垂直任务栏
                x, y = rect.left, rect.bottom - self.H - 6
            if (self.root.winfo_x(), self.root.winfo_y()) != (x, y):
                self.root.geometry(f"+{x}+{y}")
            # 每轮重申置顶, 防止任务栏偶尔抢占层级盖住时钟
            self._raise_top()
        self.update_visibility()

    # ---------- DPI ----------
    def _check_dpi(self, initial=False):
        """显示缩放变化时(含运行中修改), 重设 Tk 缩放并重排、重新取色"""
        try:
            dpi = int(ctypes.windll.user32.GetDpiForWindow(self.root.winfo_id()))
        except Exception:
            return
        if not dpi or dpi == self._dpi:
            return
        self._dpi = dpi
        try:
            self.root.tk.call("tk", "scaling", dpi / 72.0)
        except Exception:
            pass
        if initial:
            return
        self.close_menu()
        tr = self._tray_rect()
        if tr is not None:
            self.tray_h = tr.bottom - tr.top
            self.tray_rect = tr
        self.build_layout()
        self.edge_profile = {}
        self.redraw()
        if self.docked:
            self.update_dock()             # 先挪到新位置, 再立即重新取色(同一轮内, 不会闪)
            self.taskbar_color = None
            self.refresh_theme()

    # ---------- 布局 ----------
    def _px(self, pt):
        """磅值 → 像素字号(Tk 中负数表示像素)。按当前 DPI 直接换算, 不依赖 Tk 的全局缩放:
        Python 3.11 / 3.14 自带的 Tk 版本对 DPI 的处理不同, 用磅值会出现字号时大时小。"""
        return -max(1, int(round(pt * (self._dpi or 96) / 72.0)))

    def build_layout(self):
        s = self.scale
        # 时间字号与任务栏时钟一致(~11.5pt), 日期加粗
        self.f_t = tkfont.Font(root=self.root, family=self.f_digits, size=self._px(max(9, 11.5 * s)))
        self.f_s = tkfont.Font(root=self.root, family=self.f_digits, size=self._px(max(7, 8 * s)))
        self.f_d = tkfont.Font(root=self.root, family=self.f_cjk, size=self._px(max(6, 7.0 * s)), weight="bold")

        ht = self.f_t.metrics("ascent") + 1
        hs = self.f_s.metrics("ascent")
        hd = self.f_d.metrics("ascent") + 2

        self.w_hh = self.f_t.measure("88")
        self.w_col = self.f_t.measure(":")
        self.w_mm = self.f_t.measure("88")
        self.w_sec = self.f_s.measure(":88")
        tw = self.w_hh + self.w_col + self.w_mm
        if self.show_sec:
            tw += 3 + self.w_sec
        w_d = self.f_d.measure("12月31日 周日")
        content_w = max(tw, w_d) + 14
        content_h = 5 + ht + 2 + hd + 6

        if self.docked and self.tray_h:
            # 停靠: 方形平嵌, 高度=任务栏高度-3px, 内容垂直居中
            self.W = int(content_w + 12)
            self.H = max(30, int(self.tray_h) - 3)
            self.x0, self.y0 = 0, 0
            self.x1, self.y1 = self.W, self.H
            self.radius = 0
            # 文字整体右移 1/4 窗口宽（以不越出窗口右缘为限）
            widest = max(tw, w_d)
            max_shift = max(0, self.W - 2 - 6 - widest)
            self.xt = 6 + min(int(self.W * 0.25), max_shift)
            self.yt = max(2, (self.H - content_h) // 2 + 2)
        else:
            # 悬浮卡片
            self.W = int(content_w + 2 * PAD)
            self.H = int(content_h + 2 * PAD)
            self.x0, self.y0 = PAD, PAD
            self.x1, self.y1 = self.W - PAD, self.H - PAD
            self.xt = self.x0 + 9
            self.yt = self.y0 + 5
            self.radius = 14 * s

        self.ys = self.yt + ht - hs
        self.x_col = self.xt + self.w_hh
        self.x_mm = self.x_col + self.w_col
        self.x_sec = self.x_mm + self.w_mm + 3
        self.yd = self.yt + ht + 2

    def _screen_box(self):
        """实时虚拟屏幕矩形; 取不到时退回 Tk 的屏幕尺寸"""
        vs = virtual_screen()
        if vs is not None:
            return vs
        return 0, 0, self.root.winfo_screenwidth(), self.root.winfo_screenheight()

    def _clock_pos(self):
        """停靠默认位置: 任务栏右下角的系统时钟处（高度比任务栏少 3px, 垂直居中）"""
        tr = self._tray_rect()
        if tr is not None and tr.right - tr.left > tr.bottom - tr.top:
            y = tr.top + max(0, (tr.bottom - tr.top - self.H) // 2)
            return max(tr.left, tr.right - self.W - 6), y
        l, t, r, b = self._screen_box()
        return max(l, r - self.W - 10), max(t, b - self.H)

    def _clamp_to_screen(self, x, y):
        """悬浮卡片的位置夹回到最近的显示器工作区内（分辨率变小后不会跑出屏幕）"""
        wa = monitor_work_area(int(x) + self.W // 2, int(y) + self.H // 2)
        if wa is None:
            wa = self._screen_box()
        l, t, r, b = wa
        return (min(max(l, int(x)), max(l, r - self.W)),
                min(max(t, int(y)), max(t, b - self.H)))

    def _initial_pos(self):
        if self.docked:
            return self._clock_pos()
        p = self.cfg.get("pos")
        if isinstance(p, (list, tuple)) and len(p) == 2:
            return self._clamp_to_screen(p[0], p[1])
        return self._clock_pos()

    def _hwnd(self):
        """Tk 顶层窗口的真实 HWND（winfo_id 是内部子窗口）"""
        u = ctypes.windll.user32
        u.GetParent.restype = ctypes.c_void_p
        u.GetParent.argtypes = [ctypes.c_void_p]
        wid = self.root.winfo_id()
        return u.GetParent(wid) or wid

    def _raise_top(self):
        """重新置于最顶层且不抢焦点。分辨率/缩放切换或点击任务栏后,
        任务栏可能压到时钟上面, 仅设置 -topmost 属性不一定能恢复层级。"""
        try:
            self.root.attributes("-topmost", True)
            u = ctypes.windll.user32
            u.SetWindowPos.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_int, ctypes.c_int,
                                       ctypes.c_int, ctypes.c_int, ctypes.c_uint]
            # HWND_TOPMOST; SWP_NOSIZE | SWP_NOMOVE | SWP_NOACTIVATE | SWP_NOOWNERZORDER
            u.SetWindowPos(self._hwnd(), ctypes.c_void_p(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0010 | 0x0200)
        except Exception:
            pass

    def _display_sig(self):
        """显示环境指纹: 虚拟屏幕 + 任务栏矩形 + DPI。任一变化即视为分辨率/缩放/显示器切换"""
        tr = self._tray_rect()
        try:
            dpi = int(ctypes.windll.user32.GetDpiForWindow(self.root.winfo_id()))
        except Exception:
            dpi = 0
        return (virtual_screen(), (tr.left, tr.top, tr.right, tr.bottom) if tr else None, dpi)

    def _watch_display(self):
        """每秒检查显示环境; 变化后立即重新适配, 并在 1.5s / 4s 后各补一次
        (切换分辨率时资源管理器会分几步重排任务栏)"""
        sig = self._display_sig()
        if self._last_sig is None:
            self._last_sig = sig
            return
        if sig != self._last_sig:
            self._last_sig = sig
            self.readapt(manual=False)
            for ms in (1500, 4000):
                self.root.after(ms, lambda: self.readapt(manual=False))

    QUIT_EVENT = "Local\\WorldClockQuit"

    def _open_quit_event(self):
        """打开(或创建)「请退出」事件。新实例以 --restart 启动时置位它,
        本实例在下一秒的 tick 里看到就自行保存退出, 无需 PowerShell 查进程(那要等 5~10 秒)。"""
        try:
            k = ctypes.windll.kernel32
            k.CreateEventW.restype = ctypes.c_void_p
            k.ResetEvent.argtypes = [ctypes.c_void_p]
            self._quit_evt = k.CreateEventW(None, True, False, self.QUIT_EVENT)
            if self._quit_evt:
                k.ResetEvent(self._quit_evt)          # 新实例: 清掉上一轮重启留下的信号
        except Exception:
            self._quit_evt = None

    def _quit_requested(self):
        h = getattr(self, "_quit_evt", None)
        if not h:
            return False
        try:
            k = ctypes.windll.kernel32
            k.WaitForSingleObject.argtypes = [ctypes.c_void_p, ctypes.c_uint32]
            k.WaitForSingleObject.restype = ctypes.c_uint32
            return k.WaitForSingleObject(h, 0) == 0     # WAIT_OBJECT_0
        except Exception:
            return False

    def _diag(self, msg):
        """诊断日志: 程序目录里存在 diag_on 文件时写入 worldclock_diag.log(超过 200KB 自动清空)"""
        try:
            if not os.path.exists(os.path.join(APP_DIR, "diag_on")):
                return
            p = os.path.join(APP_DIR, "worldclock_diag.log")
            mode = "w" if os.path.exists(p) and os.path.getsize(p) > 200_000 else "a"
            with open(p, mode, encoding="utf-8") as f:
                f.write(f"{datetime.now():%m-%d %H:%M:%S} {msg}\n")
        except Exception:
            pass

    # ---------- 系统消息: 显示/主题变化时由 Windows 通知, 不轮询 ----------
    WM_SETTINGCHANGE = 0x001A
    WM_DISPLAYCHANGE = 0x007E
    WM_DPICHANGED = 0x02E0
    WM_DWMCOLORIZATIONCOLORCHANGED = 0x0320

    def _hook_messages(self):
        """子类化 Tk 顶层窗口的窗口过程, 接收系统广播的显示/设置变化消息。
        失败时退回每 10 秒比对一次显示环境。"""
        try:
            u = ctypes.windll.user32
            LRESULT = ctypes.c_ssize_t
            self._WNDPROC = ctypes.WINFUNCTYPE(LRESULT, ctypes.c_void_p, ctypes.c_uint,
                                               ctypes.c_size_t, ctypes.c_ssize_t)
            u.SetWindowLongPtrW.restype = ctypes.c_void_p
            u.SetWindowLongPtrW.argtypes = [ctypes.c_void_p, ctypes.c_int, ctypes.c_void_p]
            u.CallWindowProcW.restype = LRESULT
            u.CallWindowProcW.argtypes = [ctypes.c_void_p, ctypes.c_void_p, ctypes.c_uint,
                                          ctypes.c_size_t, ctypes.c_ssize_t]
            self._wndproc = self._WNDPROC(self._on_wnd_msg)       # 必须保持引用, 防止被回收
            hwnd = self._hwnd()
            try:
                u.RegisterWindowMessageW.restype = ctypes.c_uint
                self._msg_taskbar_created = u.RegisterWindowMessageW("TaskbarCreated")
            except Exception:
                self._msg_taskbar_created = 0
            try:                                                   # 锁屏/解锁通知
                ctypes.windll.wtsapi32.WTSRegisterSessionNotification(ctypes.c_void_p(hwnd), 0)
            except Exception:
                pass
            old = u.SetWindowLongPtrW(hwnd, -4, ctypes.cast(self._wndproc, ctypes.c_void_p))  # GWLP_WNDPROC
            if old:
                self._old_wndproc = old
                self._hooked = True
        except Exception:
            self._hooked = False
        self._diag(f"start: python={sys.version.split()[0]} exe={sys.executable} "
                   f"tk={self.root.tk.call('info', 'patchlevel')} dpi={self._dpi} hooked={self._hooked}")

    def _on_wnd_msg(self, hwnd, msg, wp, lp):
        try:
            if msg in (self.WM_DISPLAYCHANGE, self.WM_DPICHANGED):
                self._pending_display = True          # 分辨率/缩放变化
            elif msg == self.WM_SETTINGCHANGE:
                area = ""
                if lp:
                    try:
                        area = ctypes.wstring_at(lp) or ""
                    except Exception:
                        area = ""
                if wp == 0x002F or area == "TraySettings":   # SPI_SETWORKAREA / 任务栏设置
                    self._pending_display = True
                if wp == 0x0014 or area in ("ImmersiveColorSet", "WindowsThemeElement"):
                    self._pending_color = True        # 壁纸 / 深浅色主题
            elif msg in (self.WM_DWMCOLORIZATIONCOLORCHANGED, 0x031A, 0x0015):
                self._pending_color = True                   # 强调色 / WM_THEMECHANGED / WM_SYSCOLORCHANGE
            elif msg == 0x0218 and wp in (0x0007, 0x0012):   # WM_POWERBROADCAST: 睡眠/休眠唤醒
                self._pending_display = True
            elif msg == 0x02B1 and wp in (0x1, 0x3, 0x8):    # WM_WTSSESSION_CHANGE: 连接/解锁
                self._pending_display = True
            elif self._msg_taskbar_created and msg == self._msg_taskbar_created:
                self._pending_display = True                 # 资源管理器(任务栏)重启
            if msg in (self.WM_DISPLAYCHANGE, self.WM_DPICHANGED, self.WM_SETTINGCHANGE, 0x0218, 0x02B1) \
                    or (self._msg_taskbar_created and msg == self._msg_taskbar_created):
                self._diag(f"msg 0x{msg:04X} wp=0x{wp:X}")
        except Exception:
            pass
        return ctypes.windll.user32.CallWindowProcW(self._old_wndproc, hwnd, msg, wp, lp)

    def readapt(self, manual=True):
        """重新适配: 重读 DPI/任务栏/显示器, 重排尺寸、重新定位、重新取色、恢复显示与置顶。
        分辨率或缩放来回切换后自动调用; 右键菜单「重新适配显示」手动调用。"""
        self._diag(f"readapt manual={manual}")
        if manual:
            self._interact_at = _time.monotonic()
            self.close_menu()
        if self.transparent:
            self.exit_transparent()
        self._dpi = None
        self._check_dpi(initial=True)                  # 按当前 DPI 重设 Tk 缩放
        tr = self._tray_rect()
        self.tray_rect = tr
        self.tray_visible = tr is not None
        self.tray_h = (tr.bottom - tr.top) if tr is not None else None
        self.build_layout()
        self.edge_profile = {}
        self.redraw()
        if self.docked:
            if tr is not None:
                x, y = self._clock_pos()
                self.root.geometry(f"{self.W}x{self.H}+{x}+{y}")
            self.taskbar_color = None
            self.refresh_theme()
        else:
            x, y = self._clamp_to_screen(self.root.winfo_x(), self.root.winfo_y())
            self.root.geometry(f"{self.W}x{self.H}+{x}+{y}")
        if self.hidden and (manual or not self.docked or tr is not None):
            self.hidden = False
            self.root.deiconify()
        self._raise_top()
        self._last_sig = self._display_sig()
        if manual:
            self.save_cfg()
            self.show_toast("已重新适配")

    def reset_pos(self):
        """一键吸附: 停靠回任务栏时钟位置并重新适配"""
        if self.transparent:
            self.exit_transparent()
        self._interact_at = _time.monotonic()
        self.docked = True
        self.root.attributes("-alpha", DOCK_ALPHA)
        self.readapt(manual=False)
        self._interact_at = _time.monotonic()
        self.close_menu()
        self.save_cfg()
        self.show_toast("已吸附")

    # ---------- 绘制 ----------
    def render(self, dt):
        if self.h24:
            hh = f"{dt.hour:02d}"
        else:
            h = dt.hour % 12 or 12
            hh = f"{h:2d}"
        sec = f":{dt.second:02d}" if self.show_sec else ""
        date = f"{dt.month}月{dt.day}日 {WEEK_CN[dt.weekday()]}"
        return hh, f"{dt.minute:02d}", sec, date

    def redraw(self):
        cv = self.cv
        cv.delete("all")
        if self.docked:
            # 平嵌: 纯色底融入任务栏
            bg = self.taskbar_color or (30, 30, 34)
            cv.create_rectangle(0, 0, self.W, self.H, fill=rgb_str(bg), outline="")
            # 复刻任务栏顶部高光线/上下渐变, 让时钟上沿与任务栏上沿完全一致
            for y, c in self.edge_profile.items():
                cv.create_line(0, y, self.W, y, fill=rgb_str(c))
        else:
            x0, y0, x1, y1, r = self.x0, self.y0, self.x1, self.y1, self.radius
            # 柔和阴影
            cv.create_polygon(rrect_pts(x0 + 2, y0 + 3, x1 + 2, y1 + 3, r), fill="#000000", outline="", smooth=True)
            # 面板底色
            cv.create_polygon(rrect_pts(x0, y0, x1, y1, r), fill=BG_BOT, outline="", smooth=True)
            # 极淡纵向渐变
            n = max(6, int((y1 - y0) / 3))
            for i in range(n):
                t = i / (n - 1)
                cv.create_line(x0, y0 + (y1 - y0) * t, x1, y0 + (y1 - y0) * t, fill=mix(BG_TOP, BG_BOT, t))
            # 圆角裁切
            for cx, cy, a0, a1, px, py in (
                (x0 + r, y0 + r, 180, 270, x0, y0),
                (x1 - r, y0 + r, 270, 360, x1, y0),
                (x1 - r, y1 - r, 0, 90, x1, y1),
                (x0 + r, y1 - r, 90, 180, x0, y1),
            ):
                cv.create_polygon(cutout_pts(cx, cy, r, a0, a1, px, py), fill=KEY, outline="", smooth=True)
            # 细边框 + 顶部高光
            cv.create_polygon(rrect_pts(x0, y0, x1, y1, r), fill="", outline=C_BORDER, width=1, smooth=True)
            cv.create_line(x0 + 12, y0 + 2, x1 - 12, y0 + 2, fill=mix(C_BORDER, BG_TOP, 0.45), width=1)

        self.update_time(self.now_dt())

    def update_time(self, dt):
        cv = self.cv
        hh, mm, sec, date = self.render(dt)
        cv.delete("tm")
        cv.create_text(self.xt, self.yt, text=hh, anchor="nw", fill=self.time_color, font=self.f_t, tags="tm")
        cv.create_text(self.x_col, self.yt, text=":", anchor="nw", fill=self.time_color, font=self.f_t, tags="tm")
        cv.create_text(self.x_mm, self.yt, text=mm, anchor="nw", fill=self.time_color, font=self.f_t, tags="tm")
        cv.create_text(self.x_sec, self.ys, text=sec, anchor="nw", fill=self.date_color, font=self.f_s, tags="tm")
        cv.delete("dt")
        cv.delete("toast")
        if self.toast is not None:
            # 停靠模式下提示文字跟随主题色（浅色任务栏用深色字, 否则看不见）
            tcol = self.time_color if self.docked else C_TOAST
            cv.create_text(self.xt, self.yd, text=self.toast, anchor="nw", fill=tcol,
                           font=self.f_d, tags="toast")
        else:
            cv.create_text(self.xt, self.yd, text=date, anchor="nw", fill=self.date_color,
                           font=self.f_d, tags="dt")

    # ---------- 定时与可见性 ----------
    def tick(self):
        if self._quit_requested():
            self._diag("quit requested by a new instance (restart)")
            self.quit()
            return
        try:
            dt = self.now_dt()
            if dt.second != self.last_sec:
                self.last_sec = dt.second
                self.update_time(dt)
            now = _time.monotonic()
            # 系统通知的显示变化(分辨率/缩放/任务栏设置): 立即适配, 1.5s/4s 后各补一次
            if self._pending_display:
                self._pending_display = False
                self.readapt(manual=False)
                self._recheck_at = [now + 1.5, now + 4.0]
            if self._recheck_at and now >= self._recheck_at[0]:
                self._recheck_at.pop(0)
                self.readapt(manual=False)
            if not self._hooked and now - self._sig_last >= 10.0:
                self._sig_last = now               # 消息挂钩失败时的退路: 每 10 秒比对一次
                self._watch_display()
            # 全屏隐藏/任务栏自动隐藏跟随: 系统没有现成通知, 每秒轻量检查(只读窗口矩形)
            if now - self._dock_last >= 1.0:
                self._dock_last = now
                if self.docked:
                    self.update_dock()
                else:
                    self.update_visibility()
            # 取色(截一小条任务栏): 主题/壁纸/显示/解锁/唤醒时立即取; 坏样本 3 秒后重取; 平时 30 秒兜底一次
            retry_due = self._color_retry_at is not None and now >= self._color_retry_at
            if self._pending_color or retry_due or now - self._theme_last >= 30.0:
                self._pending_color = False
                self._theme_last = now
                self.refresh_theme()
        except Exception:
            pass
        # 对齐到下一整秒再唤醒（原先每 200ms 轮询一次）
        ms = 1000 - int(_time.time() * 1000) % 1000 + 15
        self.root.after(ms, self.tick)

    @staticmethod
    def _rect_covers(wr, target, slop=8):
        """wr 是否盖住 target（允许 slop 像素误差, 兼容无边框窗口略超出/略缩进）"""
        return (wr.left <= target.left + slop and wr.top <= target.top + slop and
                wr.right >= target.right - slop and wr.bottom >= target.bottom - slop)

    def _hwnd_class(self, hwnd):
        cls = ctypes.create_unicode_buffer(256)
        ctypes.windll.user32.GetClassNameW(hwnd, cls, 256)
        return cls.value

    def _root_hwnd(self, hwnd):
        if not hwnd:
            return 0
        root = ctypes.windll.user32.GetAncestor(hwnd, 2)  # GA_ROOT
        return root or hwnd

    def _is_our_hwnd(self, hwnd):
        if not hwnd:
            return False
        try:
            our = int(self.root.winfo_id())
        except Exception:
            return False
        return hwnd == our or self._root_hwnd(hwnd) == our

    def _is_on_clock_monitor(self, hwnd):
        """全屏发生在其它显示器时, 不隐藏本显示器上的时钟"""
        if not hwnd:
            return True
        try:
            our = int(self.root.winfo_id())
        except Exception:
            return True
        clock_mon = ctypes.windll.user32.MonitorFromWindow(our, 2)
        fg_mon = ctypes.windll.user32.MonitorFromWindow(hwnd, 2)
        if clock_mon and fg_mon and clock_mon != fg_mon:
            return False
        return True

    def _shell_says_fullscreen(self):
        """D3D 独占全屏 / 系统演示模式。部分游戏前台窗口矩形异常, 用此补充。"""
        try:
            state = ctypes.c_int()
            hr = ctypes.windll.shell32.SHQueryUserNotificationState(ctypes.byref(state))
            if hr != 0:
                return False
            return state.value in (3, 4)  # QUNS_RUNNING_D3D_FULL_SCREEN, QUNS_PRESENTATION_MODE
        except Exception:
            return False

    def _hwnd_is_fullscreen(self, hwnd):
        """判断窗口是否为真正全屏（含浏览器网页视频全屏）。普通最大化窗口返回 False。"""
        if not hwnd or self._is_our_hwnd(hwnd):
            return False
        hwnd = self._root_hwnd(hwnd)
        if not hwnd or self._is_our_hwnd(hwnd):
            return False
        if ctypes.windll.user32.IsIconic(hwnd):
            return False
        cls = self._hwnd_class(hwnd)
        if cls in EXCLUDE_CLASSES:
            return False
        pid = ctypes.wintypes.DWORD()
        ctypes.windll.user32.GetWindowThreadProcessId(hwnd, ctypes.byref(pid))
        if self._process_name(pid.value) in SHELL_PROCS:
            return False
        if not self._is_on_clock_monitor(hwnd):
            return False
        r = ctypes.wintypes.RECT()
        if not ctypes.windll.user32.GetWindowRect(hwnd, ctypes.byref(r)):
            return False
        info = MONITORINFO()
        info.cbSize = ctypes.sizeof(MONITORINFO)
        mon = ctypes.windll.user32.MonitorFromWindow(hwnd, 2)  # MONITOR_DEFAULTTONEAREST
        if not ctypes.windll.user32.GetMonitorInfoW(mon, ctypes.byref(info)):
            return False
        # 盖住整块显示器 → 真全屏。浏览器网页全屏常带 WS_MAXIMIZE, 不能据此排除。
        if self._rect_covers(r, info.rcMonitor):
            return True
        # HTML5 全屏有时只盖住工作区（任务栏仍在）, 但会去掉标题栏; 仅对浏览器生效,
        # 避免无边框 UWP/工具窗口最大化时误隐藏。
        style = ctypes.windll.user32.GetWindowLongW(hwnd, -16)
        has_caption = bool(style & 0x00C00000)  # WS_CAPTION
        if (not has_caption and cls.lower() in BROWSER_CLASSES and
                self._rect_covers(r, info.rcWork)):
            return True
        return False

    def _foreground_is_fullscreen(self):
        """前台是否为真正的全屏应用（PPT/WPS 放映、全屏播放、网页视频全屏、全屏游戏）"""
        if ctypes.wintypes is None:
            return False
        try:
            hwnd = ctypes.windll.user32.GetForegroundWindow()
            if self._hwnd_is_fullscreen(hwnd):
                return True
            if self._shell_says_fullscreen():
                if hwnd and not self._is_on_clock_monitor(self._root_hwnd(hwnd)):
                    return False
                return True
            return False
        except Exception:
            return False

    def _process_name(self, pid):
        try:
            h = ctypes.windll.kernel32.OpenProcess(0x1000, False, pid)  # PROCESS_QUERY_LIMITED_INFORMATION
            if not h:
                return ""
            buf = ctypes.create_unicode_buffer(260)
            size = ctypes.wintypes.DWORD(260)
            ctypes.windll.kernel32.QueryFullProcessImageNameW(h, 0, buf, ctypes.byref(size))
            ctypes.windll.kernel32.CloseHandle(h)
            return os.path.basename(buf.value).lower()
        except Exception:
            return ""

    def update_visibility(self):
        """全屏任务或(停靠时)任务栏隐藏 → 一起隐藏; 恢复后自动出现"""
        fs = self._foreground_is_fullscreen()
        # 宽限期: 用户刚操作过(点菜单/停靠/拖动/滚轮)的几秒内不隐藏,
        # 避免"吸附/操作后立刻消失"
        if fs and _time.monotonic() - self._interact_at < GRACE:
            fs = False
        tray_hidden = self.docked and not self.tray_visible
        hide = fs or tray_hidden
        if hide and not self.hidden:
            self.close_menu()
            self.hidden = True
            self.root.withdraw()
        elif not hide and self.hidden:
            self.hidden = False
            self.root.deiconify()
            self._raise_top()
            self._pending_color = True            # 退出全屏后重新取色

    # ---------- 时区切换提示（短暂闪现, 平时界面不显示时区） ----------
    def show_toast(self, name):
        if self.toast_job:
            self.root.after_cancel(self.toast_job)
        self.toast = name
        self.update_time(self.now_dt())
        self.toast_job = self.root.after(1600, self.clear_toast)

    def clear_toast(self):
        self.toast_job = None
        self.toast = None
        self.update_time(self.now_dt())

    # ---------- 交互 ----------
    def on_press(self, e):
        self._interact_at = _time.monotonic()
        self.close_menu()
        self._press_xy = (e.x_root, e.y_root)
        self._moved = False
        self.drag = (e.x_root - self.root.winfo_x(), e.y_root - self.root.winfo_y())

    def on_move(self, e):
        if not self.drag:
            return
        # 连击时手会轻微抖动: 位移未超过 DRAG_SLOP 不算拖动, 避免误脱离停靠
        if not self._moved:
            if (abs(e.x_root - self._press_xy[0]) <= DRAG_SLOP and
                    abs(e.y_root - self._press_xy[1]) <= DRAG_SLOP):
                return
            self._moved = True
        if self.docked:
            # 拖动即脱离停靠, 变为悬浮卡片
            if self.transparent:
                self.exit_transparent()
            self.docked = False
            self.root.attributes("-alpha", 1.0)
            self.build_layout()
            self.redraw()
            self.save_cfg()
        self.root.geometry(f"+{e.x_root - self.drag[0]}+{e.y_root - self.drag[1]}")

    def on_release(self, e):
        moved = self._moved
        self.drag = None
        self._press_xy = None
        self._moved = False
        self.save_cfg()
        if moved:
            return          # 拖动结束, 不计入连击
        self._register_click()

    # ---------- 连击: 单击=显示桌面, 双击=全透明, 三击=系统日历 ----------
    def _register_click(self):
        """连击计数: 单击=显示桌面, 双击=透明, 三击=日历。双击不能立刻执行: 必须等一个连击窗口, 否则三击会先触发双击动作。"""
        now = _time.monotonic()
        span = self._click_span()
        if now - self._last_click > span:
            self._click_seq = 0
        self._last_click = now
        self._click_seq += 1
        if self._click_job:
            try:
                self.root.after_cancel(self._click_job)
            except Exception:
                pass
            self._click_job = None
        if self._click_seq >= 3:
            self._click_seq = 0
            self.open_calendar()
            return
        self._click_job = self.root.after(max(120, int(span * 1000)), self._click_fire)

    def _click_fire(self):
        self._click_job = None
        n, self._click_seq = self._click_seq, 0
        if n == 1:
            self.show_desktop()
        elif n == 2:
            self.toggle_transparent()

    def _click_span(self):
        """连击判定窗口, 跟随系统双击速度设置"""
        try:
            ms = ctypes.windll.user32.GetDoubleClickTime() or 500
        except Exception:
            ms = 500
        return max(0.25, min(0.55, ms / 1000.0 * 0.8))

    def toggle_transparent(self):
        if self.transparent:
            self.exit_transparent()
        else:
            self.enter_transparent()

    def enter_transparent(self):
        """双击: 原地全透明, 位置/尺寸不变, 直接看到下面真正的系统时钟。
        真透明会让鼠标穿过窗口(Windows 分层窗口按透明区域做命中测试),
        所以此时点到的就是任务栏自己; 恢复靠"光标移开"或超时。"""
        if self.transparent:
            return
        self.transparent = True
        self._interact_at = _time.monotonic()
        self._transparent_at = _time.monotonic()
        try:
            self.root.attributes("-alpha", 0.0)
        except Exception:
            pass
        self._transparent_job = self.root.after(120, self._transparent_tick)

    def exit_transparent(self):
        if not self.transparent:
            return
        self.transparent = False
        self._interact_at = _time.monotonic()
        if self._transparent_job:
            try:
                self.root.after_cancel(self._transparent_job)
            except Exception:
                pass
            self._transparent_job = None
        try:
            self.root.attributes("-alpha", DOCK_ALPHA if self.docked else 1.0)
        except Exception:
            pass

    def _transparent_tick(self):
        self._transparent_job = None
        if not self.transparent:
            return
        held = _time.monotonic() - self._transparent_at
        if held >= TRANSPARENT_HOLD or (held >= 0.45 and self._cursor_outside()):
            self.exit_transparent()
            return
        self._transparent_job = self.root.after(120, self._transparent_tick)

    def _cursor_outside(self):
        """光标是否已离开时钟窗口（留 4px 余量）"""
        try:
            pt = ctypes.wintypes.POINT()
            ctypes.windll.user32.GetCursorPos(ctypes.byref(pt))
            x, y = self.root.winfo_rootx(), self.root.winfo_rooty()
            w, h = self.root.winfo_width(), self.root.winfo_height()
        except Exception:
            return True
        return not (x - 4 <= pt.x <= x + w + 4 and y - 4 <= pt.y <= y + h + 4)

    def open_calendar(self):
        """三击: 弹出系统日历（Win+N = Win11 通知中心/日历, Win10 操作中心）"""
        self._interact_at = _time.monotonic()
        if self.transparent:
            self.exit_transparent()
        try:
            u = ctypes.windll.user32
            u.keybd_event(0x5B, 0, 0, 0)              # VK_LWIN 按下
            u.keybd_event(ord("N"), 0, 0, 0)          # N 按下
            u.keybd_event(ord("N"), 0, 0x0002, 0)     # N 抬起
            u.keybd_event(0x5B, 0, 0x0002, 0)         # Win 抬起
        except Exception:
            pass

    def on_wheel(self, e):
        self._interact_at = _time.monotonic()
        if e.state & 0x4:  # Ctrl+滚轮: 调整大小
            d = 1.1 if e.delta > 0 else 1 / 1.1
            self.set_scale(self.scale * d)
        else:              # 滚轮: 切换时区
            self.set_tz((self.tz_idx - 1 if e.delta > 0 else self.tz_idx + 1) % len(ZONES))

    def set_tz(self, i):
        self.tz_idx = i
        self.save_cfg()    # 记住选定的时区
        self.update_time(self.now_dt())
        self.show_toast(ZONES[i][0])

    def set_scale(self, s):
        s = min(1.6, max(0.6, s))
        if abs(s - self.scale) < 0.001:
            return
        self.scale = s
        self.build_layout()
        self.redraw()
        self.save_cfg()

    def toggle_sec(self):
        self.show_sec = not self.show_sec
        self.build_layout()
        self.redraw()
        if self.docked:
            self.update_dock()
        self.save_cfg()

    def toggle_h24(self):
        self.h24 = not self.h24
        self.update_time(self.now_dt())
        self.save_cfg()

    def reset_scale(self):
        self.set_scale(1.0)
        if self.docked:
            self.update_dock()

    # ---------- 单击: 显示桌面 ----------
    def show_desktop(self):
        """单击时钟 = 系统「显示桌面」(Win+D): 再点一次恢复所有窗口"""
        self._interact_at = _time.monotonic()
        try:
            u = ctypes.windll.user32
            u.keybd_event(0x5B, 0, 0, 0)              # VK_LWIN 按下
            u.keybd_event(ord("D"), 0, 0, 0)          # D 按下
            u.keybd_event(ord("D"), 0, 0x0002, 0)     # D 抬起
            u.keybd_event(0x5B, 0, 0x0002, 0)         # Win 抬起
        except Exception:
            pass

    def copy_time(self):
        dt = self.now_dt()
        txt = dt.strftime("%Y-%m-%d %H:%M:%S")
        try:
            self.root.clipboard_clear()
            self.root.clipboard_append(txt)
            self.root.update()
        except Exception:
            return
        self.show_toast("已复制")

    def open_time_settings(self):
        try:
            os.startfile("ms-settings:dateandtime")
        except Exception:
            pass

    # ---------- 开机自启动 ----------
    def auto_enabled(self):
        try:
            r = subprocess.run(["reg", "query", RUN_KEY, "/v", RUN_NAME],
                               capture_output=True, creationflags=0x08000000)
            return r.returncode == 0
        except Exception:
            return False

    def toggle_auto(self):
        if not self.auto_enabled():
            pyw = shutil.which("pythonw") or sys.executable
            cmd = f'"{pyw}" "{os.path.abspath(__file__)}"'
            try:
                subprocess.run(["reg", "add", RUN_KEY, "/v", RUN_NAME, "/t", "REG_SZ", "/d", cmd, "/f"],
                               capture_output=True, creationflags=0x08000000)
            except Exception:
                pass
        else:
            try:
                subprocess.run(["reg", "delete", RUN_KEY, "/v", RUN_NAME, "/f"],
                               capture_output=True, creationflags=0x08000000)
            except Exception:
                pass

    # ---------- 菜单 ----------
    def _menu_dark(self):
        """菜单深浅色跟随时钟所在任务栏的实际底色"""
        c = self.taskbar_color
        if c is None:
            return self._theme_is_dark()
        return 0.299 * c[0] + 0.587 * c[1] + 0.114 * c[2] <= 150

    @staticmethod
    def _utc_label(dt):
        off = dt.utcoffset() or timedelta(0)
        mins = int(off.total_seconds() // 60)
        sign = "+" if mins >= 0 else "-"
        h, m = divmod(abs(mins), 60)
        return f"UTC{sign}{h}" + (f":{m:02d}" if m else "")

    def menu_items(self):
        dt = self.now_dt()
        zname = ZONES[self.tz_idx][0]
        week = "星期" + "一二三四五六日"[dt.weekday()]
        cities = [dict(kind="radio", text=n, checked=(i == self.tz_idx),
                       command=lambda i=i: self.set_tz(i))
                  for i, (n, z) in enumerate(ZONES) if not isinstance(z, int)]
        offsets = [dict(kind="radio", text=n, checked=(i == self.tz_idx),
                        command=lambda i=i: self.set_tz(i))
                   for i, (n, z) in enumerate(ZONES) if isinstance(z, int)]
        is_city = not isinstance(ZONES[self.tz_idx][1], int)
        return [
            dict(kind="header", text=f"{dt.year}年{dt.month}月{dt.day}日  {week}",
                 sub=f"{zname} · {self._utc_label(dt)}"),
            dict(kind="sep"),
            dict(text="显示桌面", icon=GL["desktop"], hint="单击", command=self.show_desktop),
            dict(text="打开日历", icon=GL["calendar"], hint="三击", command=self.open_calendar),
            dict(text="查看系统时钟", icon=GL["eye"], hint="双击", command=self.toggle_transparent),
            dict(kind="sep"),
            dict(text="一键吸附到任务栏", icon=GL["pin"], hint="对齐系统时钟位置", command=self.reset_pos),
            dict(text="重新适配显示", icon=GL["reset"], hint="分辨率/缩放变化后",
                 command=lambda: self.readapt(manual=True)),
            dict(kind="sep"),
            dict(kind="sub", text="城市时区", icon=GL["globe"], hint=zname if is_city else "", items=cities),
            dict(kind="sub", text="UTC 固定偏移", icon=GL["offset"], hint="" if is_city else zname,
                 items=offsets),
            dict(kind="sep"),
            dict(kind="check", text="显示秒", icon=GL["sec"], checked=self.show_sec, command=self.toggle_sec),
            dict(kind="check", text="24 小时制", icon=GL["clock"], checked=self.h24, command=self.toggle_h24),
            dict(kind="check", text="开机自启动", icon=GL["power"], checked=self.auto_enabled(),
                 command=self.toggle_auto),
            dict(text="恢复默认大小", icon=GL["zoom"], hint="Ctrl+滚轮",
                 enabled=abs(self.scale - 1.0) > 0.001, command=self.reset_scale),
            dict(kind="sep"),
            dict(text="复制当前时间", icon=GL["copy"], command=self.copy_time),
            dict(text="日期和时间设置", icon=GL["settings"], command=self.open_time_settings),
            dict(text="帮助", icon=GL["help"], command=self.show_help),
            dict(text="退出", icon=GL["exit"], command=self.quit),
        ]

    def close_menu(self):
        if self.menu is not None:
            self.menu.close()
            self.menu = None

    def on_menu(self, e):
        self._interact_at = _time.monotonic()
        if self.transparent:
            self.exit_transparent()
        self.close_menu()
        self.menu = FluentMenu(self, self.menu_items(), e.x_root, e.y_root, self._menu_dark())
        self._menu_armed = False
        self.root.after(60, self._menu_watch)

    def _menu_watch(self):
        """点击菜单以外任何地方(含其它程序)即关闭菜单。
        自绘菜单没有系统菜单的模态循环, 用按键状态轮询实现。"""
        m = self.menu
        if m is None or m.closed:
            self.menu = None
            return
        self._interact_at = _time.monotonic()
        try:
            ks = ctypes.windll.user32.GetAsyncKeyState
            down = any(ks(vk) & 0x8000 for vk in (0x01, 0x02, 0x04))   # 左/右/中键
            pt = ctypes.wintypes.POINT()
            ctypes.windll.user32.GetCursorPos(ctypes.byref(pt))
            if not down:
                self._menu_armed = True        # 打开菜单的那次右键松开后才开始判定
            elif self._menu_armed and not m.contains(pt.x, pt.y):
                self.close_menu()
                return
        except Exception:
            pass
        self.root.after(50, self._menu_watch)

    def show_help(self):
        messagebox.showinfo(
            "世界时钟",
            "停靠模式: 平嵌在任务栏, 跟随任务栏位置/主题/自动隐藏\n"
            "单击        显示桌面（再点一次恢复窗口）\n"
            "左键拖动    脱离停靠, 变为悬浮卡片并移动\n"
            "双击        原地全透明, 直接看到系统时钟（光标移开或 6 秒后自动恢复）\n"
            "三击        弹出系统日历\n"
            "滚轮        切换时区（切换瞬间提示城市名）\n"
            "Ctrl+滚轮   调整窗口大小\n"
            "右键        打开菜单（含开机自启动开关）\n"
            "            「一键吸附到任务栏」: 无论在哪都立即对齐系统时钟位置\n"
            "            「重新适配显示」: 分辨率/缩放/显示器变化后手动重新适配\n"
            "分辨率或缩放来回切换时会自动重新适配\n"
            "全屏放映/播放（含网页视频全屏）时自动隐藏, 退出全屏自动恢复",
        )

    def quit(self):
        self.close_menu()
        self.save_cfg()
        self.root.destroy()

    # ---------- 时间 ----------
    def now_dt(self):
        _, z = ZONES[self.tz_idx]
        if isinstance(z, str):
            try:
                return datetime.now(ZoneInfo(z))
            except Exception:
                z = FALLBACK_OFFSET.get(z, 0)
        if z is None:
            return datetime.now().astimezone()
        return datetime.now(timezone(timedelta(hours=z)))

    # ---------- 配置 ----------
    def load_cfg(self):
        try:
            with open(CONFIG_PATH, "r", encoding="utf-8") as f:
                return json.load(f)
        except Exception:
            return {}

    def save_cfg(self):
        if self.test:
            return
        try:
            data = {
                "tz_idx": self.tz_idx,
                "h24": self.h24,
                "sec": self.show_sec,
                "scale": round(self.scale, 3),
                "docked": self.docked,
                "pos": [self.root.winfo_x(), self.root.winfo_y()],
            }
            with open(CONFIG_PATH, "w", encoding="utf-8") as f:
                json.dump(data, f, ensure_ascii=False, indent=1)
        except Exception:
            pass


def _selftest(root, app):
    root.update()
    out = []
    for _ in range(4):
        app.tick()
        root.update()
        _time.sleep(0.21)
    out.append(f"win={app.W}x{app.H} docked={app.docked}")
    tm = app.cv.find_withtag("tm")
    out.append(f"time_items={len(tm)}")
    out.append(f"date_items={len(app.cv.find_withtag('dt'))}")
    # 全时区循环渲染
    for i in range(len(ZONES)):
        app.set_tz(i)
        root.update()
    out.append(f"tz_cycle={len(ZONES)}")
    # 时区提示
    app.set_tz(1)
    root.update()
    out.append(f"toast_items={len(app.cv.find_withtag('toast'))}")
    app.clear_toast()
    root.update()
    out.append(f"date_back={len(app.cv.find_withtag('dt'))}")
    out.append(f"fullscreen_check={app._foreground_is_fullscreen()}")
    class _R:
        def __init__(self, l, t, r, b):
            self.left, self.top, self.right, self.bottom = l, t, r, b
    mon = _R(0, 0, 1920, 1080)
    work = _R(0, 0, 1920, 1032)
    out.append(f"cover_fs={app._rect_covers(_R(0, 0, 1920, 1080), mon)}")
    out.append(f"cover_max={app._rect_covers(_R(-8, -8, 1928, 1040), mon)}")
    out.append(f"cover_html5_work={app._rect_covers(_R(0, 0, 1920, 1032), work)}")
    # 拖动脱离停靠 + 双击回停靠
    class E:
        pass
    e = E()
    e.x_root = app.root.winfo_x() + 30
    e.y_root = app.root.winfo_y() + 30
    app.on_press(e)
    e.x_root += 90
    e.y_root += 50
    app.on_move(e)
    root.update()
    out.append(f"undocked={not app.docked} alpha={app.root.attributes('-alpha')}")
    app.reset_pos()
    root.update()
    out.append(f"redocked={app.docked} H={app.H}")
    # 宽限期逻辑: 模拟全屏前台
    app._interact_at = _time.monotonic()
    app._foreground_is_fullscreen = lambda: True
    app.update_visibility()
    root.update()
    out.append(f"grace_keep_visible={not app.hidden}")
    app._interact_at = _time.monotonic() - 10.0
    app.update_visibility()
    root.update()
    out.append(f"grace_expired_hide={app.hidden}")
    app._foreground_is_fullscreen = lambda: False
    app.update_visibility()
    root.update()
    out.append(f"restored={not app.hidden}")
    # 连击: 双击透明 / 三击日历（stub 掉真实 Win+N, 避免自检时弹出系统面板）
    fired = {}
    app.open_calendar = lambda: fired.setdefault("cal", True)
    app.show_desktop = lambda: fired.setdefault("desk", True)
    # 单击: 显示桌面（等连击窗口结束后才触发, 不影响双击/三击）
    app._register_click()
    app._click_fire()
    out.append(f"single_desktop={bool(fired.get('desk'))}")
    app._register_click()
    app._register_click()
    out.append(f"click_seq2={app._click_seq}")
    app._click_fire()
    root.update()
    out.append(f"dbl_transparent={app.transparent} alpha={app.root.attributes('-alpha')}")
    app.exit_transparent()
    root.update()
    out.append(f"restored_alpha={app.root.attributes('-alpha')}")
    app._register_click(); app._register_click(); app._register_click()
    root.update()
    out.append(f"triple_calendar={bool(fired.get('cal'))}")

    # 自绘菜单: 打开 / 悬停级联 / 关闭
    class M:
        pass
    me = M()
    me.x_root, me.y_root = 400, 600
    app.on_menu(me)
    root.update()
    m = app.menu
    out.append(f"menu_open={m is not None and not m.closed} size={m.W}x{m.vh}")
    sub_i = next(i for i, it in enumerate(m.items) if it.get("kind") == "sub")
    m._open_child(sub_i)
    root.update()
    out.append(f"submenu={m.child is not None} rows={len(m.child.items)}")
    m.child._key_move(1)
    m._activate(next(i for i, it in enumerate(m.items) if it.get("text") == "24 小时制"))
    root.update()
    root.after(30)
    root.update()
    out.append(f"menu_closed={app.menu is None} h24_toggled={not app.h24}")
    app.toggle_h24()

    # 缩放
    app.set_scale(1.4)
    root.update()
    out.append(f"scale1={app.W}x{app.H}")
    app.set_scale(0.7)
    root.update()
    out.append(f"scale2={app.W}x{app.H}")
    app.set_scale(1.0)
    root.update()
    print("SELFTEST OK | " + " | ".join(out))


def _already_running():
    """单实例保护: 已有实例运行时返回 True（重复双击启动不再叠加时钟）"""
    try:
        h = ctypes.windll.kernel32.CreateMutexW(None, False, "Local\\WorldClockMutex")
        if ctypes.windll.kernel32.GetLastError() == 183:  # ERROR_ALREADY_EXISTS
            ctypes.windll.kernel32.CloseHandle(h)
            return True
        globals()["_MUTEX"] = h   # 句柄需保持存活
        return False
    except Exception:
        return False


def _signal_quit_and_wait(timeout=3.0):
    """通知正在运行的实例退出(命名事件), 等它释放单实例锁。成功返回 True。"""
    try:
        k = ctypes.windll.kernel32
        k.CreateEventW.restype = ctypes.c_void_p
        k.SetEvent.argtypes = [ctypes.c_void_p]
        k.CloseHandle.argtypes = [ctypes.c_void_p]
        h = k.CreateEventW(None, True, False, WorldClock.QUIT_EVENT)
        if not h:
            return False
        k.SetEvent(h)
        end = _time.monotonic() + timeout
        ok = False
        while _time.monotonic() < end:
            _time.sleep(0.1)
            if not _already_running():
                ok = True
                break
        k.ResetEvent.argtypes = [ctypes.c_void_p]
        k.ResetEvent(h)                      # 清掉信号, 免得新实例一启动就自己退出
        k.CloseHandle(h)
        return ok
    except Exception:
        return False


def _takeover_old_instances(include_self=False):
    """已有实例在运行（多半是旧版本/旧路径的世界时钟）: 结束它们, 由本程序接管。
    返回 True 表示已拿到单实例锁。只在检测到冲突时才执行, 正常启动不受影响。"""
    me, parent = os.getpid(), os.getppid()
    # include_self=True(--restart): 连同一路径的实例也结束, 用于重启
    mine = "::no-such-path::" if include_self else os.path.abspath(__file__).replace("'", "''")
    ps = (
        "Get-CimInstance Win32_Process | Where-Object {{ "
        "($_.Name -like 'python*' -or $_.Name -like 'pyw*') -and "
        "$_.CommandLine -match '世界时钟\\.pyw|worldclock' -and "
        "$_.CommandLine -notmatch 'idlelib|--test' -and "
        # 只结束其它路径的旧版本; 同一份脚本已在运行时不重启自己
        "$_.CommandLine.IndexOf('{2}', [StringComparison]::OrdinalIgnoreCase) -lt 0 -and "
        "$_.ProcessId -ne {0} -and $_.ProcessId -ne {1} }} | "
        "ForEach-Object {{ Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }}"
    ).format(me, parent, mine)
    try:
        subprocess.run(["powershell", "-NoProfile", "-NonInteractive", "-Command", ps],
                       capture_output=True, timeout=20, creationflags=0x08000000)
    except Exception:
        pass
    for _ in range(40):                  # 等旧进程退出、释放互斥锁; 若在跑的就是本脚本则超时退出
        if not _already_running():
            return True
        _time.sleep(0.1)
    return False


def _fix_autostart():
    """开机自启动项若指向别的路径（旧版本）, 改为指向本脚本, 保证开机只启动新版。
    未开启自启动时不做任何改动。"""
    try:
        import winreg
        path = r"Software\Microsoft\Windows\CurrentVersion\Run"
        k = winreg.OpenKey(winreg.HKEY_CURRENT_USER, path, 0,
                           winreg.KEY_QUERY_VALUE | winreg.KEY_SET_VALUE)
        try:
            old, _ = winreg.QueryValueEx(k, RUN_NAME)
        except FileNotFoundError:
            winreg.CloseKey(k)
            return
        me = os.path.abspath(__file__)
        # 用当前正在运行的这个 Python(的 pythonw), 保证开机自启和手动启动是同一个解释器/同一版 Tk
        exe = sys.executable
        w = os.path.join(os.path.dirname(exe), "pythonw.exe")
        pyw = w if os.path.isfile(w) else exe
        want = f'"{pyw}" "{me}"'
        if str(old) != want:
            winreg.SetValueEx(k, RUN_NAME, 0, winreg.REG_SZ, want)
        winreg.CloseKey(k)
    except Exception:
        pass


def main():
    test = "--test" in sys.argv
    if ZoneInfo is None:
        raise RuntimeError("需要 Python 3.9+（内置 zoneinfo 模块）")
    if not test:
        restart = "--restart" in sys.argv
        if _already_running():
            if not (restart and _signal_quit_and_wait()):
                # 旧版本(没有退出信号)或非重启启动: 用 PowerShell 结束旧路径/旧版本实例
                if not _takeover_old_instances(include_self=restart):
                    return
        _fix_autostart()
    root = tk.Tk()
    app = WorldClock(root, test=test)
    if test:
        _selftest(root, app)
        root.destroy()
    else:
        root.mainloop()


if __name__ == "__main__":
    try:
        main()
    except Exception:
        import traceback
        if "--test" in sys.argv:
            traceback.print_exc()
            sys.exit(1)
        messagebox.showerror("世界时钟出错", traceback.format_exc())
        sys.exit(1)
