# Changelog

## 1.0.4

### Map Compile (new)

- Added **MAP COMPILE** mode — stitch every cartography table you visit into one large PNG
  - Bottom-left panel on the large map: `START COMPILE` / `RESUME COMPILE (N)` (idle), `ADD TILE (N)` / `FINISH (N)` / `CANCEL` (active)
  - `ADD TILE` is enabled only when the map was opened at a cartography table; tiles within ~8m of an existing tile replace it in place (lets you re-shoot a table without ending up with duplicates)
  - Per-world, per-player session persisted to disk after every add — a crash or disconnect mid-mapping doesn't lose progress
  - `FINISH` opens a result panel with a thumbnail and five actions: **SAVE** (writes PNG to disk; morphs into **COPY DIR** afterward to put the folder on the clipboard), **COPY** (clipboard), **SEND TO DISCORD** (uses webhook + compile message template), **DISCARD**, **DONE**
  - Added `Map Compile.Max Output Dimension` config (default `2560`, range `512`–`8192`, server-synced) — caps the longest pixel dimension of the composed PNG; default keeps even dense compositions under Discord's 10MB free-tier attachment limit
  - Added `Map Compile.Compile Message Template` config (default `"{player} compiled a map from {tileCount} cartography tables."`, server-synced) — supports `{player}` and `{tileCount}` placeholders

### Capture / clipboard

- Added `Discord.Send Max Dimension` config (default `2560`, range `512`–`8192`, server-synced) — caps every image sent to Discord **or** copied to the clipboard, keeping 4K / ultrawide captures under Discord's 10MB free-tier limit
- Added `Controls.Copy Full Resolution Modifier` config (default `LeftControl`) — hold while clicking **COPY MAP** (or **COPY** in the compile result panel) to raise the cap to `4096` for high-fidelity edits in an image editor
- `MapCaptureTexture.CaptureMap` now accepts a custom output resolution; CTRL+COPY in texture-capture mode renders the shader at 4096×2304 fragments instead of upscaling a 1920×1080 capture
- `Show Biome Text` toggle and the SEND/COPY buttons now share a single uniform row height with the new compile panel

### Server sync / config

- More settings now flow through ServerSync **and** the built-in RPC sync: `Discord.Send Max Dimension`, `Discord.Message Template`, `Map Compile.Max Output Dimension`, `Map Compile.Compile Message Template`, `Pin Label.Include Distance`, `Pin Label.Include Map Item Sources`
- RPC protocol bumped to version `8` — older clients/servers will silently fall back to local config if versions don't match
- Renamed config section `Cartography Table Labels` → `Pin Label`
- Moved `Capture Super Size` and `Capture Method` from `Discord` to `General`
- Removed `UI.Button Alignment` config — capture buttons are now pinned bottom-right and the compile panel bottom-left, sharing one row at the bottom of the map

### Logging / quality of life

- Added `General.Enable Logs` config (default `false`) — silences info/warning messages from this mod in the BepInEx console and Player.log unless explicitly enabled
- All internal logging routed through a single `ModLog` helper so the toggle catches every call site

## 1.0.3

- Added **COPY MAP** button beside **SEND MAP** — copies the captured PNG to the system clipboard (paste straight into Discord/Slack/etc.)
- Added `Controls.Copy Key` config (default `F11`) — clipboard-copy hotkey while the large map is open
- Button labels now show the bound hotkey, e.g. `SEND MAP (F10)` / `COPY MAP (F11)` — auto-update when the keys are re-bound
- Added **Cartography Table Labels**: every visible cartography-table pin in the screenshot is decorated with a `{distance}m {Direction} ({bearing}°)` caption (e.g. `1240m NorthEast (45°)`), baked into the captured image
- Pin labels render **only during capture**, not every frame, and only for pins vanilla is currently rendering (icon-type filter, viewport, shared-map fade all respected)
- Added `Cartography Table Labels.Enabled` config (default `true`, server-synced) — master switch for the per-pin labels
- Renamed config section `Spawn Label` → `Cartography Table Labels`
- Removed `Use Full Direction Names` config — directions are now always spelled out (`NorthEast` instead of `NE`)
- Removed `" of Spawn"` suffix from labels (the bearing makes the reference implicit)
- `{spawnDir}` now falls back to the player's current position when the map is opened via the `M` key (no cartography table or map item involved), so Discord captures always include a direction
- `{spawnDir}` is auto-appended to the Discord message when the placeholder is missing from a legacy `Message Template` value (BepInEx never overwrites existing config defaults)
- Removed the Discord hint that was appended to the cartography-table hover prompt — the in-map buttons (now labeled with their hotkeys) make it discoverable
- Added **Show Biome Text** toggle to the capture container (cloned from Valheim's native SharedPanel toggle) — checks whether the biome label appears in captured images
- Added `Discord.Show Biome In Capture` config (default `false`): client-only, also writable via the in-map toggle
- Large-map canvas is now elevated to the top sort order during screen capture, preventing external HUD elements from overlapping the captured image
- Changed `UI.Button Alignment` default from `Center` to `Right`

## 1.0.2

- Renamed internal button class from `DiscordButton` to `CaptureButton`
- Capture now hides all large-map UI overlays (panels, buttons, hints, etc.) except the map image and biome label, producing cleaner screenshots
- Added `UI.Button Alignment` config (`Left`, `Center`, `Right`) to reposition the capture button container without restarting
- Server-managed webhook URL: when using the built-in RPC sync, the server pushes `Webhook URL` to clients in memory only — it is never written to client config files

## 1.0.1

- Document and package release aligned with current mod behavior
- README: spaced config key names, screen capture as default, spoiler/cloud options, server-synced field list
- Capture UX: webhook required before the UI control is usable; minimap `Button` UI hidden during capture for clean images
- Hide clouds applies to both screen and texture capture paths

## 1.0.0

- Initial Thunderstore package setup
- Added single capture button flow (texture or screen based on config)
- Added texture capture fallback to screen capture
- Added server-authoritative config sync path
