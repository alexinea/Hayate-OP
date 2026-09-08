@echo off

echo =======================================================================
echo Hayate Object Pool
echo =======================================================================

::go to parent folder
cd ..

::create nuget_packages
if not exist nuget_packages (
    md nuget_packages
    echo Created nuget_packages folder.
)

::clear nuget_packages
for /R "nuget_packages" %%s in (*) do (
    del "%%s"
)
echo Cleaned up all nuget packages.
echo.

::start to package all projects

::hayate-op
dotnet pack src/HayateOP -c Release -o nuget_packages --no-restore

::hayate-op extensions
dotnet pack src/HayateOP.Extensions.DependencyInjection   -c Release -o nuget_packages --no-restore
dotnet pack src/HayateOP.Extensions.Configuration         -c Release -o nuget_packages --no-restore
dotnet pack src/HayateOP.Extensions.Diagnostics           -c Release -o nuget_packages --no-restore
dotnet pack src/HayateOP.Extensions.Endpoints             -c Release -o nuget_packages --no-restore
dotnet pack src/HayateOP.Extensions.HealthCheck           -c Release -o nuget_packages --no-restore

::hayate-op extensions (PR-C: T14 OpenTelemetry / T15 ObjectPoolCompat)
dotnet pack src/HayateOP.Extensions.OpenTelemetry          -c Release -o nuget_packages --no-restore
dotnet pack src/HayateOP.Extensions.ObjectPoolCompat       -c Release -o nuget_packages --no-restore

for /R "nuget_packages" %%s in (*symbols.nupkg) do (
    del "%%s"
)

echo.
echo.

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