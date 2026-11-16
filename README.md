# GAR Revival

A community patch that brings **Guns and Robots** back after the official
servers shut down: the garage, the store, robot building and the offline Test
Run all work again. It runs entirely on your PC; no game files are
redistributed. The patcher modifies your own install and keeps the originals so
it can be undone.


## Status

| Works | Not yet |
| --- | --- |
| Launching past the "servers are being shut down" notice | Original stats for engines, batteries, heads, weapon cases and devices (lost; designed, see below) |
| Garage, UI, portraits | |
| Store with all 190 parts and 81 head/hat/eyewear/device modules, official names and descriptions | |
| Real stats for 28 chassis, 21 bodies and 26 barrels (the 2017 live balance, see below) | |
| Buying and selling, credits, sparks exchange | |
| A starter robot, robot builder, module editor | |
| **Test Run** in the training ground | |
| **Bot matches**: Team Deathmatch, Capture the Batteries, Base Destruction | |
| Battle rewards (credits, XP, levels, lifetime stats, loot drops) | |
| Battle wear and repairs (manual or auto repair); consumable boosts in battle | |
| Ammo weapons: ammo types, buying, loading, auto resupply, use and pickups in battle | |
| Part upgrades and tech levels, timed boosts, VIP, inventory slots, renting | |
| Paint colours, crates and keys, daily login rewards | |
| Part skins: 12 camo, stripe and metal patterns drawn by the mod (SKINS button or F8) | |
| Daily missions, achievements, ladder rating and league badge | |
| Ladder table (Stats > Ladder): your league among the players in your session, and the master server's ladder | |
| Profile saved between sessions | |
| Hosting and joining sessions (Steam or direct), shared garage chat | |
| Friends, whispers, squads and guilds among players in a session | |
| Guild benefits: unlock, build, hurry and activate; their effects in battle | |
| Battles together in a hosted session (two of the three modes tested) | |
| Servers with their own profiles, starting money and reward rates | |
| Dedicated servers (host without playing, headless or windowed) | |
| Redeem codes (Menu > Redeem Code) from `codes.cfg` | |
| Watch Tutorial (the official video, in the Steam overlay) | |
| The garage arcade machine's shooting gallery | |
| A master server anyone can run (`start-master.cmd`): internet server list, FIND MATCH, a ladder | |
| Server browser: internet, LAN, Steam and favourites, with ping, filters, sorting, direct connect | |

Building modules and robots by drag and drop is implemented on the server side
but has only been checked through the UI screens, not by hand. Please report
anything that misbehaves (see Debugging).

## Install

1. Download `GARRevival-<version>.zip` from the releases page.
2. In Steam, right-click **Guns and Robots** > **Manage** > **Browse local
   files**. That opens the game folder, the one with `GAR.exe` in it.
3. Unzip the download into that folder, so you get a `GARRevival` folder next
   to `GAR.exe`.
4. Double-click `GARRevival\install.cmd`.
5. Start Guns and Robots from Steam as usual.

Nothing else is needed, and there is no installer program in the download:
`install.cmd` compiles the installer from its source (`installer\Program.cs`)
with the C# compiler that is part of Windows 10 and 11 (.NET Framework 4), runs
it and deletes it again. The installer changes two of the game's files
(`Assembly-CSharp.dll`, `Photon3Unity3D.dll`), keeps the originals next to them
(`*.garrevival-orig`) and adds `GarRevival.dll`. Running it again is safe; it
always re-patches from the saved originals. If Steam ever verifies or updates
the game files, run `install.cmd` again.

Your saves, settings and logs live in the `GARRevival` folder. To update the
mod, unzip the new version over it and run `install.cmd` again; your saves are
kept.

## Uninstall

Double-click `GARRevival\uninstall.cmd`. It puts the original game files back
and removes `GarRevival.dll`. Steam's "Verify integrity of game files" does the
same. Delete the `GARRevival` folder too if you don't want your saves.

`start-server.cmd` creates `steam_appid.txt` in the game folder so the server
can start without the Steam client launching it (Steam must still be running).
Delete it if you don't want that.

## Making a release

```powershell
.\tools\make-release.ps1 -Version 0.1.0
```

