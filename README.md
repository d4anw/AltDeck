<div align="center">

<img src="AltDeck.ico" alt="AltDeck logo" width="96">

# AltDeck

### All your Roblox accounts in one launcher, signed in and ready to play.

A local-only multi-account launcher for Windows 10 and 11.<br>
Your sign-ins stay encrypted on your own PC and are only ever sent to Roblox.

[![Latest release](https://img.shields.io/github/v/release/d4anw/AltDeck?style=for-the-badge&color=ff7a1a&labelColor=111111&label=release)](https://github.com/d4anw/AltDeck/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/d4anw/AltDeck/total?style=for-the-badge&color=2b2b2b&labelColor=111111)](https://github.com/d4anw/AltDeck/releases)
[![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4?style=for-the-badge&logo=windows&labelColor=111111)](#-install)

![.NET 8](https://img.shields.io/badge/.NET-8-512bd4?style=flat-square&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-ff7a1a?style=flat-square)
![C#](https://img.shields.io/badge/C%23-12-239120?style=flat-square)
![No telemetry](https://img.shields.io/badge/telemetry-none-ff7a1a?style=flat-square)

<br>

[**⬇ Download**](https://github.com/d4anw/AltDeck/releases/latest) &nbsp;·&nbsp;
[Quick start](#-quick-start) &nbsp;·&nbsp;
[Privacy](#-safety-and-privacy) &nbsp;·&nbsp;
[Changelog](#-changelog) &nbsp;·&nbsp;
[Build from source](#-build-from-source)

</div>

<br>

## 📑 Contents

| For everyone | For developers |
|---|---|
| [What is AltDeck?](#-what-is-altdeck) | [Build from source](#-build-from-source) |
| [Features](#-features) | [Project layout](#-project-layout) |
| [Install](#-install) | [Changelog](#-changelog) |
| [Quick start](#-quick-start) | |
| [Safety and privacy](#-safety-and-privacy) | |
| [Troubleshooting](#-troubleshooting) | |

<br>

## 💡 What is AltDeck?

Playing on more than one Roblox account normally means signing out, signing back in, finding the game again,
and only having one Roblox window open at a time. AltDeck takes care of that.

- **Add each account once**, by signing in through a browser window or by pasting a cookie.
- **Launch one account or many** into any game, private server or share link.
- **Run several Roblox clients side by side**, with live previews and CPU / memory / GPU dials.
- **Get kicked or crash? AltDeck rejoins for you**, back into the same server when it can.

Everything runs on your PC. There's no AltDeck account, no AltDeck server and no tracking.

<br>

## ✨ Features

<table>
<tr>
<td width="50%" valign="top">

### 🔐 Encrypted vault
Sign-ins are encrypted with **Windows DPAPI**, or with your own **master password**
(PBKDF2-SHA256, 600,000 rounds + AES-256-GCM). Portable password-locked `.altdeck` backups.

</td>
<td width="50%" valign="top">

### 👥 Accounts
Search, status and group filters, favourites, per-account details and live presence.
Add by browser login or by cookie.

</td>
</tr>
<tr>
<td valign="top">

### 🚀 Play anything
Game links, place IDs, private servers and `roblox.com/share` links. Launch presets with
their own hotkeys, a server browser and "join someone".

</td>
<td valign="top">

### 🪟 Multi-instance
Run many clients at once. A launch queue with pause, skip and cancel. Window layouts
you can save and restore, and windows smaller than Roblox's 800×600 minimum.

</td>
</tr>
<tr>
<td valign="top">

### 🔁 Auto-rejoin and restart
Catches disconnects (Error 277, 279 and others), crashes and frozen windows, then relaunches
into the same game. Waits for your internet to come back first.

</td>
<td valign="top">

### 🖥️ Home
A card for every client with a live preview, plus CPU, memory and GPU dials. Show, minimize,
tile, Play again and Anti-AFK (experimental).

</td>
</tr>
<tr>
<td valign="top">

### 🎚️ Performance
Three profiles (Max alts / Balanced / Roblox defaults) plus fine-tune: FPS cap, graphics flags,
start muted, start tiny and a CPU limit for background clients.

</td>
<td valign="top">

### 🎨 Your look
Black theme with orange accents by default. Change the accent colour, density and background
image. Your Discord profile shows "Playing AltDeck" automatically.

</td>
</tr>
</table>

<details>
<summary><b>All features, in detail</b></summary>
<br>

| Area | What you get |
|---|---|
| 🔐 **Vault** | `vault.dat` in `%AppData%\AltDeck`, encrypted with Windows DPAPI for your user. Optional master password (asked once at every start, can't be recovered). Export / import an `.altdeck` backup locked with a password you choose; it works on another PC. Import only adds. |
| 👥 **Accounts** | Sign in through an isolated browser window (the profile is deleted after the session cookie is captured), or paste a cookie. Search, status and group filters, favourites, live presence. A periodic sign-in check (every 30 min by default) tells you when one expired. |
| 🚀 **Play** | Game links, place IDs, private server links, `roblox.com/share?code=…&type=Server` links. Favourites. Launch presets: save the ticked accounts and target, then launch them in one click or with a global hotkey. Browse servers (fewest / most players / lowest ping). Join someone by username. |
| 🪟 **Launch queue** | "3 of 8 launched, next: Alt4 in 7 s" with Pause / Resume, Skip next and Cancel. Each account's sign-in is checked right before it launches. Launch delay and custom launch arguments (default and per account). |
| 🔁 **Recovery** | Auto-rejoin reads each client's Roblox log to catch disconnects; the first rejoin goes back to the same server. Auto-restart for crashes and windows "Not responding" for 30 s. Give-up limit, or keep trying every 5 minutes. Sign-ins are kept apart so clients don't sign each other out. |
| 🖥️ **Home** | Live previews refreshed every second (a minimized window keeps its last picture). GPU load and video memory from Windows' own counters. Windows menu: Tile windows, Minimize all, save / restore layouts. |
| 🎚️ **Performance** | FPS cap, MSAA, texture quality, rendering mode, gray sky, no grass and lowest graphics, written as FastFlags just before each launch and put back afterwards, so normal Roblox stays untouched. Skipped under Bloxstrap / Fishstrap. A "Full quality" account is left alone. |
| ⬆️ **Auto-update** | Checks GitHub Releases at start and every 6 hours, downloads the new `AltDeck.exe` in the background, checks it against GitHub's SHA-256, then asks to restart. Choose later and it installs when you close AltDeck. Switch it off or check by hand in Settings. |
| ⏲️ **Automation** | Panic hotkey to stop every client, a stop timer (30m / 1h / 2h / 4h), hold-to-stop-all button, close to tray. |
| 📋 **Activity** | Timestamped launches, drops, rejoins and errors, with a "Problems only" filter and Copy for bug reports. Also written to `%AppData%\AltDeck\logs`, kept for 14 days. |
| 💬 **Discord** | "Playing AltDeck" on your Discord profile with how many alts are running. Works out of the box with the Discord desktop app open; switch it off in Settings. |

</details>

<br>

## 📥 Install

1. Download **`AltDeck.exe`** from the [latest release](https://github.com/d4anw/AltDeck/releases/latest).
2. Run it. It's one file, needs no install and doesn't need .NET.
3. Windows SmartScreen may say *"Windows protected your PC"* because the exe isn't code-signed: click **More info → Run anyway**.
4. That's it: AltDeck **updates itself** whenever a new release comes out.

> [!NOTE]
> "Sign in with a browser" needs the **Microsoft Edge WebView2 Runtime**. It's already on Windows 11.

<br>

## 🏁 Quick start

1. **Close all Roblox windows**, then open AltDeck.
2. Go to **Accounts → Add** and sign in through the browser window (or paste a cookie).
3. Go to **Play**, tick the accounts you want and paste a game link or place ID.
4. Press **Launch**. If the second client closes the first one, press **Close Roblox & enable multi-instance** on the Play page.

> [!TIP]
> Keep the Discord desktop app open and your profile shows **Playing AltDeck** with how many alts are running.
> If it doesn't show, turn on **Share your detected activity** in Discord (Settings → Activity Privacy).

<br>

## 🔒 Safety and privacy

- **Your data stays on your PC.** Accounts, settings and logs live in `%AppData%\AltDeck`, never in the exe or in this repo.
- **Sign-ins are encrypted** with Windows DPAPI, or your master password if you set one.
- **Nothing is sent anywhere except Roblox**, plus your running-alt count to the Discord app on your PC (switch it off in Settings).
- **Updates come only from this repo's GitHub Releases** and are checked against GitHub's SHA-256 before they're installed.
- **No telemetry**, no accounts, no AltDeck server.
- **Nothing is injected into Roblox.** Window sizing uses a hook that watches from outside, and graphics settings use Roblox's own FastFlags file, which is put back after launch.

> [!WARNING]
> Never share your `vault.dat`, an `.altdeck` backup or a `.ROBLOSECURITY` cookie. Anyone who has them can sign in as you.

<br>

## 🛠️ Troubleshooting

| Problem | Fix |
|---|---|
| The second account closes the first | The Play page shows whether multi-instance is ON. If it's OFF, press **Close Roblox & enable multi-instance**. |
| The login window is blank | Install the Microsoft Edge WebView2 Runtime. |
| Forgot the master password | It can't be recovered. Close AltDeck, delete `vault.dat` in `%AppData%\AltDeck` and add your accounts again (or import a backup). |
| Something else went wrong | Errors show in a message box and on the **Activity** page. Use **Copy** there for a bug report. |

<br>

## 🔨 Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

| To | Do |
|---|---|
| Run it | Double-click `Run AltDeck.bat`, or run `dotnet run` in this folder |
| Make a shareable exe | Double-click `Build AltDeck.exe.bat`. It makes `publish\AltDeck.exe` (one file, ~60–70 MB, runs on any 64-bit Windows 10/11 PC without .NET) |

The version shown in the app, the exe's details and backups all come from `<Version>` in `AltDeck.csproj`.

**Releasing an update:** raise `<Version>`, build the exe, and publish a GitHub release tagged `v<version>` with the file attached as exactly `AltDeck.exe`. Everyone's AltDeck picks it up within 6 hours (or at their next start).

<br>

## 📁 Project layout

```
AltDeck/
├─ Views/            Home, Play, Accounts, Performance, Activity and Settings pages
├─ ViewModels/       MainViewModel (app state and commands)
├─ Services/         Vault, Launcher, RobloxApi, auto-rejoin, monitors, tray, hotkeys, Discord …
├─ Models/           Account, settings, presets, layouts
├─ Controls/         Dials, avatars, setting rows
├─ *.xaml            Main window, splash, login and dialogs
└─ AltDeck.csproj    .NET 8 WPF project (version lives here)
```

<br>

## 📝 Changelog

<details open>
<summary><b>0.4.2</b></summary>
<br>

- **Auto-update:** AltDeck downloads new releases from GitHub by itself and installs them on restart (Settings → General, and "Check for updates" under About).
- Discord status works out of the box: AltDeck has its own built-in Discord app, so there's no Application ID to fill in.
- Auto-rejoin also catches Roblox's "Disconnected" box (Error 277 and the like) by reading each client's log in `%LocalAppData%\Roblox\logs`. Teleports (285) are ignored; Error 273 is logged but not rejoined. A client that hasn't joined after 3 minutes counts as a failed join.
- The first rejoin goes back to the same server; later tries go to any server. Private servers keep their own link. After a shutdown (275), a 279 or a client that never got in, the first rejoin picks a different server.
- If your internet or Roblox is down, rejoins wait and start once it's back, without using up a try.
- Rejoins launch one at a time, at least 5 s apart, and the new window goes where the old one was.
- "Connection Failed (Error 279)" is caught too, and a join that hasn't been accepted after 90 s counts as failed.
- **Keep sign-ins apart** (on by default): the shared Roblox sign-in store is emptied right before each launch, so clients no longer sign each other out. Your own Roblox sign-in is backed up and put back afterwards.
- **Keep trying** (on by default): past the give-up limit, AltDeck tries every 5 minutes until it works.
- A client that loses its sign-in is relaunched right away with a fresh one.
- Client windows can be smaller than Roblox's 800×600 minimum (smallest 200×150). Tile windows lets you pick the screen.

</details>

<details>
<summary><b>0.4.1</b></summary>
<br>

- Only one AltDeck runs at a time; starting it again brings the running one to the front.
- Dark title bars on dialogs, the login window and the server browser in the dark theme.
- Account details: one Play button that turns into "Already playing".
- Home: Tile windows, Minimize all and layouts are in one Windows menu.
- Clearer launch errors ("signed out", "Roblox is limiting sign-ins").
- `Build AltDeck.exe.bat` makes a single shareable exe. Per-account stats removed.

</details>

<details>
<summary><b>0.4.0</b></summary>
<br>

- Launch queue panel with Pause / Resume, Skip next and Cancel.
- Launch presets with global hotkeys.
- Periodic sign-in check with a badge for expired accounts.
- Activity page and daily log files.
- Window layouts: save and restore where each account's window is.
- Encrypted `.altdeck` backups.

</details>

<details>
<summary><b>0.3.x</b></summary>
<br>

- **0.3.1:** app icon, start-up splash (can be skipped), English only.
- **0.3.0:** master password, close to tray, panic hotkey, stop timer, auto-restart, launch arguments, server browser, join someone.

</details>

<br>

## 📜 Disclaimer

AltDeck is a fan-made tool and is not affiliated with, endorsed by or connected to Roblox Corporation.
Use it only with accounts you own, and follow Roblox's Terms of Use.
