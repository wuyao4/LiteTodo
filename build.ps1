# Build LiteTodo.exe - needs only the built-in .NET Framework 4.8 and the Roslyn compiler from VS Build Tools
param([switch]$Run, [string]$OutDir = "")
$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$src = Join-Path $root 'src'
$out = if ($OutDir) { $OutDir } else { Join-Path $root 'dist' }
New-Item -ItemType Directory -Force $out | Out-Null

# 1. Locate Roslyn csc.exe (modern C# syntax)
$csc = $null
$vswhere = "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe"
if (Test-Path $vswhere) {
    $vs = & $vswhere -latest -products * -property installationPath
    if ($vs) {
        $cand = Join-Path $vs 'MSBuild\Current\Bin\Roslyn\csc.exe'
        if (Test-Path $cand) { $csc = $cand }
    }
}
if (-not $csc) { throw 'Roslyn csc.exe not found. Please install Visual Studio Build Tools.' }

# 2. Icon
$ico = Join-Path $src 'app.ico'
if (-not (Test-Path $ico)) {
    powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $root 'tools\make-icon.ps1') -Out $ico
    if (-not (Test-Path $ico)) { throw 'Icon generation failed' }
}

# 3. Compile
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
$refs = @(
    "$fw\mscorlib.dll", "$fw\System.dll", "$fw\System.Core.dll", "$fw\System.Xml.dll", "$fw\System.Xaml.dll",
    "$fw\System.Runtime.Serialization.dll", "$fw\System.Drawing.dll", "$fw\System.Windows.Forms.dll",
    "$fw\WPF\WindowsBase.dll", "$fw\WPF\PresentationCore.dll", "$fw\WPF\PresentationFramework.dll"
) | ForEach-Object { "-r:$_" }

$cscArgs = @(
    '-nologo', '-noconfig', '-nostdlib+', '-target:winexe', '-optimize+', '-platform:anycpu', '-langversion:latest',
    '-nowarn:1701,1702', '-utf8output',
    "-out:$out\LiteTodo.exe",
    "-win32icon:$ico",
    "-win32manifest:$src\app.manifest",
    "-resource:$src\Styles.xaml,Styles.xaml",
    "-resource:$src\MainWindow.xaml,MainWindow.xaml",
    "-resource:$ico,app.ico"
) + $refs + (Get-ChildItem $src -Filter *.cs | ForEach-Object { $_.FullName })

& $csc @cscArgs
if ($LASTEXITCODE -ne 0) { throw "Build failed ($LASTEXITCODE)" }

$size = [Math]::Round((Get-Item "$out\LiteTodo.exe").Length / 1KB)
Write-Host "OK -> $out\LiteTodo.exe ($size KB)" -ForegroundColor Green
if ($Run) { Start-Process "$out\LiteTodo.exe" }
