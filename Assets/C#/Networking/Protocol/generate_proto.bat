@echo off
REM ============================================================================
REM  Generate C# (Game.cs) from game.proto with protoc.
REM
REM  Usage:
REM    - Double-click this file, or
REM    - generate_proto.bat "D:\protobuf\bin\protoc.exe"
REM
REM  Output: Game.cs in this folder, namespace Game.Protocol
REM
REM  NOTE: keep this file ASCII-only. cmd.exe parses .bat in the system code
REM        page, so non-ASCII comments can corrupt the script.
REM ============================================================================
setlocal

set "PROTOC=%~1"
if "%PROTOC%"=="" set "PROTOC=protoc"

if exist "%PROTOC%" goto :run

where "%PROTOC%" >nul 2>nul
if errorlevel 1 (
    echo [ERROR] protoc not found.
    echo         Add protoc.exe to PATH, or pass the full path:
    echo         generate_proto.bat "D:\protobuf\bin\protoc.exe"
    exit /b 1
)

:run
pushd "%~dp0"

"%PROTOC%" --version
if errorlevel 1 (
    echo [ERROR] protoc failed to run.
    popd
    exit /b 1
)

"%PROTOC%" --proto_path=. --csharp_out=. game.proto
if errorlevel 1 (
    echo [ERROR] code generation failed.
    popd
    exit /b 1
)

echo.
echo [OK] Generated: %~dp0Game.cs
popd
endlocal
