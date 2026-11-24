# Builds MMOOTSetup.exe (the LoZ: Masks of Time installer) with the .NET Framework compiler that ships with Windows.
#
#   powershell -NoProfile -File MMOOT/tools/make_installer.ps1
#   powershell -NoProfile -File MMOOT/tools/make_installer.ps1 -Console   # console build, for --validate/--check-updates
#
# Output: MMOOT/build/installer/MMOOTSetup.exe
param([switch]$Console)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
if (-not (Test-Path (Join-Path $root 'MMOOT'))) { $root = Split-Path -Parent $PSScriptRoot }
$source = Join-Path $PSScriptRoot 'installer/MMOOTSetup.cs'
$outDir = Join-Path $root 'build/installer'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

$candidates = @(
    "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319\csc.exe",
    "$env:WINDIR\Microsoft.NET\Framework\v4.0.30319\csc.exe"
)
$csc = $candidates | Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $csc) { throw "csc.exe (.NET Framework 4) not found; install the .NET Framework 4.x SDK/redistributable." }

$target = if ($Console) { 'exe' } else { 'winexe' }
$name = if ($Console) { 'MMOOTSetup-console.exe' } else { 'MMOOTSetup.exe' }
$out = Join-Path $outDir $name

& $csc /nologo /target:$target /platform:anycpu /optimize+ /warn:4 `
    /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll `
    /out:$out $source
if ($LASTEXITCODE -ne 0) { throw "csc failed with exit code $LASTEXITCODE" }

$file = Get-Item $out
$hash = (Get-FileHash $out -Algorithm SHA256).Hash.ToLower()
Write-Host ("built {0}  {1:N0} bytes  sha256 {2}" -f $file.FullName, $file.Length, $hash)
