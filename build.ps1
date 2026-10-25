# Builds ByeDPI for Windows into dist\ByeDPI (and dist\ByeDPI-windows-x64.zip)
#
#   ciadpi.exe  - byedpi v0.13 (core\byedpi, with Windows patches), built with zig cc
#   ByeDPI.exe  - GUI, built with the C# compiler that ships with .NET Framework 4.8
#   bin\        - hev-socks5-tunnel + wintun from third_party\hev-socks5-tunnel
#
# Usage: .\build.ps1 [-Zig C:\path\to\zig.exe] [-SkipCore]
param(
    [string]$Zig = $env:ZIG,
    [switch]$SkipCore
)
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$build = Join-Path $root 'build'
$dist = Join-Path $root 'dist\ByeDPI'
New-Item -ItemType Directory -Force $build | Out-Null

# ---- ciadpi.exe ----
if (-not $SkipCore) {
    if (-not $Zig) { $Zig = (Get-Command zig -ErrorAction SilentlyContinue).Source }
    if (-not $Zig) { throw 'zig not found: pass -Zig <path to zig.exe> or set ZIG (https://ziglang.org/download/)' }
    Push-Location (Join-Path $root 'core\byedpi')
    try {
        $src = 'packets.c', 'main.c', 'conev.c', 'proxy.c', 'desync.c', 'mpool.c', 'extend.c', 'win_service.c'
        & $Zig cc -target x86_64-windows-gnu -O2 -fno-sanitize=undefined -std=c99 -D_DEFAULT_SOURCE -I. `
            -Wall -Wno-unused -Wno-format -s @src -lws2_32 -lmswsock -o (Join-Path $build 'ciadpi.exe')
        if ($LASTEXITCODE -ne 0) { throw "zig cc failed ($LASTEXITCODE)" }
    } finally { Pop-Location }
    Write-Host 'Built ciadpi.exe'
}

# ---- ByeDPI.exe ----
$csc = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
$app = Join-Path $root 'src\ByeDPI'
$sources = Get-ChildItem (Join-Path $app '*.cs') | ForEach-Object { $_.FullName }
& $csc -nologo -target:winexe -platform:x64 -optimize+ -warn:4 `
    "-out:$build\ByeDPI.exe" "-win32icon:$app\app.ico" "-win32manifest:$app\app.manifest" `
    "-resource:$app\app.ico,ByeDPI.app.ico" `
    -r:System.dll -r:System.Core.dll -r:System.Drawing.dll -r:System.Windows.Forms.dll @sources
if ($LASTEXITCODE -ne 0) { throw "csc failed ($LASTEXITCODE)" }
Write-Host 'Built ByeDPI.exe'

# ---- dist ----
if (Get-Process ByeDPI, ciadpi, hev-socks5-tunnel -ErrorAction SilentlyContinue) {
    throw "ByeDPI is running and locks dist\ByeDPI. Exit it (tray icon -> Exit), then run the build again. build\ByeDPI.exe is up to date."
}
if (Test-Path $dist) { Remove-Item -Recurse -Force $dist }
New-Item -ItemType Directory -Force "$dist\bin", "$dist\licenses" | Out-Null
Copy-Item "$build\ByeDPI.exe" $dist
Copy-Item "$build\ciadpi.exe" "$dist\bin"
Copy-Item "$root\third_party\hev-socks5-tunnel\*" "$dist\bin"
Copy-Item "$root\core\byedpi\LICENSE" "$dist\licenses\byedpi-LICENSE.txt"
Copy-Item "$root\third_party\licenses\*" "$dist\licenses" -ErrorAction SilentlyContinue
Copy-Item "$root\README.md" "$dist\README.md" -ErrorAction SilentlyContinue

$zip = Join-Path $root 'dist\ByeDPI-windows-x64.zip'
if (Test-Path $zip) { Remove-Item $zip }
Compress-Archive -Path $dist -DestinationPath $zip
Write-Host "Done: $dist"
Write-Host "      $zip"
