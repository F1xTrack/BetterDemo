# BetterDemo Build and Run

Run these commands from `G:\_Projects\BetterDemo` in a PowerShell 7 or Visual Studio Developer PowerShell session. The repository targets .NET 8 (`net8.0` and `net8.0-windows10.0.19041.0`) and pins the installed .NET SDK `9.0.308` in `global.json`.

## Toolchain checks

```powershell
dotnet --version
dotnet --list-sdks
java -version

$vsBuildTools = 'G:\Visual Studio Build Tools'
$vsCommunity = 'G:\визуал студио коммунити'
$vsPackages = 'G:\визуал студио пакагес'
Test-Path $vsBuildTools
Test-Path $vsCommunity
Test-Path $vsPackages
& "$vsBuildTools\MSBuild\Current\Bin\amd64\MSBuild.exe" -version
& "$vsCommunity\MSBuild\Current\Bin\amd64\MSBuild.exe" -version
```

The Visual Studio package directory is detected for provenance only; NuGet restore continues to use the configured NuGet sources. The Appx build tasks come from the Community MSBuild installation.

## Clean restore and x64 builds

Close BetterDemo Editor before cleaning or rebuilding its output; the running unpackaged app can lock its WinUI assemblies. The helper reports the process IDs if its own editor is still running.

```powershell
$cleanRoots = @('.\src', '.\tests', '.\android')
foreach ($cleanRoot in $cleanRoots) {
    Get-ChildItem -LiteralPath $cleanRoot -Directory -Recurse -Force |
        Where-Object Name -in @('bin', 'obj') |
        Remove-Item -Recurse -Force
}

.\build-desktop.ps1
```

For `dotnet build` without the helper, pass the Appx task directory explicitly:

```powershell
dotnet restore .\BetterDemo.sln -p:Platform=x64
$appxTasks = 'G:\визуал студио коммунити\MSBuild\Microsoft\VisualStudio\v17.0\AppxPackage\'
dotnet build .\BetterDemo.sln -c Debug -p:Platform=x64 --no-restore "-p:AppxMSBuildToolsPath=$appxTasks"
dotnet build .\BetterDemo.sln -c Release -p:Platform=x64 --no-restore "-p:AppxMSBuildToolsPath=$appxTasks"
```

The .NET SDK's own `v17.0\AppxPackage` directory may not contain the WinUI tasks, which makes a bare `dotnet build` fail with `MSB4062` for `ExpandPriContent`. The commands above point it at the installed Community tasks. You can also build through Community MSBuild directly; these full-solution commands were verified in both configurations:

```powershell
$vsCommunityMsBuild = Join-Path $vsCommunity 'MSBuild\Current\Bin\amd64\MSBuild.exe'
& $vsCommunityMsBuild .\BetterDemo.sln -p:Configuration=Debug -p:Platform=x64 -restore
& $vsCommunityMsBuild .\BetterDemo.sln -p:Configuration=Release -p:Platform=x64 -restore
```

## Tests

```powershell
dotnet test .\BetterDemo.sln -c Debug -p:Platform=x64 --no-build --no-restore
dotnet test .\BetterDemo.sln -c Release -p:Platform=x64 --no-build --no-restore
```

## Manual unpackaged launch

Build Debug first, then start the produced executable directly from PowerShell:

```powershell
$exe = '.\src\BetterDemo.App\bin\x64\Debug\net8.0-windows10.0.19041.0\BetterDemo.App.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw "Build the Debug configuration first: $exe" }
$process = Start-Process -FilePath $exe -PassThru
Get-Process -Id $process.Id | Select-Object Id, ProcessName, MainWindowTitle, HasExited
Stop-Process -Id $process.Id
Get-Process -Id $process.Id -ErrorAction SilentlyContinue
```

The expected process name is `BetterDemo.App` and the expected editor title is `BetterDemo Editor`. Use **Start preview** to create and show the separate `OutputWindow`; use **Stop preview** to release the capture and render resources. `WindowsPackageType=None` keeps this launch unpackaged.

## Android prerequisite detection and checks

The Android controller requires JDK 17, Android SDK Platform 35, and Android Build Tools 35.0.0. No emulator is required for Gradle `check`; run `assembleDebug` to create the installable debug APK. Pairing and a scene change against the actual WinUI editor passed on a phone on the same private LAN; see [qa-matrix.md](qa-matrix.md) for the remaining acceptance checks.

```powershell
$androidSdk = $env:ANDROID_HOME
if (-not $androidSdk) { $androidSdk = "$env:LOCALAPPDATA\Android\Sdk" }
$env:ANDROID_HOME = $androidSdk
Test-Path "$androidSdk\platforms\android-35"
Test-Path "$androidSdk\build-tools\35.0.0"
Get-Command adb -ErrorAction SilentlyContinue
java -version

Push-Location .\android
try {
    .\gradlew.bat --no-daemon tasks
    .\gradlew.bat --no-daemon check
}
finally {
    Pop-Location
}
```

The wrapper pins Gradle 8.9 and validates its distribution SHA-256 checksum. If JDK, SDK Platform 35, Build Tools 35.0.0, or the wrapper distribution is unavailable, the command is recorded as blocked rather than reported as a successful Android build.

## Reproducibility and cleanup

Do not commit `bin`, `obj`, `.gradle`, `local.properties`, emulator state, or signing material. After any interrupted command, terminate the process and repeat the clean restore before comparing build results.
