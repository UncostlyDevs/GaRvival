# Guns and Robots network protocol

Everything a replacement server needs to know to talk to the unmodified game
client, derived from the client itself. Three files:

| File | What it is | How it is made |
| --- | --- | --- |
| `junkwars.proto` | All 122 data messages (player, parts, modules, robots, prices, missions, guilds, battle results...) with exact field numbers | `tools/ProtoSchema`, using the game's own `JunkWars.dll` and protobuf-net 2.0.0.668 |
| `MESSAGES.md` / `protocol.json` | Every operation (96), response and event (99): codes, parameter keys, types, which client signal each raises | `tools/protocol_extract.py` from the decompiled client |
| `PROTOCOL.md` | This overview: transport, connection flow, conventions, roadmap | by hand |

Regenerate after a client update:

```powershell
dotnet build tools\ProtoSchema -c Release -o build\protoschema
dotnet build\protoschema\ProtoSchema.dll ..\GAR_Data\Managed protocol\junkwars.proto
ilspycmd -p -o <src> ..\GAR_Data\Managed\Assembly-CSharp.dll
python tools\protocol_extract.py <src> protocol src\GarRevival\GameServer.cs
```

## Transport

- **Photon 3** client library (`Photon3Unity3D.dll` 3.0.1.1), reliable UDP
  (ENet-style), Photon's binary serialization (`Protocol.cs` in that library).
  The original servers were self-hosted Photon Server applications.
- Custom Photon types registered by the client (`CustomTypes.cs`): 87 Vector2,
  86 Vector3, 84 Transform, 81 Quaternion, 80 PhotonPlayer, 73 PhotonViewID.
- A message is an **operation request** (client -> server), an **operation
  response** (server -> client, same code, a `ReturnCode` short, a
  `DebugMessage` string) or an **event** (server -> client). All three carry a
  `Dictionary<byte, object>` of parameters.
- Requests marked *encrypted* in `MESSAGES.md` use Photon's built-in encryption,
  which the client sets up with `EstablishEncryption()` (Diffie-Hellman key
  exchange) right after connecting and before Authenticate.
- A non-zero `ReturnCode` is a failure. `DebugMessage` is normally a
  localisation key (`SM_0026`...) the client shows in a popup.

### Parameter values

Simple values are Photon-serialized (`string`, `int`, `byte`, `bool`, `float`,
arrays, `Hashtable`). Structured data is a `byte[]` holding a protobuf message
from `junkwars.proto`, in one of three wrappings (`DataSerialization` in
`JunkWars.dll`):

| Notation | Wrapping |
| --- | --- |
| `proto<T>` | raw protobuf |
| `lz4<T>` | protobuf written through an LZ4 stream (`LZ4s.dll`, 1 MB blocks) |
| `bzip2<T>` | protobuf compressed with BZip2 (SharpZipLib, `CompressionHelper`) |

protobuf-net specifics: empty lists are not written, so they arrive as `null`
(the original server data never had empty lists where the client iterates
without a null check; a new server must either send non-empty data or the
client must be patched, as GAR Revival does). `DateTime` uses protobuf-net's
`bcl.proto`. Sub-types are fields 101+ on the base message.

## Servers and connection flow

The client uses two independent connections (peers):

| Peer | Class | Servers |
| --- | --- | --- |
| Lobby peer | `NetworkingPeer` (`PhotonNetwork`) | login server, then garage server |
| Battle peer | `GamePeer` (`GameNetwork`) | game master, then a game server |

