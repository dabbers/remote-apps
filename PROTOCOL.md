# RemoteApp wire protocol

A language- and OS-neutral description of the protocol spoken between a
RemoteApp **client** (iOS/iPadOS, macOS Catalyst, Windows) and a RemoteApp
**server** (macOS, Windows). The reference implementation is the Swift package
`RemoteAppProtocol/`; the C#/.NET implementation is `Windows/RemoteApp.Protocol`
(its tests round-trip every sample in §5). This document is the contract both
must match.

Everything here is derived from the Swift types in
`RemoteAppProtocol/Sources/RemoteAppProtocol/`. Where the JSON shape is subtle,
the exact bytes a Swift `JSONEncoder` produces are shown — match them.

Protocol version: **1** (`Hello.protocolVersion`; bump on an incompatible change).

---

## 1. Transport

- **TLS 1.3 over TCP** is the default and the target for the first Windows
  client. ALPN protocol token: **`remoteapp/1`**. The server presents a
  self-signed certificate; **the client does not verify it** (the tailnet's
  WireGuard layer is the real trust boundary). A Windows client should accept
  any server certificate for this ALPN.
- **QUIC** is an alternative (one bidirectional stream, TLS 1.3 built in, same
  ALPN). Optional for Windows; do TCP+TLS first.
- **Ports:** the server listens on **53123** (its tailnet interface) and
  **53124** (loopback, for a client on the same host). A client connects to
  `host:53123` normally, or `127.0.0.1:53124` when on the same machine.
- Both sides may send at any time once connected; framing (below) delimits
  messages on the single byte stream.

## 2. Discovery

- Primary: the user types the server's **Tailscale MagicDNS name** or tailnet IP
  (and optional `:port`). No discovery protocol is involved — just connect.
- Optional LAN fallback: **Bonjour / mDNS-SD**. Service types
  `_remoteapp._tcp` (TLS) and `_remoteapp._udp` (QUIC). A Windows client can
  skip Bonjour and rely on a typed address.

## 3. Framing

Every message on the stream is:

```
┌───────────────┬──────────┬───────────────────────────┐
│ length: u32BE │ kind: u8 │ payload: length-1 bytes    │
└───────────────┴──────────┴───────────────────────────┘
```

- `length` is big-endian and **counts the kind byte plus the payload** (so the
  payload is `length - 1` bytes).
- `kind`: **1 = control** (payload is UTF-8 JSON, see §5), **2 = video frame**
  (payload is binary, see §8).
- Max frame length: **64 MiB** (`64 * 1024 * 1024`). Reject anything larger.
- A reader buffers bytes until it has `4 + length`, emits the message, repeats.

## 4. JSON encoding rules (control messages)

Control payloads are the JSON encoding of a Swift `Codable` enum
(`ControlMessage`) with associated values. The shape is mechanical but specific
— a C#/System.Text.Json implementation must reproduce it:

- **Envelope = a single-key object** whose key is the message name:
  `{"<name>": <body>}`.
- **Body depends on the case's associated values:**
  - **One unlabeled value** → wrapped under `"_0"`: `{"hello":{"_0":{…}}}`.
  - **Labeled value(s)** → the label(s) as keys: `{"authFailed":{"reason":"…"}}`,
    `{"launchApp":{"bundleIdentifier":"…"}}`, `{"menuAction":{"path":[…]}}`.
  - **No value** → empty object: `{"listApps":{}}`, `{"requestClipboard":{}}`.
- **Nesting follows the same rules.** `input` holds one unlabeled `InputEvent`,
  itself an enum: `{"input":{"_0":{"tap":{…}}}}`. Inside `InputEvent`,
  `tap/longPress/scroll/drag` use **labeled** fields (no `_0`), but `key` holds
  one unlabeled `KeyEvent`, so it is `{"key":{"_0":{…}}}`.
- **Optionals are omitted entirely when null** (not emitted as `null`).
- **`Data` (byte blobs) → standard base64 string** (e.g. a PNG icon, a cert, a
  parameter set, clipboard bytes).
- **Numbers:** integers encode without a decimal point; `Double` fields may
  encode as an integer when integral (e.g. `"scale":3`). Emit valid JSON
  numbers; the Swift decoder accepts int or float for a `Double`.
- Swift escapes `/` as `\/` in strings (e.g. `"text\/plain"`); you need not —
  `"text/plain"` decodes identically. Both sides must accept either.
- Field order does not matter.

## 5. Control message catalogue

Exact encodings (one representative instance each). **C→S** = client sends,
**S→C** = server sends.

### Handshake / auth

