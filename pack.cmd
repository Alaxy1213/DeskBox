@echo off
chcp 65001 >nul
cd /d "%~dp0"

rem 打一个干净的分发包：只有 DeskBox.exe + README.txt，不含任何个人数据
rem （配置 / items 文件 / 日志 / 备份都不打进去）。

echo === 1/3 编译 ===
dotnet build -c Release -v m --nologo
if errorlevel 1 (
    echo.
    echo [失败] 编译出错，未打包。
    pause
    exit /b 1
)

echo.
echo === 2/3 收集文件 ===
if exist dist rmdir /s /q dist
mkdir dist\DeskBox
copy /y "bin\Release\net48\DeskBox.exe" "dist\DeskBox\" >nul
copy /y "README.txt" "dist\DeskBox\" >nul
if not exist "dist\DeskBox\DeskBox.exe" (
    echo [失败] 没找到编译产物。
    pause
    exit /b 1
)

echo.
echo === 3/3 压缩 ===
powershell -NoProfile -ExecutionPolicy Bypass -Command ^
  "Compress-Archive -Path 'dist\DeskBox' -DestinationPath 'dist\DeskBox-win.zip' -CompressionLevel Optimal -Force"
if not exist "dist\DeskBox-win.zip" (
    echo [失败] 压缩失败。
    pause
    exit /b 1
)

echo.
echo [完成] 分发包：%~dp0dist\DeskBox-win.zip
echo         解压后运行 DeskBox\DeskBox.exe 即可（免安装）。
start "" "%~dp0dist"
