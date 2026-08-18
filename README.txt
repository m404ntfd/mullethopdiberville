MULLET HOP WAIVER KIOSK FOR WINDOWS
==================================

This package creates a full-screen Windows kiosk for:

https://mullet.lilypadpos.app/public/onlinewaiver/waiver.php?l=English

GitHub repository and update feed:

https://github.com/m404ntfd/mullethopdiberville

Latest Windows installer:

https://github.com/m404ntfd/mullethopdiberville/releases/latest


QUICK INSTALL
-------------

1. Open https://github.com/m404ntfd/mullethopdiberville/releases/latest.
2. Download MulletHop.WaiverKiosk-Setup.exe.
3. Exit an older copy of the kiosk with Ctrl + Alt + Shift + F12.
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

Staff can also press Ctrl + Alt + Shift + F12, open Staff Settings, and select
"Check for Kiosk Update." Update failures are written to the normal kiosk log
and do not prevent the waiver from opening.

The first upgrade from the older script-built kiosk to version 2.0.0 cannot be
automatic. Run the Velopack Setup file once to establish the updater. All later
updates can be delivered remotely.

The GitHub repository containing the Releases feed must be public. Do not place a
GitHub password, personal access token, or other secret in the kiosk application.


STAFF SETTINGS AND EXIT
-----------------------

Press all four keys together:

Ctrl + Alt + Shift + F12

Enter the staff password, then select "Open Staff Settings." The menu provides:

Settings are separated into Connection & Updates, Date & Time, Waiver Station,
and Ads & Staff Tools tabs.

* Exit Kiosk.
* Check whether the computer can reach the live waiver website.
* Check GitHub for a newer kiosk version and install it immediately.
* Preview the waiver using a selected browser date and time.
* Return a date/time preview to the live date and time.
* Add, edit, enable, disable, or delete scheduled JPG advertisements.
* Preview the complete thank-you page with all ads active for the current or
  staff-previewed date and time.
* Turn the guest-facing "Waiver Station Closed" page on or off. This setting is
  retained after the kiosk or computer restarts.
* Select how many minutes of guest inactivity pass before the video screensaver
  begins. The default is 3 minutes, and the saved delay is retained after the
  kiosk or computer restarts.
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
  language. Ctrl + Alt + Shift + F12 continues to open Staff Settings from both
  closed pages. The password dialog opens in the foreground with its entry field
  focused so staff can type immediately. Staff Settings also controls the
  screensaver delay, and Return to Kiosk reloads a clean waiver starting page.


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
2. Commit and push the tested change to the repository's main branch.
3. Open the repository's Actions tab.
4. Choose "Publish Kiosk Update" and select "Run workflow."
5. Wait for the workflow to finish successfully. It creates a public GitHub
   Release containing the Setup program and Velopack update packages.
6. Restart a kiosk, or use "Check for Kiosk Update" in Staff Settings.

The workflow uses GitHub's built-in GITHUB_TOKEN only while publishing the
release. No token is compiled into or stored on the kiosk computer.
