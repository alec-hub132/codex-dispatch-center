[CmdletBinding()]
param([ValidateSet('Debug','Release')][string]$Configuration='Debug')
$ErrorActionPreference='Stop'
$repo=Split-Path $PSScriptRoot -Parent
$env:DOTNET_CLI_TELEMETRY_OPTOUT='1'
$env:DOTNET_GENERATE_ASPNET_CERTIFICATE='false'
$env:MSBuildEnableWorkloadResolver='false'
$env:PYTHONIOENCODING='utf-8'
$env:DISPATCH_CONFIGURATION=$Configuration
. (Join-Path $PSScriptRoot 'Resolve-SolidWorksInterop.ps1')
Push-Location $repo
try {
    foreach($project in @('Dispatch.Daemon','Dispatch.McpBridge','Dispatch.Desktop')) {
        dotnet build "src/$project/$project.csproj" -c $Configuration
        if($LASTEXITCODE -ne 0) { throw "$project 编译失败" }
    }
    dotnet test tests/Dispatch.Tests/Dispatch.Tests.csproj -c $Configuration --logger 'trx;LogFileName=core.trx' --results-directory artifacts/tests
    if($LASTEXITCODE -ne 0) { throw '核心测试失败' }
    node --test tests/continuation-policy.test.mjs tests/queue-wake-policy.test.mjs tests/queue-ipc.test.mjs
    if($LASTEXITCODE -ne 0) { throw '续跑策略测试失败' }
    python tests/test_connection.py
    if($LASTEXITCODE -ne 0) { throw '连接安装器测试失败' }
    python tests/integration_smoke.py
    if($LASTEXITCODE -ne 0) { throw '进程集成测试失败' }
    $testKey='desktop-'+[Guid]::NewGuid().ToString('N')
    $smokeRoot=Join-Path $repo "artifacts\desktop\$testKey"
    dotnet "src/Dispatch.Desktop/bin/$Configuration/net8.0-windows/Dispatch.Desktop.dll" --self-test --instance $testKey --data "$smokeRoot\data" --home "$smokeRoot\codex" --output "$smokeRoot\render"
    if($LASTEXITCODE -ne 0) { throw "桌面烟测失败，请读取 $smokeRoot\data\logs\bug.jsonl" }
    Write-Output "桌面烟测证据：$smokeRoot\render"
} finally { Pop-Location }
