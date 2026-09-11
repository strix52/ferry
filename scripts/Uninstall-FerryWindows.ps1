param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

$AppDir = Join-Path $env:LOCALAPPDATA "Ferry\app"
$InstalledExe = Join-Path $AppDir "Ferry.exe"
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$StartupDir = Join-Path $StartMenuDir "Startup"
$StartMenuShortcut = Join-Path $StartMenuDir "Ferry.lnk"
$WpfShortcut = Join-Path $StartMenuDir "Ferry (WPF).lnk"
$StartupShortcut = Join-Path $StartupDir "Ferry.lnk"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"

Remove-Item -LiteralPath $StartMenuShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $WpfShortcut -Force -ErrorAction SilentlyContinue
Remove-Item -LiteralPath $StartupShortcut -Force -ErrorAction SilentlyContinue
Remove-ItemProperty -Path $RunKey -Name "Ferry" -Force -ErrorAction SilentlyContinue

# Stop running Ferry processes
Get-Process -Name "Ferry" -ErrorAction SilentlyContinue |
  ForEach-Object { Stop-Process -Id $_.Id -Force -ErrorAction SilentlyContinue }

# Firewall rule cleanup
$isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if ($isAdmin) {
  try {
    Get-NetFirewallRule -DisplayName "Ferry" -ErrorAction SilentlyContinue | Where-Object {
      ($_ | Get-NetFirewallApplicationFilter).Program -eq $InstalledExe
    } | Remove-NetFirewallRule -ErrorAction SilentlyContinue
  } catch { }
} else {
  Write-Host "To remove the firewall rule, run this in an elevated PowerShell prompt:"
  Write-Host "Get-NetFirewallRule -DisplayName `"Ferry`" | Where-Object { (`$_ | Get-NetFirewallApplicationFilter).Program -eq `"$InstalledExe`" } | Remove-NetFirewallRule"
}

Write-Host "Removed Ferry Start menu shortcuts and autostart entry."
