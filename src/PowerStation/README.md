# PowerStation for Zeus

Control and monitor the Shelly relays, plugs and dimmers in your shack from
inside Zeus. By KQ4WLR.

## What it does (version 0.1)

- **Turn outputs on and off** on Shelly Gen2, Gen3, Gen4 and "Powered by
  Shelly" devices (Plus / Pro relays, Pro 3, Pro 4PM, Plug US, Dimmer Gen3,
  Ogemray 25A and similar).
- **Dim lights** with a slider or −/+ buttons.
- **Live readings** for metering devices: watts, volts, amps, energy (kWh)
  and device temperature, plus a running total for the whole station.
- **Warnings** the device raises, such as overpower or overheating.

Gen1 devices, automatic discovery, and automations (Zeus start/stop, TX,
band, idle time) are coming in the next versions.

## Setting up

1. Set each device up with the **Shelly app** first and give it a fixed IP
   address (a DHCP reservation in your router is easiest).
2. In Zeus, add the **PowerStation Devices** panel. Enter the device's IP
   address and choose **Add device**. If the device has a password, you'll be
   asked for it.
3. Add the **PowerStation** panel to your workspace to control everything.

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
