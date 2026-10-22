<#
.SYNOPSIS
  Registra el conector de PassDesk para Chrome, Edge y/o Brave (sin permisos de administrador).

.DESCRIPTION
  Hace tres cosas, todas dentro de tu usuario:
    1. Escribe junto a PassDesk.NativeHost.exe la lista de extensiones autorizadas (allowed-origins.txt).
    2. Crea el manifiesto de Native Messaging (browser\com.passdesk.autofill.json) con la ruta ABSOLUTA del conector
       (admite espacios) y como único origen permitido el de la extensión de PassDesk.
    3. Lo registra en HKCU\Software\<navegador>\NativeMessagingHosts\com.passdesk.autofill para cada navegador elegido.

  NO instala la extensión en el navegador (hay que cargarla desde browser\extension: ver docs\NAVEGADOR.md), NO activa el
  autorrelleno (se activa en PassDesk › Ajustes › Navegador) y NO toca la bóveda, los documentos ni las copias de seguridad.

.PARAMETER InstallDir
  Carpeta donde está instalado PassDesk (la de install.ps1). Debe contener PassDesk.NativeHost.exe.

.PARAMETER Browsers
  Navegadores a registrar: Chrome, Edge, Brave (por defecto, los tres).

.PARAMETER ExtensionId
  Identificador de la extensión. Por defecto se calcula de la clave de browser\extension\manifest.json. Solo hace falta si
  usas una extensión empaquetada con otra clave (por ejemplo, la publicada en una tienda).

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install-browser.ps1 -Browsers Chrome,Edge
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\PassDesk"),
    [ValidateSet("Chrome", "Edge", "Brave")]
    [string[]]$Browsers = @("Chrome", "Edge", "Brave"),
    [string]$ExtensionId
)

$ErrorActionPreference = "Stop"
$hostName = "com.passdesk.autofill"
$hostExe = Join-Path $InstallDir "PassDesk.NativeHost.exe"
$extensionDir = Join-Path $InstallDir "browser\extension"
$browserDir = Join-Path $InstallDir "browser"

if (-not (Test-Path $hostExe)) {
    throw "No se encuentra $hostExe. Publica e instala PassDesk primero: scripts\publish.ps1 y scripts\install.ps1."
}

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
    Brave  = "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\$hostName"
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
