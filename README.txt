MULLET HOP WAIVER KIOSK FOR WINDOWS
==================================

This package creates a full-screen Windows kiosk for:

https://mullet.lilypadpos.app/public/onlinewaiver/waiver.php?l=English

GitHub repository and update feed:

https://github.com/m404ntfd/mullethopdiberville

Latest Windows installer:

https://github.com/m404ntfd/mullethopdiberville/releases/latest


SOURCE FOLDER LAYOUT
--------------------

* src\ contains the full-screen Mullet Hop Waiver Kiosk application.
* controller\src\ contains the office Kiosk Controller application.
* controller\install\ contains the controller installer and uninstaller.
* controller\README.md contains controller setup and network instructions.
* pos-controller\src\ contains the full-screen front-desk Kiosk Status Viewer.
* pos-controller\install\ contains its installer and uninstaller.
* pos-controller\README.md contains viewer, Firefox, status, and linking instructions.
* assets\ and scripts\ contain kiosk branding and screensaver build files.
* MulletHopKioskSuite.sln opens all three Windows applications together in Visual
  Studio.

The three applications remain separate installers, settings, and update channels,
but they are built and released
together so their local-network commands remain compatible.


QUICK INSTALL
-------------

1. Open https://github.com/m404ntfd/mullethopdiberville/releases/latest.
2. Download MulletHop.WaiverKiosk-Setup.exe.
3. Exit an older copy of the kiosk with Ctrl + Alt + M.
4. Run the downloaded Setup file. This first Velopack installation is required
   once; later versions install automatically.
5. The kiosk opens after installation. Existing staff passwords, settings, and
   advertisement files are retained. A fresh computer asks you to create a
   4-8 digit numerical staff password.

The installer creates a desktop shortcut named "Mullet Hop Waiver Kiosk."
If the previous kiosk was configured to start with Windows, the new installation
keeps that preference and retargets the startup shortcut automatically.


AUTOMATIC UPDATES
-----------------

The Velopack-installed kiosk checks its public GitHub Releases feed whenever the
program starts. When a newer release is available, the kiosk downloads it,
installs it, and restarts automatically before opening for guests.

Staff can also press Ctrl + Alt + M, open Staff Settings, and select
"Check for Updates." Update failures are written to the normal kiosk log
and do not prevent the waiver from opening.

The first upgrade from the older script-built kiosk to version 2.0.0 cannot be
automatic. Run the Velopack Setup file once to establish the updater. All later
updates can be delivered remotely.

The GitHub repository containing the Releases feed must be public. Do not place a
GitHub password, personal access token, or other secret in the kiosk application.


REMOTE KIOSK CONTROLLER
-----------------------

Version 2.1.0 and newer can be paired with the optional Mullet Hop Kiosk
Controller installed on another Windows PC on the same private network. The
controller can show each kiosk's online status, version, open/closed screen, last
check-in, and IP address. Staff can open or close one kiosk, open or close all
kiosks, check one kiosk for an update without installing it, or tell one kiosk to
install an available update. The controller can also publish Business Hours and
Kiosk Appearance settings, including Auto/Light/Dark mode and a selected-day
Dark-mode schedule, to one kiosk or all kiosks. The lower-right Controller
Program section can check for controller updates, choose the single master
controller, restart the controller, or close it. Red and green lenses show the
saved master role, and local controllers discover each other when they launch.
The controller also checks for its own updates automatically whenever it opens.

Waiver Kiosk version 2.10.3 is the matching package release for the application
formerly named POS Controller version 1.3.0. Waiver Kiosk version 2.10.1 places
guest assistance in a compact highlighted card
with the other right-side waiver controls and moves that card stack near the top
so it does not cover waiver information. Version 2.10.0 adds the guest-assistance
request itself. A guest can call for help, which produces a flashing yellow alert
in the Kiosk Controller and in the kiosk's assigned Kiosk Status Viewer panel.
Selecting Acknowledge in the viewer acknowledges the call and changes the kiosk
message to tell the guest that assistance is coming. The kiosk continues flashing
until the guest or assisting staff member selects Clear Assistance Call.

Download the controller package from:

https://github.com/m404ntfd/mullethopdiberville/releases/latest

