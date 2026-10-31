param([string]$OutputRoot = (Join-Path $PSScriptRoot '../../artifacts/hdr-smoke/run'))
$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
$qaRoot = Join-Path $OutputRoot 'qa'
if (Test-Path -LiteralPath $qaRoot) { throw 'Use a fresh OutputRoot; existing QA results must not be reused.' }
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$appDirectory = Join-Path $OutputRoot 'app'
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Plain Craft Launcher 2/Plain Craft Launcher 2.csproj'))
$injection = Join-Path $PSScriptRoot 'Inject.targets'
& dotnet build $project -c Release -p:Platform=x64 "-p:CustomBeforeMicrosoftCommonTargets=$injection" '-p:IntermediateOutputPath=obj/HdrSmoke/' "-p:OutDir=$appDirectory/" *> (Join-Path $OutputRoot 'build.log')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostic build failed; see build.log.' }
$oldQaRoot = $env:PCL_HDR_QA_ROOT
try {
    $env:PCL_HDR_QA_ROOT = $qaRoot
    $process = Start-Process -FilePath (Join-Path $appDirectory 'Plain Craft Launcher 2.exe') -WindowStyle Hidden -PassThru
    if (!$process.WaitForExit(60000)) { throw "Isolated HDR process $($process.Id) timed out; inspect it before retrying." }
    $report = Join-Path $qaRoot 'hdr-report.txt'
    if (!(Test-Path -LiteralPath $report)) { throw 'HDR report missing.' }
    Get-Content -LiteralPath $report
    if (Select-String -LiteralPath $report -Pattern '^FAIL ') { throw 'HDR smoke failed.' }
} finally {
    $env:PCL_HDR_QA_ROOT = $oldQaRoot
}
