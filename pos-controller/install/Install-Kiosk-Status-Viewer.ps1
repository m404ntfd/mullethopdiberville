$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host 'Mullet Hop Kiosk Status Viewer Installer' -ForegroundColor Cyan
Write-Host '----------------------------------------' -ForegroundColor Cyan
Write-Host ''

$setupExe = Join-Path $PSScriptRoot 'MulletHop.KioskStatusViewer-Setup.exe'
if (-not (Test-Path -LiteralPath $setupExe -PathType Leaf)) {
    throw 'MulletHop.KioskStatusViewer-Setup.exe must be in the same folder as this installer.'
}

Get-Process -Name 'MulletHopPosController' -ErrorAction SilentlyContinue |
    Stop-Process -Force

$installFolder = Join-Path $env:LOCALAPPDATA 'MulletHop.POSController'
$installedExe = Join-Path $installFolder 'MulletHopPosController.exe'
$setup = Start-Process `
    -FilePath $setupExe `
    -ArgumentList @('--silent', '--installto', ('"{0}"' -f $installFolder)) `
    -Wait `
    -PassThru
if ($setup.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
    throw 'The Kiosk Status Viewer application could not be installed.'
}

$shell = New-Object -ComObject WScript.Shell
$startMenuFolder = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Mullet Hop'
New-Item -ItemType Directory -Path $startMenuFolder -Force | Out-Null
$legacyShortcut = Join-Path $startMenuFolder 'Mullet Hop POS Controller.lnk'
if (Test-Path -LiteralPath $legacyShortcut) {
    Remove-Item -LiteralPath $legacyShortcut -Force
}
$shortcut = $shell.CreateShortcut((Join-Path $startMenuFolder 'Mullet Hop Kiosk Status Viewer.lnk'))
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $installFolder
$shortcut.Description = 'Status and front-desk controls for Mullet Hop waiver kiosks'
$shortcut.Save()

Write-Host 'Installation complete.' -ForegroundColor Green
Write-Host 'The Kiosk Status Viewer has been added to the Start menu.' -ForegroundColor Green
Write-Host 'The first launch will ask you to create the Staff Menu passcode.' -ForegroundColor Green
Write-Host ''

Start-Process -FilePath $installedExe -WorkingDirectory $installFolder
