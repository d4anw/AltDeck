# AltDeck v0.4.1
Multi-account Roblox launcher for your own accounts. Windows 10/11. Black theme with orange accents (change it in Settings).

## Build an AltDeck.exe to share
Double-click `Build AltDeck.exe.bat` (needs the .NET 8 SDK, same as `dotnet run`). It makes `publish\AltDeck.exe`:
one file, about 60-70 MB, that runs on any 64-bit Windows 10/11 PC without installing .NET. Send just that file.
Each person's accounts, settings and logs stay in their own `%AppData%\AltDeck`, never in the exe.
Windows SmartScreen may say "Windows protected your PC" because the exe isn't code-signed: More info > Run anyway.
The "Sign in with a browser" option needs the Microsoft Edge WebView2 Runtime (already on Windows 11).

## Run
Needs the .NET 8 SDK (and the WebView2 Runtime, preinstalled on Windows 11).
Close ALL Roblox windows, then double-click `Run AltDeck.bat` (or run `dotnet run` in this folder).

## What works
- Home: client cards, CPU / memory / GPU dials, Show / Minimize / Tile windows / Minimize all, Play again, Anti-AFK (experimental)
- Live previews (Home, Detailed view on): each card shows a picture of that client's window, refreshed every second.
  A minimized window can't be photographed, so its card keeps the last picture and says "Minimized · preview paused".
  With "Minimize on launch", a new client stays up for about 12 seconds first so its card gets a real picture of the game.
- GPU: each card and the Home dial show GPU load (the busiest GPU engine, like Task Manager) and the video memory the clients hold.
  Read from Windows' own GPU counters (Windows 10 1709+); on a PC without them the dial says N/A.
- Show on Discord (Settings): your Discord profile shows "Playing AltDeck" and how many alts are running.
  Create a free app at discord.com/developers/applications, name it AltDeck (that name is what Discord shows), copy its
  Application ID from General Information into Settings. For the picture on the card, upload the logo under Rich Presence > Art Assets
  with the name altdeck_logo. Keep the Discord desktop app open. In Discord, "Share your
  detected activity" (Settings > Activity Privacy) must be on.
- Play: pick accounts, paste a game link / place ID / private server link (including roblox.com/share?code=...&type=Server links), favourites, multi-instance status + fix button
- Accounts: search, status + group filters, favourites, per-account details, add by browser login or cookie, live presence
- Settings: theme, accent colour, density, background image, multi-instance, launch delay, auto-rejoin / auto-restart with give-up limit, close grace, tray, hotkey, stop timer, master password
- Performance: three profiles (Max alts / Balanced / Roblox defaults) plus fine-tune. FPS cap and the graphics flags (MSAA, texture quality,
  rendering mode, gray sky, no grass) are written to Roblox as FastFlags just before each launch; "Lowest graphics" sets Roblox's own quality level.
  "Start muted" sets Roblox's volume to 0 for the launch. All of it is put back ~90 seconds after the last launch, when AltDeck closes, and after a crash, so normal Roblox is untouched.
  Running clients: start tiny (with a normal title bar so you can move/close them), minimize on launch, and a CPU limit for clients you are not looking at. A "Full quality" account is left alone.
  Under Bloxstrap / Fishstrap and similar launchers the flags are skipped (they keep their own).
- Hold to stop all (hold the red sidebar button for 1 second)

## Not released yet
- Auto-rejoin now also catches Roblox's "Disconnected" box (Error 277 and the like). The client stays open on that box, so before,
  nothing noticed. AltDeck reads each client's own log in `%LocalAppData%\Roblox\logs` (matched to the account by its user ID),
  and when a disconnect isn't followed by a new join within 5 s, it closes that client and relaunches it.
  Teleports (reason 285) are ignored. Error 273 (the account joined from somewhere else) is logged but not rejoined.
  A client AltDeck started that hasn't joined any server after 3 minutes counts as a failed join too.
- The first rejoin goes back to the same server; later tries go to any server. Private servers keep their own link.
- If your internet or Roblox is down, rejoins wait (checking every 5 s) and start once it's back. Waiting doesn't use up a try.
- Rejoins are launched one at a time, at least 5 s (or your launch delay) apart, and also never at the same moment as the launch queue.
- A rejoined client's window goes back to where the old window was.
- Stop all also cancels rejoins that are still waiting.
- Keep sign-ins apart (Settings > Launching, on by default): every Roblox client shares one sign-in store
  (`%LocalAppData%\Roblox\LocalStorage\RobloxCookies.dat` and the account-switcher entries in `appStorage.json`). Each new client
  loaded the previous one's sign-in first, and with 6 clients one session got signed out seconds after launch: every request
  then got 401 Unauthorized, so the game's teleports failed with Error 769 and its anti-AFK rejoin stopped working.
  Now the store is emptied right before each launch. Your own Roblox sign-in is backed up to `roblox-signin-backup` in the
  data folder and put back ~90 s after the last launch, when AltDeck closes, or at the next start after a crash.
- Client windows can be smaller than Roblox's 800×600 minimum. AltDeck places windows without asking Roblox first
  (SWP_NOSENDCHANGING), so "Start clients tiny" (420×280), tiling and layouts get the exact size. When you move a small window,
  or drag its edges in past 800×600, Roblox still holds it to its minimum during the drag; when you let go AltDeck sets it to
  what the mouse asked for (smallest 200×150). Nothing is injected into Roblox: it's a WinEvent hook watching from outside.