```jsonc
// C→S  hello  (first message after TLS is up)
{"hello":{"_0":{"clientName":"Dave's iPhone","credential":"123456","deviceID":"A1B2-C3D4","protocolVersion":1,"screen":{"height":844,"scale":3,"width":390},"token":"tok_abc"}}}

// S→C  helloAck  (auth succeeded; issuedToken present only after a code pairing)
{"helloAck":{"_0":{"apps":[{"bundleIdentifier":"com.apple.TextEdit","name":"TextEdit","pid":501,"windowCount":1}],"hostScale":2,"issuedToken":"tok_new","serverName":"dab-mba"}}}

// S→C  authFailed
{"authFailed":{"reason":"wrong pairing code"}}
```

`Hello`: `clientName` (display), `deviceID` (stable per-install id), `credential`
(the 6-digit pairing code, or `""` when using a token), `token` (a saved pairing
token, or omit/null), `screen` = `{width,height,scale}` in points.
`HelloAck`: `serverName`, `hostScale` (host display backing scale), `apps`
(running, streamable apps), `issuedToken` (save it; present it as `token` next
time to skip the code).

### App list & selection

```jsonc
{"listApps":{}}                                              // C→S
{"appList":{"_0":[{"bundleIdentifier":"com.apple.TextEdit","iconPNG":"iVBORw==","name":"TextEdit","pid":501,"windowCount":1}]}}  // S→C
{"selectApp":{"_0":{"pid":501}}}                             // C→S  stream this app
{"requestLaunchable":{}}                                     // C→S  list installed apps
{"launchableApps":{"_0":[{"bundleIdentifier":"com.apple.TextEdit","name":"TextEdit"}]}}  // S→C
{"launchApp":{"bundleIdentifier":"com.apple.TextEdit"}}      // C→S  launch (if needed) then stream
```

`AppInfo`: `pid`, `bundleIdentifier`, `name`, `windowCount`, `iconPNG?`
(base64 PNG). `LaunchableApp`: `bundleIdentifier`, `name`, `iconPNG?`.

### Geometry & video config

```jsonc
{"screenSize":{"_0":{"height":844,"scale":3,"width":390}}}   // C→S  my drawable area changed (rotate/resize)
{"windowGeometry":{"_0":{"height":790,"pixelHeight":2370,"pixelWidth":1170,"width":390,"windowTitle":"Untitled"}}}  // S→C
{"videoConfig":{"_0":{"codec":"h264","nalLengthSize":4,"parameterSets":["Z0IA","aM4="],"pixelHeight":2370,"pixelWidth":1170}}}  // S→C  before first keyframe & on change
```

`WindowGeometry`: window size in host points (`width/height`), encoded pixel size
(`pixelWidth/Height`), `windowTitle?`. If width/height came back smaller than the
client asked for, the host clamped the resize and the client letterboxes.
`VideoConfig`: `codec` (`"h264"` | `"hevc"`), `parameterSets` (base64 NAL units:
SPS, PPS[, VPS]), pixel size, `nalLengthSize` (AVCC length prefix size, 4).

### Input (C→S)

```jsonc
{"input":{"_0":{"tap":{"at":{"x":0.5,"y":0.25},"button":"left"}}}}
{"input":{"_0":{"longPress":{"at":{"x":0.5,"y":0.25}}}}}
{"input":{"_0":{"scroll":{"at":{"x":0.5,"y":0.5},"dx":0,"dy":-40}}}}
{"input":{"_0":{"drag":{"at":{"x":0.5,"y":0.5},"phase":"began"}}}}      // phase: began|moved|ended|cancelled
{"input":{"_0":{"key":{"_0":{"isDown":true,"modifiers":0,"text":"a"}}}}}             // printable char
{"input":{"_0":{"key":{"_0":{"isDown":true,"modifiers":8,"text":"c"}}}}}             // ⌘C (command bit set)
{"input":{"_0":{"key":{"_0":{"isDown":true,"keyCode":36,"modifiers":0}}}}}           // Return
{"input":{"_0":{"key":{"_0":{"isDown":true,"keyCode":123,"modifiers":5}}}}}          // ⇧⌥Left
```

See §9 for coordinates, key codes and modifiers.

### Menu bar

```jsonc
{"requestMenu":{}}                                           // C→S
{"menuTree":{"_0":[{"checked":false,"children":[{"checked":false,"children":[],"enabled":true,"isSeparator":false,"shortcut":"⌘N","title":"New"},{"checked":false,"children":[],"enabled":true,"isSeparator":true,"title":""},{"checked":false,"children":[],"enabled":true,"isSeparator":false,"shortcut":"⌘W","title":"Close"}],"enabled":true,"isSeparator":false,"title":"File"}]}}  // S→C
{"menuAction":{"path":[0,2]}}                                // C→S  run top menu 0 → item 2
```

