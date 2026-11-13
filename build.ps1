# Offline build with the .NET Framework 4.x compiler that ships with Windows (no Visual Studio, NuGet or network needed).
param([string]$OutputDirectory = (Join-Path $PSScriptRoot 'bin'))
$ErrorActionPreference = 'Stop'
$compiler = Join-Path $env:WINDIR 'Microsoft.NET\Framework64\v4.0.30319\csc.exe'
if (-not (Test-Path -LiteralPath $compiler)) { throw '.NET Framework 4.x compiler (csc.exe) not found.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$sources = @(Get-ChildItem -LiteralPath (Join-Path $PSScriptRoot 'src') -Filter '*.cs' -File | ForEach-Object FullName)
$binary = Join-Path $OutputDirectory 'RemoteDictation.exe'
$icon = Join-Path $PSScriptRoot 'app.ico'
& $compiler /nologo /target:winexe /platform:x64 /optimize+ /utf8output /warn:4 /codepage:65001 "/out:$binary" "/win32manifest:$(Join-Path $PSScriptRoot 'app.manifest')" "/win32icon:$icon" "/resource:$icon,RemoteDictation.app.ico" /reference:System.dll /reference:System.Core.dll /reference:System.Drawing.dll /reference:System.Windows.Forms.dll /reference:System.Web.Extensions.dll $sources
if ($LASTEXITCODE -ne 0) { throw "Build failed: $LASTEXITCODE" }
Get-Item -LiteralPath $binary | Select-Object FullName,Length
