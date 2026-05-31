param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path,
  [int]$Port = 8787,
  [switch]$GenerateIconOnly,
  [switch]$NoOpen
)

$ErrorActionPreference = "Stop"

Add-Type -AssemblyName System.Windows.Forms
Add-Type -AssemblyName System.Drawing

$DataDir = Join-Path $ProjectRoot "data"
$PidFile = Join-Path $DataDir "ferry.pid"
$IconFile = Join-Path $DataDir "ferry.ico"
$OutLog = Join-Path $DataDir "ferry.out.log"
$ErrLog = Join-Path $DataDir "ferry.err.log"
$OpenUrl = "http://localhost:$Port"
$HealthUrl = "http://127.0.0.1:$Port"

function Ensure-DataDir {
  if (!(Test-Path -LiteralPath $DataDir)) {
    New-Item -ItemType Directory -Path $DataDir | Out-Null
  }
}

function New-FerryIconFile {
  Ensure-DataDir
  if (Test-Path -LiteralPath $IconFile) { return }

  $bmp = New-Object System.Drawing.Bitmap 32, 32
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.SmoothingMode = [System.Drawing.Drawing2D.SmoothingMode]::AntiAlias

  $bg = New-Object System.Drawing.SolidBrush ([System.Drawing.Color]::FromArgb(125, 92, 132))
  $pen = New-Object System.Drawing.Pen ([System.Drawing.Color]::White), 2
  $pen.StartCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.EndCap = [System.Drawing.Drawing2D.LineCap]::Round
  $pen.LineJoin = [System.Drawing.Drawing2D.LineJoin]::Round

  $g.FillRectangle($bg, 0, 0, 32, 32)
  $g.DrawLine($pen, 6, 18, 26, 18)
  $g.DrawLine($pen, 6, 18, 8, 22)
  $g.DrawLine($pen, 26, 18, 24, 22)
  $g.DrawLine($pen, 8, 22, 24, 22)
  $g.DrawLine($pen, 10, 18, 10, 12)
  $g.DrawLine($pen, 10, 12, 18, 12)
  $g.DrawLine($pen, 18, 12, 21, 18)
  $g.DrawLine($pen, 13, 12, 13, 9)
  $g.DrawLine($pen, 13, 9, 16, 9)

  $icon = [System.Drawing.Icon]::FromHandle($bmp.GetHicon())
  $fs = [System.IO.File]::Create($IconFile)
  try {
    $icon.Save($fs)
  } finally {
    $fs.Dispose()
    $icon.Dispose()
    $pen.Dispose()
    $bg.Dispose()
    $g.Dispose()
    $bmp.Dispose()
  }
}

function Test-FerryServer {
  try {
    $res = Invoke-RestMethod -Uri "$HealthUrl/api/info" -TimeoutSec 2
    return ($res.port -eq $Port)
  } catch {
    return $false
  }
}

function Get-FerryPortProcessId {
  try {
    $conn = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction Stop | Select-Object -First 1
    if ($conn) { return [int]$conn.OwningProcess }
  } catch {}
  return $null
}

function Start-FerryServer {
  Ensure-DataDir
  if (Test-FerryServer) {
    $existingPid = Get-FerryPortProcessId
    if ($existingPid) {
      Set-Content -LiteralPath $PidFile -Value $existingPid -Encoding ASCII
    }
    return $true
  }

  $portPid = Get-FerryPortProcessId
  if ($portPid) {
    Set-Content -LiteralPath $PidFile -Value $portPid -Encoding ASCII
    return (Test-FerryServer)
  }

  $node = (Get-Command node -ErrorAction Stop).Source
  $proc = Start-Process -FilePath $node `
    -ArgumentList "--no-warnings", "server.js" `
    -WorkingDirectory $ProjectRoot `
    -WindowStyle Hidden `
    -RedirectStandardOutput $OutLog `
    -RedirectStandardError $ErrLog `
    -PassThru

  Set-Content -LiteralPath $PidFile -Value $proc.Id -Encoding ASCII

  for ($i = 0; $i -lt 30; $i++) {
    Start-Sleep -Milliseconds 400
    if (Test-FerryServer) { return $true }
  }

  return $false
}

