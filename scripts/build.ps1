#Requires -Version 7
<#
.SYNOPSIS
  构建 EchoGarage 解决方案。
.DESCRIPTION
  默认把 .NET 与 NuGet 缓存放在仓库内（.dotnet / .nuget），避免污染用户目录；
  在受限制环境（沙箱、无命名管道）中自动关闭编译器服务器与节点复用。
#>
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Debug',
  [switch]$Restore,
  [switch]$OfflineSafe
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot

$env:DOTNET_CLI_HOME = Join-Path $root '.dotnet'
$env:NUGET_PACKAGES = Join-Path $root '.nuget'
$env:TEMP = Join-Path $root '.tmp'
$env:TMP = $env:TEMP
$env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
New-Item -ItemType Directory -Force $env:TEMP | Out-Null

if ($Restore) {
  dotnet restore (Join-Path $root 'EchoGarage.sln')
  if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }
}

$arguments = @(
  'build', (Join-Path $root 'EchoGarage.sln'),
  '-c', $Configuration,
  '--nologo'
)

if ($Restore) { $arguments += '--no-restore' }
if ($OfflineSafe) {
  $arguments += @('-nodeReuse:false', '-p:UseSharedCompilation=false', '-m:1')
}

dotnet @arguments
exit $LASTEXITCODE
