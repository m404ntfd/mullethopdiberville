# Mullet Hop Kiosk Controller

The controller is installed on a Windows 10 or Windows 11 office computer on
the same private network as the waiver kiosks. Additional local controller
computers discover each other, and one can be designated as the master. It provides:

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
* Optional secure Cloudflare synchronization for a controller installed away
  from the kiosk network.
* A signed status/control connection for the separate front-desk POS Controller.
* Local-network kiosk discovery with a required approval prompt on the kiosk.
* A one-code manual pairing fallback when automatic kiosk discovery is blocked.
* A persistent red/green master-controller indicator with single-master checks.


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


REMOTE ACCESS (OPTIONAL)
------------------------

Select **Remote Access** in the Controller Program section. The on-site
controller should enable secure cloud synchronization and leave **This is a
remote machine** unchecked. On the off-site controller, paste the setup code,
check **This is a remote machine**, then select **Save and Restart**.

The on-site controller remains the only computer that talks directly to waiver
kiosks. Both controllers make outbound HTTPS connections to the Cloudflare
relay, so router port forwarding is not required. Remote commands wait in the
relay until the on-site controller receives them. Kiosk status and scheduled
advertisements synchronize in both directions, and local kiosk operation
continues when the internet or cloud relay is temporarily unavailable.

The installer ZIP includes `Cloudflare-Setup/Setup-Cloudflare-Relay.cmd` for the
one-time Worker, D1, R2, and access-key setup. Treat the access key and copied
setup code as staff credentials.


DISCOVER AND ADD EACH WAIVER KIOSK ONCE
---------------------------------------

Install the current Waiver Kiosk app and Kiosk Controller app before using
network discovery.

1. Make sure the controller computer and waiver kiosk are on the same private
   network, then leave the Waiver Kiosk app open.
2. On the kiosk, press **Ctrl + Alt + M**, open Staff Settings, and select
   **Remote Control Options**.
3. Check **Enable remote control and network discovery**, enter a unique kiosk
   name, and save. No controller address or pairing key is needed.
4. On the controller, select **Discover Kiosks**. A fresh 15-second network scan
   starts automatically. Select **Scan Again** whenever you want to repeat it.
5. Select the named kiosk from the fresh scan results and choose **Request Add**.
6. At the waiver kiosk, verify the controller computer and address shown in the
   prompt, then select **Yes** within two minutes.
7. The controller reports **Added and Saved** after the kiosk completes its
   first authenticated check-in.

Repeat those steps for each kiosk. The discovery exchange encrypts the pairing
key, and the kiosk saves the approved connection before authenticated remote
check-ins begin. The controller saves the kiosk in its managed-device history. A
linked POS Controller then adds it to the next open Kiosk 1–4 position and saves
that assignment automatically.

The controller address and pairing key are exchanged automatically only after
approval on the kiosk. Check-ins and commands remain authenticated with an
HMAC-SHA256 signature and a short-lived timestamp. If the controller's local IP
address changes, an enabled kiosk verifies the controller with its saved key and
updates the saved address automatically.

If a kiosk does not appear in the discovery scan:

1. Select **Add Kiosk Manually** in Controller Connection Information and copy
   the generated setup code.
2. At the kiosk, open **Remote Control Options**. This page now displays the
   active connection, adapter, IPv4 address, subnet mask, default gateway, and
   stable Device ID.
3. Paste the code under **Manual Connection Fallback** and select **Connect and
   Save**. The kiosk tests the signed controller connection before saving it.

The setup code contains the selected controller address and secure pairing key,
so staff do not enter either value separately. Treat the code like the pairing
key. The controller saves the kiosk by its persistent Device ID. Its last IP
address is refreshed on every check-in, so a new kiosk address assigned by DHCP
does not create a duplicate and does not break the saved connection. A MAC
address is not used because computers can have multiple adapters and randomized
Wi-Fi MAC addresses.


MASTER CONTROLLER ROLE
----------------------

The Controller Program section has dark red and green indicator lenses and a
**Make This Master** button. Green means this controller is the master; red means
it is not. Local controller applications scan the private network when they
launch, recheck known controllers every few seconds, and show the detected
master computer by name.

Selecting **Make This Master** requires confirmation and performs a fresh
network scan. The change is refused while another reachable controller is
already master. If two previously isolated controllers later meet while both
claim the role, they use the saved master time and stable controller ID to
resolve the conflict automatically, leaving one master. Off-site Remote Mode
controllers cannot be assigned as the local master.


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

The lower-right Controller Program section includes the controller's own
Auto/Light/Dark appearance selector, master-controller indicator and toggle,
plus Check Updates, Manage Ads, Business Hours, Remote Access, Restart, and Close
buttons. If a downloaded update is
waiting, Restart offers to install it. Closing the controller does not change a
kiosk's current open/closed state, but new remote commands are unavailable until
the controller starts again.


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


MANAGE HOURS AND KIOSK APPEARANCE
---------------------------------

Select Business Hours in the Controller Program section. The Business Hours tab
sets each day's opening and closing time, the Business Closed screen duration,
and the pre-opening screensaver time. The Kiosk Appearance tab selects Auto,
Light, or Dark for the kiosks and can schedule selected days and a time to switch
Light kiosks to Dark.

A scheduled Dark override ends at the next configured business opening. Auto
then follows the kiosk's Windows setting; if Windows is still using Dark mode,
the kiosk remains dark. Save & Publish stores one combined Hours and Appearance
profile. Sync Selected Kiosk and Sync All Kiosks send that profile through the
same authenticated connection used for other controller commands.


NETWORK NOTES
-------------

* Controller port: TCP 47832.
* Windows network profile on the controller should be Private.
* Kiosks make outbound connections to the controller; no inbound firewall rule
  is required on kiosk computers.
* If the controller PC's IP address changes, enabled kiosks discover the new
  address and securely reconnect using their saved pairing key.
* Open/close commands normally appear on a kiosk within five seconds.

Kiosk Controller version 1.10.0 adds manual setup codes, controller peer
discovery, and the single-master indicator and toggle. Waiver Kiosk version 2.9.0
adds current IPv4, subnet, gateway, connection, and stable Device ID details plus
the manual connection fallback. Automatic approval-based discovery remains
available. Controller version 1.7.0 adds the separate POS Controller connection.
Waiver Kiosk version 2.6.0 adds live open-to-guests and error-state reporting plus
the reset-to-start command.


REMOVE THE CONTROLLER
---------------------

Run Uninstall-Kiosk-Controller.ps1 with PowerShell. It removes the application,
shortcuts, firewall rule, URL reservation, pairing key, and saved kiosk history.
