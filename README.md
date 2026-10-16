# RemoteApp — stream one app window to another device

Proof of concept: a server captures a single app's main window, encodes it
with hardware H.264, and streams it to a client that shows it and sends taps,
scrolls and keystrokes back. The macOS server streams to an iPhone/iPad/Mac
client; the **Windows client and server** (`Windows/`) show a remote app as a
"virtual" application window on a PC and share a Windows app the same way.
See [Windows](#windows).

**The server is reachable only over your Tailscale tailnet, never the open
internet.** It binds to the Mac's tailnet address and to loopback, and to
nothing else. Discovery and connection use the Tailscale MagicDNS name. See
[Network exposure](#network-exposure).

```
RemoteAppProtocol/   Swift package shared by both apps: messages, framing, transport
Server/              macOS SwiftUI app (RemoteAppServer)
Client/              iOS SwiftUI app (RemoteAppClient)
Windows/             .NET solution: protocol library + tests, Windows client, Windows server
PROTOCOL.md          language-neutral wire spec (the contract between all of the above)
project.yml          xcodegen spec → RemoteApp.xcodeproj
Config/              build config: Base.xcconfig + Local.xcconfig.example (signing)
```

**Nothing machine-specific is committed.** There is no bundled TLS key (the
server generates a self-signed one on first launch), and no team ID, bundle
prefix, tailnet name or developer identity in the tree — signing lives in a
gitignored `Config/Local.xcconfig` (see [Running](#running)).

## How it works

| Stage | Host (macOS) | Client (iOS) |
|---|---|---|
| Discovery | Reads its tailnet identity from the Tailscale local API (`tailscale status --json`); shows its MagicDNS name as the connect target. Bonjour stays as an optional LAN fallback | Connect by MagicDNS name / tailnet IP; Bonjour list is a fallback |
| Binding | Listeners scoped (`IP_BOUND_IF`) to the Tailscale interface (`utun*:53123`) and loopback (`lo0:53124`) only, never a public interface | — |
| Transport | TLS 1.3 over TCP over the WireGuard tunnel (default), with a self-signed identity generated on first launch into Application Support; QUIC selectable for LAN | Trusts any server certificate (fine: WireGuard already encrypts and authenticates the tailnet) |
| Auth | 6-digit pairing code shown in the server window; the device is then remembered with a persistent token | Enter the code once per device; later connects skip it |
| App list | `SCShareableContent` → running apps with windows; plus all installed apps enumerated for launching | "Running" list, and a + button to open any installed app |
| Launch | Opens an installed app via `NSWorkspace`, waits for its window, then streams it | Searchable installed-apps picker |
| Window sizing | Accessibility API moves/resizes the app's main window to the phone's point size, then reports what the app accepted | Letterboxes (`resizeAspect`) when the app clamped; pinch to zoom |
| Capture | `SCContentFilter(display:including:[app])` cropped with `sourceRect` to the main window: menus, sheets and popovers of that app are included, other apps are not | — |
| Encode/decode | `VTCompressionSession`, H.264 High, real-time, no B-frames, 60 fps, 12 Mbit/s | `AVSampleBufferDisplayLayer` (VideoToolbox decode) fed AVCC access units |
| Input | `CGEvent`s posted through the HID event tap after bringing the app frontmost, so clicks are hit-tested like real ones; a client's pointer hover (`move`) moves the host cursor without activating anything | Tap = click (double-click by timing), one-finger pan = scroll, long press = right click, keyboard = key events, two-finger tap = toggle HUD; a pointer (Mac, iPad trackpad/mouse) hovering over the video moves the host cursor |
| Menu bar | Read through the Accessibility API (`AXMenuBar` → `AXMenuBarItem` → `AXMenu` → `AXMenuItem`), with titles, enabled/checked state and shortcut hints; items performed with `AXPress` | iPhone/iPad: native menu sheet from the HUD. **Mac: the streamed app's menus appear in the real Mac menu bar**, plus a "Remote" menu to manage the session |
| Keyboard | Virtual key codes and modifier flags posted as `CGEvent`s; a `⌘`/`⌃`+character is posted by its US-layout key code so menu shortcuts (Copy, Paste…) fire | Software keyboard plus an **accessory bar**: Esc, Tab, arrows, and sticky `⌃⌥⇧⌘` modifiers that arm for the next key (`⌘` then `c` = ⌘C) |
| Clipboard | **Shared both ways** via `NSPasteboard`, in a platform-neutral form: text as UTF-8, images as PNG/JPEG, files written to `~/Downloads/RemoteApp` and put on the pasteboard | HUD → clipboard: pull the Mac's clipboard (text/images paste-ready), or push this device's clipboard, picked **Photos**, or **Files**; received files/photos saved via the share sheet |

Wire format: `u32 length | u8 kind | payload`; kind 1 is a JSON `ControlMessage`,
kind 2 is a binary video frame (`pts`, capture wall-clock time, keyframe flag,
AVCC NAL units). See `RemoteAppProtocol/Sources/RemoteAppProtocol/Messages.swift`.

## Network exposure

The server is designed to be reachable **only over your Tailscale tailnet**:

- On launch it reads its tailnet identity from the Tailscale local API
  (`tailscale status --json`, via the GUI app's bundled CLI or a `tailscale` on
  `PATH`) and finds the interface that carries the tailnet address (e.g.
  `utun4`). It then binds one listener **scoped to that interface** on port
  **53123**, and one scoped to the **loopback** interface (`lo0`) on port
  **53124** for same-Mac clients. It never binds a general/public interface,
  and there is no port-mapping, STUN/TURN, relay or public-signaling code.
- The interface scoping uses `NWParameters.requiredInterface` (`IP_BOUND_IF`).
  `requiredLocalEndpoint` would pin to the exact IP and read more clearly in
  `lsof`, but on current macOS it breaks every accepted connection (the child
  inherits the requirement and re-binds the listener's address, failing with
  `EADDRINUSE`), so interface scoping is used instead.
- The default transport is **TLS/TCP** over the WireGuard tunnel. (QUIC is
  selectable but cannot be pinned to a single interface this way on current
  macOS, so leave it for LAN use.) WireGuard already encrypts and authenticates
  every peer, so TLS here is for the app's framing, not the trust boundary; the
  client does not pin the certificate in this prototype.
- The 6-digit pairing code is a second factor on top of tailnet identity.

**Verifying it is not public.** Because the sockets are interface-scoped rather
than address-bound, `lsof` shows `*:53123` / `*:53124`, not the specific IP:

```bash
lsof -nP -iTCP -sTCP:LISTEN -a -c RemoteAppServer
```

`IP_BOUND_IF` means the kernel only delivers packets that arrive on the bound
interface, so the real proof is functional: from a host that is **not** on your
tailnet, a connection to this Mac's LAN IP on port 53123 is refused/times out,
while a tailnet peer connecting to the MagicDNS name succeeds. The server window
and log show exactly which interface each listener bound to
(`listening on tailnet utun4:53123`). Find the MagicDNS name with
`tailscale status` (it is also shown in the server window).

## Requirements

- macOS 14+ host (tested on macOS 26), Xcode 16+ (tested with Xcode 27).
- iOS 17+ client or simulator.
- [Tailscale](https://tailscale.com) on the Mac, signed in, and on the same
  tailnet as the iPhone (Tailscale app installed on the phone). Without it the
  server binds loopback only and warns.
- [xcodegen](https://github.com/yonaskolb/XcodeGen) to regenerate the project
  after editing `project.yml` (`brew install xcodegen`). The generated
  `RemoteApp.xcodeproj` is committed, so you can also just open it.

## Permissions (host Mac)

RemoteApp Server needs two grants in **System Settings › Privacy & Security**:

1. **Screen Recording** — for ScreenCaptureKit. Without it the app list is empty
   or capture fails.
2. **Accessibility** — to find, move and resize the streamed window
   (`AXUIElement`) and to post input events. Without it selecting an app fails
   with "no main window".

The server asks for both on first launch. Toggle them on, then quit and relaunch
the server. The project is **ad-hoc signed** by default (no developer account
needed); macOS ties these grants to the code signature, so **each rebuild of the
server requires re-granting**. To avoid that — and to install the client on a
physical device — sign with your own team: copy `Config/Local.xcconfig.example`
to `Config/Local.xcconfig` (gitignored), fill in your `DEVELOPMENT_TEAM`, and
rebuild. A stable Development signature keeps the grants across rebuilds. You can
also set `REMOTEAPP_BUNDLE_PREFIX` there to use your own bundle-id prefix.

The TLS identity is **not committed**: on first launch the server generates a
self-signed certificate into `~/Library/Application Support/RemoteAppServer/`
(`server.p12`, mode 0600, with a per-install random passphrase beside it). It is
used only over the tailnet, where WireGuard is the real trust boundary, so the
client does not verify it.

The client needs Local Network access (prompted on a real iPhone, not enforced in
the simulator).

## Running

1. Build and run **RemoteAppServer** (scheme `RemoteAppServer`, My Mac). Grant
   the permissions above and relaunch. The window shows the connect target
   (`MagicDNS-name : 53123`), the tailnet status, the pairing code and a log;
   the log is also written to `~/Library/Logs/RemoteAppServer.log`.
2. Open the app you want to stream on the Mac (e.g. TextEdit with a document).
3. Build and run **RemoteAppClient** on an iPhone on the same tailnet, or a
   simulator on the same Mac (scheme `RemoteAppClient`).
4. In the client, under **Connect over Tailscale**, type the MagicDNS name shown
   in the server window, tap Connect, enter the pairing code, then pick the app.
   Server and client must be on the same transport (both default to TLS/TCP).
   - On the **same Mac**, use the loopback listener instead: connect to
     `127.0.0.1:53124`. (The Simulator can't use the Mac's own tailnet IP — that
     path loops back through the tunnel and times out.)
5. The host window is resized to the phone's size, raised, and streamed. The HUD
   shows end-to-end latency (capture → decode enqueue, valid when host and
   client clocks agree, which is always true for a simulator) and fps.
   - **Keyboard:** tap the keyboard icon to type; `⌫` and Return are forwarded as
     key codes. While the keyboard is up the client reports its reduced visible
     height, so the host window shrinks to sit fully above the keyboard instead
     of being covered.
   - **Controls:** the eye icon hides the HUD; **two-finger tap** anywhere shows
     it again.
   - **Menu bar:** the menu-bar-rectangle icon opens the host app's menus as a
     native iOS sheet (File/Edit/Format/…), with submenus, disabled items greyed,
     checkmarks, and shortcut hints. Tapping a leaf runs it on the Mac; the sheet
     refreshes so checkmarks update. The menu is translated through Accessibility,
     not captured — **context menus (right-click/long-press) still appear in the
     video stream**, as do any menus the app draws itself.

Command-line equivalent:

```bash
xcodebuild -project RemoteApp.xcodeproj -scheme RemoteAppServer -configuration Debug -derivedDataPath build/DerivedData build
```

```bash
xcodebuild -project RemoteApp.xcodeproj -scheme RemoteAppClient -configuration Debug -sdk iphonesimulator -destination 'generic/platform=iOS Simulator' -derivedDataPath build/DerivedData build CODE_SIGNING_ALLOWED=NO
```

## Known limitations

- **One client, one app, one window.** Secondary windows of the same app are
  only visible where they overlap the main window's rectangle (menus, sheets
  and popovers usually do). Other apps' windows never appear.
- **Minimum window sizes.** Most Mac apps cannot shrink to iPhone width, so the
  stream is letterboxed; pinch to zoom. The server reports the real size in
  `windowGeometry`.
- **The host is in use.** The streamed app is activated and its window moved to
  the top-left of the main display; input is injected into that app. Nobody
  else can use the Mac at the same time.
- **Security is prototype grade, but not internet-exposed.** The tailnet is the
  trust boundary (WireGuard encryption + device identity); on top of it there is
  a self-signed cert (unpinned) and the pairing code.
  `OpenDirectoryAuthenticator` in `Server/Sources/Auth.swift` verifies a local
  account password and can replace `PairingCodeAuthenticator`.
- **Needs Tailscale.** Without it the server binds loopback only (same-Mac
  Simulator still works); it does not fall back to exposing the LAN. MagicDNS
  name resolution on the client needs the Tailscale app on the phone.
- **QUIC handshake delay.** The first connect sometimes takes ~5 s while
  Network.framework races interfaces (IPv4, IPv6, AWDL); stale attempts show
  as "Connection refused" in the server log and are harmless. TLS/TCP is
  available as a fallback in both UIs.
- **Drag is in the protocol but not bound to a gesture** on the client; one
  finger pans (scroll). Text selection by dragging is therefore not possible yet.
- **No audio, no clipboard, no multi-display awareness** beyond picking the
  display that contains the window.
- Key events use a placeholder virtual key code with a Unicode payload; apps
  that read key codes (games, some shortcuts) will not see the right key.
- Latency is bounded by dropping frames when more than 4 are in flight; on
  slow networks the picture updates in bursts instead of lagging.

## Windows

`Windows/RemoteApp.slnx` (.NET 10, C#, WinForms + Direct2D + Media Foundation,
Win32 via [CsWin32](https://github.com/microsoft/CsWin32)) contains:

| Project | What it is |
|---|---|
| `RemoteApp.Protocol` | The wire protocol from `PROTOCOL.md`: framing, the JSON message shapes (matching Swift's Codable envelope), TLS transport, H.264 NAL helpers. Platform-neutral. |
| `RemoteApp.Protocol.Tests` | xUnit tests: every sample in `PROTOCOL.md` §5 round-trips, framing, NAL conversion. |
| `RemoteApp.WindowsCommon` | Clipboard bridge (Win32 clipboard ↔ neutral items) and icon helpers shared by client and server. |
| `RemoteApp.Client` | **Windows client.** Connects to a macOS or Windows server and shows the remote app as a *virtual application*: a normal, resizable Windows window with the remote app's title and icon, the remote menu bar reproduced as native menus, and the host window kept the same size as this one. Decodes with DXVA into D3D11 textures and presents them through a D3D11 video processor straight into a swap chain. |
| `RemoteApp.Server` | **Windows server.** Shares one Windows app with any client: DXGI Desktop Duplication capture cropped to the window (menus, dialogs and tooltips included), GPU color conversion with a D3D11 video processor, Media Foundation H.264 (NVIDIA/Intel/AMD hardware MFTs, software fallback), `SendInput` injection, Win32 menu bar, clipboard, Tailscale-only listeners. |

**Build and run** (needs the .NET 10 SDK; no Visual Studio required):

```bash
dotnet build Windows/RemoteApp.slnx
```

```bash
dotnet test Windows/RemoteApp.Protocol.Tests
```

```bash
dotnet run --project Windows/RemoteApp.Server
```

```bash
dotnet run --project Windows/RemoteApp.Client
```

**Client.** Enter the server's MagicDNS name or tailnet IP (or
`127.0.0.1:53124` for a server on the same PC), enter the pairing code once
(the server remembers the device), pick a running app or *Open an app…* to
launch an installed one. The app opens as its own window:

- **Resizing is two-way.** Resize the window and the host resizes its window
  to match (debounced, in points); if the app refuses a size (minimum sizes),
  the host's real geometry snaps this window back, so the picture is normally
  pixel-exact rather than letterboxed. DPI scaling on both ends is accounted
  for (`screen.scale` / `hostScale`).
- **Menus.** The host app's menu bar (read through Accessibility on a Mac,
  `GetMenu` on Windows) appears in the window's menu bar, with enabled/checked
  state and shortcut hints; the *Remote* menu manages the session, the shared
  clipboard (pull the host's, push this PC's, send files) and options.
- **Keyboard.** Keys go to the host first. Ctrl sends ⌘ by default (so Ctrl+C
  is ⌘C on a Mac), Alt sends ⌥; *Remote ▸ Ctrl Key Sends ⌘ Command* switches
  Ctrl to ⌃. Printable text is sent as text (layout-independent); arrows, Tab,
  Return, Escape, Delete, Home/End, Page Up/Down and F-keys as protocol key codes.
- **Mouse.** Click = tap (double-clicks by timing on the host), press-and-move
  = drag, right/middle click, wheel and horizontal wheel = scroll. Hovering over
  the video moves the host cursor (`move` events, at most ~60/s, only when the
  server lists the `move` feature in its hello), so tooltips, hover highlights
  and cursor shapes follow the pointer.
- Settings (device ID, recent hosts, pairing tokens) live in
  `%APPDATA%\RemoteAppClient\settings.json`. Received files land in
  `Downloads\RemoteApp`.

**Server.** Mirrors the Mac server: pairing code, remembered devices, listeners
bound to the Tailscale address (port 53123, IPv4 and IPv6) and loopback
(53124) only, a log (also in `%LOCALAPPDATA%\RemoteAppServer\RemoteAppServer.log`).
The TLS identity is generated on first launch into
`%LOCALAPPDATA%\RemoteAppServer\server.pfx` (random per-install passphrase
beside it) — nothing is bundled or committed. Host "points" are DIPs at the
monitor's DPI (`hostScale` = DPI / 96); the shared window is moved to the
top-left of the primary work area and resized to the client's size.

**Smoke test.** The client has an unattended mode used to verify a build against
a running server; it connects, pairs, selects (or launches) an app, types,
hovers (on loopback it checks that the local cursor followed), resizes, pulls
the clipboard, opens and closes the Remote menu while a menu update arrives,
then writes a report and a screenshot:

```bash
Windows/RemoteApp.Client/bin/Debug/net10.0-windows/RemoteAppClient.exe --smoke 127.0.0.1:53124 <code|-> Notepad report.txt shot.png 16
```

The server has `RemoteAppServer.exe --type-test "text"`, which types into its own
window through the same input injector.

**Video pipeline.** Both Windows apps share one Direct3D 11 device with Media
Foundation through a DXGI device manager, so pixels stay on the GPU:

- *Server:* the captured desktop region is a BGRA texture; a D3D11 video
  processor converts it to NV12 (BT.709) on the GPU; the NV12 frame is read
  back (1.5 bytes/px) and fed to the H.264 encoder MFT. Handing the texture
  itself to the encoder is possible (`REMOTEAPP_ENCODER_GPU_INPUT=1`) but
  measured several times slower on the NVIDIA MFT, so the readback is the default.
- *Client:* the H.264 decoder runs with DXVA and outputs D3D11 NV12 textures;
  a video processor converts, scales and letterboxes them straight into a
  flip-model swap chain on the window. No system-memory pixels, no Direct2D.
- Fallbacks: without video support on the adapter, or with
  `REMOTEAPP_FORCE_CPU_VIDEO=1`, the server converts on the CPU and the client
  decodes to system memory and draws with Direct2D.

Measured on one PC (RTX 2080 Ti, a 2560×1216 window repainting continuously,
the desktop composed at 32 Hz by a remote display adapter), per process as a
share of one core:

| Path | Frame rate | Capture → present | Server CPU | Client CPU |
|---|---|---|---|---|
| GPU (default) | 32 fps (display-limited) | ~10 ms | ~5% | ~5% |
| CPU (`REMOTEAPP_FORCE_CPU_VIDEO=1`) | 32 fps | ~24 ms | ~10% | ~60% |

**Windows limitations (prototype).**

- Capture is the monitor region under the window, so other windows overlapping
  it are visible (as on the Mac, "the host is in use"); the window is activated
  before input is injected and input is dropped if it cannot be brought to the
  front. Hover never activates it: the host cursor follows the remote pointer
  only while the shared window already is in the foreground. Only the primary
  graphics adapter's monitors can be captured.
- Classic Win32 menu bars only; apps that draw their own menus (WinUI, Electron,
  Office) report none — their menus still appear in the video.
- The mouse pointer is composited from the duplication API's pointer shape (a
  small readback of the region under it), but pointer-only updates are not
  streamed, so the remote cursor moves with the next content change.
- Typing a string into XAML/WinUI apps is paced (a few ms per character) because
  they read modifier state late; single keystrokes are unaffected. Characters
  the active keyboard layout cannot produce are sent as Unicode key events,
  which classic Win32 controls take exactly but the WinUI Notepad flattens to
  the base letter (é → e); use the shared clipboard for such text.
- TLS/TCP only (no QUIC), no Bonjour; connect by name or address.
- The capture→present latency in the status bar compares the host's frame
  timestamps with this PC's clock, so it shows "—" when the two clocks disagree
  (e.g. a Mac host a second off); receive→present timing is unaffected.

## Next steps

Remote access beyond the tailnet (WebRTC), HEVC, drag gestures on iOS,
certificate pinning and real account auth.

## License

MIT. See [LICENSE](LICENSE). Copyright (c) 2026 [dabbers](https://github.com/dabbers).
