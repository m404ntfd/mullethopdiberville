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
Write-Host 'Mullet Hop Systems Controller Installer' -ForegroundColor Cyan
Write-Host '----------------------------------------' -ForegroundColor Cyan
Write-Host ''

$setupExe = Join-Path $PSScriptRoot 'MulletHop.KioskController-Setup.exe'
if (-not (Test-Path -LiteralPath $setupExe)) {
    throw 'MulletHop.KioskController-Setup.exe must be in the same folder as this installer.'
}

$installFolder = Join-Path $env:LOCALAPPDATA 'MulletHop.KioskController'
$installedExe = Join-Path $installFolder 'MulletHopKioskController.exe'
$legacyInstallFolder = Join-Path $env:ProgramFiles 'Mullet Hop Kiosk Controller'
$urlPrefix = 'http://+:47832/mullethop/'
$firewallName = 'Mullet Hop Systems Controller (TCP 47832)'
$legacyFirewallName = 'Mullet Hop Kiosk Controller (TCP 47832)'
$startupTaskName = 'Mullet Hop Systems Controller'
$currentUser = [Security.Principal.WindowsIdentity]::GetCurrent().Name

Get-Process -Name 'MulletHopKioskController' -ErrorAction SilentlyContinue |
    Stop-Process -Force

$setup = Start-Process `
    -FilePath $setupExe `
    -ArgumentList @('--silent', '--installto', ('"{0}"' -f $installFolder)) `
    -Wait `
    -PassThru
if ($setup.ExitCode -ne 0 -or -not (Test-Path -LiteralPath $installedExe -PathType Leaf)) {
    throw 'The controller application could not be installed.'
}

if (Test-Path -LiteralPath $legacyInstallFolder) {
    Remove-Item -LiteralPath $legacyInstallFolder -Recurse -Force
}

& netsh.exe http delete urlacl url=$urlPrefix 2>$null | Out-Null
& netsh.exe http add urlacl url=$urlPrefix user=$currentUser | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Windows could not reserve the controller network address.'
}

@($firewallName, $legacyFirewallName) | ForEach-Object {
    Get-NetFirewallRule -DisplayName $_ -ErrorAction SilentlyContinue |
        Remove-NetFirewallRule
}
New-NetFirewallRule `
    -DisplayName $firewallName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort 47832 `
    -Profile Private | Out-Null

$startupFolder = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup'
@(
    (Join-Path $startupFolder 'Mullet Hop Kiosk Controller.lnk'),
    (Join-Path $startupFolder 'Mullet Hop Systems Controller.lnk')
) | ForEach-Object {
    if (Test-Path -LiteralPath $_) {
        Remove-Item -LiteralPath $_ -Force
    }
}

$startupAction = New-ScheduledTaskAction `
    -Execute $installedExe `
    -Argument '--windows-startup' `
    -WorkingDirectory $installFolder
$startupTrigger = New-ScheduledTaskTrigger -AtLogOn -User $currentUser
$startupPrincipal = New-ScheduledTaskPrincipal `
    -UserId $currentUser `
    -LogonType Interactive `
    -RunLevel Highest
$startupSettings = New-ScheduledTaskSettingsSet `
    -StartWhenAvailable `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero) `
    -MultipleInstances IgnoreNew
$startupTask = New-ScheduledTask `
    -Action $startupAction `
    -Trigger $startupTrigger `
    -Principal $startupPrincipal `
    -Settings $startupSettings `
    -Description 'Starts the Mullet Hop Systems Controller elevated and hidden in the system tray.'
Register-ScheduledTask `
    -TaskName $startupTaskName `
    -InputObject $startupTask `
    -Force | Out-Null

Write-Host 'Installation complete.' -ForegroundColor Green
Write-Host 'The controller will start elevated in the system tray when this Windows account signs in.' -ForegroundColor Green
Write-Host 'Mullet Hop POS starts afterward only when automatic startup is enabled in POS Settings.' -ForegroundColor Green
Write-Host 'Controller updates will be checked and installed automatically when it opens.' -ForegroundColor Green
Write-Host 'Windows Firewall allows kiosk check-ins on private networks only.' -ForegroundColor Green
Write-Host ''

Start-ScheduledTask -TaskName $startupTaskName
