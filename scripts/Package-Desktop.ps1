[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$source=Join-Path $repo 'src\Dispatch.Desktop\bin\Release\net8.0-windows'
if(-not(Test-Path -LiteralPath (Join-Path $source 'Dispatch.Desktop.exe'))) { throw '请先通过 Release 编译与测试。' }
$releaseKey='0.21.0-'+[Guid]::NewGuid().ToString('N').Substring(0,8)
$destination=Join-Path $repo "artifacts\release\$releaseKey"
if(Test-Path -LiteralPath $destination) { throw '候选目录已存在，拒绝覆盖。' }
New-Item -ItemType Directory -Path $destination | Out-Null
foreach($item in Get-ChildItem -LiteralPath $source) { Copy-Item -LiteralPath $item.FullName -Destination $destination -Recurse }
[IO.File]::WriteAllText((Join-Path $destination '启动调度工具.cmd'),"@echo off`r`nstart `"`" `"%~dp0Dispatch.Desktop.exe`"`r`n",[Text.Encoding]::ASCII)
$files=Get-ChildItem -LiteralPath $destination -Recurse -File | Where-Object {$_.Extension -ne '.pyc' -and $_.FullName -notmatch '[\\/]__pycache__[\\/]'} | ForEach-Object {
    [pscustomobject]@{Path=[IO.Path]::GetRelativePath($destination,$_.FullName);SHA256=(Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash}
}
[pscustomobject]@{Version='0.21.0';ResourceContinuationProtocolSupported=$true;WorkspaceSceneRestoreVerified=$false;AutomaticResourceWakeupSupported=$false;IndependentModulesSupported=$true;SupervisorTakeoverProtocolSupported=$true;ResourcePermitsSupported=$true;ModelSwitchSupported=$false;ModelHandoffProtocolSupported=$true;SupervisorReturnRequired=$true;Files=@($files)} | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $destination 'package-manifest.json') -Encoding utf8
Write-Output $destination
