param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
  [int]$Port = 8787
)

$ErrorActionPreference = "Stop"

$PowerShellExe = Join-Path $env:SystemRoot "System32\WindowsPowerShell\v1.0\powershell.exe"
$TrayScript = Join-Path $ProjectRoot "scripts\Ferry.Tray.ps1"
$BuildScript = Join-Path $ProjectRoot "scripts\Build-FerryTray.ps1"
$TrayExe = Join-Path $ProjectRoot "bin\FerryTray.exe"
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
& $PowerShellExe -NoProfile -ExecutionPolicy Bypass -File $BuildScript -ProjectRoot $ProjectRoot

$arguments = "--project `"$ProjectRoot`" --port $Port"

function New-FerryShortcut {
  param(
    [string]$Path,
    [switch]$NoOpen
  )

  $wsh = New-Object -ComObject WScript.Shell
  $shortcut = $wsh.CreateShortcut($Path)
  $shortcut.TargetPath = $TrayExe
  $shortcut.Arguments = if ($NoOpen) { "$arguments --no-open" } else { $arguments }
  $shortcut.WorkingDirectory = $ProjectRoot
  $shortcut.IconLocation = $TrayExe
  $shortcut.Description = "Start Ferry"
  $shortcut.Save()
}

$launchCommand = "`"$TrayExe`" $arguments --no-open"
New-FerryShortcut -Path $StartMenuShortcut
Set-ItemProperty -Path $RunKey -Name "Ferry" -Value $launchCommand
Remove-Item -LiteralPath $OldStartupShortcut -Force -ErrorAction SilentlyContinue

Write-Host "Installed Ferry shortcuts:"
Write-Host "  Start menu: $StartMenuShortcut"
Write-Host "  Autostart:  HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Ferry"
Write-Host ""
Write-Host "Launch Ferry from Start, or run:"
Write-Host "  `"$TrayExe`" --project `"$ProjectRoot`" --port $Port"
