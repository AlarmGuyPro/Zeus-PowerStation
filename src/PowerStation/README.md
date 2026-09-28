# PowerStation for Zeus

Control and monitor the Shelly relays, plugs, dimmers and energy meters in
your shack from inside Zeus, and let Zeus switch them for you. By KQ4WLR.

## What it does (version 0.6.2)

- **Turn outputs on and off** on Shelly Gen1, Gen2, Gen3, Gen4 and "Powered
  by Shelly" devices: Shelly 1 / 1PM / 2.5 (relay mode), Plug and Plug S,
  Dimmer 1 and 2, ShellyEM, Plus and Pro relays, Pro 3, Pro 4PM, Plug US,
  Dimmer Gen3, wall dimmers, Plus RGBW PM, Ogemray 25A and similar.
- **Dim lights** on a drawing of the wall dimmer: click a level dot, the
  square to switch on and off, or − and + for 10% steps.
- **Colour lights**: a Shelly Plus RGBW PM in RGB or RGBW mode gets a colour
  tile with an LED-strip preview, on/off, a level slider, ten preset colours
  plus any custom colour, and a white slider in RGBW mode. Scenes and rules
  can set a colour, so an on-air sign can light red while you transmit. In
  Light mode the RGBW PM shows as four dimmers. Choose the mode in the
  Shelly app.
- **Scenes**: a named set of outputs and dimmer levels in one click, with an
  optional fade, and **All off**. They sit in their own box at the top of the
  panel.
- **Live readings**: watts, volts, amps, energy and temperature, plus a total
  for the station. The ShellyEM shows its relay and both clamp meters.
- **Normal ranges**: mains voltage and each output's current are checked
  against a normal range. Out of range shows amber, past the limit shows red,
  and every excursion is logged.
- **Rules** that switch things for you when Zeus starts or closes, when you
  transmit (on-air light only), when you change band or tune into a
  frequency range, after the station has been idle, or at a time of day.
- **Safety timers**: the device itself switches an output off if Zeus stops
  looking after it (a crash, a power cut, or just closing Zeus).
- **Finds your devices** on your networks and VLANs and follows them when
  DHCP gives them a new address.

## Setting up

1. Set each device up with the **Shelly app** first. DHCP addresses are fine.
2. In Zeus, add the **PowerStation** panel (Switches category). The gear at
   the top right opens setup; **Done** closes it. On first run it opens there.
3. **Setup › Devices › Find devices**: add each network your Shellies are on
   (for example `192.168.50.0/24`) and choose **Scan now**, then **Add** next
   to each device. You can also add a device by its address.
4. **Setup › Layout**: pick Auto or 1 to 4 columns, and **Arrange cards…**
   to drag them into place on the panel.
5. Optional: add the small **PowerStation Idle** panel somewhere always
   visible. It shows the idle countdown with **I'm here** and **+30 min**.

When you update from 0.4, remove the old **PowerStation Setup** panel if you
had it docked; setup now lives behind the gear.

### Passwords

- **Gen2 and newer**: the password is checked with the device and stored only
  as a one-way hash.
- **Gen1** devices use plain HTTP login and have no hashed form, so PowerStation
  stores a Gen1 password as you enter it, in Zeus's settings on this computer.
  Enter the user name too if you changed it from `admin`.

Each device's settings (**Setup › Devices › Edit**) have one **Save
changes** button for the name, address, output and meter names, safety
timers and supply. The password has its own **Check and save password**,
because it's checked with the device first.

## Rules (Setup › Automations)

Each rule reads **When** something happens **then** do something:
apply a scene, turn a scene all off, or turn one output on or off (dimmers
can ramp). Rules for lasting conditions also say what happens **when it
ends**: put back how it was, turn it off, something else, or leave it.

