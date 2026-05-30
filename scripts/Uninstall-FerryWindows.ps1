param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$StartupDir = Join-Path $StartMenuDir "Startup"
$StartMenuShortcut = Join-Path $StartMenuDir "Ferry.lnk"
$StartupShortcut = Join-Path $StartupDir "Ferry.lnk"
$PidFile = Join-Path $ProjectRoot "data\ferry.pid"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

Remove-Item -LiteralPath $StartMenuShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $StartupShortcut -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $RunKey -Name "Ferry" -Force -ErrorAction SilentlyContinue

if (Test-Path -LiteralPath $PidFile) {
  $pidText = (Get-Content -LiteralPath $PidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
  $serverPid = 0
  if ([int]::TryParse($pidText, [ref]$serverPid)) {
    $proc = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
    if ($proc -and $proc.ProcessName -eq "node") {
      Stop-Process -Id $serverPid -Force -ErrorAction SilentlyContinue
    }
  }
  Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
}

Write-Host "Removed Ferry Start menu shortcut and autostart entry."
