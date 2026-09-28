# PowerStation for Zeus

Control and monitor the Shelly relays, plugs and dimmers in your shack from
inside Zeus. By KQ4WLR.

## What it does (version 0.4.2)

- **Turn outputs on and off** on Shelly Gen2, Gen3, Gen4 and "Powered by
  Shelly" devices (Plus / Pro relays, Pro 3, Pro 4PM, Plug US, Dimmer Gen3,
  Ogemray 25A and similar).
- **Dim lights** on a drawing of the wall dimmer: click one of its seven
  level dots to jump to that level, the square to switch on and off, or
  − and + for 10% steps.
- **Your own grid**: devices snap to equal cells that fill the panel, and
  cards in a row share a height. Choose Auto or 1 to 4 columns on the
  Status tab, then **Arrange** to drag a device onto another to swap
  places (or use the arrow buttons). The column count you pick is kept
  unless the panel is too narrow for it, in which case a note says so. The
  layout is saved and survives Zeus restarts.
- **Live readings** for metering devices: watts, volts, amps, energy (kWh)
  and device temperature, plus a running total for the whole station.
- **Warnings** the device raises, such as overpower or overheating.
- **Finds your devices**: a scan on the Setup tab looks across the networks
  you list (each VLAN your Shellies use) and listens for devices announcing
  themselves on this computer's network (mDNS). Add what it finds in one
  click.
- **Keeps up with DHCP**: devices are tracked by their Shelly device ID, not
  their address. If a device stops answering, PowerStation looks for it on
  its old network and your saved networks and moves it to its new address
  automatically. You don't need DHCP reservations.
- **Status lamps** on every output: green on, orange off, blue for a dimmer
  on below 100%, grey when the device isn't answering.
- **Scenes**: a named set of outputs and dimmer levels applied in one click,
  with an optional fade for dimmers. Every scene also has **All off**, so a
  scene doubles as an on/off group. If one device is offline, the rest of
  the scene still runs and PowerStation tells you what was missed.

Gen1 devices (a scan shows them as "coming soon") and automations (Zeus
start/stop, TX, band, idle time) are coming in the next versions.

## Setting up

1. Set each device up with the **Shelly app** first. DHCP addresses are fine.
2. In Zeus, add the **PowerStation** panel (Switches category). It has two
   tabs: **Status** for everyday control and **Setup** for devices and scenes.
   On first run it opens on Setup.
3. On **Setup**, under **Find devices**, add each network your Shellies are
   on (for example `192.168.50.0/24`) and choose **Scan now**. Choose **Add**
   next to each device you want. You can also add a device by its address.
   If the device has a password, you'll be asked for it.
4. To make a scene, scroll to **Scenes** on the Setup tab and choose **New
   scene**. Tick the outputs, pick On or Off and a level for each dimmer, or
   press **Use current states** to capture the station as it is. Scene
   buttons then appear at the top of the Status tab.

**PowerStation Setup** in the panel list is the same panel opening on the
Setup tab, if you'd like both views docked side by side.

Devices on another VLAN work as long as the Zeus computer can reach them on
port 80. mDNS only reaches this computer's own network; the scan covers other
VLANs. A scan covers up to 4,096 addresses at a time (a /20, or several
/24s). If Windows asks whether Zeus may use the network, allow it on private
networks so mDNS can work.

## Privacy and safety

- PowerStation only talks to devices on your local network (private,
  link-local and loopback addresses). It never contacts the Internet.
- Device passwords are checked with the device and then stored only as a
  one-way hash (the device's own digest format), never as the password.
- PowerStation never controls your radio. It does not key the transmitter
  and does not touch PureSignal.
- Triac dimmers and some switching supplies can create RF noise. Keep them
  away from antennas and feed lines, and add ferrites if you hear hash.

## License

GPL-2.0-or-later. Source: https://github.com/AlarmGuyPro/zeus-powerstation
