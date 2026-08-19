# Mullet Hop Kiosk Controller

The controller is installed on one Windows 10 or Windows 11 office computer on
the same private network as the waiver kiosks. It provides:

* Online/offline, version, open/closed, last-seen, and IP status per kiosk.
* Open Selected and Close Selected controls.
* Open All and Close All controls.
* A non-installing update check per selected kiosk.
* An install-update command per selected kiosk.
* A central scheduled-advertisement catalog with automatic kiosk synchronization.
* A lower-right Controller Program section with update, ad-management, restart,
  and close controls.
* Queued commands: if a kiosk is temporarily offline, the newest command waits
  until that kiosk reconnects.


INSTALL THE CONTROLLER PC
-------------------------

Download the latest Windows package from:

https://github.com/m404ntfd/mullethopdiberville/releases/latest

1. Extract the complete Mullet-Hop-Kiosk-Controller zip file.
2. Right-click Install-Kiosk-Controller.cmd and choose Run as administrator.
3. Allow the Windows prompt. The installer adds the required private-network
   firewall rule and starts the controller.
4. Leave the controller installed on a computer whose local IP address will not
   change. A DHCP reservation in the router is recommended.

The controller starts automatically when that Windows account signs in. It also
checks GitHub for controller updates whenever it opens. When an update is found,
the controller downloads it and offers Restart and Install Now or Install Later.
Choosing Install Later displays a red "! Update Ready to Install" notice until
the controller is restarted. Use Check Updates in the lower-right Controller
Program section to check manually.

Version 1.1.0 must be installed once with the new package to establish the
automatic updater. After that one-time installation, later controller versions
can update themselves. Existing pairing information and kiosk history are kept.

The controller does not need internet access for open/close commands. It needs
internet access only to update itself, and each kiosk still needs internet
access to check GitHub and install a kiosk update.


PAIR EACH WAIVER KIOSK ONCE
---------------------------

The kiosk must be version 2.1.0 or newer.

1. On the controller, click Copy Address and Copy Key. Select View Key if staff
   need to read the complete key on screen.
2. On the kiosk, press Ctrl + Alt + Shift + F12 and enter the staff password.
3. Open Staff Settings, then Remote Control Setup.
4. Turn on remote management.
5. Give the kiosk a clear, unique name such as Front Kiosk or Party Desk Kiosk.
6. Enter the controller address and pairing key.
7. Select Test Controller Connection, then Save Settings.
8. Within five seconds the station appears in the controller list.

Repeat those steps for each kiosk. The pairing key is a staff credential. Do not
post it publicly. Check-ins and commands are authenticated with an HMAC-SHA256
signature and a short-lived timestamp.


USE THE DASHBOARD
-----------------

Select one kiosk in the list, then choose:

* Open Selected: removes the staff-controlled closed screen and resets to a
  fresh waiver.
* Close Selected: displays the normal Waiver Station Closed screen.
* Check Kiosk Update: checks the public GitHub release without installing it.
* Install Kiosk Update: checks, downloads, installs, and restarts that kiosk when a
  newer version exists.

Double-clicking a kiosk row also toggles that kiosk between open and closed.

Open All and Close All apply to every known station. Close All asks for
confirmation. Offline stations retain the latest queued command and carry it out
when they reconnect.

The lower-right Controller Program section checks for controller updates and
provides Manage Ads, Restart, and Close buttons. If a downloaded update is waiting, Restart
offers to install it. Closing the controller does not change a kiosk's current
open/closed state, but new remote commands are unavailable until the controller
starts again.


MANAGE AND SYNC ADVERTISEMENTS
------------------------------

Select Manage Ads in the lower-right Controller Program section. The manager
uses the same JPG, one-time date range, weekly schedule, and enable/disable
options as the kiosk. Every saved change publishes a new catalog. Connected
kiosks running version 2.2.0 or newer automatically download it on their next
check-in. Sync All Kiosks republishes the current catalog when staff want to
force a fresh synchronization without changing an ad.

The Kiosk Sync Status box and progress bar show how many known kiosks have
reported the current catalog revision. Each kiosk also shows its last ad-sync
time in the dashboard. On an individual kiosk, Staff Settings → Manage
Advertisements includes transfer progress, the last successful sync time, and a
Sync Ads Now button. Advertisement transfers stay on the private local network
and use the same signed pairing-key connection as other controller requests.

Each kiosk keeps the complete last successful catalog locally. If the manager
PC is offline, unavailable, or moved to a different address, the kiosk continues
using the saved JPG files and schedules until a later sync succeeds.


NETWORK NOTES
-------------

* Controller port: TCP 47832.
* Windows network profile on the controller should be Private.
* Kiosks make outbound connections to the controller; no inbound firewall rule
  is required on kiosk computers.
* If the controller PC's IP address changes, copy its new address to the Remote
  Control Setup page on each kiosk.
* Open/close commands normally appear on a kiosk within five seconds.


REMOVE THE CONTROLLER
---------------------

Run Uninstall-Kiosk-Controller.ps1 with PowerShell. It removes the application,
shortcuts, firewall rule, URL reservation, pairing key, and saved kiosk history.
