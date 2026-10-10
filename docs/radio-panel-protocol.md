# Radio panel USB protocol

These notes cover the Logitech / Saitek Pro Flight Radio Panel. They agree with the public notes in
[bjanders/fpanels](https://github.com/bjanders/fpanels), and `RadioPanelProtocol` implements them.

| | |
|---|---|
| USB ids | vendor `06A3`, product `0D05` |
| Class | HID, no report ids (report id 0) |
| Input | 3-byte interrupt report |
| Output | 22-byte feature report (`SET_REPORT`, type feature, id 0) |

## Input report

The three bytes form a little-endian 24-bit field (`b0 | b1 << 8 | b2 << 16`). On Windows, HID APIs
put the report id (0) in front, so a read returns 4 bytes.

| Bits | Meaning |
|---|---|
| 0–6 | Upper selector: COM1, COM2, NAV1, NAV2, ADF, DME, XPDR (one bit set) |
| 7–13 | Lower selector: COM1 … XPDR |
| 14 | Upper ACT/STBY pressed |
| 15 | Lower ACT/STBY pressed |
| 16, 17 | Upper inner knob: clockwise, counter-clockwise |
| 18, 19 | Upper outer knob: clockwise, counter-clockwise |
| 20, 21 | Lower inner knob: clockwise, counter-clockwise |
| 22, 23 | Lower outer knob: clockwise, counter-clockwise |

- A selector briefly has **no** bit set while it moves between positions. The decoder keeps the
  last position.
- A knob bit **pulses** for each detent. The decoder counts one detent per rising edge, like fpanels.

## Display report

The report holds 22 bytes: four displays of five digits, then two zero bytes. The displays come in
this order: upper left (active), upper right (standby), lower left, lower right. Each digit byte is
one of:

| Byte | Shows |
|---|---|
| `0x00`–`0x09` | digit 0–9 |
| `0xD0`–`0xD9` | digit 0–9 with its decimal point lit |
| `0x0F` | blank |
| `0xEF` | dash |

RadioBridge builds displays from short strings: digits, `.` (lights the point of the digit before),
space and `-`, right-aligned. Too-long text keeps its last five digits. Examples:

| Text | Bytes |
|---|---|
| `23.405` | `02 D3 04 00 05` |
| `118.30` | `01 01 D8 03 00` |
| `1200.` | `0F 01 02 00 D0` |

The panel itself can only show digits, so text such as a welcome message can't be shown on it.