`MenuNode`: `title`, `enabled`, `isSeparator`, `checked`, `shortcut?` (display
hint like `"⌘N"`), `children[]`. `menuAction.path` indexes children from the root
of the last `menuTree`.

### Clipboard (symmetric, both directions)

```jsonc
{"requestClipboard":{}}                                      // either side: "send me your clipboard"
{"clipboard":{"_0":{"items":[{"data":"aGVsbG8=","kind":"text","mimeType":"text/plain"},{"data":"iVA=","kind":"file","mimeType":"image/png","name":"photo.png"}]}}}
```

`ClipboardItem`: `kind` (`text`|`image`|`file`), `mimeType`, `name?`
(for file/image), `data` (base64). Text is UTF-8; images are PNG/JPEG. See §10.

### Keepalive & errors

```jsonc
{"ping":{"_0":1700000000000}}   // either side; echo the number back as pong
{"pong":{"_0":1700000000000}}
{"error":{"message":"something failed"}}   // S→C (or C→S) non-fatal error to surface
```

Send `ping` ~every 5 s while no video is flowing (QUIC drops idle after 30 s).
A `ping` is answered with `pong` carrying the same value; it may arrive before
`helloAck`.

## 6. Connection lifecycle

1. Open TCP, complete TLS (ALPN `remoteapp/1`, accept any cert).
2. Client → `hello`. If `token` is present and valid the server authenticates by
   token (replies `helloAck` with `issuedToken` null). Otherwise it checks
   `credential` (the pairing code); on success it mints a token and returns it in
   `helloAck.issuedToken` (save it, keyed by server). On failure → `authFailed`
   (ask the user for the code and send a new `hello`).
3. After `helloAck`, the server has sent the running `apps`. Client may
   `listApps`, `requestLaunchable`, then `selectApp`/`launchApp`.
4. On selection the server sends `windowGeometry`, `videoConfig`, then a stream
   of **kind-2 video frames**, plus `menuTree`. It re-sends `videoConfig` before
   each new keyframe sequence and whenever parameter sets change (e.g. after a
   resize).
5. Client sends `input`, `screenSize` (on rotate/resize), `requestMenu`,
   `menuAction`, `requestClipboard`, `clipboard` as the user acts.
6. Either side may `ping`/`pong`. Closing the socket ends the session.

## 7. Video frames (kind = 2)

Binary payload layout (no JSON):

```
┌──────────────────┬──────────────────────────┬──────────────┬──────────────┐
│ ptsMicros: u64BE │ captureWallMicros: u64BE  │ keyframe: u8 │ access unit  │
└──────────────────┴──────────────────────────┴──────────────┴──────────────┘
      8 bytes                 8 bytes               1 byte       rest of frame
```

- `ptsMicros`: presentation timestamp, microseconds, monotonic on the host.
- `captureWallMicros`: wall-clock capture time, microseconds since the Unix
  epoch (used for end-to-end latency when clocks agree).
- `keyframe`: 1 if this access unit is an IDR/keyframe, else 0.
- Access unit: **AVCC** format — each NAL unit prefixed by its length in
  `nalLengthSize` (=4) big-endian bytes (not Annex-B start codes). H.264 High
  profile, no B-frames, up to 60 fps.

**Decoding on Windows (Media Foundation):** feed the SPS/PPS from
`videoConfig.parameterSets` to configure the decoder, then each access unit. MF's
H.264 decoder expects Annex-B, so convert: replace each 4-byte AVCC length prefix
with the start code `00 00 00 01`, and prepend `00 00 00 01`-framed SPS/PPS to
each keyframe (or set them as the sequence header). FFmpeg/`openh264` are
alternatives if MF is awkward.

## 8. Coordinates

`NormalizedPoint` `{x,y}` are fractions in **[0,1]** of the streamed window:
`(0,0)` = top-left, `(1,1)` = bottom-right. The server maps them to host window
coordinates, so the client needs no knowledge of host pixels. `scroll.dx/dy` are
in **client points** (positive `dy` = content moved down / finger down); the
server scales them to host points.

## 9. Keys and modifiers (cross-platform notes)

`KeyEvent` = `{ text?, keyCode?, modifiers, isDown }`.

- **`text`** — the character(s) to type, as a string. Prefer this for all
  printable input; it is OS-neutral. The Mac server inserts it as a Unicode key
  event.
