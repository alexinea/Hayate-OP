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

for /R "nuget_packages" %%s in (*symbols.nupkg) do (
    del "%%s"
)

echo.
echo.

::push nuget packages to server
for /R "nuget_packages" %%s in (*.nupkg) do (
::    dotnet nuget push "%%s" -s "Release" --skip-duplicate --no-symbols
    dotnet nuget push "%%s" -s "Release" --skip-duplicate
    echo.
)

::get back to build folder
cd scripts