param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
  [int]$Port = 8787
)

$ErrorActionPreference = "Stop"

$PowerShellExe = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$TrayScript = Join-Path $ProjectRoot "scripts\Ferry.Tray.ps1"
$DataDir = Join-Path $ProjectRoot "data"
$IconFile = Join-Path $DataDir "ferry.ico"
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$StartMenuShortcut = Join-Path $StartMenuDir "Ferry.lnk"
$OldStartupShortcut = Join-Path (Join-Path $StartMenuDir "Startup") "Ferry.lnk"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

if (!(Test-Path -LiteralPath $TrayScript)) {
  throw "Missing tray script: $TrayScript"
}

if (!(Test-Path -LiteralPath $DataDir)) {
  New-Item -ItemType Directory -Path $DataDir | Out-Null
}

& $PowerShellExe -NoProfile -ExecutionPolicy Bypass -STA -File $TrayScript -ProjectRoot $ProjectRoot -Port $Port -GenerateIconOnly

$arguments = "-NoProfile -ExecutionPolicy Bypass -STA -File `"$TrayScript`" -ProjectRoot `"$ProjectRoot`" -Port $Port"

function New-FerryShortcut {
  param(
    [string]$Path,
    [switch]$NoOpen
  )

  $wsh = New-Object -ComObject WScript.Shell
  $shortcut = $wsh.CreateShortcut($Path)
  $shortcut.TargetPath = $PowerShellExe
  $shortcut.Arguments = if ($NoOpen) { "$arguments -NoOpen" } else { $arguments }
  $shortcut.WorkingDirectory = $ProjectRoot
  $shortcut.IconLocation = $IconFile
  $shortcut.Description = "Start Ferry"
  $shortcut.Save()
}

$launchCommand = "`"$PowerShellExe`" $arguments -NoOpen"
New-FerryShortcut -Path $StartMenuShortcut
Set-ItemProperty -Path $RunKey -Name "Ferry" -Value $launchCommand
Remove-Item -LiteralPath $OldStartupShortcut -Force -ErrorAction SilentlyContinue

Write-Host "Installed Ferry shortcuts:"
Write-Host "  Start menu: $StartMenuShortcut"
Write-Host "  Autostart:  HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Ferry"
Write-Host ""
Write-Host "Launch Ferry from Start, or run:"
Write-Host "  powershell -NoProfile -ExecutionPolicy Bypass -STA -File `"$TrayScript`""
