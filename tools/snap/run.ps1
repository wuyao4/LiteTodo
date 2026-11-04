# Dev only: build LiteTodo + Snap into a temp folder, then
#   render UI screenshots:  .\run.ps1 -Data sample.json -Prefix demo
#   run logic tests:        .\run.ps1 -Data sample.json -Test
param([Parameter(Mandatory)][string]$Data, [string]$Prefix = "snap", [string]$Out = "$env:TEMP\litetodo-snap", [switch]$Test)
$ErrorActionPreference = 'Stop'
$dev = Join-Path $Out 'bin'
New-Item -ItemType Directory -Force $dev | Out-Null
& "$PSScriptRoot\..\..\build.ps1" -OutDir $dev | Out-Null
$csc = Join-Path (& "${env:ProgramFiles(x86)}\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath) 'MSBuild\Current\Bin\Roslyn\csc.exe'
$fw = "$env:WINDIR\Microsoft.NET\Framework64\v4.0.30319"
& $csc -nologo -noconfig -nostdlib+ -target:exe -langversion:latest "-out:$dev\Snap.exe" "-r:$dev\LiteTodo.exe" `
    "-r:$fw\mscorlib.dll" "-r:$fw\System.dll" "-r:$fw\System.Core.dll" "-r:$fw\System.Drawing.dll" "-r:$fw\System.Xaml.dll" `
    "-r:$fw\WPF\WindowsBase.dll" "-r:$fw\WPF\PresentationCore.dll" "-r:$fw\WPF\PresentationFramework.dll" "$PSScriptRoot\Snap.cs" "$PSScriptRoot\Video.cs"
if ($LASTEXITCODE -ne 0) { throw 'Snap build failed' }
$copy = Join-Path $Out 'data.json'
Copy-Item $Data $copy -Force   # never touch the real data file
& "$dev\Snap.exe" $copy $Out ($(if ($Test) { '--test' } else { $Prefix }))
