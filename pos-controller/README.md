# Mullet Hop POS

Mullet Hop POS is the full-screen front-desk application for Mullet Hop.
It uses Mozilla Firefox installed on the computer to load:

`https://mullet.lilypadpos.app/public/Login.php`

LilyPad POS fills the main area. A right sidebar shows the four assigned waiver
kiosks and keeps their status available without covering the POS page. Firefox
keeps keyboard focus unless the user deliberately interacts with the sidebar.

## Sidebar behavior

* Select **Collapse Controls** at the top to reduce the sidebar to K1–K4, one
  status dot per kiosk, and a compact assistance acknowledgement button.
* Select the arrow again to expand kiosk names, status messages, and the Close,
  Open, and Reset controls.
* Clicking or touching LilyPad immediately returns keyboard focus to Firefox.
  After that input completes, the sidebar collapses automatically. Its status
  lights keep updating in the background, but unchanged status responses do not
  rewrite the controls or take focus from Firefox.
* A green dot means the kiosk is online and open to guests.
* A blue dot means the kiosk is showing the scheduled Business Closed screen or
  its business-hours blackout screen.
* A red dot means the kiosk was closed by staff, is offline, cannot reach the
  waiver website, or is reporting another error.
* A gray dot means that dashboard position is not assigned.
* When a guest requests assistance, the kiosk's dot flashes between its current
  status color and yellow. Select **ACK** or **Acknowledge** to tell the guest
  that assistance is on the way. The button then reads **Answered**, stays gray
  and disabled, and the dot keeps flashing yellow until the call is cleared at
  the kiosk.
* **Close** asks whether this is a **Staff Closure** or **Business Closure**.
  Staff Closure displays the normal closed screen and reports red. Business
  Closure starts the Business Closed video and reports blue.
* **Open** removes the staff-controlled closed screen and starts a fresh waiver.
  It also ends a manually selected Business Closure; the configured business
  schedule still applies.
* **Reset** returns the kiosk to the beginning of the waiver.

The bottom of the sidebar contains Restore Keyboard, Refresh Lilypad, Settings,
Check for Updates, Minimize, and Exit Application. **Restore Keyboard** returns
focus to Firefox without closing the browser, changing pages, or losing the
current sale. **Minimize** sends Mullet Hop POS and its Firefox window to the
Windows taskbar. Select the Mullet Hop POS taskbar icon to restore the full-screen
window. Only the passcode-protected **Exit Application** command closes Firefox
and stops the application. Press **Ctrl + Alt + M** while Mullet Hop POS is active
to open the protected Settings window.

Firefox is embedded with its normal browser controls instead of kiosk mode. The
application restores Firefox input focus when the POS window activates, continuously
protects Firefox focus while the sidebar is idle, and checks for a hung browser, a
failed startup, a Firefox process exit, and a tab-crash title.
It terminates and reopens the complete embedded Firefox session once automatically.
Only if the recovery also fails does a red banner ask staff to use **Refresh
Lilypad**. Refresh Lilypad forcibly terminates the Firefox process tree, clears
every saved tab and session, and opens one fresh LilyPad home page.

The dedicated Firefox session always starts from a clean tab/session state and loads
the LilyPad login page with JavaScript and fresh HTTP responses enabled. On LilyPad,
moving from the username field into the password field fires the page's native
username-change request; LilyPad then supplies the location and station choices for
that employee. The POS host restores cross-process Firefox focus after activation so
that this username → password → location-selection sequence continues to work after
the POS window has been minimized or covered.

## Installation

1. Install Mozilla Firefox on the front-desk computer.
2. Keep the on-site Mullet Hop Kiosk Controller running on the same private
   network as the waiver kiosks and front-desk computer.
3. Extract the complete `Mullet-Hop-POS` ZIP package.
4. Run `Install-Mullet-Hop-POS.cmd`.
5. Create a 4–8 digit Staff Menu passcode on the first launch.
6. Open **Settings**, then copy the controller address and pairing key from the
   main Kiosk Controller.
7. Select **Connect & Remember**. The verified controller address and pairing key
   are saved immediately.
8. Under **Known Machines & Dashboard Assignments**, choose the machine for each
   Kiosk 1–4 position. Choosing a machine already assigned elsewhere moves or
   swaps it automatically.
9. Select **Save Kiosk Assignments** or **Save Settings**.

Mullet Hop POS talks to the on-site Kiosk Controller over TCP 47832 using the existing
signed local-network connection. It does not open a listening port and does not
require a firewall exception on the POS computer. New paired devices are saved
in the next open position without renumbering existing assignments. Three POS
workstations can run at the same time; each workstation sees and controls the same
four kiosk assignments through the master controller.

## Firefox profile and saved data

The application starts Firefox with a dedicated profile stored with the existing
application data. This keeps the LilyPad login and Firefox site data available between
restarts without changing the user's normal Firefox profile.

Version 1.7.3 keeps keyboard focus on Firefox while the status sidebar works in the
background, immediately restores focus when browser input is detected, auto-collapses
the sidebar after that input completes, adds a non-destructive Restore Keyboard
command, and only redraws kiosk cards when their status changes. Assistance changes
still flash the light and enable acknowledgment immediately. Version 1.7.2 requests LilyPad's POS/location list after username input settles,
before the Password field must receive focus. It also stops routine Firefox health
checks from recalculating the embedded window frame, which preserves password focus
and native dropdown popups. Version 1.7.1 adds a loopback-only Firefox compatibility bridge for LilyPad's
legacy login form. Focusing or clicking Password now asks LilyPad for the location
and station list when Username contains a value, even if the page's Username
change event did not fire. Version 1.7.0 always opens one clean LilyPad login session, avoids stale cached
login responses, and strengthens cross-process Firefox focus after the POS window
is activated. This preserves LilyPad's native username-change request when staff
move into the password field, allowing LilyPad to display the employee's location
and station choices. Version 1.5.1 minimizes to the Windows taskbar, renames the controls to Settings
and Refresh Lilypad, restores Firefox input focus, and performs one automatic
full-session recovery before reporting a Firefox failure. Version 1.5.0 keeps
Firefox's menu and browser controls visible, detects a crashed
Firefox tab or process, and provides a Reload LilyPad action that terminates the
dedicated Firefox session, clears all of its tabs, and opens one fresh LilyPad
home page. It also identifies the POS workstation to the controller so three
front-desk machines can be shown as active. Version 1.4.1 is the first release
named **Mullet Hop POS**. It adds the blue
business-hours-closed status, prompts for Staff or Business Closure, and keeps
acknowledged assistance calls flashing until they are cleared. It preserves the
former POS Controller's internal package ID, `pos` update channel, saved-data
folder, controller key, passcode, remembered kiosks, and Kiosk 1–4 assignments so
existing installations upgrade in place. It also renames the existing Start Menu
shortcut automatically.

Mullet Hop POS checks the public `mullethopdiberville` GitHub release
feed when it starts. Select **Check for Updates** in the sidebar to check manually.
When an update is downloaded, Mullet Hop POS can install it and restart automatically.

Version 1.4.0 added the full-screen Firefox/LilyPad layout and collapsible kiosk
sidebar. Version 1.3.0 added Ctrl + Alt + M Staff Menu access and automatic move/swap
assignment behavior. Version 1.2.0 added guest-assistance alerts and response
controls. Version 1.1.1 added persistent controller connections and assignments.
