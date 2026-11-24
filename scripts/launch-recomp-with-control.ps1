<#
  Switch Achievements - Phase 0 helper.

  Enables Longshot's control bridge by setting HOS_CONTROL for this PowerShell
  session and its children, then launches RecompLauncher so you can start the
  game the normal way.

  - No persistent change is made to your system or to Longshot / RecompLauncher.
  - The bridge is read-only by default (HOS_CONTROL_MEM is never set).
  - If RecompLauncher is already running, close it first so the new instance
    (which inherits HOS_CONTROL) is the one that starts the game.
#>
param(
    [string]$PipeName = 'longshot_control'
)

$ErrorActionPreference = 'Stop'
$env:HOS_CONTROL = $PipeName

$candidates = @(
    "$env:LOCALAPPDATA\RecompLauncher\recomplauncher.exe",
    "$env:LOCALAPPDATA\com.recomplauncher.app\recomplauncher.exe"
)

$exe = $null
foreach ($c in $candidates) {
    if (Test-Path -LiteralPath $c) { $exe = $c; break }
}

if (-not $exe) {
    $lnk = "$env:APPDATA\Microsoft\Windows\Start Menu\Programs\RecompLauncher.lnk"
    if (Test-Path -LiteralPath $lnk) {
        $shell = New-Object -ComObject WScript.Shell
        $target = $shell.CreateShortcut($lnk).TargetPath
        if ($target -and (Test-Path -LiteralPath $target)) { $exe = $target }
    }
}

if (-not $exe) {
    Write-Error 'Could not locate RecompLauncher. Install it, or add its path to this script.'
    exit 1
}

Write-Host "Control bridge enabled: HOS_CONTROL=$PipeName"
Write-Host "Named pipe: \\.\pipe\$PipeName"
Write-Host "Launching: $exe"
Write-Host '-> In RecompLauncher, click Play to start Longshot, then run:'
Write-Host '   dotnet run --project src/SwitchAchievements.Longshot.Poc -- watch'
Start-Process -FilePath $exe