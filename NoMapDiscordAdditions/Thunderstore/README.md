# NoMapDiscordAdditions

Two map workflows on Valheim's large map:

1. **SEND MAP** / **COPY MAP** — capture the visible map and either post it to a Discord webhook or paste it directly into Discord, Slack, or an image editor.
2. **MAP COMPILE** — visit cartography tables one by one and stitch every reading into a single high-resolution PNG you can save, copy, or send to Discord.

Cartography-table pins are decorated with distance/bearing-from-spawn captions baked into the captured image, and every potentially-noisy setting (capture method, message templates, output size, pin labels) can be server-synced so a host enforces the same look for everyone.

---

## SEND MAP / COPY MAP

The bottom-right of the large map gets a `Show Biome Text` toggle, a **SEND MAP** button (default `F10`), and a **COPY MAP** button (default `F11`).

![Large map UI — capture buttons in idle mode](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/1.jpg)

- **SEND MAP** posts the capture to your Discord webhook with a configurable message template
- **COPY MAP** writes the PNG to the system clipboard — paste straight into Discord, Slack, or an image editor
- Hold **Left CTRL** (configurable) when clicking **COPY MAP** to bypass the Discord-friendly cap and copy at up to 4096px on the long edge — useful when you want full quality for image-editor work
- The bound hotkey is shown in each button label (`SEND MAP (F10)` / `COPY MAP (F11)`) and updates automatically when the keys are re-bound
- The **SEND MAP** button is hidden until a webhook URL is available (locally or server-pushed); **COPY MAP** is always available
- Captures hide every UI overlay (panels, buttons, hints, hotbar) — only the map and (optionally) the biome label remain

| Discord post | Spoiler-tagged |
|---|---|
| ![SEND MAP — Discord post with biome and spawn-direction caption](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/1.png) | ![SEND MAP — spoiler-tagged attachment](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/5.png) |

---

## MAP COMPILE

A second panel appears on the bottom-left of the large map. The intent: walk between cartography tables, add each reading as a tile, and on **COMPILE** the mod composites every tile into a single PNG that preserves world coordinates. The session is never lost when you save/copy/send — pause your mapping adventure and resume it at the next table or after a restart.

| Idle — `START COMPILE` / `RESUME COMPILE (N)` | Compiling — `ADD TILE (N)` / `COMPILE (N)` / `SHARE (N)` / `CANCEL` |
|---|---|
| ![Compile panel — idle](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/1.jpg) | ![Compile panel — adding tiles](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/2.jpg) |

- **ADD TILE** is enabled only when the map was opened at a cartography table (no `M`-key adds, no portable map items)
- Re-adding within ~8m of an existing tile **replaces** that tile in place — re-shoot a table without ending up with duplicates
- The session is saved to disk after every add, scoped to the current world + character. If you crash, disconnect, or just go on an adventure, click **RESUME COMPILE (N)** the next time you open the map
- **COMPILE** opens the result panel:

![MAP COMPILED result panel — preview + SAVE / COPY / SEND TO DISCORD / DISCARD / DONE](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/7.png)

- **SAVE** writes a **full native-resolution** PNG to disk — it recomposes so the sharpest tile maps 1:1 and no tile is downscaled below what it was captured at (a 2000×500 capture stays 2000×500 in its region), giving you a zoomable, editable map. Capped at 8192px on the longest axis; the status line says whether you got native resolution or hit the clamp, and the filename includes the dimensions. The button then morphs into **COPY DIR** so a second click puts the containing folder on the clipboard
- **COPY** writes the PNG to the clipboard at the Discord-safe size (CTRL+COPY raises the cap to 4096px, same as **COPY MAP**)
- **SEND TO DISCORD** posts the composed image (Discord-safe size) with the compile message template (`{player}`, `{tileCount}` placeholders)
- **SAVE / COPY / SEND / DONE are all non-destructive** — your session is kept on disk and stays resumable. **DONE** drops you back into compile mode with every tile intact so you can keep adding tables. Only **DISCARD** (or **CANCEL** while compiling) deletes the session
- Closing the map while the result panel is open doesn't strand the session — on the next map open you get a **RESUME COMPILE (N)** button that drops you straight back into compile mode with every tile intact (it does **not** reopen the result panel; the transient compiled PNG is discarded — just click **COMPILE** again when you want a fresh result)

### Sharing tiles between players

In a no-map world each player only reveals the area around the tables they visit. **SHARE** lets a group pool coverage:

