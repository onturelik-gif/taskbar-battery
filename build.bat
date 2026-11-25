@echo off
C:\Windows\Microsoft.NET\Framework64\v4.0.30319\csc.exe /nologo /target:winexe /optimize+ /codepage:65001 /win32manifest:app.manifest /out:TaskbarBattery.exe /r:System.Drawing.dll /r:System.Windows.Forms.dll TaskbarBattery.cs
if %ERRORLEVEL% EQU 0 (
    echo Build OK: TaskbarBattery.exe
) else (
    echo Build FAILED!
    exit /b %ERRORLEVEL%
)
