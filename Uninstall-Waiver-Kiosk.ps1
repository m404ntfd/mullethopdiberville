$ErrorActionPreference = 'Stop'

Write-Host ''
Write-Host 'Mullet Hop Waiver Kiosk - Uninstall' -ForegroundColor Cyan
Write-Host ''

$answer = Read-Host 'Remove the kiosk application, shortcuts, settings, and local browsing data? (Y/N)'
if ($answer -notmatch '^[Yy]') {
    Write-Host 'No changes were made.'
    exit 0
}

$processes = Get-Process -Name 'MulletHopWaiverKiosk' -ErrorAction SilentlyContinue
if ($processes) {
    throw 'The kiosk is running. Exit it with Ctrl + Alt + M, then run this uninstaller again.'
}

$velopackRoot = Join-Path $env:LOCALAPPDATA 'MulletHop.WaiverKiosk'
$velopackUpdater = Join-Path $velopackRoot 'Update.exe'
$legacyInstallRoot = Join-Path $env:LOCALAPPDATA 'MulletHopWaiverKiosk'
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Mullet Hop Waiver Kiosk.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Mullet Hop Waiver Kiosk.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Mullet Hop Waiver Kiosk.lnk')
)

foreach ($shortcutPath in $shortcutPaths) {
    if (Test-Path $shortcutPath) {
        Remove-Item $shortcutPath -Force
    }
}

if (Test-Path -LiteralPath $velopackUpdater) {
    $uninstaller = Start-Process -FilePath $velopackUpdater -ArgumentList @('uninstall', '--silent') -Wait -PassThru
    if ($uninstaller.ExitCode -ne 0) {
        throw "The Velopack uninstaller ended with exit code $($uninstaller.ExitCode)."
    }
}

if (Test-Path -LiteralPath $legacyInstallRoot) {
    Remove-Item -LiteralPath $legacyInstallRoot -Recurse -Force
}

Write-Host 'The waiver kiosk was removed from this Windows account.' -ForegroundColor Green
