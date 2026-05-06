# NoMapDiscordAdditions

Adds **SEND MAP** and **COPY MAP** controls on the large map. Captures the visible map and either posts it to a Discord webhook or copies the PNG to the system clipboard. Cartography-table pins are decorated with distance/bearing-from-spawn labels in the captured image.

## Features

- **SEND MAP** posts the capture to a Discord webhook (`F10` by default)
- **COPY MAP** copies the captured PNG to the system clipboard — paste straight into Discord, Slack, etc. (`F11` by default)
- **Cartography-table pin labels** baked into the capture: every visible table pin is annotated with `{distance}m {Direction} ({bearing}°)`, e.g. `1240m NorthEast (45°)`. Labels appear only during the capture, not in the live UI
- Discord message includes a `{spawnDir}` placeholder, falling back to the player's position when the map is opened via the `M` key
- **Screen capture** (default) or **texture capture** with automatic fallback if the texture path fails
- **Clean captures**: all UI overlays (panels, buttons, hints, etc.) are hidden during capture; biome label inclusion is configurable
- Optional **Hide Clouds** during capture (screen and texture paths)
- Optional **spoiler-tagged** Discord attachments (`Spoiler Image Data`)
- **Server-authoritative config** when [ServerSync](https://thunderstore.io/c/valheim/p/DrakeMods/ServerSync/) is installed; otherwise a lightweight RPC sync for the same settings
- **Server-managed webhook URL**: when using the built-in RPC sync, the server pushes the webhook URL to clients in memory — it is never written to client config files, so players cannot see or copy it
- The **SEND MAP** control is hidden until a webhook URL is available (local or server-pushed); the **COPY MAP** control is always available
- Button labels reflect the bound hotkeys (`SEND MAP (F10)` / `COPY MAP (F11)`) and auto-update when the keys are re-bound

## Configuration

| Key | Notes |
|-----|--------|
| `Discord.Webhook URL` | Discord incoming webhook. Set on the server to push it to all clients without exposing it in their config files (RPC sync only). |
| `Discord.Message Template` | Supports `{player}`, `{biome}`, and `{spawnDir}` (e.g. ` — 1240m NorthEast (45°)`). When `{spawnDir}` is missing from the template, it is appended automatically so legacy configs still get spawn-direction info. |
| `Discord.Capture Method` | `ScreenCapture` (default) or `TextureCapture` |
| `Discord.Capture Super Size` | Screen-capture quality multiplier `1`–`4` |
| `Discord.Spoiler Image Data` | Discord spoiler attachment; default `false` |
| `Discord.Hide Clouds` | Strip cloud overlay for the capture; default `true` |
| `Discord.Show Biome In Capture` | Include biome label in captured map images; default `false`. Client-only — also toggled via the **Show Biome Text** toggle in the capture container. |
| `Controls.Screenshot Key` | Send-to-Discord hotkey while the large map is open; default `F10` |
| `Controls.Copy Key` | Copy-to-clipboard hotkey while the large map is open; default `F11` |
| `UI.Button Alignment` | `Left`, `Center`, or `Right` (default) — horizontal position of the capture button container |
| `Cartography Table Labels.Enabled` | Master toggle for the per-pin labels baked into the screenshot; default `true` |
| `Cartography Table Labels.Include Distance` | Prepend the distance to the label (e.g. `1240m NorthEast (45°)` vs `NorthEast (45°)`); default `true` |
| `Cartography Table Labels.Include Map Item Sources` | Also show the spawn label when the map is opened from a portable map item (e.g. ZenMap parchment), not just from a cartography table; default `false` |

When **ServerSync** is present, `Discord.Lock Configuration` is also available (standard ServerSync lock behavior).

## Server behavior

### RPC sync (no ServerSync installed)

These settings are pushed from the server to all clients on connect:

- `Discord.Capture Method`
- `Discord.Capture Super Size`
- `Discord.Spoiler Image Data`
- `Discord.Hide Clouds`
- `Cartography Table Labels.Enabled`
- `Discord.Webhook URL` — stored in memory only, never written to client config files

### ServerSync

When ServerSync is installed it manages the synced settings above (except `Webhook URL`, which remains local to each client under that workflow).

`Message Template`, `UI.Button Alignment`, `Show Biome In Capture`, hotkey bindings, and the remaining `Cartography Table Labels` entries are always local to each client.

## Dependencies

- [BepInEx Pack for Valheim 5.4.2333](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) (declared in the manifest)

Optional: [ServerSync](https://thunderstore.io/c/valheim/p/DrakeMods/ServerSync/) for the preferred config sync and lock workflow.
