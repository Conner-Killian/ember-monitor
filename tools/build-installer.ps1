# Publishes Ember (self-contained, no .NET install needed) and builds the installer.
#   powershell -ExecutionPolicy Bypass -File tools\build-installer.ps1 [-Version 1.0.0]
param([string]$Version = "1.2.0")
$ErrorActionPreference = 'Stop'
$root = Resolve-Path "$PSScriptRoot\.."
$publish = Join-Path $root "publish"
$iscc = @("$env:LOCALAPPDATA\Programs\Inno Setup 6\ISCC.exe", "${env:ProgramFiles(x86)}\Inno Setup 6\ISCC.exe", "$env:ProgramFiles\Inno Setup 6\ISCC.exe") |
        Where-Object { Test-Path $_ } | Select-Object -First 1
if (-not $iscc) { throw "Inno Setup 6 not found. Install with: winget install JRSoftware.InnoSetup" }

if (Test-Path $publish) { Remove-Item -Recurse -Force $publish }
Write-Host "Publishing Ember $Version..." -ForegroundColor Cyan
dotnet publish "$root\src\Ember\Ember.csproj" -c Release -r win-x64 --self-contained true `
    -p:PublishReadyToRun=true -p:Version=$Version -p:DebugType=none -o $publish -nologo
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed" }

Write-Host "Building installer..." -ForegroundColor Cyan
& $iscc "/DAppVersion=$Version" "$root\installer\Ember.iss"
if ($LASTEXITCODE -ne 0) { throw "Inno Setup failed" }
Get-ChildItem "$root\dist\Ember-Setup-$Version.exe" | Select-Object FullName, @{n='SizeMB';e={[math]::Round($_.Length/1MB,1)}}
