#Requires -Version 7
<#
.SYNOPSIS
  以隔离的数据目录启动 EchoGarage，便于测试而不影响日常配置。
#>
param(
  [string]$DataDirectory = (Join-Path (Split-Path -Parent $PSScriptRoot) '.devdata'),
  [string[]]$OpenFiles = @()
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$exe = Join-Path $root 'src/EchoGarage.App/bin/Debug/net10.0-windows/EchoGarage.exe'
if (-not (Test-Path $exe)) {
  Write-Error "请先构建：scripts/build.ps1"
  exit 1
}

$env:ECHOGARAGE_DATA_DIR = $DataDirectory
New-Item -ItemType Directory -Force $DataDirectory | Out-Null

$arguments = @()
foreach ($file in $OpenFiles) { $arguments += "`"$file`"" }
Start-Process -FilePath $exe -ArgumentList $arguments
