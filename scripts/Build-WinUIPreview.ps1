[CmdletBinding()]
param(
    [switch]$Start
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& {
    $ErrorActionPreference = 'Stop'

    function Assert-Native([string]$Stage) {
        if ($LASTEXITCODE -ne 0) {
            throw "FAIL: $Stage (native exit code $LASTEXITCODE)"
        }
        Write-Host "PASS: $Stage" -ForegroundColor Green
    }

    $repo = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    $project = Join-Path $repo 'src\FluentMediaGallery.App\FluentMediaGallery.App.csproj'
    $exe = Join-Path $repo 'src\FluentMediaGallery.App\bin\x64\Release\net10.0-windows10.0.19041.0\win-x64\FluentMediaGallery.App.exe'

    if (-not (Test-Path -LiteralPath $project -PathType Leaf)) {
        throw "Project not found: $project"
    }

    $branch = & git -C $repo branch --show-current
    Assert-Native 'Git branch'
    if (($branch | Out-String).Trim() -ne 'develop') {
        throw "Expected develop branch; actual: $branch"
    }

    $changes = @(& git -C $repo status --porcelain)
    Assert-Native 'Git status'
    if ($changes.Count -gt 0) {
        throw 'Local Git changes exist. Aborting before pull/build.'
    }

    & git -C $repo pull --ff-only origin develop
    Assert-Native 'Fast-forward develop'

    # Fail before MSBuild retries. Never terminate an app that might be indexing or writing metadata.
    $running = @(Get-CimInstance Win32_Process -Filter "Name='FluentMediaGallery.App.exe'" -ErrorAction Stop)
    if ($running.Count -gt 0) {
        $list = ($running | ForEach-Object {
            "PID=$($_.ProcessId), EXE=$($_.ExecutablePath)"
        }) -join [Environment]::NewLine
        throw ("Close FluentMediaGallery normally and rerun; existing processes:" +
            [Environment]::NewLine + $list)
    }

    & dotnet build $project -c Release -r win-x64 -p:Platform=x64
    Assert-Native 'WinUI Release x64 build'

    if (-not (Test-Path -LiteralPath $exe -PathType Leaf)) {
        throw "EXE not found after build: $exe"
    }

    if ($Start) {
        $proc = Start-Process -FilePath $exe -WorkingDirectory (Split-Path -Parent $exe) -PassThru
        Start-Sleep -Seconds 2
        $proc.Refresh()
        if ($proc.HasExited) {
            throw "FluentMediaGallery exited immediately (code $($proc.ExitCode))."
        }
        Write-Host "PASS: Preview started with PID $($proc.Id); verify the window and thumbnails." -ForegroundColor Green
        Write-Host "IMPORTANT: Close the app before your next build." -ForegroundColor Yellow
    } else {
        Write-Host 'PASS: Build finished; preview was not started.' -ForegroundColor Green
    }
}
