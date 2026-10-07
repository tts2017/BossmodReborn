[CmdletBinding()]
param(
    [ValidateRange(1, 1000000)]
    [int]$MatrixPatterns = 20000,

    [ValidateRange(1, 32)]
    [int]$MatrixWorkers = 4,

    [switch]$SkipBuild
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path
$solution = Join-Path $root 'BossModReborn.sln'
$harnessProject = Join-Path $root 'tools\mnk_real_harness\MnkRealHarness.csproj'
$regressionProject = Join-Path $root 'tools\mnk_regression\MnkRegression.csproj'
$mnkSource = Join-Path $root 'BossMod\Autorotation\Standard\xan\Melee\MNK.cs'
$harnessSource = Join-Path $root 'tools\mnk_real_harness\Program.cs'
$resultFiles = @(
    (Join-Path $root 'tools\mnk_regression\results\emulated_current.json'),
    (Join-Path $root 'tools\mnk_regression\results\emulated_report.md')
)
$backupDirectory = Join-Path ([IO.Path]::GetTempPath()) ("mnk-preflight-" + [guid]::NewGuid())
$resultBackups = @{}
$matrixJobs = @()
$pushedLocation = $false
$success = $false
$failureMessage = $null
$stopwatch = [Diagnostics.Stopwatch]::StartNew()

function Invoke-DotnetCommand {
    param(
        [string]$Name,
        [string[]]$Arguments
    )

    Write-Output "BEGIN $Name"
    & dotnet @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$Name failed with exit code $LASTEXITCODE"
    }
    Write-Output "PASS $Name"
}

function Backup-RegressionResults {
    New-Item -ItemType Directory -Path $backupDirectory | Out-Null
    foreach ($resultFile in $resultFiles) {
        if (Test-Path -LiteralPath $resultFile) {
            $backupFile = Join-Path $backupDirectory ([IO.Path]::GetFileName($resultFile))
            Copy-Item -LiteralPath $resultFile -Destination $backupFile -Force
            $resultBackups[$resultFile] = $backupFile
        }
        else {
            $resultBackups[$resultFile] = $null
        }
    }
}

function Restore-RegressionResults {
    foreach ($resultFile in $resultFiles) {
        $backupFile = $resultBackups[$resultFile]
        if ($null -ne $backupFile) {
            Copy-Item -LiteralPath $backupFile -Destination $resultFile -Force
        }
        elseif (Test-Path -LiteralPath $resultFile) {
            Remove-Item -LiteralPath $resultFile -Force
        }
    }

    if (Test-Path -LiteralPath $backupDirectory) {
        Remove-Item -LiteralPath $backupDirectory -Recurse -Force
    }
}

try {
    Push-Location $root
    $pushedLocation = $true
    Backup-RegressionResults

    if (-not $SkipBuild) {
        Invoke-DotnetCommand 'solution-build' @('build', $solution, '-c', 'Debug', '-v', 'minimal')
        Invoke-DotnetCommand 'real-harness-build' @('build', $harnessProject, '-c', 'Debug', '-v', 'minimal')
    }

    Invoke-DotnetCommand 'real-mnk-suite' @('run', '--no-build', '--project', $harnessProject, '--', 'suite')

    $workers = [Math]::Min($MatrixWorkers, $MatrixPatterns)
    $baseLimit = [Math]::Floor($MatrixPatterns / $workers)
    $remainder = $MatrixPatterns % $workers
    $start = 0
    for ($index = 0; $index -lt $workers; ++$index) {
        $limit = $baseLimit + $(if ($index -lt $remainder) { 1 } else { 0 })
        $matrixJobs += Start-Job -ArgumentList $root, $harnessProject, $start, $limit -ScriptBlock {
            param($jobRoot, $jobProject, $jobStart, $jobLimit)

            Set-Location $jobRoot
            $output = & dotnet run --no-build --project $jobProject -- run --start $jobStart --limit $jobLimit 2>&1 | Out-String
            [pscustomobject]@{
                Start = $jobStart
                Limit = $jobLimit
                ExitCode = $LASTEXITCODE
                Output = $output
            }
        }
        $start += $limit
    }

    $matrixResults = @($matrixJobs | Receive-Job -Wait -AutoRemoveJob)
    $matrixJobs = @()
    if ($matrixResults.Count -ne $workers) {
        throw "matrix returned $($matrixResults.Count) results for $workers workers"
    }

    foreach ($matrixResult in $matrixResults | Sort-Object Start) {
        Write-Output $matrixResult.Output.TrimEnd()
        if ($matrixResult.ExitCode -ne 0) {
            throw "matrix start=$($matrixResult.Start) limit=$($matrixResult.Limit) failed with exit code $($matrixResult.ExitCode)"
        }

        foreach ($expected in @('failures=0', 'duplicate_patterns=0', 'policy_failures=0', 'empty_queues=0')) {
            if ($matrixResult.Output -notmatch "(?m)^$([regex]::Escape($expected))\r?$") {
                throw "matrix start=$($matrixResult.Start) did not report $expected"
            }
        }
    }
    Write-Output "PASS real-mnk-matrix patterns=$MatrixPatterns workers=$workers"

    Write-Output 'BEGIN mnk-regression'
    $regressionOutput = & dotnet run --project $regressionProject -- --all 2>&1 | Out-String
    Write-Output $regressionOutput.TrimEnd()
    if ($LASTEXITCODE -ne 0) {
        throw "mnk-regression failed with exit code $LASTEXITCODE"
    }
    if ($regressionOutput -notmatch '(?m)^Verdict: ADOPTABLE\r?$') {
        throw 'mnk-regression did not report Verdict: ADOPTABLE'
    }
    Write-Output 'PASS mnk-regression'

    Write-Output 'BEGIN diff-check'
    & git diff --check -- $mnkSource $harnessSource
    if ($LASTEXITCODE -ne 0) {
        throw "diff-check failed with exit code $LASTEXITCODE"
    }
    Write-Output 'PASS diff-check'
    $success = $true
}
catch {
    $failureMessage = $_.Exception.Message
}
finally {
    foreach ($job in $matrixJobs) {
        Stop-Job -Job $job -ErrorAction SilentlyContinue
        Remove-Job -Job $job -Force -ErrorAction SilentlyContinue
    }

    Restore-RegressionResults
    if ($pushedLocation) {
        Pop-Location
    }
    $stopwatch.Stop()
}

if ($success) {
    Write-Output ("MNK_PREPLAY_GATE=PASSED elapsed_seconds={0:f1}" -f $stopwatch.Elapsed.TotalSeconds)
    exit 0
}

Write-Error ("MNK_PREPLAY_GATE=BLOCKED reason={0}" -f $failureMessage)
exit 1