- **SHARE (N)** (shows **EXPORT (N)** if no webhook is set) exports every tile in your session as an individual PNG with its world rectangle embedded in the image itself. With a webhook configured the tiles are posted to Discord (one per message); copies are always saved to `BepInEx/config/NoMapDiscordAdditions/compile-share/out/<world>/` so you can drag them in manually.
- To receive: save the PNGs a teammate shared into `BepInEx/config/NoMapDiscordAdditions/compile-share/incoming`. The next time you open the large map during a compile session, tiles for **your current world** are merged into your active session automatically — then **COMPILE** composites yours and theirs together. (A Discord webhook is send-only, so import is folder-based rather than read back from Discord.)
- Re-importing or re-sharing the same tile updates it in place; tiles for a different world are skipped. Handled files move into `incoming/processed` or `incoming/ignored`.
- Tile sharing can be turned off entirely with `Map Compile.Enable Map Sharing` (server-synced) — the **SHARE/EXPORT** button disappears and nothing is auto-imported, so compile mode stays purely local.

| Composed PNG (4 tables stitched) | Sent to Discord | Spoiler-tagged |
|---|---|---|
| ![Composed map — 4 tiles stitched](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/2.png) | ![Compile — Discord post](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/4.png) | ![Compile — spoiler-tagged Discord post](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/3.png) |

---

## Cartography-table pin labels

Every visible cartography-table pin in a capture is decorated with `{distance}m {Direction} ({bearing}°)` — e.g. `1240m NorthEast (45°)`. Labels render **only during the capture**, never in the live UI, and respect Valheim's icon filters, viewport, and shared-map fade. The Discord message template gets a matching `{spawnDir}` placeholder; if your map is opened via the `M` key (no table involved), `{spawnDir}` falls back to the player's current position so Discord captures always include a direction.

### Naming a table

A table can carry a **human-readable name** that flows into the Discord message (`{table}` placeholder) and the compiled-map captions (`{name} — {distance}m {Direction}`). There's no separate naming UI — the mod reads the closest named map pin sitting on the table (within ~8m):

- ZenMap auto-pins every cartography table with the vanilla "house" icon. **Rename that pin** and you've named the table.
- Or drop a **Town pin** on the table and name that.

Leave the pin unnamed and nothing changes — captions and messages fall back to just the direction, exactly as before. The name is captured the moment you add a tile in MAP COMPILE and stored in the session, so it stays correct even when the table is far away or unloaded at compile time.

---

## Configuration

### Discord

