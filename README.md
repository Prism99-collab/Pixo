<div align="center">

<img src="docs/logo.png" alt="Pixo" width="128" />

# Pixo

A notch for Windows: live activity at the top of the screen, a control center under it, and a search that gets things done.

[![Latest release](https://img.shields.io/github/v/release/Prism99-collab/Pixo?label=download&color=0A84FF)](https://github.com/Prism99-collab/Pixo/releases/latest)
[![Platform](https://img.shields.io/badge/Windows-10%20%7C%2011-0078D4?logo=windows)](#requirements)
[![License: MIT](https://img.shields.io/badge/license-MIT-30D158)](LICENSE)

</div>

## Install

Download the installer from the [latest release](https://github.com/Prism99-collab/Pixo/releases/latest):
`x64` for Intel and AMD PCs, `arm64` for Snapdragon PCs. If you're not sure, use `x64`. No admin rights needed.

The app isn't code-signed yet, so Windows may warn about an unknown publisher: choose **More info → Run anyway**.
Checksums are in `SHA256SUMS.txt`.

To uninstall: **Settings → Apps → Installed apps → Pixo**.

## How it works

The notch is a thin bar at the top of the screen. It grows when something happens (music, a call, a timer, a
download, a notification) and stays out of the way the rest of the time: clicks pass through it, it slims down over
maximized windows, and it hides in full screen and in games.

Push the pointer against the top edge, press <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>N</kbd> or click the tray icon to
open the control center, then swipe between its pages:

| Page | |
|---|---|
| Home | Now playing, Wi-Fi, Bluetooth, focus, volume and app mixer, brightness |
| Flow | What you're working on, your chats and projects, and two weeks of notifications |
| Search | The search and the AI chat, side by side |
| Calls | Mute, camera, share and leave for Discord, Teams, Zoom, Meet and more; a mic check between calls |
| Hardware | CPU and every core, graphics, memory, network, ping, battery, the busiest apps and the drives |
| AI usage | Your Claude Code and ChatGPT limits, how fast you're using them, and what Claude Code cost |
| Weather | Now, the next hours and the week for a place you pick |
| Tools | Timer, snip, colour picker, keep awake and quick shortcuts |
| Terminal | A PowerShell in the notch that keeps your folder and last commands |
| Converter | Units and currencies, both ways at once, rates kept for offline |
| Clipboard & Files | Shelf, clipboard history, recent downloads and screenshots |
| Colors | The notch's colour studio |
| Settings | Every option |

Pick which pages show and in what order in **Preferences → Pages**.

### Search

Press <kbd>Alt</kbd>+<kbd>Space</kbd>, click the magnifier, or just start typing while the notch is open.

It looks through your apps, settings, open windows, clipboard history and the files on every drive of your PC.
Apps come first, then everything else. It also remembers what you pick, so the thing you want tends to be on top
next time. Right-click any result for more options, like opening the folder it's in or copying its path.

You can narrow files down in plain words: `pdf invoice`, `pictures today`, `videos last month`, `big videos`.

It understands short instructions too:

```
remind me to call mum at 6        timer 25            focus for 45 min
volume 30 and dark mode           turn off bluetooth  shut down in an hour
15% of 80                         5 km in miles       time in tokyo
```

Each instruction shows what it will do, and nothing happens until you press Enter. Everything runs on your PC,
except web searches and what you ask the AI chat.

If you keep your desktop icons hidden, the search shows your desktop instead, so nothing gets lost.
You can change the shortcut and what the search looks through in **Preferences → Search**.

### AI chat

Press <kbd>Tab</kbd> in the search, or pick **Ask AI**.

**Setting it up (two minutes, free):**

1. The first time you open the chat, it walks you through a free key from Google AI Studio: sign in, click
   **Create API key**, copy it, paste it in the chat.
2. Optional, but worth it: right after, it offers a second free key from **Groq** (console.groq.com/keys →
   **Create API Key** → paste). Groq answers in under a second and takes over whenever Google is busy or used up.
   You can also add it later with `/groq` or in **Settings → AI chat**.

Both keys stay on your PC, encrypted for your Windows account. The chat's home page shows what's left for today.

Ask it anything. It searches the web when it needs to, finds and reads your files, makes Word, Excel and PDF files and
pictures, and does things on your PC and inside your apps: `call Sam on Discord`, `close discord and set volume to 30`, `what's using my CPU?`, `make a table of my
calls today`. It also knows what the notch knows, like your reminders, notifications and headset battery.

Anything risky waits for your OK first, and you can turn off web use or PC actions from its ⋯ menu.

### Also

- **Shelf**: drop files, pictures or text on the notch and drag them out later.
- **Clipboard history** with pins and pictures.
- **Game mode**: a small live FPS counter.
- **Themes** (Mono, the default, is plain white on black) and a colour editor.
- **Your own notch**: a notch, a capsule or a thin line, at the size and place you like, with the clock, date,
  battery, weather or CPU beside the face. The open panel comes in four sizes.
- **Hide from screen sharing**: Pixo stays on your screen but out of calls, recordings and screenshots.
- **Notices from your scripts**: `Pixo.exe --notify "Build done" --icon ok`.
- **Companion**: an optional little character that sits on a swing under the notch.
- **Touch and pen** gestures.

## Requirements

Windows 10 (version 2004 or later) or Windows 11, on x64 or ARM64.

## Troubleshooting

- **Can't see it:** push the pointer against the top edge or press <kbd>Win</kbd>+<kbd>Alt</kbd>+<kbd>N</kbd>.
- **Something's wrong:** attach `%APPDATA%\Pixo\app.log` and `error.log` to an
  [issue](https://github.com/Prism99-collab/Pixo/issues).
- **Reset settings:** exit from the tray icon, delete `%APPDATA%\Pixo\settings.json`, then open it again.

## Building

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download) and [Inno Setup 6](https://jrsoftware.org/isinfo.php).

```powershell
./scripts/build.ps1                     # installers in ./artifacts
dotnet run --project src/DynamicNotch   # run for development
```

Pushing a `v*` tag builds the installers and publishes a release.

## Privacy

No account, no ads, no telemetry. See the [privacy policy](PRIVACY.md).

## License

The code is under the [MIT license](LICENSE). The name, logo and companion are covered by
[TRADEMARKS.md](TRADEMARKS.md). Open-source components are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md).
Other product names belong to their owners.
