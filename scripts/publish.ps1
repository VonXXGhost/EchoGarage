#Requires -Version 7
<#
.SYNOPSIS
  发布 EchoGarage（win-x64，框架依赖 .NET 10 Desktop Runtime）。
#>
param(
  [switch]$SelfContained,
  [string]$Output = 'artifacts/publish/win-x64'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:TEMP = Join-Path $root '.tmp'
$env:TMP = $env:TEMP
New-Item -ItemType Directory -Force $env:TEMP | Out-Null

$outputPath = Join-Path $root $Output
$arguments = @(
  'publish', (Join-Path $root 'src/EchoGarage.App/EchoGarage.App.csproj'),
  '-c', 'Release',
  '-r', 'win-x64',
  '-o', $outputPath,
  '--nologo',
  '-nodeReuse:false',
  '-p:UseSharedCompilation=false'
)

if ($SelfContained) {
  $arguments += @('--self-contained', 'true', '-p:PublishSingleFile=false')
} else {
  $arguments += @('--self-contained', 'false')
}

dotnet @arguments
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "发布完成：$outputPath"