| Key | Notes |
|---|---|
| `Discord.Webhook URL` | Discord incoming webhook. Set on the server to push it to all clients without exposing it in their config files (RPC sync only). |
| `Discord.Message Template` | Supports `{player}`, `{biome}`, `{spawnDir}` (e.g. ` — 1240m NorthEast (45°)`), and `{table}` (the cartography table's name — see below). When `{spawnDir}` or `{table}` is missing from the template it is appended automatically. **Server-synced.** |
| `Discord.Spoiler Image Data` | Tag attachments as Discord spoilers; default `false`. **Server-synced.** |
| `Discord.Hide Clouds` | Strip the cloud overlay before capture; default `true`. **Server-synced.** |
| `Discord.Show Biome In Capture` | Include biome label in captured map images; default `false`. Client-only — also toggled via the **Show Biome Text** toggle on the map. |
| `Discord.Send Max Dimension` | Cap on the longest pixel dimension of any image sent to Discord OR copied via COPY MAP / COPY (compile). Default `2560`, range `512`–`8192`. Keeps 4K screens under Discord's 10MB free-tier limit. **Server-synced.** |

### General

| Key | Notes |
|---|---|
| `General.Capture Method` | `ScreenCapture` (default) or `TextureCapture`. **Server-synced.** |
| `General.Capture Super Size` | Screen-capture quality multiplier `1`–`4`. **Server-synced.** |
| `General.Normalize Capture Lighting` | Render the map as if at noon in `TextureCapture` mode so tiles captured at different times of day don't produce dark/light seams in a compiled map; default `true`. Client-only. |
| `General.Enable Logs` | Print info/warning messages to the BepInEx console and Player.log; default `false`. Turn on if you need to investigate a problem. |

### Map Compile

| Key | Notes |
|---|---|
| `Map Compile.Max Output Dimension` | Longest pixel dimension of the composed PNG used for the **preview, COPY and SEND TO DISCORD**. Default `2560`, range `512`–`8192`. Default keeps dense compositions under Discord's 10MB cap; raise to 3072 for sharper output, or 4096+ if you don't plan to send via Discord. **Does not affect SAVE** — SAVE always writes full native resolution (hard-capped at 8192px). **Server-synced.** |
| `Map Compile.Compile Message Template` | Discord message used by **SEND TO DISCORD** in the compile result panel. Supports `{player}`, `{tileCount}`. Default `"{player} compiled a map from {tileCount} cartography tables."` **Server-synced.** |
| `Map Compile.Enable Map Sharing` | Master toggle for cross-player tile sharing; default `true`. When off, the **SHARE/EXPORT** button is hidden and the `compile-share/incoming` folder is no longer auto-imported — compile mode stays purely local. A server can disable sharing for everyone. **Server-synced.** |
| `Map Compile.Share Message Template` | Discord message sent once (with the first attachment) by **SHARE TILES**. Supports `{player}`, `{tileCount}`. Default points teammates at the `compile-share/incoming` folder. **Server-synced.** |

### Pin Label

| Key | Notes |
|---|---|
| `Pin Label.Enabled` | Master toggle for the per-pin labels baked into the screenshot; default `true`. **Server-synced.** |
| `Pin Label.Include Distance` | Prepend the distance (`1240m NorthEast (45°)` vs `NorthEast (45°)`); default `true`. **Server-synced.** |
| `Pin Label.Include Map Item Sources` | Also show the spawn label when the map is opened from a portable map item (e.g. ZenMap parchment), not just from a cartography table; default `false`. **Server-synced.** |
| `Pin Label.Show on Compile Mode` | Whether the captions are stamped onto the **MAP COMPILE** composite — one per cartography table, drawn on top with an outline (still gated by `Pin Label.Enabled`); default `true`. Disable for label-free compiled maps without affecting plain COPY/SEND. **Server-synced.** |

### Controls

| Key | Notes |
|---|---|
| `Controls.Screenshot Key` | SEND MAP hotkey while the large map is open; default `F10`. |
| `Controls.Copy Key` | COPY MAP hotkey while the large map is open; default `F11`. |
| `Controls.Copy Full Resolution Modifier` | Hold while clicking **COPY MAP** / compile panel **COPY** to raise the cap to `4096`; default `LeftControl`. |

When **ServerSync** is present, `Discord.Lock Configuration` is also available (standard ServerSync lock behavior).

---

## Server behavior

### Built-in RPC sync (no ServerSync installed)

These settings are pushed from the server to all clients on connect:

- `General.Capture Method`
- `General.Capture Super Size`
- `Discord.Spoiler Image Data`
- `Discord.Hide Clouds`
- `Discord.Send Max Dimension`
- `Discord.Message Template`
- `Map Compile.Max Output Dimension`
- `Map Compile.Compile Message Template`
- `Map Compile.Enable Map Sharing`
- `Map Compile.Share Message Template`
- `Pin Label.Enabled`
- `Pin Label.Include Distance`
- `Pin Label.Include Map Item Sources`
- `Pin Label.Show on Compile Mode`
- `Discord.Webhook URL` — stored in memory only, never written to client config files

### ServerSync

When ServerSync is installed it manages the synced settings above (except `Webhook URL`, which remains local to each client under that workflow).

`Show Biome In Capture`, `Enable Logs`, hotkey bindings, and the modifier key are always local to each client.

---

## Dependencies

- [BepInEx Pack for Valheim 5.4.2333](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) (declared in the manifest)
- [JsonDotNET 4.5.2](https://thunderstore.io/c/valheim/p/ValheimModding/JsonDotNET/) (declared in the manifest) — used to persist compile sessions to disk
- [ZenMap ≥ 1.7.3](https://thunderstore.io/c/valheim/p/ZenDragon/ZenMap/) (declared in the manifest) — required for MAP COMPILE. 1.7.3 also fixes a `Graphics.CopyTexture` size-mismatch error on expanded worlds (e.g. a 4× map); update ZenMap if you see that error

Optional: [ServerSync](https://thunderstore.io/c/valheim/p/DrakeMods/ServerSync/) for the preferred config sync and lock workflow.

---

## Support & questions

Found a bug, have a question, or want to request a feature? Reach me as **virtualbjorn** on either of the Valheim modding Discord servers:

- **OdinPlus** — https://discord.gg/mbkPcvu9ax
- **ValheimModding** — https://discord.gg/MJWtxQs

Ping `virtualbjorn` in the relevant modding/support channel and include your `BepInEx/LogOutput.log` if you're reporting an issue. You can also open an issue on the [GitHub repo](https://github.com/codewithbjim/ValheimMods).
