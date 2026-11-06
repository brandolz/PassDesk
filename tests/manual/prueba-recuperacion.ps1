<#
.SYNOPSIS
  Prueba manual de la recuperación de la bóveda (P-1) con perfiles FICTICIOS. Nunca toca %LOCALAPPDATA%\PassDesk.

.DESCRIPTION
  Paso 1   -Base         Compila PassDesk y lo abre con un perfil vacío. Crea ahí la bóveda de prueba (ver
                         PRUEBA-RECUPERACION.md), añade dos cuentas y sal (icono de la bandeja › Salir).
  Paso 2   -Escenarios   Copia ese perfil en una carpeta por escenario y simula en cada una un daño distinto.
  Paso 3   -Abrir <n>    Abre PassDesk con el escenario <n> (por ejemplo «-Abrir 2-sin-principal-bak»).
           -Estado <n>   Archivos de la bóveda del escenario (tamaño y huella): para comprobar que no se borró nada.
           -Lista        Escenarios y resultado esperado.

  Las carpetas de prueba van en %TEMP%\PassDesk-prueba-recuperacion (o en -Carpeta). Ejecútalo desde el panel de Terminal.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File tests\manual\prueba-recuperacion.ps1 -Base
#>
param(
    [switch]$Base,
    [switch]$Escenarios,
    [string]$Abrir,
    [string]$Estado,
    [switch]$Lista,
    [string]$Carpeta = (Join-Path $env:TEMP "PassDesk-prueba-recuperacion")
)

