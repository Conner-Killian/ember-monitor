# Builds Ember's Linux packages from Windows: publishes the Linux build here, then packages it
# inside WSL (Debian) with tools/build-linux.sh. Output: dist\linux\
#   powershell -ExecutionPolicy Bypass -File tools\build-linux.ps1 [-Version 1.0.6] [-Distro Debian]
param([string]$Version = "", [string]$Distro = "Debian")
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
Set-Location $root
if (-not $Version) { $Version = ([xml](Get-Content src\Ember.Linux\Ember.Linux.csproj)).Project.PropertyGroup.Version | Select-Object -First 1 }

$publish = "out\linux\publish"
if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
dotnet publish src\Ember.Linux\Ember.Linux.csproj -c Release -r linux-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:DebugType=none -p:Version=$Version -o $publish -nologo -v q
if ($LASTEXITCODE) { throw "dotnet publish failed" }

# Run the packaging script in WSL as root (installs curl if missing; no password needed).
function To-Wsl([string]$p) {
    $full = (Resolve-Path $p).Path
    '/mnt/' + $full.Substring(0, 1).ToLower() + ($full.Substring(2) -replace '\\', '/')
}
$script = To-Wsl "$root\tools\build-linux.sh"
$pub = To-Wsl "$root\$publish"
# Pass package metadata (HOMEPAGE, MAINTAINER, LICENSE) through to WSL when set.
$env:WSLENV = (@('HOMEPAGE/u', 'MAINTAINER/u', 'LICENSE/u') + @($env:WSLENV -split ':' | Where-Object { $_ })) -join ':'
wsl.exe -d $Distro -u root -- bash "$script" --version $Version --publish-dir "$pub"
if ($LASTEXITCODE) { throw "Linux packaging failed" }
