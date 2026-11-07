[CmdletBinding()]
param(
    [switch]$Apply
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

& {
    $ErrorActionPreference = 'Stop'

    $repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
    if (-not (Test-Path -LiteralPath (Join-Path $repoRoot '.git'))) {
        throw 'Safety stop: script must run from an existing FluentMediaGallery checkout.'
    }

    $rootPrefix = $repoRoot.TrimEnd([char]'\', [char]'/') + [System.IO.Path]::DirectorySeparatorChar
    $targets = [System.Collections.Generic.List[string]]::new()

    foreach ($segment in @('src', 'tests')) {
        $scope = Join-Path $repoRoot $segment
        if (-not (Test-Path -LiteralPath $scope -PathType Container)) { continue }

        $projects = @(Get-ChildItem -LiteralPath $scope -Filter '*.csproj' -File -Recurse -ErrorAction Stop)
        foreach ($project in $projects) {
            $parent = $project.Directory
            if ($null -eq $parent) { continue }
            if (($parent.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }

            foreach ($name in @('bin', 'obj', 'TestResults')) {
                $candidate = Join-Path $parent.FullName $name
                if (-not (Test-Path -LiteralPath $candidate -PathType Container)) { continue }
                $item = Get-Item -LiteralPath $candidate -Force -ErrorAction Stop
                if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { continue }
                $absolute = [IO.Path]::GetFullPath($item.FullName)
                if (-not $absolute.StartsWith($rootPrefix, [StringComparison]::OrdinalIgnoreCase)) {
                    throw "Safety stop: path is outside checkout: $absolute"
                }
                if ($item.Name -notin @('bin', 'obj', 'TestResults')) {
                    throw "Safety stop: unexpected directory: $absolute"
                }
                if (-not $targets.Contains($absolute)) { $targets.Add($absolute) }
            }
        }
    }

    if ($targets.Count -eq 0) {
        Write-Host 'No disposable build outputs found.' -ForegroundColor Green
        return
    }

    Write-Host "Found $($targets.Count) disposable directories:" -ForegroundColor Cyan
    foreach ($path in $targets) { Write-Host "  $path" }

    if (-not $Apply) {
        Write-Host 'PREVIEW ONLY. No files removed. Use -Apply after reviewing these paths.' -ForegroundColor Yellow
        return
    }

    foreach ($path in $targets) {
        Remove-Item -LiteralPath $path -Recurse -Force -ErrorAction Stop
        Write-Host "Removed: $path" -ForegroundColor Green
    }

    Write-Host 'CLEANUP PASS' -ForegroundColor Green
}
