# Builds, signs and publishes an Ember release on GitHub.
#   powershell -ExecutionPolicy Bypass -File tools\release.ps1 -Notes release-notes.md [-Version 1.2.0] [-Draft] [-SkipBuild]
#
# 1. Builds the Windows installer and the Linux packages (tools\build-installer.ps1, tools\build-linux.ps1).
# 2. Signs Ember-Setup-<v>.exe with the update key (tools\EmberSign) and verifies it with the
#    public key built into the app. Installed copies of Ember only install signed updates.
# 3. Writes SHA256SUMS and creates the GitHub release v<version> with every file attached.
#
# The private key stays in %USERPROFILE%\.ember-signing (or $env:EMBER_SIGNING_KEY). This script
# refuses to run if it's inside the repository and never uploads key files.
param(
    [Parameter(Mandatory)] [string]$Notes,
    [string]$Version = "",
    [switch]$Draft,
    [switch]$SkipBuild
)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path "$PSScriptRoot\..").Path
Set-Location $root
$env:Path = [Environment]::GetEnvironmentVariable('Path', 'Machine') + ';' + [Environment]::GetEnvironmentVariable('Path', 'User')
$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = 'C:\Program Files\GitHub CLI\gh.exe' }
if (-not $Version) { $Version = ([xml](Get-Content src\Ember\Ember.csproj)).Project.PropertyGroup.Version | Select-Object -First 1 }
$Notes = (Resolve-Path $Notes).Path
$tag = "v$Version"

# Windows PowerShell turns anything a native tool writes to stderr (progress, "not found") into a
# terminating error under 'Stop'. Run native tools with 'Continue' and check $LASTEXITCODE instead.
function Invoke-Native([scriptblock]$Block) {
    $old = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
    try { & $Block } finally { $ErrorActionPreference = $old }
}

# ---- safety checks ------------------------------------------------------------------------
$key = if ($env:EMBER_SIGNING_KEY) { $env:EMBER_SIGNING_KEY } else { Join-Path $env:USERPROFILE '.ember-signing\update-signing-key.pem' }
if (-not (Test-Path $key)) { throw "Signing key not found at $key" }
if ((Resolve-Path $key).Path.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "The signing key is inside the repository ($key). Move it out first." }
if (git status --porcelain) { throw "Uncommitted changes. Commit and push first, so the release tag matches the code." }
Invoke-Native { git fetch --quiet origin 2>&1 | Out-Null }
if ((git rev-parse HEAD) -ne (git rev-parse '@{u}')) { throw "Local main and origin/main differ. Push (or pull) first." }
Invoke-Native { & $gh release view $tag 2>&1 | Out-Null }
if ($LASTEXITCODE -eq 0) { throw "Release $tag already exists." }

# ---- build ----------------------------------------------------------------------------------
if (-not $SkipBuild) {
    & "$root\tools\build-installer.ps1" -Version $Version
    & "$root\tools\build-linux.ps1" -Version $Version
}
$exe = "dist\Ember-Setup-$Version.exe"
$linux = @(
    "dist\linux\ember-monitor_$Version-1_amd64.deb",
    "dist\linux\ember-monitor-$Version-1.x86_64.rpm",
    "dist\linux\ember-monitor-$Version-1-x86_64.pkg.tar.zst",
    "dist\linux\Ember-$Version-x86_64.AppImage",
    "dist\linux\ember-monitor-$Version-linux-x64.tar.gz",
    "dist\linux\PKGBUILD")
foreach ($f in @($exe) + $linux) { if (-not (Test-Path $f)) { throw "Missing $f" } }

# ---- sign + verify ----------------------------------------------------------------------------
Invoke-Native { dotnet build tools\EmberSign\EmberSign.csproj -c Release -nologo -v q | Out-Null }
if ($LASTEXITCODE) { throw "EmberSign build failed" }
$sign = "tools\EmberSign\bin\Release\net8.0\EmberSign.dll"
Invoke-Native { dotnet $sign sign $exe 2>&1 }
if ($LASTEXITCODE) { throw "Signing failed" }
Invoke-Native { dotnet $sign verify $exe 2>&1 }
if ($LASTEXITCODE) { throw "Signature check against the app's trusted key failed" }

# ---- checksums + publish ----------------------------------------------------------------------
$assets = @($exe, "$exe.sig") + $linux
$bad = $assets | Where-Object { $_ -match '\.(pem|key|p8|p12|pfx)$' -or (Select-String -Path $_ -Pattern 'PRIVATE KEY-----' -SimpleMatch -Quiet -ErrorAction SilentlyContinue) }
if ($bad) { throw "Refusing to upload what looks like a key: $bad" }
$sums = "dist\SHA256SUMS-$Version"
$lines = $assets | Where-Object { $_ -notlike '*PKGBUILD' } | ForEach-Object { "$((Get-FileHash $_ -Algorithm SHA256).Hash.ToLower())  $(Split-Path $_ -Leaf)" }
[IO.File]::WriteAllText((Join-Path $root $sums), ($lines -join "`n") + "`n", (New-Object Text.UTF8Encoding($false)))
Copy-Item $sums "dist\SHA256SUMS" -Force

$ghArgs = @('release', 'create', $tag, '--target', 'main', '--title', "Ember $Version", '--notes-file', $Notes)
if ($Draft) { $ghArgs += '--draft' } else { $ghArgs += '--latest' }
Invoke-Native { & $gh @ghArgs @assets "dist\SHA256SUMS" 2>&1 }
if ($LASTEXITCODE) { throw "gh release create failed" }
Write-Host "Released $tag" -ForegroundColor Green
