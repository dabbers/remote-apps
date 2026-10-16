# RemoteApp — stream one macOS app window to an iPhone

Proof of concept: a macOS server captures a single app's main window with
ScreenCaptureKit, encodes it with hardware H.264, and streams it over QUIC to
an iOS client that shows it full screen and sends taps, scrolls and keystrokes
back.

**The server is reachable only over your Tailscale tailnet, never the open
internet.** It binds to the Mac's tailnet address and to loopback, and to
nothing else. Discovery and connection use the Tailscale MagicDNS name. See
[Network exposure](#network-exposure).

```
RemoteAppProtocol/   Swift package shared by both apps: messages, framing, transport
Server/              macOS SwiftUI app (RemoteAppServer)
Client/              iOS SwiftUI app (RemoteAppClient)
project.yml          xcodegen spec → RemoteApp.xcodeproj
Scripts/gen-cert.sh  regenerates the self-signed dev TLS identity (Server/Resources/server.p12)
```

## How it works

| Stage | Host (macOS) | Client (iOS) |
|---|---|---|
| Discovery | Reads its tailnet identity from the Tailscale local API (`tailscale status --json`); shows its MagicDNS name as the connect target. Bonjour stays as an optional LAN fallback | Connect by MagicDNS name / tailnet IP; Bonjour list is a fallback |
| Binding | Listeners scoped (`IP_BOUND_IF`) to the Tailscale interface (`utun*:53123`) and loopback (`lo0:53124`) only, never a public interface | — |
| Transport | TLS 1.3 over TCP over the WireGuard tunnel (default), with a bundled self-signed identity; QUIC selectable for LAN | Trusts any server certificate (fine: WireGuard already encrypts and authenticates the tailnet) |
| Auth | 6-digit pairing code shown in the server window; the device is then remembered with a persistent token | Enter the code once per device; later connects skip it |
| App list | `SCShareableContent` → running apps with windows; plus all installed apps enumerated for launching | "Running" list, and a + button to open any installed app |
| Launch | Opens an installed app via `NSWorkspace`, waits for its window, then streams it | Searchable installed-apps picker |
| Window sizing | Accessibility API moves/resizes the app's main window to the phone's point size, then reports what the app accepted | Letterboxes (`resizeAspect`) when the app clamped; pinch to zoom |
| Capture | `SCContentFilter(display:including:[app])` cropped with `sourceRect` to the main window: menus, sheets and popovers of that app are included, other apps are not | — |
| Encode/decode | `VTCompressionSession`, H.264 High, real-time, no B-frames, 60 fps, 12 Mbit/s | `AVSampleBufferDisplayLayer` (VideoToolbox decode) fed AVCC access units |
| Input | `CGEvent.postToPid` on the app's PID: the host cursor does not move | Tap = click (double-click by timing), one-finger pan = scroll, long press = right click, keyboard = key events, two-finger tap = toggle HUD |
| Menu bar | Read through the Accessibility API (`AXMenuBar` → `AXMenuBarItem` → `AXMenu` → `AXMenuItem`), with titles, enabled/checked state and shortcut hints; items performed with `AXPress` | Native iOS menu sheet (not streamed pixels) from the HUD's menu button; tap a leaf to run it |

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
server requires re-granting**. To avoid that, set `DEVELOPMENT_TEAM` and
`CODE_SIGN_STYLE: Automatic` in `project.yml` and regenerate.

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

## Next steps

Windows host (Windows.Graphics.Capture + Media Foundation + SendInput), remote
access (WebRTC or Tailscale), HEVC, drag gestures, certificate pinning and real
account auth.
