# Changelog

## 1.1.1

### Map Compile

- Added a **`REMOVE TILE`** button to the compile panel — wipes the tile belonging to the cartography table you're currently standing at, deleting its PNG and updating the session index. Mirrors the dedup-by-table-position rule the existing `UPDATE TILE` uses, so it always acts on the same tile `UPDATE TILE` would replace. Lets you undo a tile capture without wiping the whole session via `CLEAR`
- The button only appears when you're at a table whose tile is already in the session (it pairs with `UPDATE TILE`); placed next to it in the panel so the trio at the current table — `UPDATE TILE`, `REMOVE TILE`, `COMPILE` — read together. Greyed out and labelled `REMOVE (L-CTRL)` until you hold **Left CTRL**, so a misclick on `UPDATE TILE`'s neighbour can't silently wipe a hard-won tile

## 1.1.0

### Output format (new — applies to SAVE and SEND TO DISCORD)

- New `Output` config section with three settings:
  - **`Output.Output Format`** — `JPEG` (default) or `IndexedPNG`. Drives the on-disk format for the compile result-panel **SAVE** button **and** the wire format for **SEND TO DISCORD** captures and compile sends. The file extension follows the format (`.jpg` / `.png`) so the on-disk filename matches the actual bytes
  - **`Output.JPEG Quality`** — 50-100, default `88`. Quality for the JPEG path; 88 keeps pin captions readable, below ~80 noticeably softens label edges
  - **`Output.Indexed PNG Colours`** — 16-256, default `64`. Palette size for the indexed-PNG encoder. Maps quantize very well (~6 dominant biome colours) so 32-64 is usually indistinguishable from a full-colour PNG while being ~5× smaller; raise to 128-256 if `Map Style` is enabled and the gradient shading is banding
- New indexed-PNG encoder — 8bpp palette built via median-cut on a 15-bit histogram with Floyd-Steinberg dither. Keeps pin-label edges crisp (no DCT ringing) while delivering JPEG-class compression for typical maps
- **COPY MAP** clipboard output always emits JPEG (regardless of the Output Format) — indexed-PNG palette quantization on an 8192² image takes multiple seconds on the main thread, long enough that the COPY button looks frozen; JPEG encodes the same image in well under a second. The shared `Output.JPEG Quality` controls both paths so a tuned value carries over
- SAVE status line now reports format, encoded size, and dimensions (`Saved 7234×4521px JPEG q88 3.4 MB native resolution`) so format A/B testing doesn't require opening the file
- SEND TO DISCORD compile send now updates the result-panel status with success/failure when the request finishes — previously the panel was stuck on `Sending to Discord...` forever

### Removed configs (replaced by the new pipeline)

- Removed **`General.Capture Method`** — texture capture is the only path now, and it now handles everything the screen-capture path used to. Compile tile capture follows the same simplification (texture-only)
- Removed **`General.Capture Super Size`** — implicit; the texture capture path now sizes off the on-screen map rect and scales up to the longest-edge cap automatically
- Removed **`Controls.Copy Full Resolution Modifier`** — COPY MAP now always captures at full resolution (capped at 8192 px on the longest edge). The CTRL modifier's purpose was raising the cap above the Discord-safe one; that cap is gone, so the modifier has nothing to gate
- Removed **`Discord.Send Max Dimension`** — replaced by the Output Format encoder. SEND now sizes the capture at the longest-edge cap and relies on JPEG / indexed-PNG compression to stay under Discord's 10 MB attachment limit (a typical capture lands ~2-5 MB at JPEG q88)
- Removed **`Map Compile.Max Output Dimension`** — preview/COPY/SEND use a hardcoded 4096 px cap (keeps recodes snappy at compose time); SAVE goes up to 8192 via the existing native path. The previous setting only ever clamped the preview, so its absence doesn't change SAVE behaviour

### Renamed

