<#
.SYNOPSIS
  Quita el registro del conector de PassDesk en Chrome, Edge y Brave.

.DESCRIPTION
  Elimina únicamente las entradas HKCU\Software\<navegador>\NativeMessagingHosts\com.passdesk.autofill (y solo si apuntan al
  manifiesto de PassDesk), el manifiesto browser\com.passdesk.autofill.json y allowed-origins.txt. NUNCA toca la bóveda
  cifrada, los documentos ni las copias de seguridad (%LOCALAPPDATA%\PassDesk), ni el programa.

  La extensión se quita desde el propio navegador (Extensiones › Quitar). Para desactivar el autorrelleno sin desinstalar nada,
  desmarca la casilla en PassDesk › Ajustes › Navegador.
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\PassDesk")
)

$ErrorActionPreference = "Stop"
$hostName = "com.passdesk.autofill"
$manifestFile = Join-Path $InstallDir "browser\$hostName.json"

$keys = @(
    "HKCU:\Software\Google\Chrome\NativeMessagingHosts\$hostName",
    "HKCU:\Software\Microsoft\Edge\NativeMessagingHosts\$hostName",
    "HKCU:\Software\BraveSoftware\Brave-Browser\NativeMessagingHosts\$hostName"
)
foreach ($key in $keys) {
    if (-not (Test-Path $key)) { continue }
    $value = (Get-ItemProperty -Path $key).'(default)'
    # Solo se borra la entrada si es la de PassDesk: nunca la de otro programa con el mismo nombre.
    if ($value -and ($value -ieq $manifestFile)) {
        Remove-Item -Path $key -Force
        Write-Host "Quitado: $key" -ForegroundColor Green
    }
    else {
        Write-Host "Se deja $key (no apunta al manifiesto de PassDesk)." -ForegroundColor Yellow
    }
}

foreach ($file in @($manifestFile, (Join-Path $InstallDir "allowed-origins.txt"))) {
    if (Test-Path $file) { Remove-Item $file -Force }
}

Write-Host ""
Write-Host "Conector desregistrado. Tu bóveda, documentos y copias de seguridad no se han tocado."
Write-Host "Quita la extensión desde el navegador (Extensiones › Quitar) si ya no la quieres."
