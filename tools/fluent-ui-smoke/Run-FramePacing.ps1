param(
    [string]$OutputRoot = (Join-Path $PSScriptRoot '../../artifacts/frame-pacing/run'),
    [string]$TraceTool = (Join-Path $PSScriptRoot '../../artifacts/frame-pacing/tools/dotnet-trace.exe'),
    [switch]$SkipBuild,
    [switch]$SystemTrace
)
$ErrorActionPreference = 'Stop'
$OutputRoot = [IO.Path]::GetFullPath($OutputRoot)
New-Item -ItemType Directory -Force -Path $OutputRoot | Out-Null
$appDirectory = Join-Path $OutputRoot 'app'
$qaRoot = Join-Path $OutputRoot 'qa'
if (Test-Path -LiteralPath $qaRoot) { throw 'QA output already exists. Use a fresh OutputRoot to avoid accepting stale reports.' }
$project = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../../Plain Craft Launcher 2/Plain Craft Launcher 2.csproj'))
$injection = Join-Path $PSScriptRoot 'Inject.targets'
if (!$SkipBuild) {
    & dotnet build $project -c Release --no-restore "-p:CustomBeforeMicrosoftCommonTargets=$injection" '-p:IntermediateOutputPath=obj/FramePacing-Release/' "-p:OutDir=$appDirectory/" *> (Join-Path $OutputRoot 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Release diagnostic build failed; see build.log' }
}
$executable = Join-Path $appDirectory 'Plain Craft Launcher 2.exe'
if (!(Test-Path -LiteralPath $executable)) { throw "Missing isolated executable: $executable" }
@{
    configuration = 'Release'
    startedUtc = [DateTime]::UtcNow.ToString('o')
    executable = $executable
    mainAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $appDirectory 'Plain Craft Launcher 2.dll')).Hash
    coreAssemblySha256 = (Get-FileHash -LiteralPath (Join-Path $appDirectory 'PCL.Core.dll')).Hash
    requestedSystemTrace = [bool]$SystemTrace
} | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $OutputRoot 'run-manifest.json')
Set-Content -LiteralPath (Join-Path $appDirectory 'frame-pacing-only.flag') -Value ''
$systemRecording = $false
if ($SystemTrace) {
    # A failed start never grants ownership of an existing recording. Do not cancel it.
    & wpr -start GeneralProfile -start GPU -start DesktopComposition -start DotNET -filemode *> (Join-Path $OutputRoot 'wpr-start.txt')
    $systemRecording = $LASTEXITCODE -eq 0
}
$oldQaRoot = $env:PCL_FRAME_QA_ROOT
try {
    $env:PCL_FRAME_QA_ROOT = $qaRoot
    if (Test-Path -LiteralPath $TraceTool) {
        $traceArguments = @('collect', '--profile', 'dotnet-sampled-thread-time,gc-collect', '--providers', 'PCL-FramePacing:0xFFFFFFFF:4', '--output', ('"' + (Join-Path $OutputRoot 'managed.nettrace') + '"'), '--', ('"' + $executable + '"'))
        $process = Start-Process -FilePath $TraceTool -ArgumentList $traceArguments -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $OutputRoot 'trace-stdout.txt') -RedirectStandardError (Join-Path $OutputRoot 'trace-stderr.txt')
    } else {
        'Unavailable: dotnet-trace not installed. Managed stack samples and timed GC events were not captured.' | Set-Content (Join-Path $OutputRoot 'managed-trace-unavailable.txt')
        $process = Start-Process -FilePath $executable -WindowStyle Hidden -PassThru
    }
    $process.WaitForExit()
    if ($process.ExitCode -ne 0) { throw "Diagnostic process exited with code $($process.ExitCode)" }
    $report = Join-Path $qaRoot 'frame-pacing-report.txt'
    if (!(Test-Path -LiteralPath $report)) { throw 'No complete frame-pacing report; inspect qa/report.txt and trace logs' }
    Get-Content -LiteralPath $report
} finally {
    $env:PCL_FRAME_QA_ROOT = $oldQaRoot
    if ($systemRecording) {
        & wpr -stop (Join-Path $OutputRoot 'system.etl') *> (Join-Path $OutputRoot 'wpr-stop.txt')
    }
    if (!$systemRecording) {
        'Unavailable: GPU/DWM presentation events, native render-thread stacks, and exact WPF layout timing were not captured. Rendering callbacks are not displayed FPS.' | Set-Content (Join-Path $OutputRoot 'system-trace-unavailable.txt')
    }
}
