<div align="center">

<img src="RoDeck.ico" alt="RoDeck logo" width="96">

# RoDeck

### All your Roblox in one app: accounts, games, friends, avatar, marketplace and trades.

A local-only Roblox app for Windows 10 and 11, with every account you own a click away.<br>
Your sign-ins stay encrypted on your own PC and are only ever sent to Roblox.<br>
<sub>Formerly <b>AltDeck</b>. Not affiliated with or endorsed by Roblox Corporation.</sub>

[![Latest release](https://img.shields.io/github/v/release/d4anw/RoDeck?style=for-the-badge&color=ff7a1a&labelColor=111111&label=release&cacheSeconds=300)](https://github.com/d4anw/RoDeck/releases/latest)
[![Downloads](https://img.shields.io/github/downloads/d4anw/RoDeck/total?style=for-the-badge&color=2b2b2b&labelColor=111111&cacheSeconds=300)](https://github.com/d4anw/RoDeck/releases)
[![Windows 10 | 11](https://img.shields.io/badge/Windows-10%20%7C%2011-0078d4?style=for-the-badge&logo=windows&labelColor=111111)](#-install)

![.NET 8](https://img.shields.io/badge/.NET-8-512bd4?style=flat-square&logo=dotnet&logoColor=white)
![WPF](https://img.shields.io/badge/UI-WPF-ff7a1a?style=flat-square)
![C#](https://img.shields.io/badge/C%23-12-239120?style=flat-square)
![No telemetry](https://img.shields.io/badge/telemetry-none-ff7a1a?style=flat-square)

<br>

[**⬇ Download**](https://github.com/d4anw/RoDeck/releases/latest) &nbsp;·&nbsp;
[Quick start](#-quick-start) &nbsp;·&nbsp;
[Privacy](#-safety-and-privacy) &nbsp;·&nbsp;
[Changelog](#-changelog) &nbsp;·&nbsp;
[Build from source](#-build-from-source)

</div>

<br>

## 📑 Contents

| For everyone | For developers |
|---|---|
| [What is RoDeck?](#-what-is-rodeck) | [Build from source](#-build-from-source) |
| [Features](#-features) | [Project layout](#-project-layout) |
| [Install](#-install) | [Changelog](#-changelog) |
| [Quick start](#-quick-start) | [License](#️-license) |
| [Safety and privacy](#-safety-and-privacy) | |
| [Troubleshooting](#-troubleshooting) | |

<br>

## 💡 What is RoDeck?

RoDeck replaces the Roblox app on your PC, and it's built for people with more than one account.
Pick an account top right and everything (games, friends, avatar, marketplace, trades) is for that account;
switch with one click, no signing out.

- **Play:** game charts, search, favourites and a server finder (best ping, fewest or most players, where your friends are).
- **Friends:** who's online and where, join them, requests, search players, star your favourites.
- **Avatar editor:** wardrobe, body, colours, outfits, R6/R15, a 3D preview and accessory adjustment.
- **Marketplace:** browse and search items, try them on, get free items in one click, buy behind a confirm screen.
- **Trades:** live deals below RAP or value, a watchlist that tells you when an item hits your price, a trade calculator, every limited with its value and RAP history, inventory value per account, and your trades with a win/loss verdict.
- **Many accounts at once:** launch one or many into any game, run clients side by side, and RoDeck rejoins when one gets kicked or crashes.

Everything runs on your PC. There's no RoDeck account, no RoDeck server and no tracking.
RoDeck used to be called **AltDeck**; updating from AltDeck keeps all your accounts and settings.

<br>

## ✨ Features

<table>
<tr>
<td width="50%" valign="top">

### 🎮 Games
Charts as rows (open any chart for all of it), search, favourites (synced with your Roblox favourites) and
recently played. Each game has its own page with pictures, stats and a **server finder**: best ping, fewest or
most players, or where your friends are. Play closes your other client first.

</td>
<td valign="top">

### 👫 Friends
Online first, with where they are and a join button. Accept or decline requests in the list,
find players (by name, user ID or profile link), open profiles and star favourites.

</td>
</tr>
<tr>
<td valign="top">

### 🧍 Avatar editor
Wardrobe by category, body colours and scales, outfits, R6 / R15, a turnable 3D preview,
accessory adjustment and "copy this look to another account".

</td>
<td valign="top">

### 🛒 Marketplace
Categories, search, sort and price filters. **Try on** shows it on your avatar first. Every item has its own page
with similar items and more from its creator; limiteds show every seller. Free items in one click; Robux purchases
always show item, price, account and balance first, with an optional spending limit.

</td>
</tr>
<tr>
<td valign="top">

### 💎 Trades and deals
**Deals:** limiteds listed below their RAP or value, newest first, live; buy from a card on the page, or turn
on **insta-buy** (one click, no confirm, at your own risk). **Watchlist:** a price or a % off per item, and a
notification when it's listed at or below it. **Calculator**, **Limiteds** (values, demand, trend, RAP history),
**inventory value** per account, and **your trades** with both sides, value/RAP difference and accept / counter / decline.

</td>
<td valign="top">

### 🔔 Notifications
Optional Windows notifications for friends coming online, new trades, trades you sent that were accepted
or declined (pick which), friend requests and big deals. All off until you turn them on.

</td>
</tr>
</table>

<table>
<tr>
<td width="50%" valign="top">

### 🔐 Encrypted vault
Sign-ins are encrypted with **Windows DPAPI**, or with your own **master password**
(PBKDF2-SHA256, 600,000 rounds + AES-256-GCM). Portable password-locked `.rodeck` backups.

</td>
<td width="50%" valign="top">

### 👥 Accounts
Robux and Premium per account, live presence, groups and favourites. Add by browser login,
paste many cookies at once, or import an `AccountData.json` file.

</td>
</tr>
<tr>
<td valign="top">

### 🚀 Play anything
Game links, place IDs, private servers and `roblox.com/share` links, or **Browse games** to pick one from
Games. Launch presets with their own hotkeys, a server browser and "join someone".

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
Continue playing and your favourite games, a card for every client with a live preview, plus CPU, memory and
GPU dials. Show, minimize, tile, Play again and Anti-AFK (experimental).

</td>
</tr>
<tr>
<td valign="top">

### 🎚️ Performance
Two profiles: **Playing** (Play on Home, Games or Friends) and **Multi-launch** (many accounts), so your own
game can look good while alts stay light. Each has its presets (Max alts / Balanced / Roblox defaults / My preset),
fine-tune (FPS cap or unlimited, graphics flags, start muted, start tiny, CPU limit) and your own FastFlags.

</td>
<td valign="top">

### 🎨 Your look
Black theme with orange accents by default. Change the accent colour, density and background
image. Your Discord profile shows "Playing RoDeck" automatically.

</td>
</tr>
<tr>
<td valign="top">

### ⬆️ Updates itself
When a new version is out, RoDeck asks as soon as it opens and shows what's new.
One click downloads it with a progress bar and restarts into the new version.

</td>
<td valign="top">

### 🐞 Report a bug
One button opens a GitHub bug report with your versions filled in and copies your
Activity log to paste in. After every update, "What's new" shows what changed.

</td>
</tr>
</table>

<details>
<summary><b>All features, in detail</b></summary>
<br>

| Area | What you get |
|---|---|
| 🔐 **Vault** | `vault.dat` in `%AppData%\RoDeck`, encrypted with Windows DPAPI for your user. Optional master password (asked once at every start, can't be recovered). Export / import an `.rodeck` backup locked with a password you choose; it works on another PC. Import only adds. |
| 👥 **Accounts** | Sign in through an isolated browser window (the profile is deleted after the session cookie is captured), or paste cookies: one or many at once, one per line. Search, status and group filters, favourites, live presence. A periodic sign-in check (every 30 min by default) tells you when one expired. |
| 📥 **Import** | Import a list of accounts from an `AccountData.json` file (or a `.txt` with one cookie per line). Names, notes and groups in the file fill in what's empty; accounts you already have are refreshed, not doubled; passwords in the file are ignored. Encrypted files can't be read: save them unencrypted first. |
| 💰 **Robux & Premium** | Each account's Robux balance and Premium status in the list and its details, plus the total Robux across all your accounts. Refreshed with every sign-in check. |
| 🚀 **Play** | Game links, place IDs, private server links, `roblox.com/share?code=…&type=Server` links. Favourites. Launch presets: save the ticked accounts and target, then launch them in one click or with a global hotkey. Browse servers (fewest / most players / lowest ping). Join someone by username. |
| 🪟 **Launch queue** | "3 of 8 launched, next: Alt4 in 7 s" with Pause / Resume, Skip next and Cancel. Each account's sign-in is checked right before it launches. Launch delay and custom launch arguments (default and per account). |
| 🔁 **Recovery** | Auto-rejoin reads each client's Roblox log to catch disconnects; the first rejoin goes back to the same server. Auto-restart for crashes and windows "Not responding" for 30 s. Give-up limit, or keep trying every 5 minutes. Sign-ins are kept apart so clients don't sign each other out. |
| 🖥️ **Home** | Live previews refreshed every second (a minimized window keeps its last picture). GPU load and video memory from Windows' own counters. Windows menu: Tile windows, Minimize all, save / restore layouts. |
| 🎚️ **Performance** | Two profiles, each with its own presets, fine-tune and FastFlags: **Playing** for games started with Play on Home, Games or Friends, **Multi-launch** for Multi-launch, launch presets and rejoins. FPS cap (or unlimited), MSAA, texture quality, rendering mode, gray sky, no grass and lowest graphics, written as FastFlags just before each launch and put back afterwards, so normal Roblox stays untouched. Skipped under Bloxstrap / Fishstrap. A "Full quality" account is left alone. |
| 💎 **Trades** | Deals: Rolimons' live listings (checked every second once you've opened Deals) plus every limited's cheapest price, checked round after round; newest first, with filters. Buy from a card, or with insta-buy on, in one click. Watchlist: a price or a % off under the value per item, checked in the background (also from the tray) with a notification at your price. Item pages with a RAP / value chart and every seller. |
| ⬆️ **Updates** | When RoDeck opens it checks GitHub Releases. If a new version is out it asks whether to update, shows what's new, downloads with a progress bar, checks the file against GitHub's SHA-256 and restarts into it. "Not now" asks again next start; "Don't notify me about updates again" stops asking. Check by hand anytime under Settings → About. |
| ⏲️ **Automation** | Panic hotkey to stop every client, a stop timer (30m / 1h / 2h / 4h), hold-to-stop-all button, close to tray. |
| 📋 **Activity** | Timestamped launches, drops, rejoins and errors, with a "Problems only" filter and Copy. Also written to `%AppData%\RoDeck\logs`, kept for 14 days. |
| 🐞 **Report a bug** | One button (Activity page, or Settings → About) opens a GitHub issue with your RoDeck and Windows versions filled in, and copies your Activity log so you can paste it in after checking it for names. |
| 🆕 **What's new** | After an update, RoDeck shows that version's release notes once. |
| 💬 **Discord** | "Playing RoDeck" on your Discord profile with how many alts are running. Works out of the box with the Discord desktop app open; switch it off in Settings. |

</details>

<br>

## 📥 Install

1. Download **`RoDeck.exe`** from the [latest release](https://github.com/d4anw/RoDeck/releases/latest).
2. Run it. It's one file, needs no install and doesn't need .NET.
3. Windows SmartScreen may say *"Windows protected your PC"* because the exe isn't code-signed: click **More info → Run anyway**.
4. That's it. When a new version comes out, RoDeck **asks you to update** the next time you open it.

> [!NOTE]
> "Sign in with a browser" needs the **Microsoft Edge WebView2 Runtime**. It's already on Windows 11.

<br>

## 🏁 Quick start

1. **Close all Roblox windows**, then open RoDeck.
2. Go to **Multi-launch → Accounts → Add account** and sign in through the browser window. Have lots of accounts? Paste many cookies at once (one per line) or import an `AccountData.json` file.
3. Pick an account top right: Home, Games, Friends, Avatar, Marketplace and Trades are now for that account. Press **Play** on any game to join it.
4. Many accounts at once: go to **Multi-launch → Launch**, tick the accounts, paste a game link (or **Browse games**) and press **Launch**. If the second client closes the first one, press **Close Roblox & enable multi-instance** there.

> [!TIP]
> Keep the Discord desktop app open and your profile shows **Playing RoDeck** with how many alts are running.
> If it doesn't show, turn on **Share your detected activity** in Discord (Settings → Activity Privacy).

<br>

## 🔒 Safety and privacy

- **Your data stays on your PC.** Accounts, settings and logs live in `%AppData%\RoDeck`, never in the exe or in this repo.
- **Sign-ins are encrypted** with Windows DPAPI, or your master password if you set one.
- **Nothing is sent anywhere except Roblox**, plus your running-alt count to the Discord app on your PC (switch it off in Settings).
- **Updates come only from this repo's GitHub Releases** and are checked against GitHub's SHA-256 before they're installed.
- **Report a bug only opens GitHub when you click it.** Nothing is sent by itself, and you check the log before pasting it.
- **No telemetry**, no accounts, no RoDeck server.
- **Nothing is injected into Roblox.** Window sizing uses a hook that watches from outside, and graphics settings use Roblox's own FastFlags file, which is put back after launch.

> [!WARNING]
> Never share your `vault.dat`, an `.rodeck` backup or a `.ROBLOSECURITY` cookie. Anyone who has them can sign in as you.

<br>

## 🛠️ Troubleshooting

| Problem | Fix |
|---|---|
| The second account closes the first | Multi-launch → Launch shows whether multi-instance is ON. If it's OFF, press **Close Roblox & enable multi-instance**. |
| The login window is blank | Install the Microsoft Edge WebView2 Runtime. |
| "Windows protected your PC" | RoDeck isn't code-signed yet, so SmartScreen warns about it. Click **More info → Run anyway**. |
| Import says the file is encrypted | RoDeck can only read files saved without encryption. Turn encryption off in the program that saved the file, let it save again, then import that file. |
| Forgot the master password | It can't be recovered. Close RoDeck, delete `vault.dat` in `%AppData%\RoDeck` and add your accounts again (or import a backup). |
| Something else went wrong | Errors show at the bottom of the window and on **Multi-launch → Activity**. Press **Report a bug** there to tell us (needs a free GitHub account). |

<br>

## 🔨 Build from source

Needs the [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0).

| To | Do |
|---|---|
| Run it | Double-click `Run RoDeck.bat`, or run `dotnet run` in this folder |
| Make a shareable exe | Double-click `Build RoDeck.exe.bat`. It makes `publish\RoDeck.exe` (one file, ~60–70 MB, runs on any 64-bit Windows 10/11 PC without .NET) |

The version shown in the app, the exe's details and backups all come from `<Version>` in `RoDeck.csproj`.

**Releasing an update:** raise `<Version>`, build the exe, and publish a GitHub release tagged `v<version>` with the file attached as exactly `RoDeck.exe`, **and the same file a second time as `AltDeck.exe`** (installs from before the rename look for that name). Everyone's RoDeck offers it the next time they open it.

<br>

## 📁 Project layout

```
RoDeck/
├─ Views/            Home, Play, Accounts, Performance, Activity and Settings pages
├─ ViewModels/       MainViewModel (app state and commands)
├─ Services/         Vault, Launcher, RobloxApi, Updater, auto-rejoin, monitors, tray, hotkeys, Discord …
├─ Models/           Account, settings, presets, layouts
├─ Controls/         Dials, avatars, setting rows
├─ *.xaml            Main window, splash, login, update window, What's new and dialogs
└─ RoDeck.csproj    .NET 8 WPF project (version lives here)
```

<br>

## 📝 Changelog

<details open>
<summary><b>1.0.0: AltDeck is now RoDeck</b></summary>
<br>

AltDeck was an alt launcher; RoDeck is a whole Roblox app. Your accounts and settings come over by themselves.

- **New look:** a sidebar with Home, Games, Friends, Avatar, Marketplace, Trades, Multi-launch and FPS & FastFlags. Pick an account top right and every page is for that account. Back / forward with your mouse's side buttons.
- **Home:** continue playing, your favourite games, your friends, and a card for every running client.
- **Games:** charts as rows, search, favourites (synced with Roblox) and recently played, game pages with stats and a server finder.
- **Friends:** who's online and where, join them, requests, player search, profiles.
- **Avatar editor:** wardrobe, body, colours, outfits, R6 / R15, a 3D preview and copy a look to another account.
- **Marketplace:** browse, search and try on items; item pages with similar items, more from the creator and every seller for limiteds.
- **Trades:** live deals (newest first) with a buy card or insta-buy, a watchlist with notifications, a trade calculator, limiteds with values and RAP history, inventory value, your trades with accept / counter / decline, and Rolimons trade ads.
- **Notifications:** friends online, trades received, trades you sent being accepted or declined, friend requests and big deals.
- **FPS & FastFlags:** separate **Playing** and **Multi-launch** profiles, each with presets, fine-tune and your own FastFlags. Unlimited FPS really is unlimited, and a 240 cap really is 240.
- **Multi-launch:** Browse games under Where to?, a full-width Accounts page and a Client windows toolbar.
- Lots of fixes from a full code review: safer settings saves, steadier side buttons, a single game window isn't shrunk anymore, and more.

</details>

<details>
<summary><b>0.4.4</b></summary>
<br>

- **New update window:** when a new version is out, RoDeck asks as soon as it opens, shows what's new, downloads with a progress bar and restarts into the new version by itself.
- **Not now** asks again next time; tick **Don't notify me about updates again** to stop the question (Settings → General → Ask about updates). You can always check by hand under Settings → About.
- "What's new" no longer pops up at the same time as an update.

</details>

<details>
<summary><b>0.4.3</b></summary>
<br>

- **Add many accounts at once:** paste as many cookies as you like, one per line.
- **Import accounts from a file:** `AccountData.json`, or a `.txt` with one cookie per line.
- **Robux & Premium** for every account, and your total Robux on the Accounts page.
- **What's new:** after an update, RoDeck shows what changed.
- **Report a bug** button on the Activity page and in Settings → About.

</details>

<details>
<summary><b>0.4.2</b></summary>
<br>

- **Auto-update:** RoDeck downloads new releases from GitHub by itself and installs them on restart (Settings → General, and "Check for updates" under About).
- Discord status works out of the box: RoDeck has its own built-in Discord app, so there's no Application ID to fill in.
- Auto-rejoin also catches Roblox's "Disconnected" box (Error 277 and the like) by reading each client's log in `%LocalAppData%\Roblox\logs`. Teleports (285) are ignored; Error 273 is logged but not rejoined. A client that hasn't joined after 3 minutes counts as a failed join.
- The first rejoin goes back to the same server; later tries go to any server. Private servers keep their own link. After a shutdown (275), a 279 or a client that never got in, the first rejoin picks a different server.
- If your internet or Roblox is down, rejoins wait and start once it's back, without using up a try.
- Rejoins launch one at a time, at least 5 s apart, and the new window goes where the old one was.
- "Connection Failed (Error 279)" is caught too, and a join that hasn't been accepted after 90 s counts as failed.
- **Keep sign-ins apart** (on by default): the shared Roblox sign-in store is emptied right before each launch, so clients no longer sign each other out. Your own Roblox sign-in is backed up and put back afterwards.
- **Keep trying** (on by default): past the give-up limit, RoDeck tries every 5 minutes until it works.
- A client that loses its sign-in is relaunched right away with a fresh one.
- Client windows can be smaller than Roblox's 800×600 minimum (smallest 200×150). Tile windows lets you pick the screen.

</details>

<details>
<summary><b>0.4.1</b></summary>
<br>

- Only one RoDeck runs at a time; starting it again brings the running one to the front.
- Dark title bars on dialogs, the login window and the server browser in the dark theme.
- Account details: one Play button that turns into "Already playing".
- Home: Tile windows, Minimize all and layouts are in one Windows menu.
- Clearer launch errors ("signed out", "Roblox is limiting sign-ins").
- `Build RoDeck.exe.bat` makes a single shareable exe. Per-account stats removed.

</details>

<details>
<summary><b>0.4.0</b></summary>
<br>

- Launch queue panel with Pause / Resume, Skip next and Cancel.
- Launch presets with global hotkeys.
- Periodic sign-in check with a badge for expired accounts.
- Activity page and daily log files.
- Window layouts: save and restore where each account's window is.
- Encrypted `.rodeck` backups.

</details>

<details>
<summary><b>0.3.x</b></summary>
<br>

- **0.3.1:** app icon, start-up splash (can be skipped), English only.
- **0.3.0:** master password, close to tray, panic hotkey, stop timer, auto-restart, launch arguments, server browser, join someone.

</details>

<br>

## ⚖️ License

RoDeck is open source under the [MIT License](LICENSE).

<br>

## 📜 Disclaimer

RoDeck is an independent, open-source project. It is not affiliated with, endorsed by, sponsored by or connected to
Roblox Corporation. "Roblox" is a trademark of Roblox Corporation.
Use it only with accounts you own, and follow Roblox's Terms of Use.
Limited values come from Rolimons and are estimates; buying and trading always ask you first.