- Tile windows ▾ picks the screen. Show all retries windows that didn't come back.
- "Connection Failed … No response from server (Error 279)" is caught too. Roblox shows it when it sends a client to a server
  that's gone (after "Roblox has shut down this game server for maintenance", Error 275, every rejoin went to the dead server
  and sat on this box). No disconnect line is written for it, so AltDeck reads "Failed to connect to server" instead.
  A join or teleport the server hasn't accepted after 90 s counts as failed too, whatever box Roblox shows.
- After a shutdown (275), a 279 or a client that never got in, the first rejoin doesn't go back to that same server.
- Keep trying after that (Settings > Launching, on by default): past the give-up limit, AltDeck tries once every 5 minutes
  until it works instead of stopping. Turn it off to get the old "give up and tell me" behaviour.
- A client that still loses its sign-in (5 or more 401s, a 401 on joining, or "Teleport failed because A Http error")
  is relaunched right away with a fresh sign-in, instead of sitting there until Roblox kicks it for being idle.

## New in 0.4.1
- Per-account stats removed.
- Only one AltDeck runs at a time. Starting it again brings the running one to the front (also from the tray).
- Dialogs, the login window and the server browser get dark title bars in the dark theme.
- Account details: one Play button that turns into a disabled "Already playing" (before, both could show at once), and the status line always readable.
- Home: Tile windows, Minimize all and the window layouts are in one "Windows" menu, so the header fits narrow windows; long titles are cut with "…".
- Clearer launch errors: "signed out" and "Roblox is limiting sign-ins" instead of a generic message.
- The version shown in the app, the exe's details and backups all come from `<Version>` in AltDeck.csproj.
- `Build AltDeck.exe.bat` makes a single shareable exe.

## New in 0.4.0
- Launch queue: launching several accounts shows a panel in the corner ("3 of 8 launched, next: Alt4 in 7 s") with Pause / Resume,
  Skip next, Cancel, and a skip button on each waiting row. Launching more while it runs adds them to the end. "Stop all" also cancels it.
  Each account's sign-in is checked just before it launches (unless it was checked in the last 10 minutes); a signed-out one is skipped.
- Launch presets (Play page, step 3): "+ Save current picks" saves the ticked accounts and the target. Click a preset to launch it.
  Right-click for: load without launching, replace with current picks, set a hotkey (works from any app, also from the tray), rename, delete.
- Sign-in check (Settings > Automation, every 30 min by default, 0 = off): quietly checks every account and tells you when one expired.
  A red badge on Accounts shows how many; click it to see them. Rate limits and outages no longer count as "signed out".
- Activity page (sidebar): timestamped launches, drops, rejoins, give-ups, frozen restarts, sign-in problems and errors.
  "Problems only" filter, Copy (oldest first, for bug reports), Open log folder. Also written to `logs\altdeck-YYYY-MM-DD.log`
  in the data folder, kept for 14 days.
- Window layouts (Home > Windows): save where each account's client window is, restore it later. Windows are matched by account,
  so it works after a relaunch. Minimized windows can't be measured, so show them before saving.
- Backup (Settings > Data): Export writes every account (with its sign-in), favourites, presets and layouts into one `.altdeck` file
  locked with a password you choose (PBKDF2-SHA256 600,000 rounds + AES-256-GCM). Unlike `vault.dat` it isn't tied to this Windows user,
  so it works on another PC or after a reinstall. Import only adds: accounts you already have are kept, a signed-out one gets the backup's sign-in.
- "Protect each client's sign-in" is gone: it never did anything, and each client already gets its own sign-in ticket at launch.

## New in 0.3.1
- App icon (`AltDeck.ico`): shown on the taskbar, in the exe, and in Alt+Tab.
- Start-up splash animation (logo, name, progress line). Turn it off with Settings > General > Skip splash screen.
- The Language setting is gone (AltDeck is English only).

## New in 0.3.0
- Master password (Settings > Security): locks the vault with your own password (PBKDF2-SHA256, 600,000 rounds, + AES-256-GCM). Optional and off by default.
  Once on, AltDeck asks for it once at every start. It cannot be recovered: if you forget it, delete `vault.dat` in the data folder and add your accounts again.
  "Turn off" goes back to Windows-only (DPAPI) protection.
- Close to tray (Settings > General): the window hides, clients and auto-rejoin keep working. Click the tray icon to open it, right-click for Stop all / Quit.
- Panic hotkey (Settings > Automation): click the box and press the keys (e.g. Ctrl+Shift+F12). Stops every client from any app. Backspace turns it off.
- Stop timer (Settings > Automation): 30m / 1h / 2h / 4h, then every Roblox client is closed. AltDeck and the PC stay on. Not remembered across restarts.
- Auto-restart (Settings > Launching): relaunches a client whose process ended, or whose window has been "Not responding" for 30 s, into the same game.
  Shares the give-up limit with auto-rejoin. Per-account "Bring it back if it drops" still applies. (A client that exits is brought back by either switch.)
- Launch arguments (Settings > Launching, and per account under More details): extra words after the launch link. When any are set, AltDeck starts
  the launcher registered for `roblox-player:` links (Roblox's, or Bloxstrap and the like) directly instead of opening the link.
- Browse servers (Play page): lists the open public servers of the game in the target box (fewest players / most players / lowest ping). Double-click one to use it.
- Join someone (Play page): enter a username; AltDeck checks where they are using one of your signed-in accounts and fills in that server.
  Roblox only shows this to accounts the player allows (friends, or everyone if they let anyone join).

## Troubleshooting
- Second account closes the first: the Play page shows whether multi-instance is ON. If OFF, press "Close Roblox & enable multi-instance".
- Blank login window: install the Microsoft Edge WebView2 Runtime.
- Errors show in a message box and on the Activity page. For a bug report, use Copy on the Activity page (or send the day's log file).
