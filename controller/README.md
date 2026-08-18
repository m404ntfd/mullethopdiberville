# Mullet Hop Kiosk Controller

The controller is installed on one Windows 10 or Windows 11 office computer on
the same private network as the waiver kiosks. It provides:

* Online/offline, version, open/closed, last-seen, and IP status per kiosk.
* Open Selected and Close Selected controls.
* Open All and Close All controls.
* A non-installing update check per selected kiosk.
* An install-update command per selected kiosk.
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

The controller starts automatically when that Windows account signs in. The
controller does not need internet access for open/close commands. Each kiosk
still needs internet access to check GitHub and install a kiosk update.


PAIR EACH WAIVER KIOSK ONCE
---------------------------

The kiosk must be version 2.1.0 or newer.

1. On the controller, click Copy Address and Copy Key.
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
* Check for Update: checks the public GitHub release without installing it.
* Install Update: checks, downloads, installs, and restarts that kiosk when a
  newer version exists.

Double-clicking a kiosk row also toggles that kiosk between open and closed.

Open All and Close All apply to every known station. Close All asks for
confirmation. Offline stations retain the latest queued command and carry it out
when they reconnect.


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