- **`keyCode`** — for non-printing keys, a **protocol constant** (these happen to
  equal macOS virtual key codes, but treat them as protocol values):
  `Return=36, Delete/Backspace=51, Tab=48, Escape=53, LeftArrow=123,
  RightArrow=124, DownArrow=125, UpArrow=126`. A Windows client maps its own VK
  codes to these constants. Extended values a desktop client may also send
  (macOS virtual key codes, which a Mac server posts verbatim and a Windows
  server maps): `ForwardDelete=117, Home=115, End=119, PageUp=116,
  PageDown=121, Space=49, F1=122, F2=120, F3=99, F4=118, F5=96, F6=97, F7=98,
  F8=100, F9=101, F10=109, F11=103, F12=111`.
- **`modifiers`** — integer bitmask: **shift=1, control=2, option(alt)=4,
  command=8** (OR them together, e.g. `⇧⌥` = 5, `⌘` = 8).
- **`isDown`** — currently always `true`; the server synthesises a down+up pair.
- **Shortcuts:** send the character in `text` plus the modifier bits (e.g. `⌘C`
  = `{text:"c", modifiers:8}`). The **Mac server** maps the character to a US
  key code so the shortcut fires. A Windows client controlling a Mac should
  decide how to present modifiers: typically map the PC **Ctrl** key to
  `command` (8) so `Ctrl+C` becomes `⌘C` on the Mac, and offer the real `control`
  (2) when needed. (For the future Windows *server*, it will read `command`/
  `control` and apply the platform-appropriate shortcut.)

## 10. Clipboard

Platform-neutral so any OS maps it to its own clipboard:

- `text` — UTF-8 bytes, `mimeType` `text/plain`.
- `image` — PNG or JPEG bytes (`image/png` / `image/jpeg`).
- `file` — arbitrary bytes with a `name`; `mimeType` is advisory
  (`application/octet-stream` when unknown).

`requestClipboard` asks the peer to reply with its current `clipboard`.
A `clipboard` push sets the peer's clipboard. On Windows: read/write via the
Win32 clipboard (CF_UNICODETEXT, CF_DIB/PNG, CF_HDROP); write received files to
a folder and offer them. A single copy may carry several items (e.g. text + an
image); take what you can use.

## 11. Windows notes

Both sides exist in `Windows/` (see the README). Platform specifics of the
**Windows server**:

- `hostScale` is the primary monitor's DPI / 96; host points are DIPs. The
  shared window is moved to the top-left of the primary work area and its
  *visible* (DWM) bounds are resized to the client's screen size.
- `AppInfo.bundleIdentifier` is the executable's file name (e.g. `notepad.exe`);
  `LaunchableApp.bundleIdentifier` is the shell AppsFolder item path (an
  AppUserModelID for Store apps, a parsing name for classic ones), which
  `launchApp` opens exactly like a Start menu click.
- `menuTree` is read from the window's classic Win32 menu bar (`GetMenu`);
  shortcut hints are whatever follows the tab in the item text (e.g. `Ctrl+S`).
  Apps that draw their own menus report an empty tree.
- Modifiers: `command` and `control` both become Ctrl, `option` Alt, `shift`
  Shift. `text` is typed as real key presses when the active layout has the
  character, else as a Unicode key event.
- `videoConfig` is sent before the first keyframe of each encoder instance
  (every resize rebuilds the encoder) and whenever the SPS/PPS change.

A client must keep `videoConfig` ordered with the frames that follow it: both
arrive on the same stream, and the keyframe right after a config will not
decode under the previous configuration.

## 12. Windows C# client — first milestone (historical)

Target: **Windows client → existing macOS server**, TCP+TLS.

1. **Connect & framing:** `TcpClient` + `SslStream` (accept any server cert,
   set ALPN `remoteapp/1`). Implement the §3 length-prefixed reader/writer.
2. **JSON:** model `ControlMessage` with System.Text.Json using the §4 rules
   (custom converter for the enum envelope + `_0` wrapping). Round-trip against
   the samples in §5.
3. **Handshake:** send `hello` (persist `deviceID`; prompt for the 6-digit code;
   save `issuedToken`). Handle `helloAck` / `authFailed`.
4. **App list → select:** show `apps`, send `selectApp`.
5. **Video:** parse kind-2 frames (§7), decode H.264 (Media Foundation),
   render in a window; resize-to-fit and letterbox using `windowGeometry`.
6. **Input:** map mouse → `tap`/`longPress`(right)/`scroll`/`drag`, keyboard →
   `key` (§9), send `input`. Report `screenSize` when the window resizes.
7. **Clipboard & menu:** wire the §5 `requestClipboard`/`clipboard` and
   `requestMenu`/`menuTree`/`menuAction` (a native menu or a list).
8. **Keepalive:** `ping` every ~5 s; answer `ping` with `pong`.

Keep the wire code in a small reusable library so the later **Windows server**
reuses the same framing/JSON/clipboard/input code in the other direction.
