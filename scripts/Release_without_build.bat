@echo off

echo =======================================================================
echo Hayate Object Pool (without build
echo =======================================================================

::go to parent folder
cd ..

::create nuget_packages
if not exist nuget_packages (
    md nuget_packages
    echo Created nuget_packages folder.
)

::push nuget packages to server
:: api key 来自环境变量 NUGET_APIKEY（用户级 setx 设置，避免明文入库）
if "%NUGET_APIKEY%"=="" (
    echo [ERROR] Environment variable NUGET_APIKEY is not set.
    echo Set it once with:  setx NUGET_APIKEY "your-nuget-org-api-key"
    exit /b 1
)
for /R "nuget_packages" %%s in (*.nupkg) do (
    dotnet nuget push "%%s" -s "nuget.org" -k "%NUGET_APIKEY%" --skip-duplicate
    echo.
)

::get back to build folder
cd scripts