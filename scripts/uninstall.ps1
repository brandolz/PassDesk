<#
.SYNOPSIS
  Desinstala PassDesk del usuario actual.

.DESCRIPTION
  Elimina el programa y sus accesos directos. La bóveda cifrada (%LOCALAPPDATA%\PassDesk) se CONSERVA
  salvo que se indique -RemoveData; en ese caso se pide escribir BORRAR para confirmar, porque la
  eliminación de la bóveda no se puede deshacer (exporta antes una copia de seguridad si la necesitas).
#>
param(
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\PassDesk"),
    [switch]$RemoveData
)

$ErrorActionPreference = "Stop"

$running = Get-Process -Name PassDesk -ErrorAction SilentlyContinue
if ($running) { throw "Cierra PassDesk antes de desinstalarlo (si está en la bandeja del sistema: icono › Salir)." }

foreach ($link in @(
        (Join-Path ([Environment]::GetFolderPath("Programs")) "PassDesk.lnk"),
        (Join-Path ([Environment]::GetFolderPath("Desktop")) "PassDesk.lnk"))) {
    if (Test-Path $link) { Remove-Item $link -Force }
}

# Entrada de «Iniciar con Windows», si se activó en Ajustes.
Remove-ItemProperty -Path "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run" -Name "PassDesk" -ErrorAction SilentlyContinue

if (Test-Path (Join-Path $InstallDir "PassDesk.exe")) {
    Remove-Item $InstallDir -Recurse -Force
    Write-Host "Programa eliminado de $InstallDir" -ForegroundColor Green
}
else {
    Write-Host "No se encontró PassDesk en $InstallDir"
}

$data = Join-Path $env:LOCALAPPDATA "PassDesk"
if ($RemoveData -and (Test-Path $data)) {
    $answer = Read-Host "Se borrará la bóveda cifrada en $data. Escribe BORRAR para confirmar"
    if ($answer -ceq "BORRAR") {
        Remove-Item $data -Recurse -Force
        Write-Host "Datos eliminados." -ForegroundColor Yellow
    }
    else {
        Write-Host "Datos conservados."
    }
}
elseif (Test-Path $data) {
    Write-Host "Tus datos cifrados se conservan en $data"
}