```
Lobby peer
  connect login server (shipped address 107.23.17.110:5058, app "Master")
  EstablishEncryption
  230 Authenticate   (220 app version "v1.0", 224 app id, 218 JunkWars.dll version)
  221 Login          -> 225 session id, 211 secret user id, 163/164 server time,
                        230 garage server address
  disconnect, connect garage server
  230 Authenticate   (now with 225 session id)
  229 JoinLobby
  206 prices -> 202 ammo -> 219 shop templates -> 161 promotions -> 197 portraits
  -> 152 skins -> 149 tints -> 220 player -> 181 garage init -> 169 guild buffs
  -> 179 guild -> 148/165 missions -> 182 message box -> 183 friends
  (each response triggers the next request; the garage opens when prices,
   ammo, templates, portraits, player and package prices have all arrived)

  garage actions: 218 buy, 217 sell, 216/214/215 module create/update/destroy,
  213/212 robot create/update, 209 select robot, 205 repair, 201 buy ammo ...
  The response is only an acknowledgement; state changes arrive as events:
  217 add items, 218 remove items, 216 update items, 215/214 robot slots,
  220 credits, 196 experience, 187 level, 186 lifetime statistics.

Battle
  lobby peer: 210 JoinRandomBattle -> 230 game master address (or use the
  shipped one); events 185 ReadyForBattle / 184 SelectedForBattle
  battle peer: connect, 230 Authenticate, 210 JoinRandomBattle
     -> 255 room name, 230 game server address, 212 team
  battle peer: reconnect to the game server, 227/226 create or join the room
  events: 255 Join, 206 LoadGame, 199 StartCountdown, 219 StartGame
  client: 208 LoadedGame when its arena has loaded
  combat is server-authoritative:
     client 248 Shoot / 247 Hit / 242 ProjectileAttach / 245 DeviceActivation /
            246 PickPickable / 244 GameModeEvent / 240 ConsumableBoostUse
     server events 252 Shoot, 251 Hit, 250 DeviceActivate, 198 StatisticsChanged,
            197 Destruction, 195/194 pickables, 248 GameModeEvent, 138 Resurrection
  movement and animation: Photon Unity Networking events 200-206 relayed
     between the clients in the room (253 RaiseEvent)
  213 EndGame (proto EndBattleData per player), 191 GameResultInfo, 212 ExitGame
  rewards then arrive on the lobby peer as the usual credit/XP/item events
```

`MESSAGES.md` has the parameters of every step.

## What the client already contains

- **The battle rules.** `JunkWars.Client.Arena.Offline.OfflineServerEmulator`
  (2,000 lines) is a client-side copy of the server's battle logic: shot and
  hit validation, damage distribution over modules, energy, devices and
  deployables, pickups, the three game modes, kill and win tracking, and the
  credit/XP reward formulas. A battle server can be a port of it.
- **Bots.** The original servers filled matches with headless game clients
  (`StartupArgumentsManager.clientAsBot`, `GlobalConstants.renderlessMode`,
  the RAIN behaviour trees in `JunkWars.Client.AI`). The same works today.
- **A working reference for the lobby side.** `src/GarRevival/GameServer.cs`
  answers 32 operations in-process (marked "offline server: yes" in
  `MESSAGES.md`): login, all startup data, buying/selling, modules, robots.

## What is lost

Player accounts and progress, the original part stats and prices, mission and
loot definitions. A community server starts everyone fresh on rebuilt data
(see the README, "Where the game data comes from").

## Roadmap to online play

1. **Protocol manifest** - done (this folder).
2. **Lobby server**: a standalone server that speaks the Photon transport and
   serves login + garage for many players, with a database for profiles. The
   handlers are a port of `GameServer.cs`/`PlayerState.cs`/`Catalog.cs`.
   Client side, the patch gains an "online" switch that points the lobby
   address at the community host instead of the in-process server.
   Transport options: (a) a Photon Server SDK (self-hosted) application,
   which speaks this protocol natively and can load `JunkWars.dll` directly
   (check Exit Games' current licence terms); (b) an open reimplementation of
   Photon 3's UDP/ENet layer and serializer from `Photon3Unity3D.dll`.
3. **Social**: chat (211), friends (183-186), parties (187-191), guilds.
4. **Matchmaking and battles**: queue (210), rooms, relay of PUN events,
   server-side combat ported from `OfflineServerEmulator`, end-of-battle
   rewards. Headless bot clients to fill matches.
5. **Economy**: upgrades (162/158/159), crates (156), skins, tinting, timed
   and daily missions, ladder, promotions.
