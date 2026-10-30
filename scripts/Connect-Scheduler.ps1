[CmdletBinding()]
param(
    [ValidateSet('Inspect','Connect','Disconnect','Recover','Upgrade','Split','InspectModules','DisconnectModules')][string]$Action='Inspect',
    [ValidateSet('all','queue','models')][string]$Module='all',
    [string]$TargetHome,
    [ValidateSet('Debug','Release')][string]$Configuration='Release'
)
$ErrorActionPreference='Stop'
if($Module -eq 'all') { $Action = switch($Action) { 'Inspect' {'InspectModules'} 'Connect' {'Split'} 'Disconnect' {'DisconnectModules'} default {$Action} } }
$repo=Split-Path $PSScriptRoot -Parent
$cli=Get-Command codex -ErrorAction Stop
$entry=Join-Path $PSScriptRoot 'connection.py'
$bridge=Join-Path $repo "src\Dispatch.McpBridge\bin\$Configuration\net8.0\Dispatch.McpBridge.dll"
$arguments=@($entry,'--action',$Action,'--module',$Module,'--cli',$cli.Source,'--bridge',$bridge)
$userSetting=[Environment]::GetEnvironmentVariable('CODEX_HOME','User')
if($userSetting) { $arguments+=@('--user-home',$userSetting) }
if($TargetHome) { $arguments+=@('--home',$TargetHome) }
python @arguments
if($LASTEXITCODE -ne 0) { throw '连接操作未完成，请根据上方状态检查。' }
