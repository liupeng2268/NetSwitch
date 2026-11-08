@echo off
chcp 65001 >nul
setlocal

REM ============================================================
REM  NetSwitch 构建脚本
REM
REM  作用：把 src\NetSwitch 编译打包成单个自包含 exe，放到 发布\ 目录。
REM
REM  为什么用自包含单文件：
REM    医院内网机器通常不允许随便装软件。自包含 exe 约 64 MB，
REM    拷过去双击就能跑，不需要目标机安装 .NET 运行时。
REM
REM  关于 .NET SDK：
REM    脚本优先用仓库自带的 tools\dotnet-sdk（不污染系统环境）；
REM    找不到时回退到系统安装的 dotnet。两者都没有则提示安装。
REM ============================================================

set "ROOT=%~dp0"
set "PROJ=%ROOT%src\NetSwitch\NetSwitch.csproj"
set "OUTDIR=%ROOT%发布"

echo.
echo ============================================================
echo   NetSwitch 构建
echo ============================================================
echo.

REM ---------- 1. 定位 dotnet ----------
REM 注意：只检测 dotnet.exe 是否存在是不够的 —— Windows 常常装了 .NET「运行时」
REM 但没有 SDK，此时 `dotnet build` 会报 "No .NET SDKs were found"。
REM 所以这里用 `dotnet --list-sdks` 实际验证 SDK 是否可用。
set "DOTNET="

if exist "%ROOT%tools\dotnet-sdk\dotnet.exe" (
    set "DOTNET=%ROOT%tools\dotnet-sdk\dotnet.exe"
    echo [1/4] 使用仓库自带 SDK: tools\dotnet-sdk
    goto :found
)

where dotnet >nul 2>nul
if not errorlevel 1 (
    REM 有 dotnet.exe，但要确认它真的带 SDK
    for /f "usebackq delims=" %%V in (`dotnet --list-sdks 2^>nul`) do set "HASSDK=1"
    if defined HASSDK (
        set "DOTNET=dotnet"
        echo [1/4] 使用系统安装的 dotnet SDK
        goto :found
    )
    echo [1/4] 检测到 dotnet.exe，但没有可用的 SDK
)

echo.
echo [错误] 未找到可用的 .NET SDK。
echo.
echo   已检测到：本机有 .NET 运行时，但没有 SDK（无法编译）。
echo.
echo   请任选一种方式：
echo     A) 把 .NET 8 SDK 解压到  tools\dotnet-sdk\   （推荐，绿色免安装）
echo        下载：https://dotnet.microsoft.com/download/dotnet/8.0
echo        解压后应能看到  tools\dotnet-sdk\dotnet.exe
echo.
echo     B) 安装 .NET 8 SDK 到系统
echo        https://dotnet.microsoft.com/download/dotnet/8.0
echo.
echo   说明：SDK 只用于「编译打包」这一步；
echo         生成出来的 NetSwitch.exe 是自包含的，目标机器不需要装任何东西。
echo.
pause
exit /b 1

:found

REM ---------- 2. 检查项目文件 ----------
if not exist "%PROJ%" (
    echo.
    echo [错误] 找不到项目文件：
    echo        %PROJ%
    echo.
    pause
    exit /b 1
)
echo [2/4] 项目文件已找到

REM ---------- 3. 环境准备 ----------
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_NOLOGO=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"

REM 清理旧产物，避免拿到过期 exe
if exist "%OUTDIR%" (
    echo [3/4] 清理旧的 发布\ 目录
    rmdir /s /q "%OUTDIR%" 2>nul
) else (
    echo [3/4] 无需清理
)

REM ---------- 4. 编译发布 ----------
echo [4/4] 正在编译（首次约需 1-2 分钟）...
echo.

"%DOTNET%" publish "%PROJ%" ^
    -c Release ^
    -r win-x64 ^
    --self-contained true ^
    -p:PublishSingleFile=true ^
    -p:IncludeNativeLibrariesForSelfExtract=true ^
    -p:EnableCompressionInSingleFile=true ^
    -o "%OUTDIR%"

if errorlevel 1 (
    echo.
    echo ============================================================
    echo   构建失败。请查看上面的错误信息。
    echo ============================================================
    echo.
    pause
    exit /b 1
)

if not exist "%OUTDIR%\NetSwitch.exe" (
    echo.
    echo [错误] 构建命令成功，但没有生成 NetSwitch.exe。
    echo.
    pause
    exit /b 1
)

echo.
echo ============================================================
echo   构建成功
echo ============================================================
echo.
echo   产物：%OUTDIR%\NetSwitch.exe
for %%F in ("%OUTDIR%\NetSwitch.exe") do echo   大小：%%~zF 字节
echo.
echo   分发方式：把 NetSwitch.exe 和 profiles.json 一起拷到目标机器，
echo             双击即可运行，无需安装 .NET 运行时。
echo.

REM ---------- 5. 可选：立即冒烟测试 ----------
REM 用 set /p 而非 choice：choice 在部分精简版 Windows / PE 环境不存在，
REM 且它不读标准输入，自动化时容易卡住。
set "RUNSMOKE="
set /p "RUNSMOKE=是否现在运行一次命令行自检（-ListProfiles）？[y/N] "
if /i not "%RUNSMOKE%"=="y" goto :end

echo.
"%OUTDIR%\NetSwitch.exe" -ListProfiles
echo.

:end
endlocal