On each kiosk, press Ctrl + Alt + M, open Staff Settings, and select Remote
Control Options. Check Enable remote control and network discovery, give the
kiosk a unique name, and save. No controller address or pairing key is entered
for normal discovery. On the office PC, select Discover Kiosks. A fresh 15-second network
scan starts automatically; select Scan Again to repeat it. Select the named kiosk
from the fresh results and choose Request Add. The kiosk displays the controller
computer and address and requires someone at that kiosk to approve the request
within two minutes. The encrypted pairing exchange saves the connection
information automatically, and the first authenticated check-in saves the kiosk
in the controller. A linked Kiosk Status Viewer then adds the kiosk to its next open
Kiosk 1-4 position and saves that assignment automatically.

If discovery does not show the kiosk, select Add Kiosk Manually on the
controller and enter the IPv4 address shown in Remote Control Options on that
kiosk. Select Send Secure Request. The kiosk contacts the controller using its
normal outbound discovery connection, receives the encrypted key exchange, and
asks someone at the kiosk to approve the request. No code is required for this
IP-address workflow. After approval, the first authenticated check-in saves the
kiosk permanently by Device ID.

The existing setup code remains available as a last-resort fallback. It cannot
be reduced to 8–10 characters while remaining self-contained because it carries
the controller address and full 256-bit pairing key. Treat it like the pairing
key. Paste it under Manual Connection Fallback on the kiosk only when the
code-free IP workflow cannot communicate.

The saved kiosk identity is the persistent Device ID, not the IP or MAC address.
Every check-in updates the current IP shown by the controller. If DHCP later
assigns a different address to the kiosk computer, it keeps the same Device ID,
checks in from the new address, and remains the same saved kiosk. MAC matching is
not needed and would be less dependable on computers with multiple adapters or
randomized Wi-Fi MAC addresses.

In the Controller Program section, select Make This Master to designate the
primary local controller. A confirmation and fresh peer scan are required. If
another reachable master exists, the change is refused. When two controllers
that were temporarily isolated see each other again, they automatically resolve
the duplicate role so only one remains master. The green lens is lit on the
master; the red lens is lit on every non-master controller.

Controller version 1.1.0 or newer must be installed once with its Setup-based
package to establish automatic updates. When a controller update is downloaded,
staff can restart and install it immediately or choose Install Later. A red
"! Update Ready to Install" notice remains visible when installation is deferred.
Existing pairing information and kiosk history are retained.

Kiosks check in every five seconds. Commands are authenticated with the shared
pairing key and signed timestamps. A kiosk does not accept unsolicited inbound
connections, so only the controller PC needs the TCP 47832 private-network
firewall rule created by its installer. See README.md in the controller download
for complete installation and network instructions.

Kiosk Controller version 1.11.0 fixes dashboard restoration from the fish-and-
springs system-tray icon and adds guest-assistance status to each kiosk row.
Version 1.10.2 adds system-tray operation so minimizing or closing the dashboard
does not stop remote kiosk service. Version 1.10.1 enlarges
the Controller Program buttons and adds code-free manual pairing by kiosk IPv4
address. Waiver Kiosk version 2.10.0 uses
the existing outbound discovery and encrypted approval exchange for this release.
The long self-contained setup code remains available only as a fallback.


FRONT-DESK KIOSK STATUS VIEWER
------------------------------

The Mullet Hop Kiosk Status Viewer is the third Windows application. Version
1.4.0 expands the former POS Controller into a full-screen front-desk shell. It
uses Mozilla Firefox installed on the computer to load:

https://mullet.lilypadpos.app/public/Login.php

LilyPad POS fills the main area. A docked right sidebar displays Kiosk 1 through
Kiosk 4. The button at the top collapses the sidebar to one status dot and a
compact assistance acknowledgement button per kiosk; expanding it adds names,
messages, and Close, Open, and Reset controls.

A green dot means the assigned kiosk is online and open to guests. A red dot
means it is closed, offline, outside business hours, unable to reach the waiver
website, or in another error state. A gray dot is unassigned. When a guest calls
for help, that kiosk's dot flashes between its current status color and yellow.
Selecting ACK or Acknowledge tells the guest assistance is on the way and returns
the viewer dot to its current status color.

