# Mullet Hop All Programs Installer

`MulletHop-All-Programs-Installer.exe` is the single entry point for installing
software published from the `mullethopdiberville` repository. It offers:

* Mullet Hop Waiver Kiosk
* Mullet Hop Systems Controller
* Mullet Hop POS

The user can install one application or any combination. The installer reads the
latest public GitHub release, downloads the matching setup packages, and runs
them sequentially. If matching packages are placed beside the installer, those
local files are used instead. The Systems Controller package continues to run
its administrator-approved firewall and URL-reservation setup.

The release workflow publishes the installer as a self-contained Windows x64
application named `MulletHop-All-Programs-Installer.exe`.
