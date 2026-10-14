# -*- coding: utf-8 -*-
"""进程资源采样/控制小工具（纯 ctypes，无第三方依赖）。

用法:
  python tools/procstat.py cpu <exe名> <采样秒数>    采样 CPU / 内存 / 线程 / 句柄
  python tools/procstat.py kill <exe名>              终止进程
  python tools/procstat.py list                      列出进程
"""
import ctypes
import sys
import time
from ctypes import wintypes

kernel32 = ctypes.windll.kernel32
psapi = ctypes.windll.psapi

TH32CS_SNAPPROCESS = 0x2
PROCESS_QUERY_LIMITED_INFORMATION = 0x1000
PROCESS_TERMINATE = 0x0001


class PROCESSENTRY32(ctypes.Structure):
    _fields_ = [
        ("dwSize", wintypes.DWORD),
        ("cntUsage", wintypes.DWORD),
        ("th32ProcessID", wintypes.DWORD),
        ("th32DefaultHeapID", ctypes.c_void_p),
        ("th32ModuleID", wintypes.DWORD),
        ("cntThreads", wintypes.DWORD),
        ("th32ParentProcessID", wintypes.DWORD),
        ("pcPriClassBase", ctypes.c_long),
        ("dwFlags", wintypes.DWORD),
        ("szExeFile", ctypes.c_char * 260),
    ]


class PROCESS_MEMORY_COUNTERS(ctypes.Structure):
    _fields_ = [
        ("cb", wintypes.DWORD),
        ("PageFaultCount", wintypes.DWORD),
        ("PeakWorkingSetSize", ctypes.c_size_t),
        ("WorkingSetSize", ctypes.c_size_t),
        ("QuotaPeakPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPagedPoolUsage", ctypes.c_size_t),
        ("QuotaPeakNonPagedPoolUsage", ctypes.c_size_t),
        ("QuotaNonPagedPoolUsage", ctypes.c_size_t),
        ("PagefileUsage", ctypes.c_size_t),
        ("PeakPagefileUsage", ctypes.c_size_t),
    ]


def list_processes():
    result = []
    snap = kernel32.CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0)
    if snap == -1:
        return result
    entry = PROCESSENTRY32()
    entry.dwSize = ctypes.sizeof(PROCESSENTRY32)
    ok = kernel32.Process32First(snap, ctypes.byref(entry))
    while ok:
        name = entry.szExeFile.decode("mbcs", "ignore")
        result.append((entry.th32ProcessID, name, entry.cntThreads))
        ok = kernel32.Process32Next(snap, ctypes.byref(entry))
    kernel32.CloseHandle(snap)
    return result


def find_pid(exe_name):
    for pid, name, _threads in list_processes():
        if name.lower() == exe_name.lower():
            return pid
    return None


def _ft_to_int(ft):
    return (ft.dwHighDateTime << 32) | ft.dwLowDateTime


def cpu_time(pid):
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return None
    c = wintypes.FILETIME()
    e = wintypes.FILETIME()
    k = wintypes.FILETIME()
    u = wintypes.FILETIME()
    ok = kernel32.GetProcessTimes(h, ctypes.byref(c), ctypes.byref(e), ctypes.byref(k), ctypes.byref(u))
    kernel32.CloseHandle(h)
    return _ft_to_int(k) + _ft_to_int(u) if ok else None


def mem_info(pid):
    h = kernel32.OpenProcess(PROCESS_QUERY_LIMITED_INFORMATION, False, pid)
    if not h:
        return None
    pmc = PROCESS_MEMORY_COUNTERS()
    pmc.cb = ctypes.sizeof(PROCESS_MEMORY_COUNTERS)
    ok = psapi.GetProcessMemoryInfo(h, ctypes.byref(pmc), pmc.cb)
    handles = wintypes.DWORD()
    kernel32.GetProcessHandleCount(h, ctypes.byref(handles))
    kernel32.CloseHandle(h)
    if not ok:
        return None
    return pmc.WorkingSetSize, pmc.PeakWorkingSetSize, handles.value


def sample(exe_name, seconds):
    pid = find_pid(exe_name)
    if pid is None:
        print("NOT_RUNNING")
        return
    t0 = cpu_time(pid)
    m0 = mem_info(pid)
    time.sleep(seconds)
    t1 = cpu_time(pid)
    m1 = mem_info(pid)
    if t0 is None or t1 is None or m0 is None or m1 is None:
        print("SAMPLE_FAILED")
        return
    dcpu = (t1 - t0) / 1e7  # 秒
    pct = dcpu / seconds * 100
    threads = next((t for p, n, t in list_processes() if p == pid), -1)
    print(f"pid={pid} cpu={pct:.2f}% (单核百分比) over {seconds}s | "
          f"WorkingSet={m1[0] / 1048576:.1f}MB (峰值 {m1[1] / 1048576:.1f}MB) | "
          f"threads={threads} handles={m1[2]}")


def kill(exe_name):
    pid = find_pid(exe_name)
    if pid is None:
        print("NOT_RUNNING")
        return
    h = kernel32.OpenProcess(PROCESS_TERMINATE, False, pid)
    if h:
        kernel32.TerminateProcess(h, 0)
        kernel32.CloseHandle(h)
        print("KILLED", pid)
    else:
        print("KILL_FAILED")


if __name__ == "__main__":
    cmd = sys.argv[1]
    if cmd == "cpu":
        sample(sys.argv[2], int(sys.argv[3]))
    elif cmd == "kill":
        kill(sys.argv[2])
    elif cmd == "list":
        for pid, name, threads in list_processes():
            print(pid, name, "threads=" + str(threads))
