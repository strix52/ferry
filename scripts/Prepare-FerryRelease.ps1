param(
  [string]$Version = '0.17.0',
  [string]$OutputRoot = (Join-Path $PSScriptRoot '..\artifacts\releases')
)

$ErrorActionPreference = 'Stop'
$repo = (Resolve-Path -LiteralPath (Join-Path $PSScriptRoot '..')).Path
$packageJson = Get-Content -LiteralPath (Join-Path $repo 'package.json') -Raw | ConvertFrom-Json
$lockPath = Join-Path $repo 'package-lock.json'
$lockVersions = & node.exe -p "const lock = require(process.argv[1]); lock.version + ',' + lock.packages[''].version" $lockPath
if ($LASTEXITCODE -ne 0) { throw 'Could not read package-lock.json' }
$project = [xml](Get-Content -LiteralPath (Join-Path $repo 'src\Ferry\Ferry\Ferry.csproj') -Raw)
$releaseNotes = Join-Path $repo "RELEASE_NOTES_v$Version.md"
$changelog = Get-Content -LiteralPath (Join-Path $repo 'CHANGELOG.md') -Raw

if ($packageJson.version -ne $Version -or $lockVersions -ne "$Version,$Version" -or
    $project.SelectSingleNode('//Version').InnerText.Trim() -ne $Version -or
    $project.SelectSingleNode('//FileVersion').InnerText.Trim() -ne "$Version.0" -or
    $project.SelectSingleNode('//AssemblyVersion').InnerText.Trim() -ne "$Version.0") {
  throw "Version metadata does not all match $Version."
}
if (!(Test-Path -LiteralPath $releaseNotes) -or $changelog -notmatch [regex]::Escape("## $Version")) {
  throw "Release notes or changelog entry for $Version is missing."
}

Push-Location $repo
try {
  dotnet test 'src\Ferry\Ferry.slnx' -c Release
  if ($LASTEXITCODE -ne 0) { throw 'dotnet tests failed' }
  & npm.cmd test
  if ($LASTEXITCODE -ne 0) { throw 'browser or wire conformance tests failed' }

  $output = Join-Path $OutputRoot ("v$Version-" + (Get-Date -Format 'yyyyMMdd-HHmmss'))
  $appStage = Join-Path $output 'app-stage'
  $cliStage = Join-Path $output 'cli-stage'
  $package = Join-Path $output 'package'
  New-Item -ItemType Directory -Path $appStage,$cliStage,$package -Force | Out-Null

  dotnet publish 'src\Ferry\Ferry\Ferry.csproj' -c Release -r win-x64 -p:PublishSingleFile=true -p:DebugType=None --self-contained true -o $appStage
  if ($LASTEXITCODE -ne 0) { throw 'Ferry publish failed' }
  dotnet publish 'src\Ferry\Ferry.Cli\Ferry.Cli.csproj' -c Release -r win-x64 -p:PublishSingleFile=true -p:DebugType=None --self-contained true -o $cliStage
  if ($LASTEXITCODE -ne 0) { throw 'ferryctl publish failed' }

  $appExe = Join-Path $appStage 'Ferry.exe'
  $cliExe = Join-Path $cliStage 'ferryctl.exe'
  if (!(Test-Path -LiteralPath $appExe) -or !(Test-Path -LiteralPath $cliExe) -or
      !(Test-Path -LiteralPath (Join-Path $appStage 'public\index.html'))) {
    throw 'Published app, CLI, or phone client is missing.'
  }
  $cliExtras = @(Get-ChildItem -LiteralPath $cliStage -File | Where-Object Name -ne 'ferryctl.exe')
  if ($cliExtras.Count -gt 0) { throw "ferryctl is not a single-file publish: $($cliExtras.Name -join ', ')" }

  Copy-Item -Path (Join-Path $appStage '*') -Destination $package -Recurse -Force
  Copy-Item -LiteralPath $cliExe -Destination $package
  Copy-Item -LiteralPath (Join-Path $repo 'README.md'),(Join-Path $repo 'LICENSE'),$releaseNotes -Destination $package
  $help = & (Join-Path $package 'ferryctl.exe') --help | ConvertFrom-Json
  if ($LASTEXITCODE -ne 0 -or !$help.ok -or $help.usage.Count -lt 6) { throw 'Packaged ferryctl help failed' }

  $listener = [System.Net.Sockets.TcpListener]::new([System.Net.IPAddress]::Loopback, 0)
  $listener.Start()
  $smokePort = $listener.LocalEndpoint.Port
  $listener.Stop()
  $start = New-Object System.Diagnostics.ProcessStartInfo((Join-Path $package 'Ferry.exe'))
  $start.UseShellExecute = $false
  $start.CreateNoWindow = $true
  $start.WorkingDirectory = $package
  $start.EnvironmentVariables['FERRY_DATA_DIR'] = (Join-Path $output 'smoke-data')
  $start.EnvironmentVariables['FERRY_PORT'] = [string]$smokePort
  $start.EnvironmentVariables['FERRY_PUBLIC_DIR'] = (Join-Path $package 'public')
  $smoke = [System.Diagnostics.Process]::Start($start)
  try {
    $ready = $false
    for ($attempt = 0; $attempt -lt 30; $attempt++) {
      if ($smoke.HasExited) { throw 'Packaged Ferry exited during smoke check' }
      try {
        $response = Invoke-WebRequest -Uri "http://127.0.0.1:$smokePort/api/presence" -UseBasicParsing -TimeoutSec 2
        if ($response.StatusCode -eq 200) { $ready = $true; break }
      }
      catch { Start-Sleep -Milliseconds 500 }
    }
    if (!$ready) { throw 'Packaged Ferry did not start its server' }
    $oldUrl = $env:FERRY_URL
    try {
      $env:FERRY_URL = "http://127.0.0.1:$smokePort"
      $status = & (Join-Path $package 'ferryctl.exe') status | ConvertFrom-Json
      if ($LASTEXITCODE -ne 0 -or !$status.ok -or !$status.server) { throw 'Packaged ferryctl status failed' }
    }
    finally { $env:FERRY_URL = $oldUrl }
  }
  finally {
    if (!$smoke.HasExited) { $smoke.Kill(); $smoke.WaitForExit() }
    $smoke.Dispose()
  }

  $zipName = "ferry-windows-x64-v$Version.zip"
  $zip = Join-Path $output $zipName
  Compress-Archive -Path (Join-Path $package '*') -DestinationPath $zip -CompressionLevel Optimal
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive = [System.IO.Compression.ZipFile]::OpenRead($zip)
  try {
    $entries = @($archive.Entries | ForEach-Object { $_.FullName.Replace([char]92, [char]47) })
    foreach ($required in @('Ferry.exe','ferryctl.exe','public/index.html','README.md','LICENSE')) {
      if ($entries -notcontains $required) { throw "ZIP is missing $required" }
    }
  }
  finally { $archive.Dispose() }

  $checksum = (Get-FileHash -LiteralPath $zip -Algorithm SHA256).Hash.ToLowerInvariant()
  $checksumPath = "$zip.sha256"
  Set-Content -LiteralPath $checksumPath -Value "$checksum  $zipName" -Encoding Ascii
  Write-Host "Release package: $zip"
  Write-Host "Checksum file:   $checksumPath"
  Write-Host "SHA-256:         $checksum"
}
finally { Pop-Location }
