$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host 'Mullet Hop Kiosk Status Viewer - Uninstall' -ForegroundColor Cyan
Write-Host ''
$answer = Read-Host 'Remove the Kiosk Status Viewer and its saved kiosk links? (Y/N)'
if ($answer -notmatch '^[Yy]') {
    Write-Host 'No changes were made.'
    exit 0
}

Get-Process -Name 'MulletHopPosController' -ErrorAction SilentlyContinue |
    Stop-Process -Force

$startMenuFolder = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Mullet Hop'
$shortcutNames = @('Mullet Hop Kiosk Status Viewer.lnk', 'Mullet Hop POS Controller.lnk')
foreach ($shortcutName in $shortcutNames) {
    $startMenuShortcut = Join-Path $startMenuFolder $shortcutName
    if (Test-Path -LiteralPath $startMenuShortcut) {
        Remove-Item -LiteralPath $startMenuShortcut -Force
    }
}

$installFolder = Join-Path $env:LOCALAPPDATA 'MulletHop.POSController'
$updateExe = Join-Path $installFolder 'Update.exe'
if (Test-Path -LiteralPath $updateExe -PathType Leaf) {
    $uninstall = Start-Process -FilePath $updateExe -ArgumentList @('uninstall', '--silent') -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        Write-Warning 'The Kiosk Status Viewer updater reported an uninstall error.'
    }
}

$dataFolder = Join-Path $env:LOCALAPPDATA 'MulletHopPosController'
if (Test-Path -LiteralPath $dataFolder) {
    Remove-Item -LiteralPath $dataFolder -Recurse -Force
}

Write-Host 'The Mullet Hop Kiosk Status Viewer was removed.' -ForegroundColor Green
