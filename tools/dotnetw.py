# -*- coding: utf-8 -*-
"""dotnet 构建包装器：为受限环境补全标准 Windows 环境变量后调用 dotnet。

本机沙箱环境缺少 ProgramFiles(x86) / ProgramData / SystemRoot 等变量，
会让 NuGet 的机器级配置路径解析失败（Value cannot be null. Parameter 'path1'）。
这里用 Python 补齐后再执行 dotnet，保证还原/构建/发布全程正常。

用法: python tools/dotnetw.py <dotnet 参数...>   例如: python tools/dotnetw.py build -c Release
"""
import os
import subprocess
import sys

DEFAULTS = {
    "ProgramFiles": r"C:\Program Files",
    "ProgramFiles(x86)": r"C:\Program Files (x86)",
    "ProgramW6432": r"C:\Program Files",
    "CommonProgramFiles": r"C:\Program Files\Common Files",
    "CommonProgramFiles(x86)": r"C:\Program Files (x86)\Common Files",
    "CommonProgramW6432": r"C:\Program Files\Common Files",
    "ProgramData": r"C:\ProgramData",
    "ALLUSERSPROFILE": r"C:\ProgramData",
    "SystemRoot": r"C:\Windows",
    "windir": r"C:\Windows",
    "USERPROFILE": r"C:\Users\ins",
    "HOMEDRIVE": "C:",
    "HOMEPATH": r"\Users\ins",
    "HOME": r"C:\Users\ins",
    "APPDATA": r"C:\Users\ins\AppData\Roaming",
    "LOCALAPPDATA": r"C:\Users\ins\AppData\Local",
    "TEMP": r"C:\Users\ins\AppData\Local\Temp",
    "TMP": r"C:\Users\ins\AppData\Local\Temp",
}

DOTNET = r"C:\Users\ins\.dotnet-sdk\dotnet.exe"


def main() -> int:
    env = dict(os.environ)
    for k, v in DEFAULTS.items():
        if not env.get(k):
            env[k] = v
    env["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"
    env["DOTNET_NOLOGO"] = "1"
    env["DOTNET_CLI_HOME"] = env.get("DOTNET_CLI_HOME") or env["USERPROFILE"]
    cwd = os.path.abspath(os.path.join(os.path.dirname(os.path.abspath(__file__)), ".."))
    return subprocess.call([DOTNET] + sys.argv[1:], env=env, cwd=cwd)


if __name__ == "__main__":
    sys.exit(main())
