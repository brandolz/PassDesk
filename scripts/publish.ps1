<#
.SYNOPSIS
  Compila, prueba y publica PassDesk en la carpeta dist\PassDesk.

.DESCRIPTION
  -Mode framework      (por defecto) Ejecutable único (~1 MB + dependencias) que usa el
                       "Runtime de escritorio de .NET 10" instalado en Windows.
  -Mode selfcontained  Incluye el runtime de .NET (más grande, no requiere instalar nada).
  -Zip                 (con -Mode selfcontained) Crea además el paquete de prueba
                       dist\PassDesk-prueba-<versión>-win-x64.zip y su huella SHA-256 (.sha256). Exige que no haya
                       cambios sin commitear en archivos versionados y nunca sobrescribe un ZIP que ya exista.

  vcruntime140.dll (runtime de Visual C++ que necesita libsodium) se mete dentro de PassDesk.exe si están instaladas las
  Build Tools de Visual Studio con las herramientas de C++ x64; con -Zip son obligatorias.

  Requiere el SDK de .NET 10 (https://dotnet.microsoft.com/download). Si no está en el PATH,
  se usa la instalación local de %LOCALAPPDATA%\Microsoft\dotnet.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\publish.ps1 -Mode selfcontained -Zip
#>
param(
    [ValidateSet("framework", "selfcontained")]
    [string]$Mode = "framework",
    [string]$Output = (Join-Path $PSScriptRoot "..\dist"),
    [switch]$SkipTests,
    [switch]$Zip
)

$ErrorActionPreference = "Stop"
$root = Resolve-Path (Join-Path $PSScriptRoot "..")
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

# Comprobaciones antes de compilar, para no descubrir un problema tras varios minutos.
if ($Zip) {
    if ($Mode -ne "selfcontained") { throw "-Zip crea el paquete de prueba: úsalo con -Mode selfcontained." }
    # El ZIP se reparte: tiene que corresponder exactamente a un commit, sin cambios sin guardar en archivos versionados.
    if (& git -C $root status --porcelain --untracked-files=no) {
        throw "Hay cambios sin commitear en archivos versionados: el paquete debe salir de un commit limpio."
    }

    $version = ([xml](Get-Content (Join-Path $root "Directory.Build.props") -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1
    $zipName = "PassDesk-prueba-$version-win-x64.zip"
    $zipPath = Join-Path $Output $zipName
    if (Test-Path $zipPath) { throw "Ya existe ${zipPath}: no se sobrescribe un paquete que puede haberse repartido." }
}

# vcruntime140.dll redistribuible (carpeta Redist de Visual Studio o de sus Build Tools), o $null si no hay.
function Find-VcRuntime {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} "Microsoft Visual Studio\Installer\vswhere.exe"
    if (-not (Test-Path $vswhere)) { return $null }
    $vs = & $vswhere -latest -products * -requires Microsoft.VisualStudio.Component.VC.Tools.x86.x64 -property installationPath
    if (-not $vs) { return $null }
    $redist = Get-ChildItem -LiteralPath (Join-Path $vs "VC\Redist\MSVC") -Directory -ErrorAction SilentlyContinue |
        Where-Object { $_.Name -match '^\d+\.\d+\.\d+$' } | Sort-Object { [version]$_.Name } -Descending | Select-Object -First 1
    if (-not $redist) { return $null }
    return Get-ChildItem -Path (Join-Path $redist.FullName "x64\Microsoft.VC*.CRT\vcruntime140.dll") -ErrorAction SilentlyContinue |
        Select-Object -First 1 -ExpandProperty FullName
}

# libsodium (Argon2id) necesita vcruntime140.dll. Va dentro del ejecutable para que el probador no tenga que instalar Visual
# C++, y solo si lleva firma incrustada de Microsoft (la del catálogo de este equipo no viaja con el archivo).
$vcRuntime = Find-VcRuntime
if ($vcRuntime) {
    $signature = Get-AuthenticodeSignature -FilePath $vcRuntime
    $embedded = try { [Security.Cryptography.X509Certificates.X509Certificate]::CreateFromSignedFile($vcRuntime).Subject } catch { "" }
    if ($signature.Status -ne "Valid" -or $embedded -notmatch 'O=Microsoft Corporation') {
        throw "$vcRuntime no tiene una firma válida e incrustada de Microsoft; no se incluye."
    }
    Write-Host "Runtime de Visual C++ incluido: $vcRuntime" -ForegroundColor Cyan
}
elseif ($Zip) {
    throw "No se encontró vcruntime140.dll redistribuible. Instala las Build Tools de Visual Studio con las herramientas de C++ x64 (Microsoft.VisualStudio.Component.VC.Tools.x86.x64)."
}
else {
    Write-Warning "Sin vcruntime140.dll redistribuible: PassDesk necesitará Visual C++ instalado para libsodium."
}

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
    & $dotnet test (Join-Path $root "tests\PassDesk.Browser.Tests\PassDesk.Browser.Tests.csproj") -c Release
    if ($LASTEXITCODE -ne 0) { throw "Las pruebas de la integración con navegadores han fallado; no se publica." }
}

$target = Join-Path $Output "PassDesk"
if (Test-Path $target) { Remove-Item $target -Recurse -Force }
$selfContained = if ($Mode -eq "selfcontained") { "true" } else { "false" }

Write-Host "Publicando ($Mode)..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\PassDesk.App\PassDesk.App.csproj") `
    -c Release -r win-x64 --self-contained $selfContained `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:DebugType=none `
    "-p:VcRuntimeDll=$vcRuntime" `
    -o $target
