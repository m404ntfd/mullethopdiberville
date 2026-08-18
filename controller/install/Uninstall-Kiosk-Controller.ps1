$ErrorActionPreference = 'Stop'

function Test-Administrator {
    $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
    $principal = New-Object Security.Principal.WindowsPrincipal($identity)
    return $principal.IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

if (-not (Test-Administrator)) {
    $arguments = @(
        '-NoProfile',
        '-ExecutionPolicy', 'Bypass',
        '-File', ('"{0}"' -f $PSCommandPath)
    )
    $elevated = Start-Process -FilePath 'powershell.exe' -ArgumentList $arguments -Verb RunAs -Wait -PassThru
    exit $elevated.ExitCode
}

Write-Host ''
Write-Host 'Mullet Hop Kiosk Controller - Uninstall' -ForegroundColor Cyan
Write-Host ''
$answer = Read-Host 'Remove the controller, shortcuts, pairing information, and kiosk history? (Y/N)'
if ($answer -notmatch '^[Yy]') {
    Write-Host 'No changes were made.'
    exit 0
}

Get-Process -Name 'MulletHopKioskController' -ErrorAction SilentlyContinue |
    Stop-Process -Force

$firewallName = 'Mullet Hop Kiosk Controller (TCP 47832)'
Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
& netsh.exe http delete urlacl url='http://+:47832/mullethop/' 2>$null | Out-Null

$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Mullet Hop Kiosk Controller.lnk'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Mullet Hop Kiosk Controller.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Mullet Hop Kiosk Controller.lnk')
)
foreach ($shortcutPath in $shortcutPaths) {
    if (Test-Path -LiteralPath $shortcutPath) {
        Remove-Item -LiteralPath $shortcutPath -Force
    }
}

$installFolder = Join-Path $env:LOCALAPPDATA 'MulletHop.KioskController'
$updateExe = Join-Path $installFolder 'Update.exe'
if (Test-Path -LiteralPath $updateExe -PathType Leaf) {
    $uninstall = Start-Process `
        -FilePath $updateExe `
        -ArgumentList @('uninstall', '--silent') `
        -Wait `
        -PassThru
    if ($uninstall.ExitCode -ne 0) {
        Write-Warning 'The controller updater reported an uninstall error.'
    }
}

$legacyInstallFolder = Join-Path $env:ProgramFiles 'Mullet Hop Kiosk Controller'
if (Test-Path -LiteralPath $legacyInstallFolder) {
    Remove-Item -LiteralPath $legacyInstallFolder -Recurse -Force
}
$dataFolder = Join-Path $env:LOCALAPPDATA 'MulletHopKioskController'
if (Test-Path -LiteralPath $dataFolder) {
    Remove-Item -LiteralPath $dataFolder -Recurse -Force
}

Write-Host 'The Mullet Hop Kiosk Controller was removed.' -ForegroundColor Green
