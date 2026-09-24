$ErrorActionPreference = 'Stop'
$runner = Join-Path $PSScriptRoot '.tools/dotnet/dotnet.exe'
if (!(Test-Path $runner)) { $runner = 'dotnet' }
& $runner run --project (Join-Path $PSScriptRoot 'src/Legenda.App/Legenda.App.csproj')
