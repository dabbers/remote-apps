# RemoteApp — stream one macOS app window to an iPhone

Proof of concept: a macOS server captures a single app's main window with
ScreenCaptureKit, encodes it with hardware H.264, and streams it over QUIC to
an iOS client that shows it full screen and sends taps, scrolls and keystrokes
back.

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
| Discovery | `NWListener` advertises `_remoteapp._udp` (QUIC) via Bonjour | `NWBrowser` lists Macs |
| Transport | QUIC (TLS 1.3, one bidirectional stream) with a bundled self-signed identity; TLS-over-TCP fallback | Trusts any server certificate (prototype) |
| Auth | 6-digit pairing code shown in the server window | Entered after choosing a Mac |
| App list | `SCShareableContent` → apps with normal on-screen windows, with icons | List with icons |
| Window sizing | Accessibility API moves/resizes the app's main window to the phone's point size, then reports what the app accepted | Letterboxes (`resizeAspect`) when the app clamped; pinch to zoom |
| Capture | `SCContentFilter(display:including:[app])` cropped with `sourceRect` to the main window: menus, sheets and popovers of that app are included, other apps are not | — |
| Encode/decode | `VTCompressionSession`, H.264 High, real-time, no B-frames, 60 fps, 12 Mbit/s | `AVSampleBufferDisplayLayer` (VideoToolbox decode) fed AVCC access units |
| Input | `CGEvent.postToPid` on the app's PID: the host cursor does not move | Tap = click (double-click by timing), one-finger pan = scroll, long press = right click, keyboard = key events |

Wire format: `u32 length | u8 kind | payload`; kind 1 is a JSON `ControlMessage`,
kind 2 is a binary video frame (`pts`, capture wall-clock time, keyframe flag,
AVCC NAL units). See `RemoteAppProtocol/Sources/RemoteAppProtocol/Messages.swift`.

## Requirements

- macOS 14+ host (tested on macOS 26), Xcode 16+ (tested with Xcode 27).
- iOS 17+ client or simulator.
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
   the permissions above and relaunch. The window shows a pairing code, the
   transport (QUIC by default) and a log; the log is also written to
   `~/Library/Logs/RemoteAppServer.log`.
2. Open the app you want to stream on the Mac (e.g. TextEdit with a document).
3. Build and run **RemoteAppClient** on an iPhone or a simulator on the same
   network (scheme `RemoteAppClient`). A simulator on the same Mac works.
4. In the client: pick the Mac, type the pairing code, pick the app.
5. The host window is resized to the phone's size, raised, and streamed. The HUD
   shows end-to-end latency (capture → decode enqueue, valid when host and
   client clocks agree, which is always true for a simulator) and fps. Tap the
   keyboard icon to type; `⌫` and Return are forwarded as key codes.

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
- **Security is prototype grade.** Self-signed cert, no pinning, pairing code
  only. `OpenDirectoryAuthenticator` in `Server/Sources/Auth.swift` verifies a
  local account password and can replace `PairingCodeAuthenticator`.
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
