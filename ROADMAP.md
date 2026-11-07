# AltDeck roadmap

From multi-account launcher to a full Roblox app: your alts, games, avatar, friends, marketplace and trades in one place.
Work happens on the `next` branch; `main` only gets a phase once it's finished, tested and released.

**Sidebar:** 🏠 Home · 🎮 Games · 🧍 Avatar · 🛒 Marketplace · 💎 Trades · 👥 Friends · 🚜 Multi-launch · ⚡ FPS & FastFlags · ⚙️ Settings
**Top right:** the current account's avatar, username and Robux. Click to switch accounts.

---

## Phase 0: The new app shell → 0.5
- [x] New sidebar with all the pages above
- [x] Account switcher top right (avatar, username, Robux); switching changes the account Home, Games, Avatar, Marketplace, Trades and Friends show
- [x] Shared Roblox connection layer: sign-in, security tokens, retries, rate limits
- [x] Picture cache for game icons, avatars and items
- [x] Shared loading, empty and error states for every page

## Phase 1: Home + Multi-launch + FPS → 0.5
- [x] 🏠 New Home: friends online (and what they're playing), continue playing, recommended games, your Robux
- [x] 🚜 Multi-launch page with everything AltDeck does today, in tabs:
  - [x] Running: client cards, live previews, CPU / RAM / GPU dials, Tile / Minimize / Layouts
  - [x] Launch: pick accounts and a game, presets, the launch queue
  - [x] Accounts: add, paste many cookies, import, Robux, groups
  - [x] Farming: auto-rejoin, auto-restart, anti-AFK, keep trying, stop timer
  - [x] Activity: the log
- [x] ⚡ FPS & FastFlags page
  - [x] Uncapped FPS ("Unlimited" next to the FPS cap)
  - [x] FastFlags editor: see, add, edit, remove, presets; shows which flags Roblox still respects
  - [x] Works for every launch (single or multi); normal Roblox is put back afterwards

## Phase 2: 🎮 Games → 0.6
- [ ] Charts: Top Trending, Popular, Top Earning, Up-and-Coming… (icon, name, players online, like %)
- [ ] Search games
- [ ] Game page: pictures, description, creator, players, visits, likes, favourite
- [ ] Server finder: sort by best ping, fewest / most players, servers with your friends
- [ ] "Join best server" with one account or several alts
- [ ] Favourites and recently played

## Phase 3: 👥 Friends → 0.7
- [ ] Friend list: online / in game / offline, and which game
- [ ] Join a friend, with one or more alts
- [ ] Friend requests: accept, decline, send
- [ ] Player search and profiles

## Phase 4: 🧍 Avatar editor → 0.8
- [ ] Avatar picture and what it's wearing
- [ ] Inventory by category, with search
- [ ] Wear / take off items (Roblox re-renders the picture)
- [ ] Body colours, scales, R6 / R15
- [ ] Outfits: wear, save, rename, delete
- [ ] Copy a look to another alt

## Phase 5: 🛒 Marketplace → 0.9
- [ ] Browse avatar items: categories, search, sort, price filter, free items
- [ ] Item page with "Try on" for the current account
- [ ] Get free items in one click
- [ ] Buy with Robux, always behind a confirm screen (item, price, account) and an optional spending limit

## Phase 6: 💎 Catalog, trades & values → 0.10
- [ ] Limiteds catalog: RAP, recent sales, owners
- [ ] Values and demand from a public values source, on items, inventories and trades
- [ ] Total inventory value per account
- [ ] Trades: inbound, outbound, completed; value per side and a win / loss indicator
- [ ] Send, accept, decline and counter trades, always with a confirm screen (trading needs Premium)

## Phase 7: Polish → 1.0
- [ ] Notifications: friend online, trade received, friend request
- [ ] Screenshots and README rework for the new app
- [ ] 1.0: AltDeck replaces the Roblox app

## Every phase
- [ ] Check the Roblox web services it needs still work, before building
- [ ] Test with real accounts
- [ ] Release notes, changelog, README; merge `next` into `main`, then publish the release

**Not planned:** chat and messages (Roblox doesn't allow other apps to use them).
**Heads-up:** trades and buying involve real value, so they get extra confirm screens and are tested on cheap items first. Values come from community sites and are estimates. Roblox restricts FastFlags more and more, so uncapped FPS depends on what still works.
