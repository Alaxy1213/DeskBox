@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem 源码在本目录；实际运行/存数据在 RUN 目录（G:\DeskBox）。
rem 注意：不要放在 G:\Workspace 下运行 —— 会被工作区沙箱限制（工作区外只能新建、
rem 不能删除），拖拽"剪切"会失败。G:\DeskBox 与工作区无关，不受影响。
set "RUN=G:\DeskBox"

echo === DeskBox 构建 ===
taskkill /f /im DeskBox.exe >nul 2>&1
timeout /t 1 /nobreak >nul

where dotnet >nul 2>&1
if errorlevel 1 (
    echo [错误] 找不到 dotnet，请先安装 .NET SDK。
    pause
    exit /b 1
)

dotnet build -c Release -v m --nologo
if errorlevel 1 (
    echo.
    echo [失败] 编译出错。
    pause
    exit /b 1
)

if not exist "%RUN%" mkdir "%RUN%"
copy /y "bin\Release\net48\DeskBox.exe" "%RUN%\DeskBox.exe" >nul
echo.
echo [完成] 已更新 %RUN%\DeskBox.exe
start "" "%RUN%\DeskBox.exe"
