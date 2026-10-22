@echo off
pushd "%~dp0"
dotnet run --project Demo\Demo.csproj -c Release -- --width 2560 --height 1440 --mode 0 %*
set "exitCode=%errorlevel%"
popd
if not "%exitCode%"=="0" pause
exit /b %exitCode%