if ($LASTEXITCODE -ne 0) { throw "La publicación ha fallado." }

# Conector de Native Messaging (autorrelleno en navegadores): junto a PassDesk.exe, que es donde PassDesk espera encontrarlo.
Write-Host "Publicando el conector del navegador..." -ForegroundColor Cyan
& $dotnet publish (Join-Path $root "src\PassDesk.NativeHost\PassDesk.NativeHost.csproj") `
    -c Release -r win-x64 --self-contained $selfContained `
    -p:PublishSingleFile=true -p:DebugType=none `
    -o $target
if ($LASTEXITCODE -ne 0) { throw "La publicación del conector ha fallado." }

# Extensión sin empaquetar (se carga desde esta carpeta en Chrome, Edge o Brave).
$extensionTarget = Join-Path $target "browser\extension"
New-Item -ItemType Directory -Force -Path $extensionTarget | Out-Null
Copy-Item (Join-Path $root "extension\*") $extensionTarget -Recurse -Force
Copy-Item (Join-Path $root "docs\NAVEGADOR.md") (Join-Path $target "browser") -ErrorAction SilentlyContinue

Copy-Item (Join-Path $root "README.md") $target -ErrorAction SilentlyContinue
# Para probadores (solo versión autocontenida): instrucciones y los scripts que registran y quitan el conector desde la
# carpeta del ZIP. Visual C++ ya no hace falta: su runtime va dentro de PassDesk.exe.
if ($Mode -eq "selfcontained") {
    Copy-Item (Join-Path $root "docs\LEEME-PRUEBA.txt") $target
    Copy-Item (Join-Path $root "scripts\install-browser.ps1"), (Join-Path $root "scripts\uninstall-browser.ps1") $target
}

if ($Zip) {
    # Entrada a entrada y con «/»: ZipFile.CreateFromDirectory en Windows PowerShell 5.1 escribe «\», que el formato ZIP
    # no admite y que otras herramientas convierten en nombres de archivo con barras.
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    $base = (Resolve-Path -LiteralPath $target).ProviderPath.TrimEnd('\')
    $archive = [System.IO.Compression.ZipFile]::Open($zipPath, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem -LiteralPath $base -Recurse -File) {
            $entryName = $file.FullName.Substring($base.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally {
        $archive.Dispose()
    }
    $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash
    [IO.File]::WriteAllText("$zipPath.sha256", "$hash  $zipName`n", (New-Object System.Text.UTF8Encoding($false)))
    $commit = (& git -C $root rev-parse --short HEAD)
    $tag = (& git -C $root tag --points-at HEAD) | Where-Object { $_ -eq "v$version" }
    Write-Host ""
    Write-Host "Paquete: $zipPath" -ForegroundColor Green
    Write-Host "SHA-256: $hash"
    Write-Host "Commit:  $commit  (etiqueta: $(if ($tag) { $tag } else { 'ninguna' }))"
    if (-not $tag) { Write-Warning "HEAD no tiene la etiqueta v${version}: este ZIP sirve de ensayo, no para repartir." }
}
Write-Host ""
Write-Host "Listo: $target\PassDesk.exe" -ForegroundColor Green
Write-Host "Para instalarlo en tu usuario: powershell -ExecutionPolicy Bypass -File scripts\install.ps1"
