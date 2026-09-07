# ============================================================
#  SystemToolkit build core (V1-000)
#  Spec: Docs/09-开发规范/04-文件与目录规范.md §1.3 (product anti-fake 3 lines)
#  Usage: Tools\Build.ps1 [-NoTest]
#  Entry: users run Tools\Build.bat (wrapper of this file)
#  NOTE: must be saved as UTF-8 with BOM (required by Windows PS 5.1)
# ============================================================
[CmdletBinding()]
param(
    [switch]$NoTest,
    [switch]$NoClean
)

$ErrorActionPreference = 'Continue'
$repoRoot = Split-Path -Parent $PSScriptRoot
Set-Location $repoRoot

$script:failed = $false
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$null = New-Item -ItemType Directory -Force -Path (Join-Path $repoRoot 'Tools\logs')
$log = Join-Path $repoRoot "Tools\logs\Build-$stamp.log"

# resolve dotnet: PATH first, fallback to default install
# session shells often lack env vars - resolve via .NET APIs
$pf = [Environment]::GetFolderPath('ProgramFiles')
$localAppData = [Environment]::GetFolderPath('LocalApplicationData')
$dotnet = (Get-Command dotnet -ErrorAction SilentlyContinue).Source
if (-not $dotnet) { $dotnet = Join-Path $pf 'dotnet\dotnet.exe' }
if (-not (Test-Path $dotnet)) { Write-Host '[FAIL] dotnet not found' -ForegroundColor Red; exit 1 }

function Write-Step([string]$msg) { Write-Host "`n== $msg" }
function Write-Ok([string]$msg)   { Write-Host "  [OK]   $msg" -ForegroundColor Green }
function Write-Bad([string]$msg)  { Write-Host "  [FAIL] $msg" -ForegroundColor Red; $script:failed = $true }

Write-Host '============================================================'
Write-Host " SystemToolkit Build  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')"
Write-Host " Root: $repoRoot"
Write-Host " Log:  $log"
Write-Host '============================================================'

# ------------------------------------------------------------
# Line 1: delete bin/obj/TestResults/.artifacts  (force + verify)
# ------------------------------------------------------------
Write-Step 'Line1: clean bin/obj/TestResults/.artifacts'
if ($NoClean) { Write-Ok 'skipped by -NoClean' }
$targets = @()
foreach ($parent in @('src', 'tests')) {
    $parentPath = Join-Path $repoRoot $parent
    if (-not (Test-Path $parentPath)) { continue }
    foreach ($projDir in Get-ChildItem -Path $parentPath -Directory) {
        foreach ($name in @('bin', 'obj')) {
            $targets += (Join-Path $projDir.FullName $name)
        }
    }
}
$targets += (Join-Path $repoRoot 'TestResults')
$targets += (Get-ChildItem -Path $repoRoot -Filter '.artifacts*' -Directory -ErrorAction SilentlyContinue |
    ForEach-Object { $_.FullName })

if ($NoClean) { $targets = @() }
foreach ($dir in $targets) {
    if (Test-Path $dir) {
        Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
        if (Test-Path $dir) { Write-Bad "cannot delete $dir (close IDE / running exe)" }
    }
}
if (-not $script:failed) { Write-Ok 'build outputs cleaned' }

# ------------------------------------------------------------
# Line 2: WebView2 cache cleanup ("rebuilt but nothing changed" root cause)
# ------------------------------------------------------------
Write-Step 'Line2: clean WebView2 cache'
$wv2 = @(
    (Join-Path $localAppData 'SystemToolkit.Shell.exe.WebView2'),
    (Join-Path $localAppData 'SystemToolkit.exe.WebView2')
) | Where-Object { $_ -and (Test-Path $_) }

foreach ($dir in $wv2) {
    Remove-Item -Recurse -Force $dir -ErrorAction SilentlyContinue
    if (Test-Path $dir) { Write-Bad "cannot delete $dir" } else { Write-Ok "cleaned $dir" }
}
if (-not $wv2) { Write-Ok 'no WebView2 cache found' }

# ------------------------------------------------------------
# restore + build + test (Release, all logged)
# ------------------------------------------------------------
Write-Step 'restore / build / test (Release)'
$out = & $dotnet restore SystemToolkit.sln 2>&1; $out | Tee-Object -FilePath $log
if ($LASTEXITCODE -ne 0) { Write-Bad 'dotnet restore failed' }

if (-not $script:failed) {
    $out = & $dotnet build SystemToolkit.sln -c Release --no-restore 2>&1
    $out | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) { Write-Bad 'dotnet build failed' }
}

if (-not $script:failed -and -not $NoTest) {
    $out = & $dotnet test SystemToolkit.sln -c Release --no-build 2>&1
    $out | Tee-Object -FilePath $log -Append
    if ($LASTEXITCODE -ne 0) { Write-Bad 'dotnet test failed' }
}

# ------------------------------------------------------------
# Line 3: STALE detection
#   a) Shell dll < 10KB => truncated debris
#   b) newest source mtime > dll mtime => STALE
# ------------------------------------------------------------
Write-Step 'Line3: STALE detection'
$dll = Get-ChildItem -Recurse -Path (Join-Path $repoRoot 'src\SystemToolkit.Shell\bin') `
    -Filter 'SystemToolkit.Shell.dll' -ErrorAction SilentlyContinue | Select-Object -First 1

if (-not $dll) {
    Write-Bad 'SystemToolkit.Shell.dll not found'
}
elseif ($dll.Length -lt 10KB) {
    Write-Bad "main dll only $($dll.Length) B, truncated debris?"
}
else {
    $newestSrc = Get-ChildItem -Recurse -Path (Join-Path $repoRoot 'src'), (Join-Path $repoRoot 'tests') `
        -Include *.cs, *.xaml, *.csproj, *.props, *.manifest |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newestSrc -and $newestSrc.LastWriteTime -gt $dll.LastWriteTime) {
        Write-Bad "STALE: source $($newestSrc.FullName) is newer than output (build may have used pre-edit files!)"
    }
    else {
        Write-Ok "output $($dll.Length) B, newer than all sources"
    }
}

# ------------------------------------------------------------
# summary: on failure extract error CS/MSB from log
# ------------------------------------------------------------
if ($script:failed) {
    Write-Host ''
    Write-Host '*** BUILD FAILED, error locations: ***' -ForegroundColor Red
    Select-String -Path $log -Pattern 'error CS', 'error MSB' | ForEach-Object { $_.Line }
    Write-Host "full log: $log" -ForegroundColor Yellow
    exit 1
}

Write-Host ''
Write-Host '============================================================' -ForegroundColor Green
Write-Host " BUILD OK  $(Get-Date -Format 'yyyy-MM-dd HH:mm:ss')" -ForegroundColor Green
Write-Host " log: $log" -ForegroundColor Green
Write-Host '============================================================' -ForegroundColor Green
exit 0