Reload LilyPad, Staff Menu, Check for Updates, Minimize, and Exit Application
remain at the bottom of the sidebar. Minimize and normal close commands send the
viewer to the Windows notification area. Double-click its orange-fish tray icon
to restore it. Only the passcode-protected Exit Application command closes
Firefox and stops the viewer. Ctrl + Alt + M opens the Staff Menu. The Staff Menu
requires the viewer's 4-8 digit passcode. The Staff Menu finds
kiosks already paired with the on-site Kiosk Controller, remembers the controller
address and pairing key, and saves each kiosk's identity and Kiosk 1-4 position.
Selecting a machine already assigned elsewhere moves or swaps it automatically.

Version 1.4.0 retains the former POS Controller's internal package identity,
automatic-update channel, passcode, controller connection, known machines, and
assignments. Existing installations therefore update in place and their old
Start Menu shortcut is renamed automatically. See the README inside the
Mullet-Hop-Kiosk-Status-Viewer download for installation instructions.


STAFF SETTINGS AND EXIT
-----------------------

Press all four keys together:

Ctrl + Alt + M

Enter the staff password, then select "Open Staff Settings." The menu provides:

Settings use a left-side navigation rail with Connection & Updates, Date & Time,
Appearance, Waiver Station, Business Hours, and Ads & Staff Tools pages.

* Exit Kiosk.
* Check whether the computer can reach the live waiver website.
* Check GitHub for a newer kiosk version and install it immediately.
* Preview the waiver using a selected browser date and time.
* Return a date/time preview to the live date and time.
* Choose Auto, Light, or Dark kiosk appearance. Auto follows the Windows app
  theme, while Light and Dark override Windows.
* Schedule Dark mode for selected days and a selected time. When the underlying
  appearance is Light, the kiosk remains dark overnight and returns to Light or
  Auto at the next configured business opening.
* Add, edit, enable, disable, or delete scheduled JPG advertisements.
* View kiosk-manager advertisement sync status and progress, see the last
  successful sync time, or start a manual sync.
* Preview the complete thank-you page with all ads active for the current or
  staff-previewed date and time.
* Turn the guest-facing "Waiver Station Closed" page on or off. This setting is
  retained after the kiosk or computer restarts.
* Select how many minutes of guest inactivity pass before the video screensaver
  begins. The default is 3 minutes, and the saved delay is retained after the
  kiosk or computer restarts.
* Enable automatic business hours and set opening and closing times independently
  for every day of the week. Automatic business hours are off until staff enable
  them, so updating an existing kiosk does not unexpectedly close it.
* Select how long the Business Closed message remains visible before the display
  becomes fully black. The default is 5 minutes.
* Select how many minutes before the next scheduled opening the normal video
  screensaver begins. The default is 30 minutes; selecting 0 disables the
  pre-opening screensaver.
* Start an immediate temporary blackout. The display ignores ordinary keyboard,
  mouse, and touch input until staff use Ctrl + Alt + M, enter the
  password, and select Return to Kiosk.
* Change the staff password after verifying the current password.
* Return to the kiosk and load a clean waiver starting page.

After applying a preview date/time or returning to live time, Staff Settings
opens again automatically so additional testing can be completed without
re-entering the password.

Date/time preview mode does not change the Windows clock. It changes the date
and time reported to client-side scripts in the waiver browser. Content created
by LilYPad's server may continue to use the server's live date and time. A purple
staff-preview bar remains visible until live time is restored or the kiosk is
restarted.


CHANGE THE STAFF PASSWORD
-------------------------

Open Staff Settings and select "Change Staff Password." Enter and verify the
current password, then enter the new password twice. The OK button becomes
available only after the current password is verified. Staff passwords must
contain between 4–8 numbers only. The kiosk checks each requirement and confirms
when the new password has been saved successfully.


SCHEDULED ADVERTISEMENTS
------------------------

Open Staff Settings and select "Manage Advertisements." Each advertisement can:

* Upload a JPG or JPEG image up to 25 MB.
* Run once between a specific starting and ending date and time.
* Repeat every week on selected days and between selected daily times.
* Be temporarily disabled without deleting its image or schedule.

When one or more advertisements are active, the thank-you message moves to the
left and the active specials appear in a panel on the right. Multiple active ads
rotate automatically. When no advertisement is active, the thank-you message
remains centered.

Staff date/time preview also controls which advertisements are considered active,
so a future or repeating schedule can be checked before it goes live. The saved
JPG files are kept with this Windows account's local kiosk data.

