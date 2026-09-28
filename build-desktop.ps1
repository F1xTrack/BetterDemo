[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string[]]$Configuration = @('Debug', 'Release')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$solution = Join-Path $PSScriptRoot 'BetterDemo.sln'
if (-not (Test-Path -LiteralPath $solution)) {
    throw "Solution file not found: $solution"
}

$applicationOutputRoot = (Join-Path $PSScriptRoot 'src\BetterDemo.App\bin\x64').TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
$runningEditors = @(
    foreach ($process in Get-Process -Name 'BetterDemo.App' -ErrorAction SilentlyContinue) {
        try {
            if ($process.Path -and $process.Path.StartsWith($applicationOutputRoot, [StringComparison]::OrdinalIgnoreCase)) {
                $process
            }
        }
        catch {
        }
    }
)
if ($runningEditors.Count -gt 0) {
    $processIds = ($runningEditors.Id -join ', ')
    throw "Close the running BetterDemo Editor process(es) with PID(s) $processIds before building; the unpackaged WinUI app can lock output assemblies."
}

$programFilesX86 = [Environment]::GetFolderPath([Environment+SpecialFolder]::ProgramFilesX86)
$vswhere = Join-Path $programFilesX86 'Microsoft Visual Studio\Installer\vswhere.exe'
if (-not (Test-Path -LiteralPath $vswhere)) {
    throw "Visual Studio Locator was not found: $vswhere"
}

$instancesJson = & $vswhere -all -products '*' -format json
if ($LASTEXITCODE -ne 0) {
    throw "Visual Studio Locator failed with exit code $LASTEXITCODE."
}

$requiredPriTask = 'MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\Microsoft.Build.Packaging.Pri.Tasks.dll'
$instances = @($instancesJson | ConvertFrom-Json)
$buildInstances = @(
    $instances |
        Where-Object {
            $_.installationPath -and
            (Test-Path -LiteralPath (Join-Path $_.installationPath $requiredPriTask))
        } |
        Sort-Object { [version]$_.installationVersion } -Descending
)

if ($buildInstances.Count -eq 0) {
    throw "No Visual Studio installation with WinUI Appx build tasks was found. Install the Universal Windows Platform build tools or use the documented AppxMSBuildToolsPath override."
}

$installationPath = $buildInstances[0].installationPath
$msbuild = Join-Path $installationPath 'MSBuild\Current\Bin\amd64\MSBuild.exe'
if (-not (Test-Path -LiteralPath $msbuild)) {
    throw "MSBuild.exe was not found in the selected Visual Studio installation: $msbuild"
}

Write-Host "Using Visual Studio MSBuild: $msbuild"
foreach ($configurationName in $Configuration) {
    Write-Host "Building BetterDemo.sln ($configurationName, x64)"
    & $msbuild $solution "-p:Configuration=$configurationName" '-p:Platform=x64' '-restore' '-v:minimal'
    $buildExitCode = $LASTEXITCODE
    if ($buildExitCode -ne 0) {
        exit $buildExitCode
    }
}

Write-Host 'Desktop solution build completed successfully.'
