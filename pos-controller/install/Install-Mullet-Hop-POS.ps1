$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host 'Mullet Hop POS Installer' -ForegroundColor Cyan
Write-Host '----------------------------------------' -ForegroundColor Cyan
Write-Host ''

$setupExe = Join-Path $PSScriptRoot 'MulletHop.POS-Setup.exe'
if (-not (Test-Path -LiteralPath $setupExe -PathType Leaf)) {
    throw 'MulletHop.POS-Setup.exe must be in the same folder as this installer.'
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
    throw 'Mullet Hop POS application could not be installed.'
}

$shell = New-Object -ComObject WScript.Shell
$startMenuFolder = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Mullet Hop'
New-Item -ItemType Directory -Path $startMenuFolder -Force | Out-Null
$legacyShortcuts = @('Mullet Hop Kiosk Status Viewer.lnk', 'Mullet Hop POS Controller.lnk')
foreach ($legacyShortcutName in $legacyShortcuts) {
    $legacyShortcut = Join-Path $startMenuFolder $legacyShortcutName
    if (Test-Path -LiteralPath $legacyShortcut) {
        Remove-Item -LiteralPath $legacyShortcut -Force
    }
}
$shortcut = $shell.CreateShortcut((Join-Path $startMenuFolder 'Mullet Hop POS.lnk'))
$shortcut.TargetPath = $installedExe
$shortcut.WorkingDirectory = $installFolder
$shortcut.Description = 'Status and front-desk controls for Mullet Hop waiver kiosks'
$shortcut.Save()

Write-Host 'Installation complete.' -ForegroundColor Green
Write-Host 'Mullet Hop POS has been added to the Start menu.' -ForegroundColor Green
Write-Host 'The first launch will ask you to create the Staff Menu passcode.' -ForegroundColor Green
Write-Host ''

Start-Process -FilePath $installedExe -WorkingDirectory $installFolder