Select "Preview Thank-You Page" in Staff Settings to display the exact completion
screen guests will see. Advertisement schedules are evaluated using the date and
time currently shown in Staff Settings, including a future preview time. The
normal thank-you countdown returns the kiosk to a fresh waiver automatically.

When the kiosk is paired with version 1.3.0 or newer of the Kiosk Controller,
the controller's Manage Ads window becomes the shared advertisement source.
Adding, editing, enabling, disabling, or deleting a manager ad publishes a new
catalog. Connected kiosks automatically download the changed catalog. Staff can
also use Sync Ads Now in the kiosk advertisement window. The sync section shows
transfer progress, the result, and the last successful sync time.

Every successful sync is saved as a complete local catalog, including the JPG
files and all schedules. If the kiosk manager is offline or cannot be found, the
kiosk continues displaying that last synced local catalog. A failed sync never
clears the saved ads. A later successful manager sync replaces the local catalog
with the newly published manager catalog.


WHAT THE KIOSK DOES
-------------------

* Opens the waiver in a borderless, full-screen window.
* Allows only HTTPS pages on mullet.lilypadpos.app inside the
  /public/onlinewaiver/ section.
* Cancels outside navigation, pop-ups, downloads, permissions, browser menus,
  developer tools, and normal browser shortcut keys.
* Provides a password-protected staff settings menu for exiting, testing the waiver
  connection, temporarily previewing a different browser date and time, and
  managing scheduled advertisements.
* Checks for a signed Velopack release on GitHub at startup and installs a newer
  version before guests begin using the kiosk.
* Adds a floating Mullet Hop instruction card to the starting waiver page. The
  card stays visible while guests scroll and briefly explains when to choose
  "Just Me" or "Me and My Kids!"
* Provides a guest-facing Clear Data & Reset Form button on the starting page
  and throughout the waiver.
* Remembers the starting email and waiver type. On later waiver pages, guests
  can restart with the opposite type; the kiosk securely clears the first
  attempt, restores the email, selects the other option, and continues.
  A Start a New Waiver button is provided without displaying the normal green
  or yellow status banner. After returning to the starting page, an arrow and
  highlighted message briefly identify the newly selected option before the
  kiosk continues automatically.
* Applies the Mullet Hop theme throughout every waiver step, including
  touch-friendly fields, buttons, choices, validation messages, and the
  signature area. Touchscreen and stylus strokes are translated into the
  continuous drag events expected by the waiver's signature pad.
* Replaces the broken LilYPad header image with the full transparent Mullet Hop
  logo and embeds a clearer fish-and-springs image for the kiosk's compact logo.
* Uses a bright, softened Mullet Hop background across the live waiver,
  thank-you screen, and both closed pages. Orange trampoline energy is balanced
  with the park's blue and lime padding colors while the center remains pale so
  form text and controls stay easy to read.
* Plays the packaged Mullet Hop video as a full-screen, looping screensaver after
  the staff-selected period without screen or keyboard activity. The first touch
  or keypress clears the prior session and loads a fresh waiver starting page.
  The start prompt has a separate footer so it never covers content in the video.
  The release build reassembles and verifies the exact uploaded MP4 from the
  source parts before placing it in the installed kiosk.
* Watches the page for common waiver-completion messages and completion URLs.
* Replaces the provider's completion screen with a Mullet Hop thank-you page that
  directs guests to the front desk to purchase their jump pass and socks. Active
  scheduled specials appear on the right; otherwise the message stays centered.
* Returns to a clean starting waiver after the branded thank-you screen.
* Resets after 3 minutes without guest activity if a waiver is abandoned.
* Clears cookies, local storage, cache, and other site data when it resets so the
  previous guest's browser session is not left for the next guest.
* Replaces browser connection errors with a branded "Waiver Station Closed"
  page that directs guests to the front desk. It reacts immediately when Windows
  reports that the network is available, also checks quietly every 60 seconds,
  clears the interrupted session, and automatically loads a fresh starting page
  when the waiver website becomes available again.
* Lets staff deliberately display a separate closed page without connection-error
  language. Ctrl + Alt + M continues to open Staff Settings from both
  closed pages. The password dialog opens in the foreground with its entry field
  focused so staff can type immediately. Staff Settings also controls the
  screensaver delay, and Return to Kiosk reloads a clean waiver starting page.
