# 只定位已安装SDK文件，不创建COM对象、不启动或操作SolidWorks。
$interopCandidates=@()
if($env:SOLIDWORKS_INTEROP_PATH){$interopCandidates+= $env:SOLIDWORKS_INTEROP_PATH}
foreach($process in @(Get-Process -Name SLDWORKS -ErrorAction SilentlyContinue)) {
    if($process.Path){$interopCandidates+=Join-Path (Split-Path $process.Path) 'api/redist/SolidWorks.Interop.sldworks.dll'}
}
foreach($setup in @(Get-ItemProperty 'HKLM:/SOFTWARE/SolidWorks/SOLIDWORKS */Setup' -ErrorAction SilentlyContinue)) {
    if($setup.'SolidWorks Folder'){$interopCandidates+=Join-Path $setup.'SolidWorks Folder' 'api/redist/SolidWorks.Interop.sldworks.dll'}
}
$interopCandidates+=Join-Path $env:ProgramFiles 'SOLIDWORKS Corp/SOLIDWORKS/api/redist/SolidWorks.Interop.sldworks.dll'
$interopPath=$interopCandidates|Where-Object {Test-Path -LiteralPath $_ -PathType Leaf}|Select-Object -First 1
if(-not $interopPath){throw '未找到已安装SolidWorks SDK，请设置SOLIDWORKS_INTEROP_PATH为SolidWorks.Interop.sldworks.dll绝对路径。'}
$env:SOLIDWORKS_INTEROP_PATH=[IO.Path]::GetFullPath($interopPath)
