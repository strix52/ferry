param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
  [int]$Port = 8787
)

$ErrorActionPreference = "Stop"

$AppDir = Join-Path $env:LOCALAPPDATA "Ferry\app"
$InstalledExe = Join-Path $AppDir "Ferry.exe"
$InstalledCli = Join-Path $AppDir "ferryctl.exe"
$WindowsAppsDir = Join-Path $env:LOCALAPPDATA "Microsoft\WindowsApps"
$CliShim = Join-Path $WindowsAppsDir "ferryctl.cmd"
$StartMenuDir = Join-Path $env:APPDATA "Microsoft\Windows\Start Menu\Programs"
$StartMenuShortcut = Join-Path $StartMenuDir "Ferry.lnk"
$WpfShortcut = Join-Path $StartMenuDir "Ferry (WPF).lnk"
$OldStartupShortcut = Join-Path (Join-Path $StartMenuDir "Startup") "Ferry.lnk"
$RunKey = "HKCU:\Software\Microsoft\Windows\CurrentVersion\Run"
$Csproj = Join-Path $ProjectRoot "src\Ferry\Ferry\Ferry.csproj"
$CliCsproj = Join-Path $ProjectRoot "src\Ferry\Ferry.Cli\Ferry.Cli.csproj"

$StagingDir = Join-Path $env:TEMP ("ferry-staging-" + [System.Guid]::NewGuid().ToString("N"))
$CliStagingDir = Join-Path $env:TEMP ("ferry-cli-staging-" + [System.Guid]::NewGuid().ToString("N"))

try {
  Write-Host "Publishing Ferry to staging ($StagingDir)..."
  dotnet publish $Csproj -c Release -p:PublishSingleFile=true --self-contained false -o $StagingDir
  if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
  }

  Write-Host "Publishing ferryctl to staging ($CliStagingDir)..."
  dotnet publish $CliCsproj -c Release -p:PublishSingleFile=true --self-contained false -o $CliStagingDir
  if ($LASTEXITCODE -ne 0) {
    throw "ferryctl publish failed with exit code $LASTEXITCODE"
  }

  if (!(Test-Path -LiteralPath $AppDir)) {
    New-Item -ItemType Directory -Path $AppDir | Out-Null
  }

  Write-Host "Deploying to $AppDir..."
  Copy-Item -Path (Join-Path $StagingDir "*") -Destination $AppDir -Recurse -Force
  Copy-Item -Path (Join-Path $CliStagingDir "*") -Destination $AppDir -Recurse -Force

  $indexHtml = Join-Path $AppDir "public\index.html"
  if (!(Test-Path -LiteralPath $indexHtml)) {
    throw "FATAL: $indexHtml is absent after copy! Phone client will fail with 404."
  }
  Write-Host "Verified phone client asset: $indexHtml"

  if (!(Test-Path -LiteralPath $InstalledCli)) {
    throw "FATAL: $InstalledCli is absent after copy."
  }
  if (!(Test-Path -LiteralPath $WindowsAppsDir)) {
    New-Item -ItemType Directory -Path $WindowsAppsDir | Out-Null
  }
  $shimContent = "@echo off`r`n`"$InstalledCli`" %*`r`n"
  Set-Content -LiteralPath $CliShim -Value $shimContent -Encoding Ascii -NoNewline
  Write-Host "Installed agent command: $CliShim"

  # Clean up old shortcuts (F6)
  Remove-Item -LiteralPath $WpfShortcut -Force -ErrorAction SilentlyContinue
  Remove-Item -LiteralPath $OldStartupShortcut -Force -ErrorAction SilentlyContinue

  # Create Start Menu shortcut
  $wsh = New-Object -ComObject WScript.Shell
  $shortcut = $wsh.CreateShortcut($StartMenuShortcut)
  $shortcut.TargetPath = $InstalledExe
  $shortcut.Arguments = "--show-window"
  $shortcut.WorkingDirectory = $AppDir
  $shortcut.IconLocation = $InstalledExe
  $shortcut.Description = "Hand off text and files between this laptop and your phone."
  $shortcut.Save()

  # Configure HKCU Run autostart (replacing F5)
  $launchCommand = "`"$InstalledExe`" --minimized"
  Set-ItemProperty -Path $RunKey -Name "Ferry" -Value $launchCommand

  # Firewall rule (F7)
  $ruleName = "Ferry"
  $isAdmin = ([Security.Principal.WindowsPrincipal][Security.Principal.WindowsIdentity]::GetCurrent()).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
  if ($isAdmin) {
    try {
      $existing = Get-NetFirewallRule -DisplayName $ruleName -ErrorAction SilentlyContinue | Where-Object {
        ($_ | Get-NetFirewallApplicationFilter).Program -eq $InstalledExe
      }
      if (-not $existing) {
        New-NetFirewallRule -DisplayName $ruleName -Direction Inbound -Action Allow -Protocol TCP -Program $InstalledExe -Profile Public -Description "Allow inbound Ferry connections from phone" | Out-Null
        Write-Host "Added firewall inbound allow rule for $InstalledExe (Public profile)."
      } else {
        Write-Host "Firewall rule for $InstalledExe already present."
      }
    } catch {
      Write-Warning "Failed to set firewall rule: $($_.Exception.Message)"
      Write-Host "Please run the following command in an elevated PowerShell prompt:"
      Write-Host "New-NetFirewallRule -DisplayName `"$ruleName`" -Direction Inbound -Action Allow -Protocol TCP -Program `"$InstalledExe`" -Profile Public"
    }
  } else {
    Write-Warning "Setting firewall rule requires elevation."
    Write-Host "To allow inbound connections from your phone, run this in an elevated PowerShell prompt:"
    Write-Host "New-NetFirewallRule -DisplayName `"$ruleName`" -Direction Inbound -Action Allow -Protocol TCP -Program `"$InstalledExe`" -Profile Public"
  }

  Write-Host ""
  Write-Host "Installed Ferry:"
  Write-Host "  Binary:     $InstalledExe"
  Write-Host "  Agent CLI:  $CliShim"
  Write-Host "  Start menu: $StartMenuShortcut"
  Write-Host "  Autostart:  HKCU\Software\Microsoft\Windows\CurrentVersion\Run\Ferry"
}
finally {
  if (Test-Path -LiteralPath $StagingDir) {
    Remove-Item -LiteralPath $StagingDir -Recurse -Force -ErrorAction SilentlyContinue
  }
  if (Test-Path -LiteralPath $CliStagingDir) {
    Remove-Item -LiteralPath $CliStagingDir -Recurse -Force -ErrorAction SilentlyContinue
  }
}
