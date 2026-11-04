<#
.SYNOPSIS
  Registra el conector de PassDesk para Chrome, Edge y/o Brave (sin permisos de administrador).

.DESCRIPTION
  Hace tres cosas, todas dentro de tu usuario:
    1. Escribe junto a PassDesk.NativeHost.exe la lista de extensiones autorizadas (allowed-origins.txt).
    2. Crea el manifiesto de Native Messaging (browser\com.passdesk.autofill.json) con la ruta ABSOLUTA del conector
       (admite espacios) y como único origen permitido el de la extensión de PassDesk.
    3. Lo registra en HKCU\Software\<navegador>\NativeMessagingHosts\com.passdesk.autofill para cada navegador elegido.
       Brave no tiene clave propia en Windows: lee la de Chrome (HKCU\Software\Google\Chrome\NativeMessagingHosts).

  NO instala la extensión en el navegador (hay que cargarla desde browser\extension: ver docs\NAVEGADOR.md), NO activa el
  autorrelleno (se activa en PassDesk › Ajustes › Navegador) y NO toca la bóveda, los documentos ni las copias de seguridad.

.PARAMETER InstallDir
  Carpeta donde está instalado PassDesk. Debe contener PassDesk.NativeHost.exe. Por defecto, la carpeta de este script si el
  conector está a su lado (paquete de prueba); si no, la de install.ps1.

.PARAMETER Browsers
  Navegadores a registrar: Chrome, Edge, Brave (por defecto, los tres), separados por comas.

.PARAMETER ExtensionId
  Identificador de la extensión. Por defecto se calcula de la clave de browser\extension\manifest.json. Solo hace falta si
  usas una extensión empaquetada con otra clave (por ejemplo, la publicada en una tienda).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install-browser.ps1 -Browsers Chrome,Edge
#>
param(
    [string]$InstallDir,
    [string[]]$Browsers = @("Chrome", "Edge", "Brave"),
    [string]$ExtensionId
)

$ErrorActionPreference = "Stop"
$hostName = "com.passdesk.autofill"

if (-not $InstallDir) {
    $InstallDir = if (Test-Path (Join-Path $PSScriptRoot "PassDesk.NativeHost.exe")) { $PSScriptRoot } else { Join-Path $env:LOCALAPPDATA "Programs\PassDesk" }
}

# Con powershell -File, «Edge,Brave» llega como un solo texto: se separa aquí en vez de con ValidateSet.
$Browsers = @($Browsers -split ',' | ForEach-Object { $_.Trim() } | Where-Object { $_ })
$unknown = @($Browsers | Where-Object { $_ -notin "Chrome", "Edge", "Brave" })
if ($unknown.Count -gt 0) {
    throw "Navegador no admitido: $($unknown -join ', '). Usa Chrome, Edge o Brave."
}

if (-not (Test-Path (Join-Path $InstallDir "PassDesk.NativeHost.exe"))) {
    throw "No se encuentra PassDesk.NativeHost.exe en $InstallDir. Publica e instala PassDesk primero: scripts\publish.ps1 y scripts\install.ps1."
}

# El manifiesto necesita la ruta absoluta del conector aunque -InstallDir sea relativa (por ejemplo, «.»).
$InstallDir = (Resolve-Path -LiteralPath $InstallDir).ProviderPath
$hostExe = Join-Path $InstallDir "PassDesk.NativeHost.exe"
$extensionDir = Join-Path $InstallDir "browser\extension"
$browserDir = Join-Path $InstallDir "browser"

function Get-ExtensionIdFromManifest([string]$manifestPath) {
    # El identificador de una extensión con «key» es el SHA-256 de su clave pública, 32 primeros caracteres, con 0-f → a-p.
    $manifest = Get-Content -Raw -Path $manifestPath | ConvertFrom-Json
    if (-not $manifest.key) { return $null }
    $der = [Convert]::FromBase64String($manifest.key)
    # Compatible con Windows PowerShell 5.1 (sin SHA256.HashData).
    $hash = [System.Security.Cryptography.SHA256]::Create().ComputeHash($der)
    $hex = -join ($hash[0..15] | ForEach-Object { $_.ToString("x2") })
    return -join ($hex.ToCharArray() | ForEach-Object { [char]([int][char]'a' + [Convert]::ToInt32([string]$_, 16)) })
}

if (-not $ExtensionId) {
    $manifestPath = Join-Path $extensionDir "manifest.json"
    if (-not (Test-Path $manifestPath)) {
        throw "No se encuentra $manifestPath. Indica el identificador con -ExtensionId."
    }
    $ExtensionId = Get-ExtensionIdFromManifest $manifestPath
}
if ($ExtensionId -notmatch '^[a-p]{32}$') {
    throw "Identificador de extensión no válido: debe tener 32 letras de la a a la p."
}

$origin = "chrome-extension://$ExtensionId/"
$utf8 = New-Object System.Text.UTF8Encoding($false)

# 1) Extensiones autorizadas para el conector (solo esta).
[System.IO.File]::WriteAllText((Join-Path $InstallDir "allowed-origins.txt"), "$origin`n", $utf8)

# 2) Manifiesto de Native Messaging: ruta absoluta y un único origen permitido.
New-Item -ItemType Directory -Force -Path $browserDir | Out-Null
$manifestFile = Join-Path $browserDir "$hostName.json"
$escapedExe = $hostExe.Replace('\', '\\')
$json = @"
{
  "name": "$hostName",
  "description": "PassDesk - conector local para el autorrelleno del navegador",
  "path": "$escapedExe",
  "type": "stdio",
  "allowed_origins": [ "$origin" ]
}
"@
[System.IO.File]::WriteAllText($manifestFile, $json, $utf8)

# 3) Registro del manifiesto para cada navegador (solo HKCU).
$keys = @{
    Chrome = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
    Edge   = "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName"
    # Brave en Windows solo busca en las claves de Chrome y de Chromium (comprobado en Brave 1.98, Chromium 156).
    Brave  = "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName"
}
foreach ($browser in $Browsers) {
    New-Item -Path $keys[$browser] -Force -Value $manifestFile | Out-Null
    Write-Host "Registrado para $browser." -ForegroundColor Green
}

Write-Host ""
Write-Host "Conector: $hostExe"
Write-Host "Manifiesto: $manifestFile"
Write-Host "Extensión autorizada: $origin"
Write-Host ""
Write-Host "Siguientes pasos (ver docs\NAVEGADOR.md):" -ForegroundColor Cyan
Write-Host "  1. Carga la extensión sin empaquetar desde: $extensionDir"
Write-Host "  2. En PassDesk › Ajustes › Navegador, activa la integración."
