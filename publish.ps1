<#
.SYNOPSIS
  Bump the version, publish DesktopNames to the AppData deploy folder, and relaunch.
.DESCRIPTION
  Default bumps the patch (1.2.0 -> 1.2.1). Use -Minor / -Major to bump those instead,
  or -Version x.y.z to set an explicit version. -NoBump publishes without changing the version.
.EXAMPLE
  powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1
  powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1 -Minor
  powershell -NoProfile -ExecutionPolicy Bypass -File publish.ps1 -Version 2.0.0
#>
param(
    [switch]$Minor,
    [switch]$Major,
    [string]$Version,
    [switch]$NoBump
)
$ErrorActionPreference = 'Stop'

$csproj    = Join-Path $PSScriptRoot 'DesktopNames\DesktopNames.csproj'
$deployDir = Join-Path $env:APPDATA 'DesktopNames'
$exe       = Join-Path $deployDir 'DesktopNames.exe'

# --- read current version ---
$xml = [xml](Get-Content $csproj)
$node = $xml.Project.PropertyGroup.Version
if ($node -is [array]) { $node = $node | Where-Object { $_ } | Select-Object -First 1 }
$current = "$node".Trim()
Write-Host "Current version: $current"

# --- compute new version ---
if ($NoBump) {
    $new = $current
} elseif ($Version) {
    $new = $Version
} else {
    $p = $current.Split('.')
    while ($p.Count -lt 3) { $p += '0' }
    [int]$maj = $p[0]; [int]$min = $p[1]; [int]$pat = $p[2]
    if     ($Major) { $maj++; $min = 0; $pat = 0 }
    elseif ($Minor) { $min++; $pat = 0 }
    else            { $pat++ }
    $new = "$maj.$min.$pat"
}

# --- write it back ---
if ($new -ne $current) {
    foreach ($pg in $xml.Project.PropertyGroup) {
        if ($pg.Version) { $pg.Version = $new }
    }
    $xml.Save($csproj)
    Write-Host "Bumped version: $current -> $new" -ForegroundColor Green
} else {
    Write-Host "Version unchanged: $new"
}

# --- provenance ---
# The version number alone identifies nothing: this script deliberately publishes the
# working tree, which routinely holds uncommitted work. Stamp the commit it was built
# from, and mark it dirty when the tree doesn't match that commit, so the running build
# can always be traced back (or honestly labelled as untraceable).
$sha = & git -C $PSScriptRoot rev-parse --short HEAD 2>$null
if ($LASTEXITCODE -ne 0 -or -not $sha) { $sha = 'nogit' }
$dirty = if (& git -C $PSScriptRoot status --porcelain 2>$null) { '.dirty' } else { '' }
$stamp = "$new+$sha$dirty"
if ($dirty) { Write-Warning "working tree is dirty - the deployed build matches no commit ($stamp)" }

# --- stop, publish, relaunch ---
Get-Process DesktopNames -ErrorAction SilentlyContinue | Stop-Process -Force
Start-Sleep -Milliseconds 600

& dotnet publish $csproj -c Release -o $deployDir --nologo -p:InformationalVersion=$stamp
if ($LASTEXITCODE -ne 0) { throw "publish failed (exit $LASTEXITCODE)" }

Start-Process $exe
Start-Sleep -Milliseconds 800
$proc = Get-Process DesktopNames -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Relaunched $stamp (pid $($proc.Id))" -ForegroundColor Green
} else {
    Write-Warning "Published $stamp but process is not running."
}