* Supports an optional weekly business-hours schedule. At closing time, the kiosk
  clears the guest session and shows a branded Business Closed page for the saved
  period (5 minutes by default), then disables browser input on a completely black
  display. Only the staff shortcut and password can open Staff Settings while the
  display is black.
* Automatically changes the black display to the packaged video screensaver at
  the staff-selected lead time before the next opening. From that point the video
  behaves like the normal screensaver: the first touch or keypress clears the
  previous session and loads the waiver start page.


IMPORTANT FIRST-DAY TEST
------------------------

Complete one real waiver from beginning to end while a staff member observes it.
After the confirmation appears, verify that the branded thank-you screen appears and
the starting email page returns after the countdown. The LilYPad site controls the exact final confirmation wording and
can change it without notice. The inactivity reset remains active as a fallback.

Also test the Start a New Waiver button once in each direction. Verify that no
green or yellow status banner appears and that the orange arrow briefly points
to the newly selected choice before Continue is activated automatically.

To test advertisements, add a short schedule that is active now, complete a
waiver, and verify that the thank-you message moves left while the JPG appears on
the right. Disable the advertisement, complete another waiver, and verify that
the thank-you message is centered again. Use date/time preview to test a future
schedule without changing the Windows clock.

If the final screen does not reset automatically, record the final page address or
take a photo of the confirmation wording. The completion list can then be updated in:

%LOCALAPPDATA%\MulletHopWaiverKiosk\Data\settings.json

Close the kiosk before editing that file.


CHANGE THE RESET TIMES
----------------------

After the first launch, close the kiosk and open:

%LOCALAPPDATA%\MulletHopWaiverKiosk\Data\settings.json

IdleTimeoutMinutes controls abandoned-waiver reset time.
CompletionResetSeconds controls the delay after a detected completion.

Do not change StartUrl, AllowedHosts, or AllowedPathPrefixes unless the waiver
provider changes the website.


RESET A FORGOTTEN STAFF PASSWORD
--------------------------------

1. Exit the kiosk if it is open.
2. Right-click Reset-Staff-PIN.ps1 and choose "Run with PowerShell."
3. Confirm the reset. A new password is requested when the kiosk starts.

The reset tool keeps advertisement images, schedules, and the other kiosk
settings; it clears only the staff password.

Anyone with access to this Windows account and its files can use the reset tool, so
use a dedicated Windows kiosk account for public-facing computers.


REMOVE THE KIOSK
----------------

Right-click Uninstall-Waiver-Kiosk.ps1 and choose "Run with PowerShell."
The uninstaller asks before removing the application, shortcuts, password settings, and
local WebView browsing data.


WINDOWS LOCKDOWN NOTE
---------------------

The application locks website navigation and browser controls. For a computer left
completely unattended in a public area, also use a dedicated standard Windows user
account and Windows Assigned Access so guests cannot use Windows system shortcuts to
reach other applications. Ctrl+Alt+Delete is controlled by Windows and cannot be
disabled by an ordinary desktop application.


REQUIREMENTS
------------

* Windows 10 or Windows 11, 64-bit
* Internet connection
* Microsoft Edge WebView2 Runtime (normally already present with current Edge)
* The Velopack Setup file from the public GitHub Releases page

The installed application is self-contained and does not require the .NET SDK on
the kiosk computer. The .NET 8 SDK is needed only by GitHub Actions or a developer
building the source code.

If WebView2 Runtime is missing, install the Evergreen Standalone Runtime from:
https://developer.microsoft.com/microsoft-edge/webview2/


PUBLISHING A REMOTE UPDATE
--------------------------

The repository includes .github/workflows/release.yml. To publish a new version:

1. Change the Version value in src\MulletHopWaiverKiosk.csproj. Use a three-part
   version such as 2.0.1, 2.1.0, or 3.0.0. Never reuse an earlier version.
   When those applications change, also update the Version values in the Kiosk
   Controller and Kiosk Status Viewer project files.
2. Commit and push the tested change to the repository's main branch.
3. Open the repository's Actions tab.
4. Choose "Publish Kiosk Update" and select "Run workflow."
5. Wait for the workflow to finish successfully. It creates a public GitHub
   Release containing the Setup program and Velopack update packages.
6. Restart a kiosk, or use "Check for Updates" in Staff Settings.

The workflow uses GitHub's built-in GITHUB_TOKEN only while publishing the
release. No token is compiled into or stored on the kiosk computer.
