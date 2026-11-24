# Builds MMOOTSetup.exe with its .NET runtime included; requires the .NET 8 SDK.
# Use -Framework only for the legacy Windows .NET Framework build.
#
#   powershell -NoProfile -File MMOOT/tools/make_installer.ps1
#   powershell -NoProfile -File MMOOT/tools/make_installer.ps1 -Console   # console build, for --validate/--check-updates
#
# Output: build/installer/MMOOTSetup.exe under the project root
param([switch]$Console, [switch]$Framework,
    [ValidateSet('win-x64','win-arm64','win-x86')][string]$Runtime = 'win-x64')

$ErrorActionPreference = 'Stop'
$standaloneSource = Join-Path $PSScriptRoot 'MMOOTSetup.cs'
$standalone = Test-Path -LiteralPath $standaloneSource
$root = if ($standalone) { Split-Path -Parent $PSScriptRoot } else { Split-Path -Parent (Split-Path -Parent $PSScriptRoot) }
$source = if ($standalone) { $standaloneSource } else { Join-Path $PSScriptRoot 'installer/MMOOTSetup.cs' }
$outDir = Join-Path $root 'build/installer'
New-Item -ItemType Directory -Force -Path $outDir | Out-Null

if (-not $Framework) {
    $project = [IO.Path]::ChangeExtension($source, '.csproj')
    $portableDir = Join-Path $outDir ('portable-' + $Runtime + $(if ($Console) { '-console' }))
    $assembly = if ($Console) { 'MMOOTSetup-console' } else { 'MMOOTSetup' }
    $outputType = if ($Console) { 'Exe' } else { 'WinExe' }
    & dotnet publish $project -c Release -r $Runtime --self-contained true -o $portableDir "-p:AssemblyName=$assembly" "-p:OutputType=$outputType"
    if ($LASTEXITCODE -ne 0) { throw "Portable launcher build failed ($LASTEXITCODE)" }
    $portableExe = Join-Path $portableDir ($assembly + '.exe')
    $out = Join-Path $outDir ($assembly + '.exe')
    if (Test-Path -LiteralPath $out) { Copy-Item -LiteralPath $out -Destination ($out + '.pre_portable.bak') }
    Copy-Item -LiteralPath $portableExe -Destination $out
    $hash = (Get-FileHash -LiteralPath $out -Algorithm SHA256).Hash.ToLower()
    Write-Host "built portable $Runtime $out (runtime included), sha256 $hash"
    return
}

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
