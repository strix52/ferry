param(
  [string]$ProjectRoot = (Resolve-Path (Join-Path $PSScriptRoot "..")).Path
)

$ErrorActionPreference = "Stop"

$Source = Join-Path $ProjectRoot "scripts\FerryTray.cs"
$BinDir = Join-Path $ProjectRoot "bin"
$Out = Join-Path $BinDir "FerryTray.exe"
$Icon = Join-Path $ProjectRoot "data\ferry.ico"
$Csc = Join-Path $env:WINDIR "Microsoft.NET\Framework64\v4.0.30319\csc.exe"

if (!(Test-Path -LiteralPath $Csc)) {
  throw "Missing C# compiler: $Csc"
}

if (!(Test-Path -LiteralPath $BinDir)) {
  New-Item -ItemType Directory -Path $BinDir | Out-Null
}

$args = @(
  "/nologo",
  "/target:winexe",
  "/optimize+",
  "/reference:System.Windows.Forms.dll",
  "/reference:System.Drawing.dll",
  "/out:$Out"
)

if (Test-Path -LiteralPath $Icon) {
  $args += "/win32icon:$Icon"
}

$args += $Source

& $Csc @args
if ($LASTEXITCODE -ne 0) {
  throw "FerryTray.exe build failed."
}

Write-Host "Built $Out"
