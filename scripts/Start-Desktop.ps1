[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Release')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$exe=Join-Path $repo "src\Dispatch.Desktop\bin\$Configuration\net8.0-windows\Dispatch.Desktop.exe"
if(-not(Test-Path -LiteralPath $exe)) { throw '请先运行 scripts\Build-Test.ps1。' }
Start-Process -FilePath $exe
