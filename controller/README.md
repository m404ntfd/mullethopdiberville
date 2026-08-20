# Mullet Hop Kiosk Controller

The controller is installed on a Windows 10 or Windows 11 office computer on
the same private network as the waiver kiosks. Additional local controller
computers discover each other, and one can be designated as the master. It provides:

* Online/offline, version, open/closed, assistance, last-seen, and IP status per kiosk.
* Open Selected and Close Selected controls.
* Open All and Close All controls.
* A non-installing update check per selected kiosk.
* An install-update command per selected kiosk.
* A central scheduled-advertisement catalog with automatic kiosk synchronization.
* A lower-right Controller Program section with update, ad-management, software
  download, stored-connection recovery, restart, and close controls.
* Queued commands: if a kiosk is temporarily offline, the newest command waits
  until that kiosk reconnects.
* Optional secure Cloudflare synchronization for a controller installed away
  from the kiosk network.
* A signed status/control connection for the separate front-desk Mullet Hop POS.
* Local-network kiosk discovery with a required approval prompt on the kiosk.
* Code-free manual pairing by kiosk IPv4 address, plus a self-contained setup-code fallback.
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
   **Remote Control**.
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
linked Mullet Hop POS then adds it to the next open Kiosk 1–4 position and saves
that assignment automatically.

The controller address and pairing key are exchanged automatically only after
approval on the kiosk. Check-ins and commands remain authenticated with an
HMAC-SHA256 signature and a short-lived timestamp. If the controller's local IP
address changes, an enabled kiosk verifies the controller with its saved key and
updates the saved address automatically.

If a kiosk does not appear in the discovery scan:

1. At the kiosk, open **Remote Control** and note the displayed IPv4
   address.
2. On the controller, select **Add Kiosk Manually**, enter that IPv4 address,
   and select **Send Secure Request**.
3. The kiosk contacts the controller through its existing outbound discovery
   connection. Approve the encrypted pairing request on the kiosk within two
   minutes.
4. The first authenticated check-in permanently saves the kiosk by its stable
   Device ID. No code is required for the IP-address workflow.

The generated setup code remains available in the same dialog as a last-resort
fallback. It is intentionally longer than 8–10 characters because it is
self-contained and carries the controller address plus the full 256-bit secure
pairing key. Treat it like the pairing key. The kiosk's last IP address is
refreshed on every check-in, so a new address assigned by DHCP does not create a
duplicate or break the saved connection. A MAC address is not used because
computers can have multiple adapters and randomized Wi-Fi MAC addresses.


MASTER CONTROLLER ROLE
----------------------

The Controller Program section has dark red and green indicator lenses and a
**Make This Master** button. Green means this controller is the master; red means
it is not. Local controller applications scan the private network when they
launch, recheck known controllers every few seconds, and show the detected
master computer by name.

When no live master is detected, **Pull Connections** changes to **Connect to
Master**. Enter the master PC's private IPv4 address or its full pairing key.
After the signed connection succeeds, this controller saves the master's stable
controller ID, computer name, verified address, and connection credentials in
its local `controller.json` file. The connection window also offers **Use Saved
Master** on later attempts.

The saved computer identity is independent of its last IP address. The
controller retries the last address and Windows computer name, then scans the
current private subnet for the same controller ID. If DHCP assigns the master a
different address, the saved record is updated automatically after the signed
connection is verified. Assistance acknowledgements, reset/open/close commands,
and kiosk update commands use this recovery path before reporting that the
master is unavailable.

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

When a guest selects Call for Assistance at a kiosk, that kiosk's Assistance
column flashes yellow with HELP. Mullet Hop POS can acknowledge the call;
the controller row then displays On the way. In Mullet Hop POS, the Answered
button stays gray and disabled while its status dot continues flashing yellow.
The call remains active until it is cleared at the kiosk.

An online kiosk that is showing its scheduled Business Closed video/page or the
business-hours blackout screen is reported with a blue Business Closed indicator
and row. Staff-closed, offline, and error states remain separate.
Mullet Hop POS asks staff to choose Staff Closure or Business Closure before it
queues a Close command, ensuring the kiosk reports the intended red or blue state.
Business Hours profiles include opening, Last Jump Time Sold, and closing times
for each day, plus Show Closed Video, Blackout at closing time, and the existing
pre-opening screensaver setting.

Open All and Close All apply to every known station. Close All asks for
confirmation. Offline stations retain the latest queued command and carry it out
when they reconnect.

The lower-right Controller Program section includes the controller's own
Auto/Light/Dark appearance selector, master-controller indicator and toggle,
plus Check Updates, Manage Ads, Business Hours, Download Apps, Remote Access,
Restart Controller, Exit Program, and Pull Connections buttons. Download Apps opens a Software
Downloads window that finds the latest published Waiver Kiosk installer or Mullet
Hop POS package, asks where to save it, and shows progress. If a downloaded
update is waiting, Restart Controller offers to install it. Minimizing the window
or selecting X sends the controller to the Windows system tray while its network
service continues running. Single-click or double-click the fish-and-springs tray
icon, select Open Kiosk Controller from its tray menu, or select its notification
to restore the dashboard. Only the in-app Exit Program button ends the controller
service during normal use.

The master controller keeps a dedicated recovery catalog at
`%LOCALAPPDATA%\MulletHopKioskController\master-connections.json` whenever its
saved kiosk connections change. Select **Pull Connections** on the master to
reload that local catalog. On a non-master controller with a detected master,
the same button pulls its saved kiosk connections and saves the mirrored list on
the current PC. When no master is detected, it opens the manual/saved master
connection window described above.


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
sets each day's opening, Last Jump Time Sold, and closing time; whether to show
the Business Closed video at the cutoff; whether to black out at closing; and
the pre-opening screensaver time. The Business Closed video displays the next
opening day and time from this synced schedule. The Kiosk Appearance tab selects Auto,
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

Kiosk Controller version 1.12.2 adds manual master connection by private IPv4
address or pairing key, remembers the master by stable controller identity, and
reconnects after DHCP address changes. Version 1.12.1 adds a dedicated master
connection recovery catalog and the manual Pull Connections control. Version 1.12.0 shows the active master PC and network-wide POS
count beside the master lights. Non-master controller installations automatically
mirror the master's saved kiosk connections and relay kiosk and POS commands to
it. Three POS workstations can simultaneously use the same four kiosk assignments.
Version 1.11.1 adds the distinct business-hours-closed status used by Mullet Hop
POS. Version 1.11.0 fixes dashboard restoration from the tray
icon and adds guest-assistance status to every kiosk row. Version 1.10.2 adds
system-tray operation so minimizing or closing the dashboard does not stop remote
kiosk service. Version 1.10.1 enlarges
the Controller Program buttons and adds code-free manual pairing by kiosk IPv4
address. Waiver Kiosk version 2.10.0 uses
the existing outbound discovery and encrypted approval exchange for the IP
workflow. The long self-contained setup code remains available as a fallback.
Controller version 1.7.0 adds the separate Mullet Hop POS connection. Waiver
Kiosk version 2.6.0 adds live open-to-guests and error-state reporting plus the
reset-to-start command.


REMOVE THE CONTROLLER
---------------------

Run Uninstall-Kiosk-Controller.ps1 with PowerShell. It removes the application,
shortcuts, firewall rule, URL reservation, pairing key, and saved kiosk history.
