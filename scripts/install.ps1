<#
.SYNOPSIS
  Instala PassDesk para el usuario actual (sin permisos de administrador).

.DESCRIPTION
  Copia dist\PassDesk a %LOCALAPPDATA%\Programs\PassDesk y crea un acceso directo en el menú Inicio
  (y opcionalmente en el escritorio). Los datos cifrados se guardan aparte, en %LOCALAPPDATA%\PassDesk,
  y no se tocan al actualizar ni al desinstalar.

.EXAMPLE
  powershell -ExecutionPolicy Bypass -File scripts\install.ps1 -DesktopShortcut
#>
param(
    [string]$Source = (Join-Path $PSScriptRoot "..\dist\PassDesk"),
    [string]$InstallDir = (Join-Path $env:LOCALAPPDATA "Programs\PassDesk"),
    [switch]$DesktopShortcut,
    [switch]$NoStartMenu
)

$ErrorActionPreference = "Stop"
$exeSource = Join-Path $Source "PassDesk.exe"
if (-not (Test-Path $exeSource)) {
    throw "No se encuentra $exeSource. Ejecuta primero: powershell -ExecutionPolicy Bypass -File scripts\publish.ps1"
}

$running = Get-Process -Name PassDesk -ErrorAction SilentlyContinue | Where-Object { $_.Path -and $_.Path.StartsWith($InstallDir, [StringComparison]::OrdinalIgnoreCase) }
if ($running) {
    throw "PassDesk se está ejecutando desde $InstallDir. Ciérralo (si está en la bandeja del sistema: icono › Salir) y vuelve a intentarlo."
}

New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
Copy-Item -Path (Join-Path $Source "*") -Destination $InstallDir -Recurse -Force
$exe = Join-Path $InstallDir "PassDesk.exe"

function New-Shortcut([string]$path) {
    $shell = New-Object -ComObject WScript.Shell
    $link = $shell.CreateShortcut($path)
    $link.TargetPath = $exe
    $link.WorkingDirectory = $InstallDir
    $link.IconLocation = "$exe,0"
    $link.Description = "PassDesk - consulta segura de tus contraseñas de Apple"
    $link.Save()
}

if (-not $NoStartMenu) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath("Programs")) "PassDesk.lnk")
}
if ($DesktopShortcut) {
    New-Shortcut (Join-Path ([Environment]::GetFolderPath("Desktop")) "PassDesk.lnk")
}

Write-Host "PassDesk instalado en $InstallDir" -ForegroundColor Green
if (-not $NoStartMenu) { Write-Host "Ábrelo desde el menú Inicio buscando «PassDesk»." }
Write-Host "Datos cifrados: $(Join-Path $env:LOCALAPPDATA 'PassDesk')"
