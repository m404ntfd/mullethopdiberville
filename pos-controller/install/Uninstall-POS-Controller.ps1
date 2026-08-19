$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host 'Mullet Hop POS Controller - Uninstall' -ForegroundColor Cyan
Write-Host ''
$answer = Read-Host 'Remove the POS Controller and its saved kiosk links? (Y/N)'
if ($answer -notmatch '^[Yy]') {
    Write-Host 'No changes were made.'
    exit 0
}

Get-Process -Name 'MulletHopPosController' -ErrorAction SilentlyContinue |
    Stop-Process -Force

$startMenuShortcut = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Mullet Hop\Mullet Hop POS Controller.lnk'
if (Test-Path -LiteralPath $startMenuShortcut) {
    Remove-Item -LiteralPath $startMenuShortcut -Force
}

$installFolder = Join-Path $env:LOCALAPPDATA 'MulletHop.POSController'
$updateExe = Join-Path $installFolder 'Update.exe'
if (Test-Path -LiteralPath $updateExe -PathType Leaf) {
    $uninstall = Start-Process -FilePath $updateExe -ArgumentList @('uninstall', '--silent') -Wait -PassThru
    if ($uninstall.ExitCode -ne 0) {
        Write-Warning 'The POS Controller updater reported an uninstall error.'
    }
}

$dataFolder = Join-Path $env:LOCALAPPDATA 'MulletHopPosController'
if (Test-Path -LiteralPath $dataFolder) {
    Remove-Item -LiteralPath $dataFolder -Recurse -Force
}

Write-Host 'The Mullet Hop POS Controller was removed.' -ForegroundColor Green
