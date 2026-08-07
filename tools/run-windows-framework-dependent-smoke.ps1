[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $ExecutablePath,

    [ValidateRange(5, 180)]
    [int] $StartupTimeoutSeconds = 30,

    [ValidateRange(1, 60)]
    [int] $RunSeconds = 5
)

$ErrorActionPreference = 'Stop'
$resolvedExecutable = (Resolve-Path -LiteralPath $ExecutablePath).Path
$workingDirectory = Split-Path -Parent $resolvedExecutable
$process = Start-Process -FilePath $resolvedExecutable -WorkingDirectory $workingDirectory -PassThru

try {
    $deadline = [DateTime]::UtcNow.AddSeconds($StartupTimeoutSeconds)
    do {
        Start-Sleep -Milliseconds 250
        $process.Refresh()
        if ($process.HasExited) {
            throw "Framework-dependent process exited during startup with code $($process.ExitCode)."
        }
    } while ($process.MainWindowHandle -eq 0 -and [DateTime]::UtcNow -lt $deadline)

    $process.Refresh()
    if ($process.HasExited) {
        throw "Framework-dependent process exited during startup with code $($process.ExitCode)."
    }
    if ($process.MainWindowHandle -eq 0) {
        throw "Framework-dependent process did not create a main window within $StartupTimeoutSeconds seconds."
    }

    Start-Sleep -Seconds $RunSeconds
    $process.Refresh()
    if ($process.HasExited) {
        throw "Framework-dependent process exited during the smoke window with code $($process.ExitCode)."
    }

    if (-not $process.CloseMainWindow()) {
        throw 'Framework-dependent process did not accept a graceful window close request.'
    }
    if (-not $process.WaitForExit(30000)) {
        throw 'Framework-dependent process did not exit after the graceful close request.'
    }
    if ($process.ExitCode -ne 0) {
        throw "Framework-dependent process exited with code $($process.ExitCode) after graceful shutdown."
    }

    Write-Host "Framework-dependent smoke passed: $resolvedExecutable"
}
finally {
    if ($null -ne $process) {
        $process.Refresh()
        if (-not $process.HasExited) {
            Stop-Process -Id $process.Id -Force -ErrorAction SilentlyContinue
        }
        $process.Dispose()
    }
}
