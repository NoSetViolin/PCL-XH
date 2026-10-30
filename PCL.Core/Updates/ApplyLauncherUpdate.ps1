$ErrorActionPreference = 'Stop'
$requestPath = [IO.Path]::GetFullPath($env:PCL_XH_UPDATE_MANIFEST)
$stage = [IO.Path]::GetDirectoryName($requestPath)
$parentExited = $false
$replaced = $false
$validated = $false
$child = $null
$r = $null
$utf8 = New-Object Text.UTF8Encoding($false)
function Write-Text($path, $text) {
    [IO.File]::WriteAllText($path + '.tmp', [string]$text, $utf8)
    [IO.File]::Move($path + '.tmp', $path)
}
function Hash-File($path) {
    $stream = [IO.File]::OpenRead($path)
    $hash = [Security.Cryptography.SHA256]::Create()
    try { return [BitConverter]::ToString($hash.ComputeHash($stream)).Replace('-', '') }
    finally { $stream.Dispose(); $hash.Dispose() }
}
function Wait-Retry($action, $seconds) {
    $until = [DateTime]::UtcNow.AddSeconds($seconds)
    while ($true) {
        try { & $action; return }
        catch { if ([DateTime]::UtcNow -ge $until) { throw }; Start-Sleep -Milliseconds 200 }
    }
}
function Start-Launcher($failed) {
    $info = New-Object Diagnostics.ProcessStartInfo
    $info.FileName = $target
    $info.WorkingDirectory = [IO.Path]::GetDirectoryName($target)
    $info.UseShellExecute = $false
    $info.EnvironmentVariables.Remove('PCL_XH_UPDATE_MANIFEST')
    $info.EnvironmentVariables.Remove('PCL_XH_UPDATE_READY_REQUEST')
    $info.EnvironmentVariables.Remove('PCL_XH_UPDATE_READY_TOKEN')
    $info.EnvironmentVariables.Remove('PCL_XH_UPDATE_FAILURE')
    if ($failed) { $info.EnvironmentVariables['PCL_XH_UPDATE_FAILURE'] = [IO.Path]::Combine($stage, 'result.json') }
    else {
        $info.EnvironmentVariables['PCL_XH_UPDATE_READY_REQUEST'] = $requestPath
        $info.EnvironmentVariables['PCL_XH_UPDATE_READY_TOKEN'] = $r.Token
    }
    return [Diagnostics.Process]::Start($info)
}
try {
    if ([IO.Path]::GetFileName($requestPath) -cne 'request.json' -or
        [IO.Path]::GetFileName($stage) -notmatch '^\.pcl-update-[0-9a-f]{32}$') { throw 'Invalid update workspace' }
    $r = [IO.File]::ReadAllText($requestPath) | ConvertFrom-Json
    $target = [IO.Path]::GetFullPath($r.TargetExe)
    if ($r.Schema -ne 1 -or $r.Token -notmatch '^[0-9A-F]{64}$' -or
        $r.OldSha256 -notmatch '^[0-9A-F]{64}$' -or $r.NewSha256 -notmatch '^[0-9A-F]{64}$' -or
        $r.StartupTimeoutSeconds -lt 5 -or $r.StartupTimeoutSeconds -gt 180 -or
        $r.StartupGraceSeconds -lt 0 -or $r.StartupGraceSeconds -gt 10 -or
        ![string]::Equals([IO.Path]::GetDirectoryName($stage), [IO.Path]::GetDirectoryName($target), [StringComparison]::OrdinalIgnoreCase) -or
        ![IO.Path]::GetExtension($target).Equals('.exe', [StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid update target' }
    if (([IO.File]::GetAttributes($stage) -band [IO.FileAttributes]::ReparsePoint) -or
        ([IO.File]::GetAttributes($target) -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse targets are not supported' }
    $candidate = [IO.Path]::Combine($stage, 'new.exe')
    $backup = [IO.Path]::Combine($stage, 'old.exe')
    if ((Hash-File $candidate) -ine $r.NewSha256 -or (Hash-File $target) -ine $r.OldSha256) { throw 'Update hash mismatch' }
    $signature = [IO.File]::OpenRead($candidate)
    try { if ($signature.ReadByte() -ne 77 -or $signature.ReadByte() -ne 90) { throw 'Invalid executable' } }
    finally { $signature.Dispose() }
    $parent = [Diagnostics.Process]::GetProcessById([int]$r.ParentProcessId)
    if ($parent.StartTime.ToUniversalTime().Ticks -ne [long]$r.ParentStartTicks -or
        ![string]::Equals([IO.Path]::GetFullPath($parent.MainModule.FileName), $target, [StringComparison]::OrdinalIgnoreCase)) { throw 'Parent process identity changed' }
    Write-Text ([IO.Path]::Combine($stage, 'helper.ready')) $r.Token
    $commit = [IO.Path]::Combine($stage, 'commit.ready')
    $until = [DateTime]::UtcNow.AddSeconds(30)
    while (![IO.File]::Exists($commit)) {
        if ($parent.HasExited -or [DateTime]::UtcNow -ge $until) { throw 'Update was not committed' }
        Start-Sleep -Milliseconds 50
    }
    if ([IO.File]::ReadAllText($commit).Trim() -cne $r.Token) { throw 'Invalid commit token' }
    if (!$parent.WaitForExit(90000)) { throw 'The old launcher did not exit' }
    $parentExited = $true
    if ((Hash-File $candidate) -ine $r.NewSha256 -or (Hash-File $target) -ine $r.OldSha256) { throw 'Files changed after verification' }
    Wait-Retry { [IO.File]::Replace($candidate, $target, $backup, $true) } 30
    $replaced = $true
    $child = Start-Launcher $false
    if ($null -eq $child) { throw 'Could not start the new launcher' }
    $ready = [IO.Path]::Combine($stage, 'app.ready')
    $until = [DateTime]::UtcNow.AddSeconds([int]$r.StartupTimeoutSeconds)
    while (![IO.File]::Exists($ready)) {
        if ($child.HasExited) { throw 'New launcher exited before startup completed' }
        if ([DateTime]::UtcNow -ge $until) { throw 'New launcher startup timed out' }
        Start-Sleep -Milliseconds 100
    }
    if ([IO.File]::ReadAllText($ready).Trim() -cne $r.Token) { throw 'Invalid startup acknowledgement' }
    if ($child.WaitForExit([int]$r.StartupGraceSeconds * 1000)) { throw 'New launcher exited during startup validation' }
    $validated = $true
    $receipt = [IO.Path]::Combine([IO.Path]::GetDirectoryName($target), '.' + [IO.Path]::GetFileName($target) + '.update-version.json')
    Write-Text ([IO.Path]::Combine($stage, 'receipt.json')) (@{ ReleaseTag = $r.ReleaseTag; Sha256 = $r.NewSha256 } | ConvertTo-Json -Compress)
    [IO.File]::Copy([IO.Path]::Combine($stage, 'receipt.json'), $receipt, $true)
    Wait-Retry { [IO.File]::Delete($backup) } 15
    foreach ($name in @('new.exe', 'request.json', 'helper.ready', 'commit.ready', 'app.ready', 'receipt.json')) {
        $path = [IO.Path]::Combine($stage, $name)
        if ([IO.File]::Exists($path)) { [IO.File]::Delete($path) }
    }
    if ([IO.Directory]::GetFileSystemEntries($stage).Length -eq 0) { [IO.Directory]::Delete($stage) }
    exit 0
}
catch {
    $message = $_.Exception.Message
    try {
        if ($replaced -and !$validated) {
            if ($null -ne $child -and !$child.HasExited) { $child.Kill(); $null = $child.WaitForExit(10000) }
            # Windows PowerShell coerces a null backup argument to an invalid empty path.
            Wait-Retry { [IO.File]::Replace($backup, $target, [IO.Path]::Combine($stage, 'failed-new.exe'), $true) } 30
        }
        Write-Text ([IO.Path]::Combine($stage, 'result.json')) (@{ Success = $validated; Error = $message; RolledBack = ($replaced -and !$validated) } | ConvertTo-Json -Compress)
        if ($parentExited -and (Hash-File $target) -ieq $r.OldSha256) { $null = Start-Launcher $true }
    }
    catch { try { Write-Text ([IO.Path]::Combine($stage, 'update.log')) ($message + ' / Recovery: ' + $_.Exception.Message) } catch {} }
    exit 1
}
