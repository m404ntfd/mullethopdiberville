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
Write-Host 'Mullet Hop Kiosk Controller Installer' -ForegroundColor Cyan
Write-Host '--------------------------------------' -ForegroundColor Cyan
Write-Host ''

$sourceExe = Join-Path $PSScriptRoot 'MulletHopKioskController.exe'
if (-not (Test-Path -LiteralPath $sourceExe)) {
    throw 'MulletHopKioskController.exe must be in the same folder as this installer.'
}

$installFolder = Join-Path $env:ProgramFiles 'Mullet Hop Kiosk Controller'
$installedExe = Join-Path $installFolder 'MulletHopKioskController.exe'
$urlPrefix = 'http://+:47832/mullethop/'
$firewallName = 'Mullet Hop Kiosk Controller (TCP 47832)'
$currentUser = "$env:USERDOMAIN\$env:USERNAME"

Get-Process -Name 'MulletHopKioskController' -ErrorAction SilentlyContinue |
    Stop-Process -Force

New-Item -ItemType Directory -Path $installFolder -Force | Out-Null
Copy-Item -LiteralPath $sourceExe -Destination $installedExe -Force

& netsh.exe http delete urlacl url=$urlPrefix 2>$null | Out-Null
& netsh.exe http add urlacl url=$urlPrefix user=$currentUser | Out-Null
if ($LASTEXITCODE -ne 0) {
    throw 'Windows could not reserve the controller network address.'
}

Get-NetFirewallRule -DisplayName $firewallName -ErrorAction SilentlyContinue |
    Remove-NetFirewallRule
New-NetFirewallRule `
    -DisplayName $firewallName `
    -Direction Inbound `
    -Action Allow `
    -Protocol TCP `
    -LocalPort 47832 `
    -Profile Private | Out-Null

$shell = New-Object -ComObject WScript.Shell
$shortcutPaths = @(
    (Join-Path ([Environment]::GetFolderPath('Desktop')) 'Mullet Hop Kiosk Controller.lnk'),
    (Join-Path $env:ProgramData 'Microsoft\Windows\Start Menu\Programs\Mullet Hop Kiosk Controller.lnk'),
    (Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\Startup\Mullet Hop Kiosk Controller.lnk')
)
foreach ($shortcutPath in $shortcutPaths) {
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = $installedExe
    $shortcut.WorkingDirectory = $installFolder
    $shortcut.Description = 'Manage Mullet Hop waiver kiosks on the local network'
    $shortcut.Save()
}

Write-Host 'Installation complete.' -ForegroundColor Green
Write-Host 'The controller will start automatically when this Windows account signs in.' -ForegroundColor Green
Write-Host 'Windows Firewall allows kiosk check-ins on private networks only.' -ForegroundColor Green
Write-Host ''

Start-Process -FilePath $installedExe -WorkingDirectory $installFolder
