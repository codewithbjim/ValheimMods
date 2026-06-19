# NoMapDiscordAdditions — Wiki

A Valheim client/server mod that adds two map workflows to the large map:

1. **SEND MAP / COPY MAP** — capture the visible map and post it to a Discord webhook or paste it straight into Discord, Slack, or an image editor.
2. **MAP COMPILE** — visit cartography tables one at a time and stitch every reading into a single high-resolution PNG you can save, copy, or send to Discord.

Built for no-map (and normal) servers where the map is revealed table-by-table. Captions, output format, and pin labels can be server-synced so a host enforces one look for everyone.

---

## Contents

- [Installation](#installation)
- [Quick start](#quick-start)
- [SEND MAP / COPY MAP](#send-map--copy-map)
- [MAP COMPILE](#map-compile)
  - [The compile panel](#the-compile-panel)
  - [Adding, updating, and removing tiles](#adding-updating-and-removing-tiles)
  - [Choosing which pins appear (PINS panel)](#choosing-which-pins-appear-pins-panel)
  - [Compiling: the result panel](#compiling-the-result-panel)
  - [Sharing tiles between players](#sharing-tiles-between-players)
  - [Resuming, suspending, and clearing a session](#resuming-suspending-and-clearing-a-session)
- [Pin captions and table names](#pin-captions-and-table-names)
- [Map styles](#map-styles)
- [Output format](#output-format)
- [Configuration reference](#configuration-reference)
- [Server behavior & sync](#server-behavior--sync)
- [Files on disk](#files-on-disk)
- [Troubleshooting](#troubleshooting)
- [FAQ](#faq)
- [Support](#support)

---

## Installation

Install with a mod manager (r2modman / Thunderstore Mod Manager) — it pulls the dependencies for you. To install manually, drop the DLL into `BepInEx/plugins` and install every dependency below.

**Dependencies** (all declared in the manifest):

| Dependency | Why |
|---|---|
| [BepInExPack Valheim 5.4.2333](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) | The mod loader. |
| [JsonDotNET 13.0.4](https://thunderstore.io/c/valheim/p/ValheimModding/JsonDotNET/) | Persists compile sessions to disk. |
| [Jotunn 2.29.0](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/) | Server-authoritative config sync (`SynchronizationManager`). |
| [ZenMap ≥ 1.8.0](https://thunderstore.io/c/valheim/p/ZenDragon/ZenMap/) | Required for MAP COMPILE; also self-heals a `Graphics.CopyTexture` size-mismatch error on expanded worlds. |

> ZenMap already depends on Jotunn, so most setups already have Jotunn installed.

**Client vs. server:** install on every client that wants the buttons. Installing on the server too lets the host push synced settings (webhook URL, message templates, pin-label policy) to all clients. The mod is safe to run client-only.

---

## Quick start

1. Open the large map (`M`, or interact with a cartography table).
2. **To share a single view:** click **COPY MAP** (`F11`) and paste into Discord, or set a webhook and click **SEND MAP** (`F10`).
3. **To build a stitched map:** click **START COMPILE**, then walk between cartography tables clicking **ADD TILE** at each. When you've covered the ground you want, click **COMPILE** and **SAVE** / **COPY** / **SEND TO DISCORD**.

---

## SEND MAP / COPY MAP

The bottom-right of the large map gets a `Show Biome Text` toggle, a **SEND MAP** button (default `F10`), and a **COPY MAP** button (default `F11`).

- **SEND MAP** posts the capture to your Discord webhook with a configurable message template, encoded as JPEG or IndexedPNG per [`Output.Output Format`](#output-format). Hidden until a webhook URL is available (set locally or pushed by the server).
- **COPY MAP** writes the image to the system clipboard at full resolution (capped at 8192 px on the long edge) — paste straight into Discord, Slack, or an image editor. Always available, no webhook needed. Clipboard output is always JPEG (see [Output format](#output-format)).
- The bound hotkey is shown in each button label (`SEND MAP (F10)` / `COPY MAP (F11)`) and updates automatically when you rebind the keys.
- While a capture runs, both buttons grey out and the active one shows `SENDING...` / `COPYING...`, so you can't queue duplicate clicks during a slow (styled or full-resolution) capture.

**What's in the shot:**

- Every large-map UI overlay (panels, buttons, hints, hotbar) is hidden — only the map and, optionally, the biome label remain.
- `Player` and `Death` pins are hidden by default (they're session-scoped, and the compile composite already excludes them). **Hold Left CTRL** at click time to keep them in for a one-off share (live deaths, party positions).
- Every named pin's caption is force-shown for the capture frame, so a zoomed-out shot still includes pin names (vanilla normally hides them past a zoom threshold).
- Boss/location captions are localized and stripped of rich-text styling (`<color=orange>THE ELDER</color>` → `The Elder`).

---

## MAP COMPILE

The intent: walk between cartography tables, add each reading as a **tile**, and on **COMPILE** the mod composites every tile into one PNG that preserves world coordinates. A session is never lost when you save/copy/send — pause your mapping run and resume it at the next table or after a restart.

### The compile panel

A panel appears on the bottom-left of the large map. Its buttons change with the session state:

| State | Buttons |
|---|---|
| **Idle** (no active session) | `START COMPILE`, or `RESUME COMPILE (N)` + `CLEAR` when a saved session exists |
| **Compiling** (active session) | `ADD TILE (N)` / `UPDATE TILE (N)`, `PINS`, `COMPILE (N)`, `SHARE (N)` / `EXPORT (N)`, `EXIT` |

`N` is the current tile count.

### Adding, updating, and removing tiles

- **ADD TILE** is enabled only when the map was opened at a cartography table (no `M`-key adds; portable map items are gated by [`Map Compile.Allow From Map Items`](#map-compile)).
- Re-adding within ~8 m of an existing tile **replaces** that tile in place. When you're standing at an already-added table the button reads **UPDATE TILE** and re-shoots it — so you never end up with duplicates.
- **REMOVE TILE:** at an already-added table, **hold Left CTRL** and `UPDATE TILE` morphs to a red **REMOVE TILE** that deletes just that one tile and its PNG, leaving the rest of the session untouched. The L-CTRL hold is the accident guard — a plain click can't wipe a hard-won tile.
- Each tile is saved to disk immediately after capture, scoped to the current **world + character**. A crash or disconnect mid-run loses nothing.

> **The "L-CTRL = destructive variant" rule.** Throughout the compile panel, holding **Left CTRL** turns a button's safe action into its destructive one, and the label turns **red** to telegraph it: `UPDATE TILE` → `REMOVE TILE`, `EXIT` → `CLEAR ALL`. Nothing destructive happens on a plain click.

### Choosing which pins appear (PINS panel)

**PINS** (between the capture controls and `COMPILE`) opens the **INCLUDE PINS** overlay — a grid of every distinct pin icon that actually falls inside your captured tiles, each a clickable thumbnail.

- Click a thumbnail to drop that pin kind from the next `COMPILE`; click again to restore it. **ALL** / **NONE** toggle the whole set. **DONE** (or a click on the dimmed backdrop) closes the panel.
- Pins are grouped by **the sprite they actually draw with**, so vanilla kinds and mod-added pins (each mod ships its own sprite) get their own rows automatically — there's no hardcoded list to fall out of date.
- The list shows only pins that land **inside a captured tile**, so it mirrors exactly what the composite would stamp. A pin in an un-mapped region never clutters it.
- Default is **everything included** — a kind is only hidden once you explicitly turn it off. A pin type that first appears after you last opened the panel is included automatically.
- The `PINS` button label shows the hidden count (`PINS (3 off)`) so you can tell pins are held back without opening the panel.
- The selection is **saved with the session** and restored on `RESUME COMPILE`. It resets to all-included on `START` / `CLEAR ALL` / `DISCARD`.

> The **bed / spawn** pin is always kept off the composite (and out of the PINS list) — it marks your spawn, not a wayfinding feature. The always-on **`start`** temple landmark is unaffected: it still composites, it just isn't offered as a toggle.

### Compiling: the result panel

**COMPILE** composites every tile and opens the result panel with a preview and five actions:

- **SAVE** writes a **full native-resolution** image to disk. It recomposes so the sharpest tile maps 1:1 and no tile is downscaled below its capture resolution (a 2000×500 capture stays 2000×500 in its region), giving a zoomable, editable map. Capped at 8192 px on the longest axis. Format follows [`Output.Output Format`](#output-format); the status line reports dimensions, format, and encoded size (e.g. `Saved 7234×4521px JPEG q88 3.4 MB native resolution`) and the filename includes the dimensions. The button then morphs into **COPY DIR** so a second click puts the containing folder on the clipboard.
- **COPY** writes a JPEG to the clipboard at the preview size (4096 px cap on the long edge).
- **SEND TO DISCORD** posts the composed image with the compile message template (`{player}`, `{tileCount}` placeholders), encoded per `Output.Output Format`. The status updates to `Sent to Discord.` / `Send failed — see log.` when the request finishes.
- **DISCARD** deletes the session.
- **DONE** drops you back into compile mode with every tile intact, so you can keep adding tables.

**SAVE / COPY / SEND / DONE are all non-destructive** — your session is kept on disk and stays resumable. Only **DISCARD** (or **CLEAR ALL** while compiling) deletes it. Closing the map with the result panel open doesn't strand the session: the next map open shows **RESUME COMPILE (N)** (it does not reopen the result panel — the transient compiled PNG is discarded; click **COMPILE** again for a fresh result).

### Sharing tiles between players

In a no-map world each player only reveals the area around the tables they visit. **SHARE** lets a group pool coverage.

- **SHARE (N)** (shows **EXPORT (N)** if no webhook is set) exports every tile in your session as an individual PNG with its world rectangle embedded in the image itself (a standard `tEXt` chunk — still a normal viewable PNG). With a webhook the tiles post to Discord (one per message, up to 5 per message); copies are always written to `compile-share/out/<world>/` so you can drag them in manually.
- **To receive:** save the PNGs a teammate shared into `compile-share/incoming`. The next time you open the large map during a compile session, tiles for **your current world** are merged into your active session automatically — then **COMPILE** stitches yours and theirs together.
- Tiles dedup by stable identity (world + rect + capturer), so re-importing or re-sharing the same tile **updates it in place** instead of stacking. Tiles for a different world are skipped. Handled files move to `incoming/processed` or `incoming/ignored`.
- Turn sharing off entirely with [`Map Compile.Enable Map Sharing`](#map-compile) (server-synced): the SHARE/EXPORT button disappears and nothing is auto-imported, keeping compile mode purely local.

> A Discord webhook is send-only, so import is folder-based rather than read back from Discord.

### Resuming, suspending, and clearing a session

- **EXIT** (compiling) **suspends** the session — kept on disk and resumable any time. This is the safe, non-destructive way to leave compile mode.
- **EXIT + hold Left CTRL** → red **CLEAR ALL (L-CTRL)** wipes the whole session: every in-memory tile and the on-disk session folder.
- **RESUME COMPILE (N)** (idle) drops you straight back into a saved session with every tile intact.
- **CLEAR** (idle, next to `RESUME COMPILE`) discards a saved session without resuming it. Like every destructive control it stays greyed out and labelled `CLEAR (L-CTRL)` until you hold **Left CTRL**.

---

## Pin captions and table names

Pins carry their own **name caption** into a capture. Vanilla hides pin-name captions past a zoom threshold (and unless hovered/held), so a zoomed-out screenshot would normally lose them — during a SEND / COPY capture the mod force-shows every named pin's caption for the capture frame, then restores the live state immediately. Boss/location names are localized and stripped of rich-text styling (`<color=orange>THE ELDER</color>` → `The Elder`). On the MAP COMPILE composite the same names are stamped on top of the stitched map.

### Naming a table

A table can carry a **human-readable name** that flows into the Discord message (the `{table}` placeholder) and the compiled-map captions. There's no separate naming UI — the mod reads the closest named map pin sitting on the table (within ~8 m):

- ZenMap auto-pins every cartography table with the vanilla "house" icon. **Rename that pin** and you've named the table.
- Or drop a **Town pin** on the table and name that.

Leave the pin unnamed and nothing changes. The name is captured the moment you add a tile and stored in the session, so it stays correct even when the table is far away or unloaded at compile time.

---

## Map styles

[`Map Style.Style`](#map-style) (client-only) replaces the live in-game map look with a stylized render for SEND / COPY captures **and** MAP COMPILE tiles, reconstructed from Valheim's own map data — explored areas show full detail, unexplored stays fogged. Five settings:

- **None** *(default)* — the normal in-game map look (no styling).
- **Old Map** — aged-parchment chart: biome wash, Perlin grain, height-contour and biome-edge lines.
- **Chart** — flat topographic chart, contour and biome-edge lines, no parchment grain.
- **Topographical** — shaded-relief terrain with hillshading, contours and biome-edge lines.
- **Satellite** — naturalistic shaded terrain, no line work.

A styled capture always uses the texture-capture path. The per-pixel passes run on a background thread so the game doesn't stall, and render at native on-screen map size (the styled output is intrinsically low-frequency, so this is visually indistinguishable from a full-resolution render while keeping captures responsive).

---

## Output format

[`Output.Output Format`](#output) drives the on-disk format for compile **SAVE** and the wire format for every **SEND TO DISCORD** capture. The file extension follows the format (`.jpg` / `.png`).

- **JPEG** *(default)* — 24bpp DCT at the configured quality. Smallest files (~5-8× smaller than a lossless PNG); pin captions stay readable at quality ~85+, below that the edges soften.
- **IndexedPNG** — 8bpp palette built via median-cut on a 15-bit colour histogram with Floyd-Steinberg dither. No DCT ringing, so pin labels stay crisp. Maps quantize very well (~6 dominant biome colours), so 32-64 colours is usually indistinguishable from full colour while being ~5× smaller.

**COPY MAP** clipboard output always uses JPEG regardless of this setting — indexed-PNG palette quantization on an 8192² image takes multiple seconds on the main thread, long enough that the COPY button looks frozen. The shared `Output.JPEG Quality` controls both paths.

---

## Configuration reference

Config lives at `BepInEx/config/com.virtualbjorn.nomapdiscordadditions.cfg`. Most settings can be edited in-game via the ConfigurationManager window (F1, if installed). **Server-synced** entries are admin-only and locked on clients when a server pushes them.

### Discord

| Key | Default | Notes |
|---|---|---|
| `Webhook URL` | *(empty)* | Discord incoming webhook. **Server-synced** so sending works for every client; hidden from the in-game config window so non-admin players can't read a server-pushed URL. |
| `Message Template` | `{player} shared a map update from {biome}{table}` | Supports `{player}`, `{biome}`, `{table}` (expands to ` — <name>` from the named pin on the table). A missing `{table}` is appended automatically; the legacy `{spawnDir}` placeholder is stripped if present. **Server-synced.** |
| `Spoiler Image Data` | `false` | Tag attachments as Discord spoilers. **Server-synced.** |

### Output

| Key | Default | Notes |
|---|---|---|
| `Output Format` | `JPEG` | `JPEG` or `IndexedPNG`. On-disk format for compile SAVE and wire format for SEND TO DISCORD. Client-only. |
| `JPEG Quality` | `88` | `50`-`100`. Used for `Output Format = JPEG` and for all COPY output (always JPEG). 88 keeps captions readable; below 80 blurs text. Client-only. |
| `Indexed PNG Colours` | `64` | `16`-`256`. Palette size for IndexedPNG. 32-64 is usually indistinguishable from full colour; raise toward 128-256 if Map Style gradients band. Client-only. |

### General

| Key | Default | Notes |
|---|---|---|
| `Normalize Capture Lighting` | `true` | Render the map at fixed neutral-noon lighting so tiles captured at different times/biomes don't seam. Applies to every capture path. Client-only. |
| `Enable Logs` | `false` | Print this mod's info/warning messages to the BepInEx console and Player.log. Turn on to investigate a problem. |

### UI

| Key | Default | Notes |
|---|---|---|
| `Hide Clouds` | `true` | Strip the cloud overlay before capture. **Server-synced.** |
| `Show Biome Text` | `false` | Include the biome label in captured images. Client-only — also toggled via the **Show Biome Text** toggle on the map. |

### Map Style

| Key | Default | Notes |
|---|---|---|
| `Style` | `None` | `None`, `OldMap`, `Chart`, `Topographical`, `Satellite` — see [Map styles](#map-styles). Client-only. |

### Map Compile

| Key | Default | Notes |
|---|---|---|
| `Compile Message Template` | `{player} compiled a map from {tileCount} cartography tables.` | SEND TO DISCORD message from the result panel. Supports `{player}`, `{tileCount}`. **Server-synced.** |
| `Enable Map Sharing` | `true` | Master toggle for cross-player tile sharing. Off hides SHARE/EXPORT and disables auto-import. **Server-synced.** |
| `Allow From Map Items` | `true` | Allow compile when the map is opened from a portable map item (e.g. ZenMap parchment), not just a cartography table. **Server-synced.** |
| `Share Message Template` | *(points teammates at the incoming folder)* | Message sent once with the first SHARE attachment. Supports `{player}`, `{tileCount}`. **Server-synced.** |

### Controls

| Key | Default | Notes |
|---|---|---|
| `Send Key` | `F10` | SEND MAP hotkey while the large map is open. Client-only. |
| `Copy Key` | `F11` | COPY MAP hotkey while the large map is open. Client-only. |

> Some config keys evolve between releases — see the [CHANGELOG](CHANGELOG.md) for renames and removals (e.g. `Screenshot Key` → `Send Key`, the removed `Capture Method` / `Capture Super Size` / `Send Max Dimension` / `Max Output Dimension`). BepInEx never overwrites a value you've already saved.

---

## Server behavior & sync

Config sync is handled by **[Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/)'s `SynchronizationManager`** (a required dependency). On connect, the server pushes its value for every synced setting to all clients, the entry is locked client-side, and each client's local value is cached and restored on disconnect. There's no separate ServerSync install and no RPC protocol version to keep in step.

**Server-synced** (a host enforces one look for everyone): `Discord.Webhook URL`, `Discord.Message Template`, `Discord.Spoiler Image Data`, `UI.Hide Clouds`, `Map Compile.Compile Message Template`, `Map Compile.Enable Map Sharing`, `Map Compile.Allow From Map Items`, and `Map Compile.Share Message Template`.

**Always client-local** (personal preferences — forcing them on the party would be noisy): all `Output.*`, `Map Style.Style`, `UI.Show Biome Text`, `General.Normalize Capture Lighting`, `General.Enable Logs`, and the hotkey bindings.

---

## Files on disk

Everything lives under `BepInEx/config/NoMapDiscordAdditions/`:

| Path | Contents |
|---|---|
| `compile-sessions/<world>/<character>/` | Active compile session: tile PNGs + an `index.json` (tile world rects, table names, and your PINS selection). |
| `compile-share/out/<world>/` | Exported share tiles (self-describing PNGs) for manual drag-into-Discord. |
| `compile-share/incoming/` | Drop teammates' shared tiles here; auto-imported on the next map open during a session. |
| `compile-share/incoming/processed/` | Imported tiles are moved here. |
| `compile-share/incoming/ignored/` | Tiles for a different world (skipped) are moved here. |

Saved/compiled output (from **SAVE**) goes where the status line reports — typically a `compiled-maps`-style folder; the **COPY DIR** button puts the exact folder on your clipboard.

---

## Troubleshooting

**The SEND MAP button isn't there.** It's hidden until a webhook URL is available — set `Discord.Webhook URL` locally, or have the server push one. COPY MAP is always present.

**MAP COMPILE / ADD TILE is greyed out.** `ADD TILE` only enables when the map was opened **at a cartography table** (or, if `Allow From Map Items` is on, while reading a map item). Opening with `M` won't enable it.

**`Graphics.CopyTexture called with mismatching texture sizes` in the log.** This is a ZenMap issue on expanded/4× worlds. **Update ZenMap to ≥ 1.8.0** — it self-heals the mismatch. Don't downgrade or patch around it.

**Pins look washed out / wrong size / stretched on the composite.** These were fixed in 1.2.0 (linear-space compositing, stable vanilla pin sizing, sprite aspect-ratio preservation, off-screen ZenMap pin colors). Make sure you're on the latest version.

**A SEND attachment failed or the post is missing.** Discord's free webhook attachment limit is ~10 MB. The chosen `Output Format` encoder normally keeps a capture well under that, but if you've forced a very large output, switch to JPEG or lower `JPEG Quality`. Turn on `General.Enable Logs` and check `BepInEx/LogOutput.log` for the webhook response.

**A teammate's shared tiles didn't import.** They must be in `compile-share/incoming`, for **your current world**, and you must open the large map **during an active compile session**. Wrong-world tiles move to `incoming/ignored`.

**Nothing in the logs.** This mod is quiet by default — set `General.Enable Logs = true` to see its messages.

---

## FAQ

**Does this work without ZenMap?** SEND/COPY do, but MAP COMPILE needs ZenMap (it's a hard dependency for the table reveal/coordinate machinery).

**Does it work on a normal (map-enabled) world?** Yes. It's built for no-map servers but works anywhere the large map opens.

**Is the webhook URL safe from non-admin players?** It's hidden from the in-game config window, so it isn't displayed. It still lives in client memory / on the wire (same exposure as any synced setting) — treat it as "not displayed," not a hard secret.

**Will updating reset my settings or sessions?** No. BepInEx never overwrites a saved config value, and compile sessions persist on disk across updates. Watch the [CHANGELOG](CHANGELOG.md) for the rare key rename (rebound hotkeys may need re-setting).

**Can I keep player/death pins in a SEND/COPY shot?** Yes — hold **Left CTRL** at click time.

**Can I hide noisy pins from a compiled map?** Yes — use the **PINS** panel in compile mode to drop any pin kind from the composite.

---

## Support

Found a bug, have a question, or want to request a feature? Reach **virtualbjorn** on either Valheim modding Discord:

- **OdinPlus** — https://discord.gg/mbkPcvu9ax
- **ValheimModding** — https://discord.gg/MJWtxQs

Include your `BepInEx/LogOutput.log` when reporting an issue, or open one on the [GitHub repo](https://github.com/codewithbjim/ValheimMods).
