<#
.SYNOPSIS
  Compila, prueba y publica PassDesk en la carpeta dist\PassDesk.

.DESCRIPTION
  -Mode framework      (por defecto) Ejecutable único (~1 MB + dependencias) que usa el
                       "Runtime de escritorio de .NET 10" instalado en Windows.
  -Mode selfcontained  Incluye el runtime de .NET (más grande, no requiere instalar nada).

  Requiere el SDK de .NET 10 (https://dotnet.microsoft.com/download). Si no está en el PATH,
  se usa la instalación local de %LOCALAPPDATA%\Microsoft\dotnet.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1
#>
param(
    [ValidateSet("framework", "selfcontained")]
    [string]$Mode = "framework",
    [string]$Output = (Join-Path $PSScriptRoot "..\dist"),
    [switch]$SkipTests
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

function Find-Dotnet {
    $local = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"
    $candidates = @()
    $cmd = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($cmd) { $candidates += $cmd.Source }
    if (Test-Path $local) { $candidates += $local }
    foreach ($candidate in $candidates) {
        $sdks = & $candidate --list-sdks 2>$null
        if ($sdks | Where-Object { $_ -match '^10\.' }) { return $candidate }
    }
    throw "No se encontró el SDK de .NET 10. Instálalo con: winget install Microsoft.DotNet.SDK.10"
}

$dotnet = Find-Dotnet
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
Write-Host "Usando $dotnet" -ForegroundColor Cyan

if (-not $SkipTests) {
    Write-Host "Ejecutando pruebas automáticas..." -ForegroundColor Cyan
    & $dotnet test (Join-Path $root "tests\PassDesk.Tests\PassDesk.Tests.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Las pruebas han fallado; no se publica." }
}

$target = Join-Path $Output "PassDesk"
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
$selfContained = if ($Mode -eq "selfcontained") { "true" } else { "false" }

Write-Host "Publicando ($Mode)..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\PassDesk.App\PassDesk.App.csproj") `
    -c Release -r win-x64 --self-contained $selfContained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    -o $target
if ($LASTEXITCODE -ne 0) { throw "La publicación ha fallado." }

Copy-Item (Join-Path $root "README.md") $target -ErrorAction SilentlyContinue
# Para probadores (solo versión autocontenida): instrucciones y el instalador oficial de Visual C++ que necesita
# libsodium, si se ha descargado en dist\ (no se versiona).
if ($Mode -eq "selfcontained") {
    Copy-Item (Join-Path $root "docs\LEEME-PRUEBA.txt") $target
    $vcRedist = Join-Path $Output "vc_redist.x64.exe"
    if (Test-Path $vcRedist) { Copy-Item $vcRedist $target }
    else { Write-Warning "Falta $vcRedist (descárgalo de https://aka.ms/vc14/vc_redist.x64.exe); el paquete no lo incluirá." }
}
Write-Host ""
Write-Host "Listo: $target\PassDesk.exe" -ForegroundColor Green
Write-Host "Para instalarlo en tu usuario: powershell -ExecutionPolicy Bypass -File scripts\install.ps1"
