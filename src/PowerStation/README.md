# PowerStation for Zeus

Control and monitor the Shelly relays, plugs and dimmers in your shack from
inside Zeus. By KQ4WLR.

## What it does (version 0.2.1)

- **Turn outputs on and off** on Shelly Gen2, Gen3, Gen4 and "Powered by
  Shelly" devices (Plus / Pro relays, Pro 3, Pro 4PM, Plug US, Dimmer Gen3,
  Ogemray 25A and similar).
- **Dim lights** with a slider or −/+ buttons.
- **Live readings** for metering devices: watts, volts, amps, energy (kWh)
  and device temperature, plus a running total for the whole station.
- **Warnings** the device raises, such as overpower or overheating.
- **Scenes**: a named set of outputs and dimmer levels applied in one click,
  with an optional fade for dimmers. Every scene also has **All off**, so a
  scene doubles as an on/off group. If one device is offline, the rest of
  the scene still runs and PowerStation tells you what was missed.

Gen1 devices, automatic discovery, and automations (Zeus start/stop, TX,
band, idle time) are coming in the next versions.

## Setting up

1. Set each device up with the **Shelly app** first and give it a fixed IP
   address (a DHCP reservation in your router is easiest).
2. In Zeus, add the **PowerStation** panel (Switches category). It has two
   tabs: **Status** for everyday control and **Setup** for devices and scenes.
   On first run it opens on Setup.
3. On **Setup**, enter the device's IP address and choose **Add device**. If
   the device has a password, you'll be asked for it. Choose **Show on Status
   tab** to go back to the controls.
4. To make a scene, scroll to **Scenes** on the Setup tab and choose **New
   scene**. Tick the outputs, pick On or Off and a level for each dimmer, or
   press **Use current states** to capture the station as it is. Scene
   buttons then appear at the top of the Status tab.

**PowerStation Setup** in the panel list is the same panel opening on the
Setup tab, if you'd like both views docked side by side.

Devices on another VLAN work as long as the Zeus computer can reach them on
port 80. If a device shows **Offline**, check the address and any firewall
between the two networks.

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
