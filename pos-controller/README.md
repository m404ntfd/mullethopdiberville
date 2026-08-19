# Mullet Hop POS Controller

The POS Controller is a separate Windows application for front-desk computers.
It shows four numbered waiver-station controls based on the supplied layout:

* A bright green light means the linked kiosk is online and open to guests.
* A bright red light means the kiosk is offline, closed, outside business hours,
  unable to reach the waiver website, or reporting another error.
* Both indicator lenses remain visible in a dark state when a dashboard slot is
  not linked.
* **Close Station** displays the waiver station closed screen.
* **Put In Service** removes the staff-controlled closed screen and returns the
  station to a fresh waiver.
* **Reset to Start** clears the current waiver and returns to its starting page.

The dashboard buttons do not require a passcode. The Settings button does.
There is no settings shortcut key.

## Installation

1. Keep the on-site Mullet Hop Kiosk Controller running on the same private
   network as the waiver kiosks and POS computers.
2. Extract the complete Mullet-Hop-POS-Controller ZIP package.
3. Run `Install-POS-Controller.cmd`.
4. Create a 4–8 digit Settings passcode on the first launch.
5. Select **Settings** and enter that passcode.
6. Copy the controller address and pairing key from the main Kiosk Controller.
7. Select **Pull Devices Now**. Paired devices are automatically added to the
   next open Kiosk 1–4 position. Change the number assignments if needed.
8. Save Settings.

The POS Controller talks to the on-site Kiosk Controller over TCP 47832 using
the existing signed local-network connection. It does not open a listening port
and does not require a firewall exception on the POS computer. Commands normally
reach online kiosks within five seconds. The newest command waits when a kiosk is
temporarily offline.

While the dashboard is running, it continues pulling the paired-device list.
New devices are automatically saved in the next open position without changing
the numbers of kiosks that were already assigned. When all four positions are
filled, additional devices remain available in Settings for manual reassignment.

## Updates and saved data

The POS Controller is packaged separately under the `pos` Velopack channel and
checks for updates when it starts. Its passcode and kiosk assignments are stored
separately from both the Waiver Kiosk and Kiosk Controller settings.