$ErrorActionPreference = "Stop"
$repo = (Resolve-Path (Join-Path $PSScriptRoot "..\..")).ProviderPath
$exe = Join-Path $repo "src\PassDesk.App\bin\Release\net10.0-windows10.0.19041.0\PassDesk.exe"
$Carpeta = [IO.Path]::GetFullPath($Carpeta).TrimEnd('\')
$baseDir = Join-Path $Carpeta "base"

# Nunca la carpeta de datos real: ni ella, ni algo dentro de ella, ni una carpeta que la contenga.
$real = [IO.Path]::GetFullPath((Join-Path $env:LOCALAPPDATA "PassDesk")).TrimEnd('\')
if ($Carpeta -ieq $real -or $Carpeta.StartsWith("$real\", [StringComparison]::OrdinalIgnoreCase) -or $real.StartsWith("$Carpeta\", [StringComparison]::OrdinalIgnoreCase)) {
    throw "La carpeta de prueba no puede ser ni contener la carpeta de datos real de PassDesk ($real)."
}

$casos = [ordered]@{
    "1-normal"              = "Bóveda intacta. Debe abrirse como siempre (cuentas A y B), sin pantalla de recuperación."
    "2-sin-principal-bak"   = "Falta vault.pdv y queda .bak. Pantalla «Recuperar la bóveda»; al recuperar, solo la cuenta A."
    "3-sin-principal-tmp"   = "Falta vault.pdv y queda .tmp. Al recuperar, cuentas A y B."
    "4-sin-principal-ambas" = "Falta vault.pdv; quedan .tmp y .bak. Al recuperar, cuentas A y B (la copia más reciente)."
    "5-principal-danada"    = "vault.pdv con el contenido cifrado alterado. Al desbloquear se ofrece recuperar; resultado: cuenta A."
    "6-principal-a-medias"  = "vault.pdv cortado por la mitad. Al desbloquear se ofrece recuperar; resultado: cuenta A."
    "7-apartar"             = "Como el 2, para «Apartar las copias y crear una bóveda nueva…»: las copias pasan a recuperacion\ sin borrarse."
    "8-archivar"            = "Como el 5, para «Empezar con una bóveda nueva…»: tras archivar, se ofrece recuperar (no crear)."
}

$localDotnet = Join-Path $env:LOCALAPPDATA "Microsoft\dotnet\dotnet.exe"

# dotnet con un SDK 10 (para compilar), o $null si en esta sesión solo hay runtime.
function Get-SdkDotnet {
    $candidates = @($localDotnet) + @(Get-Command dotnet -ErrorAction SilentlyContinue | ForEach-Object { $_.Source })
    foreach ($candidate in $candidates | Where-Object { $_ -and (Test-Path $_) }) {
        if (& $candidate --list-sdks 2>$null | Where-Object { $_ -match '^10\.' }) { return $candidate }
    }
    return $null
}

# La compilación de bin\ debe ser posterior a todo el código fuente, o no incluiría la corrección.
function Assert-BuildIsCurrent {
    if (-not (Test-Path $exe)) { throw "No hay compilación en $exe y esta sesión no tiene SDK de .NET 10 para crearla." }
    $binDir = Split-Path $exe
    # Por proyecto: MSBuild no reescribe la DLL de un proyecto que no ha cambiado.
    foreach ($project in Get-ChildItem -LiteralPath (Join-Path $repo "src") -Directory) {
        $dllName = if ($project.Name -eq "PassDesk.App") { "PassDesk.dll" } else { "$($project.Name).dll" }
        $dll = Join-Path $binDir $dllName
        if (-not (Test-Path $dll)) { continue }
        $built = (Get-Item -LiteralPath $dll).LastWriteTime
        $newest = Get-ChildItem -Path $project.FullName -Recurse -File -Include *.cs, *.xaml |
            Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' } | Sort-Object LastWriteTime -Descending | Select-Object -First 1
        if ($newest -and $newest.LastWriteTime -gt $built) {
            throw "$dllName ($built) es anterior a $($newest.FullName) ($($newest.LastWriteTime)). Hay que recompilar con un SDK de .NET 10."
        }
    }
    Write-Host "Se usa la compilación existente de bin\Release: es posterior al código fuente de cada proyecto." -ForegroundColor Yellow
}

function Start-PassDesk([string]$dataDir) {
    if (-not (Test-Path $exe)) { throw "No está compilado: ejecuta primero -Base." }
    # Solo para el proceso que se abre: la terminal se queda como estaba.
    $before = @{ Data = $env:PASSDESK_DATA_DIR; Root = $env:DOTNET_ROOT }
    try {
        $env:PASSDESK_DATA_DIR = $dataDir
        # Si .NET está en la carpeta del usuario (no en Program Files), el ejecutable necesita saberlo.
        if (Test-Path $localDotnet) { $env:DOTNET_ROOT = Split-Path $localDotnet }
        Start-Process -FilePath $exe | Out-Null
    }
    finally {
        $env:PASSDESK_DATA_DIR = $before.Data
        $env:DOTNET_ROOT = $before.Root
    }
    Write-Host "PassDesk abierto con el perfil de prueba $dataDir" -ForegroundColor Green
}

# Altera un byte del contenido cifrado (AES-GCM) y vuelve a poner la capa DPAPI: la bóveda se lee, pero no se puede abrir.
function Set-Damaged([string]$path) {
    Add-Type -AssemblyName System.Security
    $entropy = [Text.Encoding]::UTF8.GetBytes("PassDesk/LocalVault/DPAPI/v1")
    $bytes = [IO.File]::ReadAllBytes($path)
    [byte[]]$blob = $bytes[6..($bytes.Length - 1)]
    $envelope = [Security.Cryptography.ProtectedData]::Unprotect($blob, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    $envelope[$envelope.Length - 2] = [byte]($envelope[$envelope.Length - 2] -bxor 0x10)
    $protected = [Security.Cryptography.ProtectedData]::Protect($envelope, $entropy, [Security.Cryptography.DataProtectionScope]::CurrentUser)
    [IO.File]::WriteAllBytes($path, [byte[]]($bytes[0..5] + $protected))
}

function Show-State([string]$dir) {
    $files = @(Get-ChildItem -LiteralPath $dir -File -Filter "vault*.pdv*")
    $recovery = Join-Path $dir "recuperacion"
    if (Test-Path $recovery) { $files += @(Get-ChildItem -LiteralPath $recovery -File) }
    if ($files.Count -eq 0) { Write-Host "(no hay archivos de bóveda)"; return }
    $files | ForEach-Object {
        [pscustomobject]@{
            Archivo = $_.FullName.Substring($dir.Length + 1)
            Bytes   = $_.Length
            Huella  = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.Substring(0, 12)
        }
    } | Format-Table -AutoSize | Out-String | Write-Host
}

function Assert-Scenario([string]$name) {
    if (-not $casos.Contains($name)) { throw "Escenario desconocido: $name. Usa -Lista." }
    $dir = Join-Path $Carpeta $name
    if (-not (Test-Path $dir)) { throw "No existe $dir. Ejecuta antes -Escenarios." }
    return $dir
}

if ($Lista) {
    $casos.GetEnumerator() | ForEach-Object { [pscustomobject]@{ Escenario = $_.Key; Esperado = $_.Value } } | Format-Table -AutoSize -Wrap | Out-String | Write-Host
}
elseif ($Base) {
    $dotnet = Get-SdkDotnet
    if ($dotnet) {
        Write-Host "Compilando PassDesk (Release) con la corrección..." -ForegroundColor Cyan
        $ErrorActionPreference = "Continue"
        & $dotnet build (Join-Path $repo "src\PassDesk.App\PassDesk.App.csproj") -c Release -v quiet -nologo
        $built = $LASTEXITCODE
        $ErrorActionPreference = "Stop"
        if ($built -ne 0) { throw "La compilación ha fallado." }
    }
    else {
        Assert-BuildIsCurrent
    }
    New-Item -ItemType Directory -Force -Path $baseDir | Out-Null
    Start-PassDesk $baseDir
    Write-Host ""
    Write-Host "En la ventana de PassDesk (perfil de prueba):" -ForegroundColor Cyan
    Write-Host "  1. Crea la bóveda con la contraseña de PRUEBA-RECUPERACION.md."
    Write-Host "  2. Añade una cuenta «Cuenta A» y, después, otra «Cuenta B» (datos inventados)."
    Write-Host "  3. Sal: icono de la bandeja › Salir. Luego ejecuta este script con -Escenarios."
}
elseif ($Escenarios) {
    $pdv = Join-Path $baseDir "vault.pdv"
    if (-not (Test-Path $pdv) -or -not (Test-Path "$pdv.bak")) {
        throw "Falta la bóveda base o su .bak en $baseDir. Ejecuta -Base, crea la bóveda, añade las dos cuentas y sal."
    }
    if (Get-Process -Name PassDesk -ErrorAction SilentlyContinue | Where-Object { $_.Path -ieq $exe }) {
        throw "Hay un PassDesk de prueba abierto. Ciérralo (icono de la bandeja › Salir) y vuelve a intentarlo."
    }
    foreach ($name in $casos.Keys) {
        $dir = Join-Path $Carpeta $name
        # Solo se sustituyen las carpetas de escenario de esta prueba (datos ficticios); la base no se toca.
        if (Test-Path $dir) { Remove-Item -LiteralPath $dir -Recurse -Force }
        Copy-Item -LiteralPath $baseDir -Destination $dir -Recurse
        $v = Join-Path $dir "vault.pdv"
        switch -Wildcard ($name) {
            "2-*" { Remove-Item -LiteralPath $v }
            "3-*" { Move-Item -LiteralPath $v -Destination "$v.tmp"; Remove-Item -LiteralPath "$v.bak" }
            "4-*" { Move-Item -LiteralPath $v -Destination "$v.tmp" }
            "5-*" { Set-Damaged $v }
            "6-*" { $b = [IO.File]::ReadAllBytes($v); [IO.File]::WriteAllBytes($v, [byte[]]$b[0..([int][Math]::Floor($b.Length / 2) - 1)]) }
            "7-*" { Remove-Item -LiteralPath $v }
            "8-*" { Set-Damaged $v }
        }
        Write-Host ("{0,-24} listo" -f $name) -ForegroundColor Green
    }
    Write-Host ""
    Write-Host "Siguiente: -Abrir <escenario> (empieza por 1-normal). -Estado <escenario> antes y después de cada prueba."
}
elseif ($Abrir) {
    $dir = Assert-Scenario $Abrir
    Write-Host "Esperado: $($casos[$Abrir])" -ForegroundColor Cyan
    Start-PassDesk $dir
}
elseif ($Estado) {
    Show-State (Assert-Scenario $Estado)
}
else {
    Get-Help $MyInvocation.MyCommand.Path -Detailed
}
