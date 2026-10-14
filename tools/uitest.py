# -*- coding: utf-8 -*-
"""UI 测试小工具：截屏 / 查找窗口 / 模拟鼠标（ctypes + Pillow，无外部依赖）。

用法:
  python tools/uitest.py shot <输出路径>
  python tools/uitest.py list                    列出可见窗口标题
  python tools/uitest.py find <标题子串>          打印该窗口的矩形 (l,t,r,b)
  python tools/uitest.py click <x> <y>
  python tools/uitest.py drag <x1> <y1> <x2> <y2>
  python tools/uitest.py cursor
"""
import sys
import time
import ctypes
from ctypes import wintypes

from PIL import ImageGrab

user32 = ctypes.windll.user32

MOUSEEVENTF_LEFTDOWN = 0x0002
MOUSEEVENTF_LEFTUP = 0x0004


def shot(path):
    img = ImageGrab.grab(all_screens=True)
    img.save(path)
    print("SHOT", path, img.size)


def enum_windows():
    result = []

    def cb(hwnd, lparam):
        if not user32.IsWindowVisible(hwnd):
            return True
        length = user32.GetWindowTextLengthW(hwnd)
        if length <= 0:
            return True
        buf = ctypes.create_unicode_buffer(length + 1)
        user32.GetWindowTextW(hwnd, buf, length + 1)
        result.append((hwnd, buf.value))
        return True

    proc = ctypes.WINFUNCTYPE(ctypes.c_bool, wintypes.HWND, wintypes.LPARAM)(cb)
    user32.EnumWindows(proc, 0)
    return result


def find_windows(title_part):
    return [(h, t) for (h, t) in enum_windows() if title_part in t]


def rect(hwnd):
    r = wintypes.RECT()
    user32.GetWindowRect(hwnd, ctypes.byref(r))
    return (r.left, r.top, r.right, r.bottom)


def move(x, y):
    user32.SetCursorPos(int(x), int(y))


def click(x, y):
    move(x, y)
    time.sleep(0.06)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.04)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.12)


def drag(x1, y1, x2, y2, steps=26):
    move(x1, y1)
    time.sleep(0.12)
    user32.mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, 0)
    time.sleep(0.06)
    for i in range(1, steps + 1):
        nx = int(x1 + (x2 - x1) * i / steps)
        ny = int(y1 + (y2 - y1) * i / steps)
        move(nx, ny)
        time.sleep(0.02)
    time.sleep(0.08)
    user32.mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, 0)
    time.sleep(0.35)


def cursor():
    p = wintypes.POINT()
    user32.GetCursorPos(ctypes.byref(p))
    return (p.x, p.y)


if __name__ == "__main__":
    cmd = sys.argv[1] if len(sys.argv) > 1 else "shot"
    if cmd == "shot":
        shot(sys.argv[2])
    elif cmd == "list":
        for h, t in enum_windows():
            print(hex(h), repr(t))
    elif cmd == "find":
        for h, t in find_windows(sys.argv[2]):
            print(hex(h), repr(t), rect(h))
    elif cmd == "click":
        click(int(sys.argv[2]), int(sys.argv[3]))
        print("CLICKED", sys.argv[2], sys.argv[3])
    elif cmd == "move":
        move(int(sys.argv[2]), int(sys.argv[3]))
        print("MOVED", sys.argv[2], sys.argv[3])
    elif cmd == "drag":
        drag(int(sys.argv[2]), int(sys.argv[3]), int(sys.argv[4]), int(sys.argv[5]))
        print("DRAGGED")
    elif cmd == "cursor":
        print(cursor())
    else:
        print("unknown command")
