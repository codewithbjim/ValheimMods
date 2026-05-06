# Changelog

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
