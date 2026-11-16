# Master server API, version 1

The contract between GAR Revival games and a master server
(`master/MasterServer.cs` is the reference implementation). Anyone may run a
master or write a different one; a game works with any server that answers
these requests the same way.

- Plain HTTP/1.1, `GET` and `POST` only. Request and response bodies are JSON
  (UTF-8, `Content-Type: application/json`), at most 8 KB, sent with
  `Content-Length` (chunked bodies get 411).
- Every path starts with `/v1/`. A future incompatible change uses `/v2/`;
  `GET /v1/info` reports `api` so clients can check.
- Errors: a status code plus `{"error": "<code>", "field": "<name>"}`
  (`field` only for `bad_field`).

| Status | `error` | Meaning |
| --- | --- | --- |
| 400 | `bad_json`, `bad_field` | body is not a JSON object (or nests too deep), or a field is missing, the wrong type or out of range |
| 403 | `bad_token`, `not_dedicated` | wrong token; results from a server that did not register as dedicated |
| 404 | `not_found`, `unknown_server` | unknown path; unknown or expired server id (register again) |
| 405 | `method_not_allowed` | |
| 408 | | the request did not arrive within 5 s |
| 411, 413 | `length_required`, `too_large` | |
| 429 | `rate_limited` | per IP; `Retry-After` gives the seconds to wait |
| 503 | `full` | the master lists as many servers as it allows |

## Values

| Name | Values |
| --- | --- |
| mode | `TeamDeadmach`, `CaptureTheFlag`, `DestoryBase` (the game's own spelling) |
| map | `random`, `Western`, `WesternNight`, `Laundry`, `Pirates`, `Medieval`, `DragonTemple`, `Pompei`, `Dungeon` |
| player name | the game's full name, `<display name>.<4 hex>`, 1-64 characters |
| version | GAR Revival's multiplayer protocol version (`Session.ProtocolVersion`), at most 16 characters |

## Game servers

### `POST /v1/servers`: register

| Field | Type | Rule |
| --- | --- | --- |
| `name` | string | 1-40 characters after control characters are removed |
| `port` | int | 1-65535; `1` for a Steam-hosted game |
| `transport` | string | `tcp` (default) or `steam` |
| `steamLobby` | string | digits, at most 20; required for `steam`, may be empty for `tcp` |
| `lan` | string[] | up to 4 private IPv4 addresses of the host; others are dropped |
| `mode`, `map` | string | see Values |
| `players` | int | 0-64 |
| `maxPlayers` | int | 1-64 |
| `version` | string | see Values |
| `rules` | string | up to 120 characters, shown in the browser |
| `dedicated` | bool | `-garserver` |
| `state` | string | `lobby` or `battle` |
| `address` | string | optional; honoured only from the master's own PC or LAN |

The listed address is the IP the request came from. When that is a loopback or
private address the master substitutes `publicAddress` from `master.cfg`.

Answer: `{"id": "<12 hex>", "token": "<32 hex>", "address": "...", "port": 27015, "timeout": 60}`.
Keep `token` secret; heartbeat well within `timeout` seconds.

### `POST /v1/servers/{id}/beat`

`{"token": "...", ...}` plus any register fields that changed. Answer
`{"ok": true, "timeout": 60}`. 404 means the master forgot the server:
register again.

### `POST /v1/servers/{id}/remove`

`{"token": "..."}`. Answer `{"ok": true}`.

### `POST /v1/results`

From a dedicated server after each match:

| Field | Type | Rule |
| --- | --- | --- |
| `id`, `token` | string | the server's registration |
| `mode`, `map` | string | see Values |
| `seconds` | int | 1-7200 |
| `winningTeam` | int | 0, 1, or -1 for a draw |
| `winners`, `losers` | string[] | full player names, up to 16 each, no name on both sides, not both empty |

Answer `{"ok": true, "rated": true, "ratings": [{"name", "rating", "delta"}]}`.
A match is rated only when both sides have players; one result per server per
30 s.

## Players

### `GET /v1/servers?mode=&version=&notFull=1`

`{"total": n, "servers": [entry]}` (at most 500 entries), where an entry is
`{"id", "name", "address", "port", "transport", "steamLobby", "lan", "mode", "map", "players", "maxPlayers", "version", "rules", "dedicated", "state", "age"}`.
`lan` is filled only for a requester on the server's own network, and
`address` is then its LAN address. `age` is seconds since the last heartbeat.

### `GET /v1/match?mode=&version=`

Up to 10 entries with room, best first: same version, most players, dedicated,
not mid-battle. `mode` may be `any`. The client joins them in order and moves
on when one is full or does not answer.

### `GET /v1/ladder?mode=&top=&name=`

`mode`: a mode or `any` (default). `top`: 1-100 (default 50). Answer
`{"mode", "total", "rows": [row], "you": row|null}` with
`row = {"rank", "name", "rating", "matches", "wins", "losses", "draws", "last"}`
(`last`: Unix time of the last rated match). `you` is the row for `name`.

### `GET /v1/info`

`{"api": 1, "servers": n, "players": n, "ladder": n}`.

## Trust

There are no accounts. A master trusts what servers report, and a dedicated
server trusts what players' games report about their own profiles. A master
that wants stronger rules (bans, verified profiles, server-side unlocks) can add
endpoints under `/v1/` without breaking games, or publish `/v2/` alongside.