| When | Settings |
|---|---|
| Zeus starts | Delay |
| Zeus closes | Runs at once. Only seen when Zeus shuts down normally |
| Transmitting | **On-air light only.** Debounce, delay and a hold after TX |
| Band | One or more bands. Debounce, delay, end wait |
| Frequency range | From / to in MHz. Debounce, delay, end wait |
| Idle | Minutes without activity, warning time, extend step |
| Time of day | Runs at a time only if the station has been idle; otherwise checks again later |

- **Nothing switches while you transmit**, except the on-air light. Other
  rules wait until TX ends; the panel shows what's waiting.
- **Don't rely on the TX rule for safety.** The light follows TX over the
  network and can lag or miss a change. Never use it for amplifiers,
  antennas or T/R sequencing.
- **Put back how it was** only restores outputs nobody changed in the
  meantime.
- **Idle** means no tuning, band or mode change, TX, or use of PowerStation.
  Zeus doesn't share mouse or keyboard activity, so just listening counts as
  idle: press **I'm here** on the countdown.
- The scene editor can add "apply when Zeus starts" and "all off when Zeus
  closes" to any scene; they show up as ordinary rules.
- **Test** runs a rule's action now. The switch at the top pauses all rules.

## Safety timers

Set one per output (**Setup › Devices › Edit**) or per scene (for the
outputs the scene turns on). While Zeus runs, PowerStation keeps renewing
the timer; the device switches the output off by itself that many minutes
after the renewals stop. That covers a crash or power cut. It also means a
protected output turns off after its timer when you close Zeus normally.
Timers are only ever renewed on an output a poll has just seen on.

## Readings (Setup › Readings)

- **Mains voltage**: 120 V (US/Canada 120/240 V split-phase service, ANSI
  C84.1: normal 114–126 V, limit 110–127 V per leg), 230 V (normal ±6%,
  limit ±10%), or your own. Shelly devices measure each leg to neutral, so a
  US panel uses 120 V here even though there's 240 V between the legs; each
  leg of a ShellyEM is checked as 120 V. The page shows what your devices
  measure now, to help you pick. Mains is one supply, so a sag or surge is
  reported once for the station.
- **Loads wired across two legs** (a 240 V amplifier on a US panel): tick
  **Wired across two legs** in that device's settings, and its voltage is
  checked against twice the range.
- **Current per output**: defaults from each device's rating where it's known
  (warn at 80%, limit at 100%). Set your own values to match a breaker, cord
  or load. **Low when on** warns if an output is on but the load draws almost
  nothing, such as a tripped supply or a blown fuse.
- A reading must stay out of range for 5 seconds (adjustable) before it
  counts, so switch-on inrush doesn't.
- Colour LED controllers run from a 12/24 V DC supply, so their voltage is
  shown but not checked against mains.
- These are warnings only. The Shelly's own overpower and overvoltage
  protection does any switching.

## When a device won't connect (Setup › Debug)

The Debug section shows the traffic between PowerStation and your devices,
newest first: every command, every error, and the device's own reply. Filter
by device, by text (a method, an address, anything in a reply), errors only,
or hide the routine status polls. **Record every poll** also keeps the
successful status reads, so you can see exactly what a device reports; it
turns itself off after 30 minutes. **Copy** or **Download** the filtered list
to share it. Passwords and login headers are never recorded.

If PowerStation can't make sense of a device's status, the device's card now
says so (instead of staying on "Connecting…"), and the reply is in the log.

## Networks

Devices on another VLAN work as long as the Zeus computer can reach them on
port 80. mDNS only reaches this computer's own network; the scan covers other
VLANs, up to 4,096 addresses at a time. If Windows asks whether Zeus may use
the network, allow it on private networks.

## Privacy and safety

- PowerStation only talks to devices on your local network. It never
  contacts the Internet.
- It reads the radio's frequency, mode and TX state to run your rules. It
  never controls the radio: it doesn't tune, key the transmitter or touch
  PureSignal.
- Triac dimmers and some switching supplies can create RF noise. Keep them
  away from antennas and feed lines, and add ferrites if you hear hash.

## License

GPL-2.0-or-later. Source: https://github.com/AlarmGuyPro/zeus-powerstation