- **`Controls.Screenshot Key`** → **`Controls.Send Key`** (BepInEx won't migrate the binding — players who rebound this will need to re-set it)

### Default change

- **`Map Style.Style`** default flipped from `Topographical` back to `None`. The full styled pipeline (biome wash, contours, hillshade, fog smoothing) was rendering at the full 8192² capture size every send/copy, which froze the SEND button for 30+ seconds on a large explored map. Style rendering now runs at native screen resolution (the styled output is intrinsically low-frequency, so a 2K-class internal render is visually indistinguishable from 8K), and the default ships off so first-run captures stay snappy

### Capture pin handling (live SEND / COPY)

- New `PinCaptureFilter` runs for the duration of each SEND / COPY texture-capture frame:
  - Hides `Player` and `Death` pins for the capture — these are session-scoped and the compile path already excludes them, so SEND/COPY now matches compile behaviour out of the box. Hold **LEFT CTRL** at SEND / COPY click time to opt out for one-off shares (deaths, party positions)
  - Strips TMP rich-text tags from boss / location captions (`<color=orange>THE ELDER</color>` → `The Elder`) so localized styling doesn't leak into the captured image
  - Force-shows every named pin's caption GameObject for the capture frame — vanilla `UpdatePins` hides captions past the `m_showNamesZoom` threshold, so a zoomed-out screenshot previously missed every pin name. Caption state is restored immediately after the capture
  - Survives **ZenMap's** per-frame `ShowLabel(IsCursorOver || IsLabelTogglePressed)` postfix via a `[HarmonyAfter("ZenDragon.ZenMap")]` patch that re-activates the captions every frame between Apply and Restore
- Texture-capture path now blits the **pin name root** (`Minimap.m_pinNameRootLarge`) in addition to the icon root — captions appear in texture-mode captures too, matching what the old screen-capture path got via the back buffer
- Pin captions in the live capture and the compile composite render with a one-pass SDF outline (wider sample band painted black under the white face) — readable over snow plains, swamp, and other busy biomes without the per-offset stamp passes the previous outline did

### Map Compile

- **Pin icons** in the composite now read from each pin's live UI `Image.sprite` and `Image.color` (with `pin.m_icon` / `Minimap.GetSprite(type)` as fallbacks). Sprite swaps from mods, discovered-location pins, and boss head outlines now reflect in the composite — previously the compositor pulled the original asset (e.g. orange filled boss head) instead of what the player actually saw
- **Pin names** are now localized + rich-text-stripped before stamping. Boss / location pins stored as `$enemy_gdking` tokens render as `The Elder` (etc.) on the composite, with no leftover style tags
- **Pin captions** sample their font size + `FontStyles` from `Minimap.m_pinNamePrefab` so compile captions look identical to live-map captions (same absolute pixel size, same italic/smallcaps if vanilla uses them) instead of the previous hardcoded scale
- **Pin sizing** now applies a 0.25× scale on top of the composite/screen px-per-px ratio so pins read as discrete markers at composite resolution — pixel-proportional sizing made them feel oversized when the composite spanned many tables' worth of world
- **Per-tile pin cull**: pins whose world position falls in the black gaps between non-adjacent captured tiles are no longer stamped on the composite. The outer bounding-rect cull (rectangle containing every tile) used to draw pins on those "no data" regions
- **Compositor seam fix**: removed the 1-pixel-wide tile-boundary averaging. The floor/ceil destination math leaves a 1-pixel overlap column at every adjacent-tile boundary; averaging both tiles' edge pixels there produced a visibly different colour from the un-averaged interior columns, lighting up as faint vertical/horizontal seams on the composite. First-paint pixel wins now, so the boundary reads as a continuation of whichever tile got there first

### Capture lighting

- **`General.Normalize Capture Lighting`** now uses fixed neutral-noon values (Meadows-tuned ambient/sun/fog) instead of reading per-biome `m_currentEnv` day colours. The previous behaviour gave consecutive compile tiles different brightness depending on which biome the player was standing in when each tile was captured — fixed values give every tile the same lighting, which is what "normalize" is supposed to do

### Compatibility

- Bumped the **ZenMap** dependency floor to `1.7.8`

## 1.0.9

### Map Style (new)

- Added **`Map Style.Style`** (client-only) — optional stylized rendering for SEND / COPY captures, reconstructed from Valheim's own map data so explored areas show detail while unexplored stay fogged. Four modes:
  - **Old Map** — aged-parchment chart with biome wash, Perlin grain, height-contour and biome-edge lines (a faithful reimplementation of ASpy's MapPrinter `GenerateOldMap` pipeline that reads directly from `Minimap` instead of rescanning the world)
  - **Chart** — flat topographic chart with contour and biome-edge lines, no parchment grain
  - **Topographical** — shaded-relief terrain with hillshading, contours and biome-edge lines (default)
  - **Satellite** — naturalistic shaded terrain, no line work
  - `None` (or removing the entry) keeps the normal in-game map look
- A selected Map Style forces the **texture-capture** path for that capture (the screen-capture path screenshots the unstyled live map, so styling has to go through the offscreen render). Capture Method config is unchanged for `None`
- **MAP COMPILE tiles also style** when `Map Style.Style` is non-`None` — each tile renders through the same pipeline at tile resolution and the composite stitches stylized terrain. Per-tile cap of `1536px` on the longest internal render edge keeps ADD TILE responsive (~7× cheaper than rendering at the full 4K tile size); the GPU bilinear blit to tile resolution is visually indistinguishable for the intrinsically low-frequency styled output
- Render runs on a background thread — the per-pixel passes (biome blur, shallow-water field, hillshade, contour and biome-edge lines, final smoothing) would otherwise stall the game for a noticeable fraction of a second on a 2048² map texture. Layer read-back and the final `Texture2D` upload are on the main thread

### Capture

- The texture-capture path's default output now matches the **player's screen resolution** (measured off the large-map image rect) instead of a fixed `1920×1080`. SEND/COPY on a 1440p/4K screen produces noticeably sharper PNGs without needing CTRL+COPY; CTRL+COPY still upsizes to the 4096px full-resolution cap. Falls back to `1920×1080` when the rect can't be measured
- **SEND MAP** and **COPY MAP** buttons now reflect in-flight state: while a capture is running, both buttons grey out and the one matching the current operation swaps its label to `SENDING...` / `COPYING...`. Restores hotkey labels and interactability when the capture finishes — useful since a styled or full-resolution capture can take noticeable wall time, and the previous silent button would let the player queue duplicate clicks

### Map Compile

- **ADD TILE** mirrors the same busy treatment — while a tile capture is in flight (including the styled-render path, which can take hundreds of ms per tile) the whole compile panel greys out and ADD TILE shows `CAPTURING TILE...`. Re-entry guard at the click handler too, so a button event queued before the layout refresh can't kick off a parallel capture
- A center-screen message announces the capture (`Capturing tile...` or `Rendering styled tile...` when a Map Style is active) so there's an immediate response even before the button label updates

## 1.0.8

### Map Compile

- Map compile now honours the global **`Capture Method`** config. Compile tiles use **screen capture by default** (matching SEND/COPY), with texture capture still available as the alternative. Brings compile in line with the rest of the mod's capture pipeline — one switch governs every capture
- Screen-capture compile tiles crop to the clamped-uv sub-rect of the visible map so each tile's PNG aligns exactly with the world rect recorded for it. No black bars from zoom-past-the-world-edge bleed into adjacent tiles in the composite
- Texture-capture compile tiles now render at a **4K-class longest edge** sized to the clamped-uv aspect ratio (was a fixed 1920×1080). Matches the per-tile detail of CTRL+COPY full-res and avoids spending pixels on the long axis when the clamped-uv is near-square

### Capture lighting

- **`Normalize Capture Lighting`** now applies to **every** capture path (texture *and* screen, SEND/COPY *and* compile). Previously it only affected the texture path; flipping it on now also keeps screen-capture SEND/COPY and compile tiles noon-lit so mixed-mode sessions stay visually consistent. Disable to have all captures reflect the live time of day

### Fixes

- Fixed compile texture-capture tiles showing **horizontally-stretched pin icons** when the large map was zoomed out past the world edge. Terrain rendered at clamped uv but pins were laid out from the raw `uvRect`, so icons got stretched/shifted in the captured PNG. The blit now remaps icon positions and sizes through the raw→clamped uv mapping; identity when zoom stays inside [0,1] so framed captures are bit-for-bit unchanged

### Compatibility

- Bumped the **ZenMap** dependency floor to `1.7.6`

## 1.0.7

### Server config sync — now via Jotunn

- Replaced the custom RPC sync and the optional ServerSync compatibility shim with **Jotunn's `SynchronizationManager`**. Server-synced settings now sync through Jotunn's standard, well-tested path: the server's value is pushed to clients, the entry is locked client-side, and the client's local value is cached and restored on disconnect. No protocol version to keep in step anymore
- **Jotunn is now a required dependency** (`ValheimModding-Jotunn-2.29.0`). ZenMap (already required) depends on Jotunn too, so most setups already have it
- ServerSync is **no longer used** — remove any `Discord.Lock Configuration` expectations; locking is handled by Jotunn's admin-only mechanism instead

### Map Compile

- Added a **`CLEAR`** button to the compile panel — wipes the whole session (in-memory tiles **and** the on-disk session folder) and returns to idle. Shown while compiling and when a saved session is resumable. Stays greyed out and labelled `CLEAR (L-CTRL)` unless **Left CTRL** is held, so a destructive wipe can't be hit by accident next to `CANCEL`

### Fixes

- Fixed compile tiles captured from a ZenMap table at full reveal radius being wrongly flagged **partial** and demoted by the compositor (so they could be overwritten by other tiles' fog). ZenMap reveals a circular disc, which the per-pixel rectangle test can never fully satisfy; compile now trusts ZenMap's own reveal-completeness ratio when ZenMap is the reveal source, and falls back to Valheim's per-pixel `m_explored` test only for vanilla walking exploration

### Config defaults

- `Pin Label.Include Distance` now defaults to **`false`** (was `true`). Existing configs are unaffected — BepInEx never overwrites a saved value

### Compatibility

- Bumped the **ZenMap** dependency floor to `1.7.4`

## 1.0.6

### Map Compile — resumable sessions & full-resolution save

- Compile sessions now survive `SAVE`/`COPY`/`SEND TO DISCORD`/`DONE` — only `DISCARD`/`CANCEL` deletes a session. `DONE` returns to compile mode with all tiles intact; sessions persist across restart (`RESUME COMPILE (N)`)
- `SAVE` now writes a full native-resolution PNG — no Discord-safe downsample, sharpest tile maps 1:1, hard-capped at 8192px on the longest axis. Filename includes pixel dimensions; the status line reports native vs. clamp. Preview/`COPY`/`SEND` payloads stay at the Discord-safe size
- Renamed compile-mode `FINISH (N)` → **`COMPILE (N)`** — produces a result without ending the session
- Compiled-map captions now render in Valheim's in-game font (Averia Serif Libre SDF + outline) via a Unity TMP pass instead of a system serif

### Map Compile — tile sharing (new)

- Added **SHARE** to the compile panel (**EXPORT** when no webhook is configured) — exports every session tile as a self-describing PNG with its world rect embedded in a standard `tEXt` chunk (still a normal viewable image). With a webhook, tiles post to Discord one attachment per message; copies are always written to `compile-share/out/<world>/`
- **Auto-import**: PNGs dropped in `compile-share/incoming` are scanned when the large map opens during a compile session, merged into the active session, then moved to `incoming/processed` or `incoming/ignored`. Tiles dedup by stable identity (world + rect + capturer), so re-sharing updates in place instead of stacking
- Added `Map Compile.Enable Map Sharing` config (default `true`, server-synced) — off hides SHARE/EXPORT and disables auto-import
- Added `Map Compile.Share Message Template` config (supports `{player}`/`{tileCount}`, server-synced)

### Fixes

- Fixed compile mode getting stuck after a compile: closing and reopening the minimap with the result panel open left no result panel and no `START`/`RESUME` button. The session is now preserved across map close; reopening shows `RESUME COMPILE (N)` (re-`COMPILE` for a fresh result panel)

### Capture

- Added `Pin Label.Show on Compile Mode` (default `true`, server-synced) — gates the "of Spawn" captions on the compiled map (still gated by `Pin Label.Enabled`) without affecting plain COPY/SEND
- **MAP COMPILE** captions are now stamped once onto the finished composite instead of baked per-tile — baked near-white captions lost to the per-tile chroma-pick wherever tiles overlapped. Drawn-on-top labels have a black outline for readability over any biome
- The screen/texture COPY/SEND paths still bake per-pin "of Spawn" captions when `Pin Label.Enabled`; the texture path now rasterizes their TMP glyphs from the font atlas (SDF-aware), matching the screen path
- Added `General.Normalize Capture Lighting` (default `true`, client-only) — texture capture renders the map as if at noon, eliminating the dark/light seams between tiles captured at different times of day; the environment's day palette is applied for the offscreen pass and globals restored immediately after

### Named cartography tables (new)

- Tables now carry a **human-readable name**, resolved from the closest named map pin sitting on the table (within ~8m). ZenMap auto-pins each table as the vanilla "house" icon — rename that pin (or drop a Town pin on the table) and the mod treats it as the table's name. No name set → behaves exactly as before
- Added a `{table}` placeholder to `Discord.Message Template`. Like `{spawnDir}`, it's substituted in place when present and auto-appended for legacy templates saved before it existed, so existing configs pick up the table name without manual edits
- The table name is captured per tile when added and persisted in the compile session JSON, so it survives even when the table is far away/unloaded at compile time. Compiled-map captions now read `{pinName} — {dist}m {dir}` (name, direction, or both — whichever is available), gated by the same `Pin Label` config

### Compatibility

- Bumped the **ZenMap** dependency floor to `1.7.3` — ≥1.7.3 self-heals the `Graphics.CopyTexture called with mismatching texture sizes` error on expanded worlds (broke fog reset/explore-all and degraded compile tiles on older versions); updating ZenMap is the fix

## 1.0.5

- Repository moved to `github.com/codewithbjim/ValheimMods` — updated `website_url` in the manifest and all README image links to the new host

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