function Stop-FerryServer {
  $pidText = $null
  if (Test-Path -LiteralPath $PidFile) {
    $pidText = (Get-Content -LiteralPath $PidFile -ErrorAction SilentlyContinue | Select-Object -First 1)
  }
  $serverPid = 0
  if (!([int]::TryParse($pidText, [ref]$serverPid))) {
    $portPid = Get-FerryPortProcessId
    if ($portPid) { $serverPid = $portPid }
  }
  if ($serverPid -gt 0) {
    $proc = Get-Process -Id $serverPid -ErrorAction SilentlyContinue
    if ($proc -and $proc.ProcessName -eq "node") {
      Stop-Process -Id $serverPid -Force -ErrorAction SilentlyContinue
    }
  }
  Remove-Item -LiteralPath $PidFile -Force -ErrorAction SilentlyContinue
}

function Open-Ferry {
  Start-Process $OpenUrl
}

function Get-FerryPhoneUrl {
  try {
    $info = Invoke-RestMethod -Uri "$HealthUrl/api/info" -TimeoutSec 2
    if ($info.primary) { return [string]$info.primary }
  } catch {}
  return $OpenUrl
}

New-FerryIconFile
if ($GenerateIconOnly) { exit 0 }

$createdNew = $false
$mutex = New-Object System.Threading.Mutex($true, "Local\FerryTray", [ref]$createdNew)

if (!$createdNew) {
  Start-FerryServer | Out-Null
  if (!$NoOpen) { Open-Ferry }
  exit 0
}

$started = Start-FerryServer
if (!$NoOpen -and $started) { Open-Ferry }

$notify = New-Object System.Windows.Forms.NotifyIcon
$notify.Icon = New-Object System.Drawing.Icon($IconFile)
$notify.Text = "Ferry"
$notify.Visible = $true

$form = New-Object System.Windows.Forms.Form
$form.Text = "Ferry"
$form.ShowInTaskbar = $false
$form.WindowState = [System.Windows.Forms.FormWindowState]::Minimized
$form.FormBorderStyle = [System.Windows.Forms.FormBorderStyle]::FixedToolWindow
$form.Opacity = 0
$form.Size = New-Object System.Drawing.Size(0, 0)
$form.Add_Shown({ $form.Hide() })

$menu = New-Object System.Windows.Forms.ContextMenuStrip
$openItem = $menu.Items.Add("Open Ferry")
$copyUrlItem = $menu.Items.Add("Copy Phone URL")
$restartItem = $menu.Items.Add("Restart Ferry")
$stopItem = $menu.Items.Add("Stop Ferry")
$folderItem = $menu.Items.Add("Open Project Folder")
$logsItem = $menu.Items.Add("Open Logs")
$menu.Items.Add("-") | Out-Null
$quitItem = $menu.Items.Add("Quit Ferry")

$openItem.Add_Click({ Open-Ferry })
$copyUrlItem.Add_Click({
  [System.Windows.Forms.Clipboard]::SetText((Get-FerryPhoneUrl))
  $notify.ShowBalloonTip(2000, "Ferry", "Phone URL copied.", [System.Windows.Forms.ToolTipIcon]::Info)
})
$restartItem.Add_Click({
  Stop-FerryServer
  if (Start-FerryServer) {
    $notify.ShowBalloonTip(2500, "Ferry", "Ferry restarted.", [System.Windows.Forms.ToolTipIcon]::Info)
  } else {
    $notify.ShowBalloonTip(5000, "Ferry", "Ferry did not start. Check data\ferry.err.log.", [System.Windows.Forms.ToolTipIcon]::Error)
  }
})
$stopItem.Add_Click({
  Stop-FerryServer
  $notify.ShowBalloonTip(2500, "Ferry", "Ferry stopped.", [System.Windows.Forms.ToolTipIcon]::Info)
})
$folderItem.Add_Click({ Start-Process $ProjectRoot })
$logsItem.Add_Click({ Start-Process $DataDir })
$quitItem.Add_Click({
  Stop-FerryServer
  $notify.Visible = $false
  $form.Close()
})

$notify.ContextMenuStrip = $menu
$notify.Add_MouseUp({
  param($sender, $eventArgs)
  if ($eventArgs.Button -eq [System.Windows.Forms.MouseButtons]::Left) {
    Open-Ferry
  }
})

if ($started) {
  $notify.ShowBalloonTip(2500, "Ferry", "Ferry is running at $OpenUrl.", [System.Windows.Forms.ToolTipIcon]::Info)
} else {
  $notify.ShowBalloonTip(5000, "Ferry", "Ferry did not start. Check data\ferry.err.log.", [System.Windows.Forms.ToolTipIcon]::Error)
}

try {
  [System.Windows.Forms.Application]::Run($form)
} finally {
  $notify.Visible = $false
  $notify.Dispose()
  $form.Dispose()
  $mutex.ReleaseMutex() | Out-Null
  $mutex.Dispose()
}
