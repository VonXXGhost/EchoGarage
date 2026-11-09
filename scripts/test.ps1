#Requires -Version 7
<#
.SYNOPSIS
  构建并运行 EchoGarage 的测试（xUnit v3 进程内 runner，不依赖 VSTest 宿主）。
#>
param(
  [ValidateSet('Debug', 'Release')]
  [string]$Configuration = 'Debug'
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
& (Join-Path $PSScriptRoot 'build.ps1') -Configuration $Configuration -OfflineSafe
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

$testExe = Join-Path $root "tests/EchoGarage.Tests/bin/$Configuration/net10.0-windows/EchoGarage.Tests.exe"
if (-not (Test-Path $testExe)) {
  Write-Error "测试可执行文件不存在：$testExe"
  exit 1
}

& $testExe
exit $LASTEXITCODE
