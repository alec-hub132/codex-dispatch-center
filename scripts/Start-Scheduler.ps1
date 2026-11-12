[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Debug', [string]$DataRoot=(Join-Path $env:LOCALAPPDATA 'CodexDispatch'))
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$dll=Join-Path $repo "src\Dispatch.Daemon\bin\$Configuration\net8.0\Dispatch.Daemon.dll"
if(-not(Test-Path -LiteralPath $dll)) { throw '请先运行 scripts\Build-Test.ps1 编译。' }
dotnet $dll --data $DataRoot
exit $LASTEXITCODE