builds everything and writes `dist\GARRevival-0.1.0.zip` (about 300 KB): the
mod, the installer's source, Mono.Cecil (MIT, see `THIRD-PARTY-NOTICES.txt`),
the scripts, this README, `SHA256SUMS.txt` and `BUILDINFO.txt` (source commit
and .NET SDK version). It prints the zip's own SHA-256 for the release notes.
It compares every file against the game's own files and refuses to pack any of
them. Commit before packing, so `BUILDINFO.txt` names a commit others can
check out. Never commit or upload `build/` (copies of the
game's assemblies used for compiling) or `bin/` from a source checkout; the
`.gitignore` keeps them out.

## Checking that it's safe

GAR Revival is built so that anyone can see exactly what it does before
running it.

**What is in the download**

| File | What it is | How to check it |
| --- | --- | --- |
| `install.cmd`, `uninstall.cmd`, `build-installer.cmd`, `start-server.cmd`, `stop-server.cmd`, `start-master.cmd` | Plain-text scripts | Open them in Notepad |
| `installer\Program.cs` | The installer, as source code (about 300 lines of C#) | Read it; `install.cmd` compiles exactly this file with Windows' own C# compiler |
| `master\MasterServer.cs` | The optional master server, as source code | Read it; `start-master.cmd` compiles exactly this file the same way |
| `bin\GarRevival.dll` | The mod itself, a .NET library | Not obfuscated: open it in [ILSpy](https://github.com/icsharpcode/ILSpy) or [dnSpy](https://github.com/dnSpyEx/dnSpy) to read it as C#, or rebuild it from this repository (below) |
| `bin\Mono.Cecil.dll` | The open-source library the installer uses to edit the game's DLLs | Identical to `lib/net40/Mono.Cecil.dll` in the [NuGet package Mono.Cecil 0.11.6](https://www.nuget.org/packages/Mono.Cecil/0.11.6) |
| `README.md`, `THIRD-PARTY-NOTICES.txt`, `VERSION.txt`, `BUILDINFO.txt`, `SHA256SUMS.txt` | Text | Read them |

There is no `.exe` anywhere in the download.

**What installing changes**

- Only files inside the game folder: `GAR_Data\Managed\Assembly-CSharp.dll` and
  `Photon3Unity3D.dll` get small hook calls added (the list of hooks is at the
  top of `installer\Program.cs`); the originals are kept next to them as
  `*.garrevival-orig`; `GarRevival.dll` is added.
- No administrator rights, nothing in the registry, no services, nothing that
  starts with Windows, nothing outside the game folder.
- `uninstall.cmd`, or Steam's "Verify integrity of game files", puts the game
  back exactly as it was.

**What it connects to**

- Its own small web server on `127.0.0.1` (this PC only), which stands in for
  the game's dead website (see "How it works").
- Other players, only when you host or join a game: a direct connection to the
  address you typed or found on your LAN or a master server's list, or a Steam
  lobby. The server browser measures ping by opening (and closing) a TCP
  connection to each listed game's port.
- Master servers: by default the one the game starts on this PC (listening on
  127.0.0.1 only), plus any you put in `settings.cfg` (`master=`) or
  `dedicated.cfg`: plain HTTP requests for the server list, the
  ladder table and, if you list your game, its heartbeat. While you host a
  LAN game it announces itself on your local network (UDP port 27016) so the
  Play screen's LAN list can find it.
- Nothing else: no telemetry, no update checks, no downloads. (Checked with the
  game running in the garage: its only open connection was the local web
  server.)

**Checking a release**

- `SHA256SUMS.txt` lists the SHA-256 of every file in the zip; the release
  notes give the SHA-256 of the zip itself.
- Each release is scanned on [VirusTotal](https://www.virustotal.com) and the
  report is linked from the release notes. A tool that edits other programs'
  files can trip a "patcher" heuristic in one or two scanners; say so in an
  issue if you see more than that.
- `GarRevival.dll` is reproducible: building the commit named in
  `BUILDINFO.txt` with the same .NET SDK version gives byte-identical output.
  With the game installed and the .NET SDK:

  ```powershell
  git clone https://github.com/UncostlyDevs/GaRvival GARRevival   # inside the game folder
  cd GARRevival
  git checkout <commit from BUILDINFO.txt>
  .\tools\verify-release.ps1 -Zip <path to the downloaded zip>
  ```

  It checks every checksum, rebuilds the mod and compares it, compares
  Mono.Cecil with the NuGet package's copy and the installer source and
  scripts with the checkout's.

(The build needs the game's own DLLs to compile against, which can't be put on
GitHub, so releases can't be built by GitHub Actions; the reproducible build
above is the substitute.)

## Battles

The BATTLE button (or F9) opens the **Play screen**:

- **BOT MATCH**: choose the arena, game mode, number of enemy and ally bots,
  match length and bot difficulty, then START MATCH.
- **HOST GAME**: open a lobby on the local network or on Steam.
- **JOIN GAME**: games found on the local network (automatically), on Steam,
  or by typing an address.

Matches use the game's own offline battle mode (the one the tutorial used):
bot AI, arenas, game modes, pickups and the original reward formulas are all
in the client. START MATCH still runs the garage's usual checks (robot
complete, energy, damage) first.

The screen saves its settings to `battle.cfg`, which can also be edited by hand:

```
map=random          # or Western, WesternNight, Laundry, Pirates, Medieval, DragonTemple, Pompei, Dungeon
mode=TeamDeadmach   # or CaptureTheFlag, DestoryBase
enemies=4           # 1 to 5
allies=3            # 0 to 4 (the arenas hold 5 robots a team)
minutes=10
weakEnemies=false
```

Bots are built at random from the same part catalog as the store. After the
match the emulator's result (credits, experience, kills, win/loss) is added to
your profile, and `revival.log` gets one line per robot (shots, hits, damage
dealt and taken, kills), which helps when tuning stats.

In a 3-minute test on Western (3 vs 2 + player) bots dealt up to ~1,500
damage each and one robot per side was destroyed. The default is 10 minutes.

Capture the Batteries and Base Destruction were tested on Western by moving
the player's robot onto the objectives: picking up the enemy battery and
delivering it twice wins the match, and so does picking up the bomb and
planting it at the enemy base; both pay the objective bonus. The bots' AI has
objective behaviour, but in the short tests no bot completed an objective.

## Multiplayer

On the Play screen:

- **HOST LAN GAME** opens a lobby on TCP port 27015 and announces it on the
  local network every two seconds (UDP broadcast, port 27016). Anyone on the
  same network sees it under JOIN GAME and joins with one click. From outside
  the network, others can join by `address:port` if the port is reachable
  (VPN or port forwarding). Windows may ask to allow the game through the
  firewall the first time; allow it on private networks.
- **HOST ON STEAM** creates a public Steam lobby; other players see it under
  "Steam games". Traffic goes over Steam's peer-to-peer network, so the host
  needs no port forwarding.
- The lobby shows the players. The host sets up the match (arena, mode, bots,
  length, one team or two) and presses START MATCH; joined players wait in
  their garage and can close the screen to keep working on their robot.
- Everyone in a lobby shares the **garage chat** (bottom left). Solo, the chat
  simply echoes.

Each player's garage, profile and unlocks stay on their own PC; their built-in
server keeps answering those operations. The session carries only what is
shared. Unlocks are therefore not verified by the host, by design.

### Server browser and FIND MATCH

JOIN GAME has two more buttons:

- **SERVER BROWSER** lists games in four tabs: INTERNET (from the master
  servers), LAN, STEAM and FAVOURITES. Columns: name, mode, map, players,
  ping, version; click a header to sort by it. Filters: mode, hide full, hide
  other versions. Double-click a row (or select it and press JOIN) to join;
  `+` adds a game to the favourites (`save/favourites.txt`). DIRECT CONNECT
  joins `host:port`. Ping is the time a TCP connection to the game's port
  takes; Steam games show "Steam". A game behind the same router is joined at
  its LAN address first.
- **FIND MATCH** asks the master server for the best servers for the chosen
  mode (same version, room, most players, dedicated first) and joins the first
  one that lets you in; a full server or one that does not answer moves on to
  the next. A strip at the bottom shows progress and CANCEL; the garage stays
  usable. Once joined, the server starts the next match as usual.

Both use the master servers in `settings.cfg` (the INTERNET tab can save it):

```
master=              # empty: the master on this PC, which the game starts
                     # or http://master.example.org:27090 (several: comma-separated; http only)
                     # or off
masterAutoStart=on   # off: never start the bundled master
```

With `master=` empty the game starts the bundled master itself (compiled from
`master\MasterServer.cs` as `start-master.cmd` does, listening on this PC only)
and it exits with the game. Every install then has a working server list, FIND
MATCH and ladder for games on this PC and LAN. To play over the internet,
everyone points `master=` at one shared master.

### Server rules

A host can run its own economy. `server.cfg` (created the first time you host,
read each time hosting starts):

```
id=3f9c2a71b0de        # identifies your server; keep it to keep players' server profiles
name=My server
profiles=own           # own = players bring their own profile and unlocks
                       # server = everyone (the host too) gets a separate profile
                       #          that exists only for this server
startingCredits=20000  # for new server profiles
startingSparks=0
creditRewards=1        # multipliers for what battles pay on this server
experienceRewards=1
maxPlayers=10          # players let in at once, you included; more are told "The server is full."
public=0               # 1 lists your hosted game on the master servers in settings.cfg
                       # (everyone can then see your public IP address)
ammo=on                # off: battles start fully loaded and use no ammo
guildBuffs=on          # off = players' guild benefits do not count in battles here
```

Files made by an older version get the new keys appended, with their comments,
the next time hosting starts; existing values are left alone.

With `profiles=server`, a player's game switches to
`save/servers/<id>/profile.dat` when they join (the garage reloads) and back to
their own profile when they leave. `unlimitedCredits` does not apply to server
profiles. Nothing is enforced against a modified game: like unlocks, this is
honour-based.

### Friends, whispers, squads, guilds

These work among the players of a session (`Social*.cs`): friend invitations
(sent from the chat participants list; the old friend search used the dead
website), private chat, squads of up to three with squad chat, and guilds with
ranks, a vault, levels, message of the day and guild chat. Friend lists and
guilds are saved on each player's PC and members are shown online while they
are in the same session. While you are in a squad the game's own rule applies:
the leader starts the match once every member has pressed Ready.

**Guild benefits** (the Benefits tab; `SocialGuild.Buffs.cs`): six structures
of five levels. The leader or an officer unlocks a level from the vault; it then
builds for 30 min to 24 h (or is finished at once for sparks from the vault)
and can be activated for 24 hours, one level per structure at a time. Level L
needs guild level L and the previous level built. Influence shown in the price
is required, not spent. Effects are the client's own battle code, per level:

| Structure | L1-L5 | Applied as |
| --- | --- | --- |
| Damage | 2-10 | damage dealt x (1 + x/100) |
| Defence | 2-10 | damage taken / (1 + x/100), so "10%" is -9.1% |
| Accuracy | 1-5 | + x/100 weapon accuracy |
| Speed | 1-5 | + x km/h |
| Debuffs | 5-25 | field repair, slow and drain times / (1 + x/100) |
| Pickables | 5-25 | pickup amounts and durations x (1 + x/100) |

Prices: unlock 10k/25k/60k/150k/400k credits, hurry 10/25/50/100/200 sparks,
activation 2k/5k/10k/20k/40k credits. Guild benefits are combat only; the
credit, XP and repair boosts are the personal timed boosts. Benefits travel
with the guild copy: unlocks merge, the earliest build start and latest expiry
win, and what each member spent is a tally like donations. Two officers who
activate the same structure while apart both pay, and the copies keep one (the
highest level); a later expiry learned that way shows after the next login.
In a hosted battle the host accepts at most one benefit per structure from
each player.

Tested: two copies of the game on one PC over a direct connection (join,
roster, chat both ways, leave); creating a Steam lobby and finding it in the
search. **Not tested:** two Steam accounts actually connecting through Steam,
which needs a second PC and account with the game.

### Ladder table

Stats > Ladder shows the game's own table (`LadderTable.cs`), served by the
local web server. Your rating and league are the ones of the league badge
(ladder points from every battle, placed after 5 battles).

- **Own league**: you and the players in your session who are in the same
  league, by points; the arrow shows whether their last battle won or lost
  points. Players send their standing when they meet and after each rated
  battle; on a server with its own profiles only those profiles are compared.
- **Master league**: with `master=` in `settings.cfg`, the first master
  server's overall ladder (top 100 by Elo, rated from dedicated servers'
  matches; see "Master server"). Without a master, or if it does not answer
  within 4 seconds, the Master league players of your session. A Master league
  player has no Master tab, so their own league shows the master's ladder.

Only dedicated servers report matches, so games hosted from the Play screen
count for the badge but not for the master's ladder.

### Battles together

When the **host** presses START MATCH with other players in the lobby, everyone
who is in their garage with a complete robot is taken into the same match.
`battle.cfg` on the host decides the map, mode, length and bots, plus:

```
humans=together   # all players on one team against bots; split = divided between the teams
```

Bots fill each team up to its configured size. How it works
(`src/GarRevival/SessionBattle.cs`):

- Every player's game runs the arena, built on the game's offline battle mode.
  Each game drives its own player's robot; the host's game also drives the bots.
  Everyone else's robots are moved by position updates (about 15 per second)
  using the game's own remote-player smoothing.
- The battle rules (damage, destruction, pickups, the clock, results) run only
  on the host. A joined player's shots are sent to the host; the host's rule
  events are replayed on every other game. As in the original game, the
  shooter's game decides what a shot hit and the rules decide the damage.
- At the end each player gets their own result, and credits/XP are added to
  their own profile on their own PC.

Tested with two copies of the game on one PC over a direct connection: both
load the same arena, see each other and the same bots in the same places, bot
shots and damage show on both, a joined player's shots reach the host, the
match ends for both and both return to the garage with their rewards. In
Capture the Batteries the joined player carried the enemy battery home twice
and both games ended with the win. Server profiles, reward multipliers, friend
and squad invitations, whispers, squad chat, and creating, joining and chatting
in a guild were tested the same way.

**Not tested yet:** real players moving and hitting each other (the test robots
stood still or were moved by script), two different PCs, the Steam connection,
devices and pickups by a joined player, Base Destruction in a hosted game, the
social screens by hand (the tests called the same operations the buttons do).

Known gaps: only the host can start a battle; a player who loads slower than
the host misses part of the countdown; a player who leaves mid-battle leaves an
idle robot behind. Sprinting is now sent with the position updates but has not
been watched from the other screen.

`GAR.exe -garprofile <name>` runs a second, separate player on the same PC
(own save, log and test script), which is how the above was tested.

### Dedicated server

`start-server.cmd` runs a copy of the game as a server that hosts without
playing (`GAR.exe -garserver -batchmode -nographics`: no window, no sound,
capped at 60 frames a second). `stop-server.cmd` stops it and leaves any game
you are playing open. Without `-batchmode -nographics` it runs in a window,
where you can watch the matches.

- It hosts as soon as it has started, and plays as its own profile `server`
  (`save-server/`, log `revival-server.log`).
- Once at least `minPlayers` players have joined, it announces the next match
  in the chat and starts it `matchDelay` seconds later; after each match it
  does the same again. Any player can type `!start` in the chat to start the
  announced match at once. Players must be in the garage with a complete robot
  when the match starts, or they sit that one out.
- It fields no robot of its own. The arena needs a robot to follow, so the
  server watches one of the bots on the players' side (the bot plays normally).
- It reads its own files, created on the first start: `dedicated.cfg` (the
  keys of `server.cfg` above, plus the ones below) and `dedicated-battle.cfg`
  (map, mode, bots and match length, like `battle.cfg`).

```
host=tcp          # tcp: LAN and the internet (forward the port), or steam: a Steam lobby
port=27015
minPlayers=1      # players needed before a match is announced
matchDelay=30     # seconds from the announcement to the start
maxPlayers=10     # players let in at once (the server does not count itself)
master=           # master servers to list it on, e.g. http://master.example.org:27090
                  # (empty: the master on this PC)
```

The server registers when it starts hosting, sends a
heartbeat every 20 seconds (sooner when players come or go), registers again if
the master forgot it, and reports each match's winners and losers for the
ladder. `stop-server.cmd` ends it without saying goodbye, so its entry stays
listed until the master's `serverTimeout`.

Over TCP the server shows up in the Play screen's LAN list; players outside
your network join by address (forward TCP `port` on the router). A Steam-hosted
server needs a Steam account of its own: Steam does not connect an account to
itself, so players on the same account as the server cannot join it.

Tested on this PC with a scripted player, windowed and headless: the server
hosted, announced and started two matches in a row (one through `!start`), its
bots fought, and the player got their rewards and wear each time. Not tested:
Steam hosting, players from another PC, several players at once, running for
hours.

### Master server

`start-master.cmd` compiles `master\MasterServer.cs` with the C# compiler that
is part of Windows (into `master\bin`, no download) and runs it; Ctrl+C stops
it. `--local` listens on 127.0.0.1 only and `--parent <pid>` exits with that
process (how the game runs its own). Anyone can run a public one, change it,
or build a different server against the same HTTP API. It needs no game install. On Linux or macOS with mono:
`mcs -out:master/bin/GarRevival.Master.exe master/MasterServer.cs` then
`mono master/bin/GarRevival.Master.exe master`. It writes `master\master.cfg`
on the first start:

```
port=27090              # TCP; allow it through the firewall / forward it
publicAddress=          # given to players for servers that register from the master's
                        # own PC or LAN (their observed address is private)
serverTimeout=60        # seconds without a heartbeat before a server is dropped
maxServers=1000
maxServersPerIp=16
requestsPerMinute=600   # per IP; a LAN party shares one
registersPerMinute=60
resultsPerMinute=60
maxConnections=64       # requests handled at once
ladderFile=master-ladder.txt
```

HTTP/1.1 + JSON, GET and POST only, plain http:

| Request | What it does |
| --- | --- |
| `POST /v1/servers` | register: `name, port, mode, map, players, maxPlayers, version, rules, dedicated, transport (tcp/steam), steamLobby, lan, address`; answers `id`, `token` (secret) and the address players are given |
| `POST /v1/servers/{id}/beat` | heartbeat with `token` and any changed fields; 404 means register again |
| `POST /v1/servers/{id}/remove` | unlist, with `token` |
| `GET /v1/servers?mode=&version=&notFull=1` | the list |
| `GET /v1/match?mode=&version=` | up to 10 servers with room, best first: same version, most players, dedicated, not mid-battle |
| `POST /v1/results` | a dedicated server's match: `id, token, mode, map, seconds, winningTeam (-1 = draw), winners, losers` (full player names) |
| `GET /v1/ladder?mode=&top=&name=` | ratings per mode and overall (`any`); `name` adds your own row |
| `GET /v1/info` | servers, players and ladder size |

The address a server is listed under is the one its requests come from, so a
server cannot list someone else's machine; only a registrant on the master's
own network may name its public address. Every field is checked and
size-limited (8 KB bodies, 5 s per request, per-IP rate limits); nothing a
request carries is executed or used as a file name. The ladder is team Elo
(start 1000, K 40 for the first 10 matches, then 24), rated only when both
sides have players; it is honour-based, since there are no accounts and a
server could report anything. `tools\test-master.ps1` compiles the master,
runs it on a spare port and checks every endpoint (including bad input,
oversize bodies, slow clients and rate limits) with curl.

### Ammo

Weapons that fire projectiles use ammo, as in the original. On the Supply
screen's Resupply tab each barrel has an ammo type and a load; OK/Buy buys
what the loads need, and **Auto Resupply** (on for every robot by default)
tops them up whenever you enter the garage, with a "You spent X credits for
resupply" notice. The two free types (`Ammo_Bullet_Free`, `Ammo_Stone_Free`)
are always topped up. A battle takes the rounds you fired from your stock; an
empty weapon cannot fire until it picks up ammo (arenas have ammo pickups; the
training ground is always fully loaded). Bots go looking for ammo when they run
low.

New profiles start with two loads for each ammo weapon, existing profiles get
the same once, and a bought barrel comes with one load.

`ammo=off` in `settings.cfg` makes every battle start fully loaded and use
nothing up. In a hosted game the host's `server.cfg` decides with the same key
(`ammo=on|off`, default on): the host decides every robot's load and tells each
player what their battle used.

## Garage extras

- **Redeem codes** (Menu > Redeem Code) used to be checked by the website. They
  now come from `codes.cfg`, created on first use with the sample code
  `REVIVAL`. Each line is a code and what it gives:

  ```
  REVIVAL = credits 5000, sparks 25
  WELCOME = credits 2000
  ```

  Codes are not case-sensitive and work once per profile (the used ones are in
  the profile's `redeemed.txt`). A server owner can hand out codes this way;
  like everything else on a player's PC, nothing stops a player from adding
  their own.
- **Buy** next to the sparks was the real-money store. It now says where sparks
  come from (Exchange, daily login rewards, missions, crates). Before, with the
  Steam overlay off, it opened a payment browser whose library no longer ships
  with the game, which hid the whole interface until a restart.
- **Watch Tutorial** opens the official tutorial video on YouTube in the Steam
  overlay (it needs the game started from Steam with the overlay on).
- The **arcade machine** in the garage is playable: click it and the camera
  moves to it, as it did while you waited in the old matchmaking queue, and
  the game's own shooting gallery starts on its screen (`ArcadeMode.cs`;
  the original only showed it in the queue, which no longer exists). Click
  the screen to start or resume, left click shoots, right click reloads, Esc
  pauses, and LEAVE ARCADE at the bottom takes you back to the garage. Levels
  get harder as you meet each level's kill quota and accuracy.
- The **banners** next to Buy open the store pages they advertise.
- **Part skins.** No skins shipped with the game, so the mod draws its own
  (`SkinCatalog.cs`, `SkinPatterns.cs`): Woodland, Desert, Urban, Arctic,
  Digital and Hazard for 3,000 credits; Tiger, Carbon Fibre, Rust and Brushed
  Steel for 25 sparks; Circuit and Flames for 60 sparks. Select a part on a
  robot and press **SKINS** (or F8): BUY asks for confirmation and puts the
  skin on once bought, EQUIP and ORIGINAL LOOK switch for free. A bought skin
  fits every part of that kind. Skins are saved in the profile and other
  players with the mod see them in hosted battles; a player without it sees
  the part's own look.

  How they are drawn (`SkinPainter.cs`): when the game loads the model
  `<part>_s<N>`, the mod copies the part and bakes the pattern into its colour
  texture, only where the part can be painted, keeping the texture's shading
  and its alpha (glow, cut-outs). The paint mask is untouched, so painting
  still works on a skinned part. The pattern repeats according to the part's
  size. Settings in `settings.cfg`:

  ```
  skinTextureSize=1024   # largest generated texture side (128-2048)
  skinCompress=true      # DXT-compress generated textures (4x less memory)
  skinCacheParts=48      # skinned part models kept ready
  ```

## Your profile

Saved to `save/profile.dat` after every change. A new profile starts with
250,000 credits, every portrait, 200 inventory slots and a starter robot
(Bugsy chassis, Chromax body, two Aeroblaster arms with Acidcasters, Annie
head). Delete the `save` folder to start over.

Part stats are re-derived from the current templates every time the profile
loads, so balance changes apply to parts you already own.

## Where the game data comes from

The original servers held all part stats, prices and missions in a database
that no longer exists. Their template XMLs were on S3 and were never archived.

What survives in the client, and is used:

- **Part identity**: each of the 197 part prefabs carries a `PartPrefab`
  component (template id, type, keywords, skins) and `DockingPoint` components
  (how parts and modules attach). That is what the developers' export tool
  turned into templates. See `parts-catalog.txt`.
- **Names and descriptions**: the localisation tables still contain
  `<id>_Name` / `<id>_Description` for every part, in every language.
- **Icons**: rendered from the 3D models at runtime by the game itself.
- **Chassis hover flag**: `ChassisCasePrefab.hovering`.

**The server's per-part numbers are not in the client.** Checked and ruled out:

- All 19 asset bundles and the `GAR_Data` asset files. Their type trees
  (readable with `tools/serialized.py`) show the part components were built
  without any stat fields; the editor-side stat classes still in the code
  (`WeaponBarrelPrefabTierableStatistics` etc.) were never serialized.
- The game code: no stat tables, only two tutorial robot presets.
- The official wiki (gar.fandom.com): every part page is strategy text, no
  numbers. The server's template XMLs on S3 were never archived.

**Real numbers** (`src/GarRevival/RealStats.cs`) come from the tables in the
Steam guide ["Best" Weapon // Body // Chassis](https://steamcommunity.com/sharedfiles/filedetails/?id=1105439577)
(August 2017, after the last rebalance):

- 28 chassis: durability, speed (km/h), weight limit, terrain (+/o/-).
- 21 bodies: durability, weight.
- 26 barrels: damage per projectile, splash, shots per second.
- Durability is the guide's value / 0.35, which gives whole numbers whose top
  tier (x1.2) lands exactly on the garage's bar maxima (Warhawk 1000 -> 1200,
  Mantis 935 -> 1122). Weights, speeds, limits and damage are as published.
- These are tier-0 values; durability, damage and splash grow to x1.2 at the
  top tier. Weights, limits, speeds and rates of fire do not change with tier.
- Parts the guide leaves out take a sibling's row: Christmas Sleigh = Warhawk,
  Maximus Pride = Bugsy, Cuckoo = Jackhammer, Maximus Legacy = Chromax; Barry
  Light and the Christmas rocket launcher scale the Rocket Launcher, Justice and
  Revenge scale the Blaster, the Witch Bow scales the Hailfire, each by the two
  barrels' roles in `Design.cs`.
- Guide names that differ from the template ids: Toad = MC_Frog, Behemoth =
  HC_Bahemoth, Pistolero = LB_Sheriff, Ratchet = LB_Bandit, Elektra =
  MB_Electra, Panzersturm = HB_Pazersturm, Slugger = LW_B_Revolver, Sprayshot =
  LW_B_SawedOff, Nailer = LW_B_NailGun, Pewpew = LW_B_SmallLaser, Sandblaster =
  LW_B_Shotgun, Reaper = LW_B_Arbalet, Stingray = LW_B_ElectroCutter, Oxidian =
  MW_B_AcidCaster, Hailfire = MW_B_Katyusha, Dragonbelch = MW_B_FalmeCaster,
  Blaster = MW_B_Laser, Chewing Gun = MW_B_StickyBombLauncher, Rocket Launcher =
  LW_B_Bertha (a heavy barrel), Thunderbolt = HW_B_MachineGun, Missile Launcher
  = HW_B_Missile.

**Designed numbers** (`Catalog.cs` and `Design.cs`) for everything else:
engines, batteries, heads, weapon cases, devices, and barrel accuracy, range,
energy and durability.

- **The original scale.** The garage's stat bars
  (`GameplayDataExtactTools`) hard-code each stat's maximum per part type
  (weapon case durability 628, range 200, battery capacity 270...). A
  top-tier heavy part gets the maximum; Light/Medium get 60%/80% and tier 0
  gets 1/1.2 of the top tier, like the real rows.
- **Masses** of chassis, weapon cases and barrels are 30%/60%/100% of the bar
  maxima (25.2/25.2/25.5) for Light/Medium/Heavy, so a body with two weapons
  of its class stays under its class's real weight limits. Four-armed bodies
  weigh what the guide says (195/260/340), so fully armed they usually run
  over; the client takes 0.05 km/h per unit over the limit, down to 7 km/h.
- **Each part's official description**, transcribed into `Design.cs`:
  chassis type (wheeler/walker/tracker/hover) for all 30 chassis, body class,
  battery quality and capacity-vs-regen, engine trade-offs (in km/h; the Yak's
  +40 weight limit matches the guide), firing mechanism modifiers (the stated
  63%/50% damage for multi-barrel Chimaera and Triceratops is applied by the
  client itself), and every barrel's role (sniper, rapid fire, cone, splash).
- **Units the client expects**: chassis and engine speeds in m/s (the garage
  shows x3.6), a barrel's rate of fire as the delay between shots, a weapon
  case's rate bonus as a delay factor (0.769 shows +30%).

Other choices:

- **Ammo** (`AmmoDesign.cs`): the localisation table still names 30 ammo
  families as `Ammo_<Family>_<Variant>` (Base, Dmg, SpDmg, SpR, Spd, Free) with
  descriptions that match the barrels. 26 barrels fire them; the five whose
  description says energy (Ant Killer, Pewpew, Death Ray, Blaster,
  Necrotron) stay energy weapons (`AmmoPerShot 0`, `MaxAmmo -1`). Variants
  give +15% to one stat (Free: -10% damage). Capacity comes from weight class
  and rate of fire (30 to 900 rounds); a round costs about 3% of the damage it
  does, in whole eighths of a credit; ammo weapons use 60% of the energy per
  shot.
- Weapon behaviour: matched to the game's 60 weapon actions by name, with a
  short override table for the rest.
- Head, hat, eyewear and device modules use the original `Mod_<part id>`
  naming, so the tutorial's preset robots find their parts.

`generated-templates.txt` (written on each start) lists every part with its
type, weight class, chosen weapon/device/chassis behaviour and where its
numbers come from (`real`, `real via <sibling>` or `designed`). `revival.log`
has a `Weight budget` line per class.

Rebalancing is a matter of editing `RealStats.cs` (real rows and siblings),
`Design.cs` (each part's role) or `Catalog.Stats()` (the designed scale), then
rebuilding.

## How it works

The final official build replaced `SteamDeployLogin.Start()` with a popup and a
QUIT button. The original login code is still in the game, just never called.

The patcher (Mono.Cecil) inserts hook calls into:

- `SteamDeployLogin.Start`, to run the original login flow, skipping the dead
  `play-gar.com` Steam ticket check.
- The game's website addresses (`play-gar.com`, its S3 storage): `LocalWeb.cs`
  points them at a small web server the mod runs on `127.0.0.1:27080`, because
  that domain no longer belongs to the game and some requests carry player
  names and Steam ids. It answers friends' online status from the session, the
  ladder table and redeem codes, and logs every request to `web-requests.log`;
  anything else gets a 404, which the game treats like the site being down. No
  request leaves the PC, except that the ladder table asks a master server set
  in `settings.cfg` for its ladder.
- `ExitGames.Client.Photon.PhotonPeer` (connect, send, dispatch, state).
  Instead of opening sockets, every operation goes to a **loopback server**
  inside `GarRevival.dll`, which answers with proper `OperationResponse`s and
  `EventData`. The game's own networking state machine and handlers process
  them unchanged.
- `ResourcesManager.LoadPartPrefab`, which hands out the generated part skins.
- `PhotonNetwork.JoinRandomBattle` starts an offline match instead of
  matchmaking, and `OfflineArena.OnEndGame` hands the result to the server.
- `RobotAvatar.AddFullAmmo`, so a robot enters a battle with the rounds it
  loaded, and energy weapons with 0 rounds instead of -1 (which made bots
  hunt for ammo pickups all the time).
- `Player.SetPlayerData`, `Player.AddItemValueObjects`,
  `Player.UpdateItemValueObjects`, `Player.UpdateRobotValueObjects` and
  `DataManager.OnShopData`, to fill in empty lists that protobuf drops in
  transit.

Startup handshake:

```
230 auth -> 221 login -> reconnect to garage -> 230 auth -> 229 join lobby
-> 206 prices -> 202 ammo -> 219 shop templates -> 161 promotions -> 197 portraits
-> 152 skins -> 149 tints -> 220 player -> 181 garage init -> 169 guild buffs
-> 179 guild -> 148/165 missions -> 182 messages -> 183 friends
```

Garage operations and the events the server answers with:

| Op | Action | Events sent |
| --- | --- | --- |
| 218 | buy part/module | 217 add items, 220 credits |
| 217 | sell part/module | 218 remove items, 220 credits |
| 216 / 214 / 215 | create / update / destroy module | 217 / 216 / 218 |
| 213 / 212 | create / update robot | 215 / 214 robot slot |
| 209, 204 | select robot, auto repair | - |
| 203 | auto resupply (on: resend the ammo state so the client buys) | 192 ammo |
| 202 / 201 | ammo templates / buy ammo (213 = credits spent, as a float, for auto resupply) | 220 credits |
| 200 | ammo distribution: checked against the catalog and the stock, never refused | 192 ammo, when the server corrected it |
| 212 / 214-216 / 217-218 | after robot, module, buy and sell events | 192 ammo, when a barrel's node changed |

## Protocol manifest (for a community server)

Online play between players needs a real server. The [protocol](protocol/PROTOCOL.md)
folder is the specification for one, generated from the game itself:

- `junkwars.proto`: all 122 data messages, exported with the game's own
  protobuf library, so field numbers are exact.
- `MESSAGES.md` / `protocol.json`: all 96 operations and 99 events with
  parameter keys and types. 22 parameters the client reads are still untyped
  (`?`), mostly battle payloads packed in hashtables.
- `PROTOCOL.md`: transport, login-to-garage and battle flows, and the roadmap
  from this offline mod to online play.

## Layout

```
GARRevival/
  src/GarRevival/   the mod (compiled against the game's .NET 3.5 assemblies)
    Hooks.cs          entry points called by the patched game code
    Loopback.cs       virtual Photon connections
    GameServer.cs     operation handlers and events
    Catalog.cs        part/module/price templates from the prefabs
    Design.cs         each part's role, from its official description
    RealStats.cs      real chassis, body and barrel stats from a 2017 guide
    PlayerState.cs    the saved profile, starter robot, item creation
    DataFactory.cs    other startup data (portraits, prices...)
    Workshop*.cs      part upgrades, tech levels, boosts, repairs, slots, renting
    Cosmetics*.cs     paint colours, crates, loot, daily login rewards
    Skin*.cs          part skins: catalog, patterns, texture baking, SKINS screen
    Mission*.cs, Ladder*.cs, MessageBoxOps.cs
                      daily missions, achievements, ladder rating and table
    Social*.cs        friends, whispers, squads, guilds
    ServerRules.cs    server.cfg: server profiles and reward rates
    DedicatedServer.cs  -garserver: hosting without playing, automatic matches
    LocalWeb.cs       local stand-in for the game's dead website
    ArcadeMode.cs     the arcade machine's mini-game
    RedeemCodes.cs    Menu > Redeem Code from codes.cfg
    BattleHud.cs      match timer and start countdown
    BattleUpkeep.cs   battle wear, consumable boosts in battle
    AmmoDesign.cs     ammo types, capacities, prices; AmmoOps.cs: stock, loads, battle use
    ArenaSpawns.cs    spawn points for oversized teams
    OfflineLogin.cs   replacement for the shutdown popup
    OfflineBattle.cs  BATTLE button -> offline match, rewards
    Session.cs        multiplayer session: host/join, roster, chat
    SessionBattle.cs  battles shared by a session (host runs the rules)
    Links.cs          direct (TCP) transport
    SteamNet.cs       Steam lobby + peer-to-peer transport
    PlayScreen.cs     the Play screen: bot match, host, join, lobby
    LanDiscovery.cs   finding LAN games (UDP broadcast)
    PlayScreen.Browser.cs  the server browser and the FIND MATCH strip
    ServerList.cs     browser entries, join targets, favourites
    MasterClient.cs   listing on master servers, lists, results; MasterHttp.cs, MiniJson.cs
    Matchmaker.cs     FIND MATCH; Joiner.cs tries targets in turn; Pinger.cs TCP ping
    CfgFile.cs        reading and updating the .cfg files, keeping comments
    Settings.cs       settings.cfg; Instance.cs: -garprofile
    RevivalDriver.cs  screenshots and test scripts
  src/Patcher/      installer / uninstaller (Mono.Cecil)
  master/           the master server (one C# 5 file; start-master.cmd builds and runs it)
  protocol/         network protocol manifest (.proto schema, message catalogue)
  tools/            asset bundle readers; ProtoSchema and protocol_extract.py
                    generate the protocol manifest; check.ps1 (compile only),
                    testrun.ps1 / testrun2.ps1 (scripted one- and two-player runs),
                    test-master.ps1 (master server endpoints)
  build.ps1         builds both into bin/
```

Build with `.\build.ps1` (needs the .NET SDK). `build/refs` holds copies of the
game's assemblies used for compiling. **Don't share that folder.**

## Debugging

- `revival.log` has every operation, each delivery to the game, the game's own
  log output and exceptions.
- F12 saves a screenshot here.
- `autoshots.txt` with seconds (e.g. `10,30,60`) takes timed screenshots.
- `autorun.txt` runs a script, one `<seconds> <command>` per line:
  - `buy Part LW_B_Revolver`, `buy Module HE_Bandit_Name`
  - `toggle ToggleStore ToggleStoreBuy` (garage UI tabs, as a click would)
  - `state TestRun` (from the robot view)
  - `battle` (presses BATTLE), `fire <seconds>` (holds the triggers in a battle)
  - `host tcp <port>`, `host steam`, `join tcp <address:port>`, `chat <text>`, `leave`
  - `start` (the Play screen's START MATCH)
  - `goto flag|delivery|bomb|base|enemy`, `plant` (move my robot onto an objective
    or next to an enemy), `boost <slot>`, `repair`
  - `call <Method> <args>` (a `PhotonNetwork` method by name, simple arguments,
    `_` for spaces), `op <code> <key>=<i|b|s|f|B>:<value>` (a raw operation; `_` is a space, `__` an underscore)
  - `server enter`, `server exit` (apply `server.cfg` without hosting)
  - `browser [internet|lan|steam|favourites]` (open the server browser),
    `rows` (log its rows with ping), `joinrow <index>` (join one),
    `findmatch [TeamDeadmach|CaptureTheFlag|DestoryBase|any|cancel]`
  - `ladder mine|master` (the Stats > Ladder table request; the log says
    `Ladder table: <tab> rows=<n> source=local|master`)
  - `ammo` (log stock and loads, and in battle the robot's rounds), `ammo buy
    <type> <tier> <qty>`, `ammo max` (Max load all + OK/Buy), `ammo auto on|off`
  - `skins` (the SKINS screen), `skin <part id|first> <skin>` (buy if needed and
    wear on the first such part on a robot; 0 = original look),
    `skindump <prefab path> <skin>` (the part's textures before and after, as
    PNGs in `skins-dump/`)
  - `shot <name>`

  Delete it afterwards, or every launch will run it.

The original new-player tutorial is marked as completed (`skipTutorial=true` in
`settings.cfg`): it takes over the BATTLE button to start a scripted practice
battle and expects features that are not back yet. The Info Screen (news from
the dead website) no longer opens by itself.

What the newer garage features do and don't do:

- **Designed numbers.** Upgrade times and costs, tech-level unlocks, boost and
  key prices, loot chances, mission goals and rewards, guild levels and
  benefits, and the daily login table are all designed here; the originals
  were server-side and are lost. They are kept together at the top of
  `WorkshopData.cs`, `CosmeticsDesign.cs`, `MissionCatalog.cs`,
  `SocialGuild.cs` and `SocialGuild.Buffs.cs`.
- **Sparks** have little income (exchange, some rewards and crates), so spark
  prices are low.
- **Achievements** on an existing profile: several complete at the first login
  after this update, with their popups and rewards.
- **Battle wear.** A tenth of the damage each part takes in a battle stays on
  it afterwards (`WearShare` in `BattleUpkeep.cs`; the original rate is lost).
  Repair on the Supply screen, or tick Auto Repair. A full repair costs 10% of
  a part's price. In a test a starter robot that took 588 damage cost 135
  credits to repair at the earlier rate of 0.15.
- **Consumable boosts** (Supply screen, Consumables) are carried into battle
  by the robot they are slotted on and can be used once per slot per battle.
  Speed and energy use the effect of the strongest pickup of that kind, the
  chassis repair revives a destroyed chassis, and the head repair clears the
  robot's own aiming trouble. Tested by script: speed and energy in a bot
  match, speed by a joined player in a hosted match. The chassis and head
  repairs have not been tried.
- Ten achievements for feats the battle code does not report (last stand,
  turret kills and so on) never progress.

Known issues:

- The game's own timing check (it quit the game when the PC stuttered) is
  switched off.
- The HUD match timer and the 5-4-3-2-1 start countdown used to read the
  online server's clock and stayed blank; `BattleHud.cs` now drives them from
  the arena's own clock.
- Teams are capped at 5 robots (5 enemy bots, you + 4 ally bots): the arenas
  have 5 spawn points a side and the game fails to build the arena with more.
  If a team still ends up larger (a hosted game), `ArenaSpawns.cs` places the
  extra robots behind existing spawn points.
- A `GarUITutorialDialogController` NullReferenceException is logged when
  leaving the garage. It is harmless.
- The game's pause menu opens when its window loses focus.
- Once during development the game stalled for over a minute right after the
  catalog was built. It did not recur in later runs. If it happens, restart the
  game and send `revival.log`.
