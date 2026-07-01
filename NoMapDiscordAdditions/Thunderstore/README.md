# NoMapDiscordAdditions

Two map workflows on Valheim's large map:

1. **SEND MAP / COPY MAP** — capture the visible map and either post it to a Discord webhook or paste it directly into Discord, Slack, or an image editor.
2. **MAP COMPILE** — visit cartography tables one by one and stitch every reading into a single high-resolution PNG you can save, copy, or send to Discord.

Built for no-map (and normal) servers. Message templates, output format, and tile-sharing policy can be **server-synced** so a host enforces the same look for everyone.

![Large map UI — capture buttons](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/1.jpg)

---

## Highlights

- **One-click sharing** — SEND MAP posts to a Discord webhook; COPY MAP puts a full-resolution image on your clipboard. Hotkeys (`F10` / `F11`) shown on the buttons and rebindable.
- **MAP COMPILE** — walk between cartography tables, add each as a tile, and composite them into one coordinate-accurate PNG. Sessions persist per world + character and resume after a restart.
- **Interactive WEB MAP** — export a self-contained, offline web viewer (double-click `index.html`, no server or internet needed) with drag-pan, zoom, a searchable pin list, and per-kind pin filtering over a pin-free base image.
- **Manage Tiles** — a `TILES` panel to review every captured tile and exclude any from the next compile without deleting it (or remove it outright), including imported tiles from tables you're nowhere near.
- **PINS filter** — choose exactly which pin kinds appear on a compiled map (mod-added pins included automatically).
- **Tile sharing** — pool map coverage with teammates by exchanging self-describing PNG tiles.
- **Map styles** — optional Old Map / Chart / Topographical / Satellite rendering for captures and compiled tiles.
- **Clean shots** — every UI overlay is hidden; `Player`/`Death` pins are dropped by default (hold Left CTRL to keep them).
- **Output control** — JPEG or indexed-PNG output, tuned to stay under Discord's attachment limit while keeping pin captions crisp.

| Composed map (4 tables stitched) | Sent to Discord |
|---|---|
| ![Composed map](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/2.png) | ![Discord post](https://raw.githubusercontent.com/codewithbjim/ValheimMods/refs/heads/main/NoMapDiscordAdditions/Thunderstore/Images/4.png) |

---

## Installation

Install with a mod manager and the dependencies come with it. To install manually, drop the DLL into `BepInEx/plugins` and install each dependency below.

| Dependency | Why |
|---|---|
| [BepInExPack Valheim 5.4.2333](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) | Mod loader. |
| [JsonDotNET 13.0.4](https://thunderstore.io/c/valheim/p/ValheimModding/JsonDotNET/) | Persists compile sessions to disk. |
| [Jotunn 2.29.0](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/) | Server-authoritative config sync. |
| [ZenMap ≥ 1.8.0](https://thunderstore.io/c/valheim/p/ZenDragon/ZenMap/) | Required for MAP COMPILE; also fixes a `Graphics.CopyTexture` size-mismatch on expanded worlds. |

> ZenMap already depends on Jotunn, so most setups have Jotunn already. Install on every client that wants the buttons; install on the server too to push synced settings.

---

## Quick start

1. Open the large map (`M`, or interact with a cartography table).
2. **Share one view:** click **COPY MAP** (`F11`) and paste into Discord, or set a webhook and click **SEND MAP** (`F10`).
3. **Build a stitched map:** click **START COMPILE**, then **ADD TILE** at each cartography table you visit. When you've covered the ground you want, click **COMPILE** → **SAVE** / **COPY** / **SEND TO DISCORD**.

---

## Full documentation

The **[Wiki](https://github.com/codewithbjim/ValheimMods/blob/main/NoMapDiscordAdditions/Thunderstore/WIKI.md)** covers everything in depth:

- Setting up a Discord webhook and message templates
- The full MAP COMPILE flow — tiles, the **PINS** include-filter, the result panel, **EXIT** / **CLEAR ALL**, resuming sessions
- Sharing tiles between players
- Map styles and output formats
- The complete **configuration reference** and which settings are server-synced
- On-disk file layout, troubleshooting, and FAQ

---

## Server behavior

Config sync uses **[Jotunn](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/)'s `SynchronizationManager`** (a required dependency). On connect, the server pushes its value for every synced setting, the entry is locked client-side, and each client's local value is restored on disconnect. Server-synced settings include the webhook URL, message templates, cloud-hiding, and the tile-sharing toggles; output format, map style, lighting, and hotkeys stay local to each client. See the [Wiki](https://github.com/codewithbjim/ValheimMods/blob/main/NoMapDiscordAdditions/Thunderstore/WIKI.md#server-behavior--sync) for the full list.

---

## Support & questions

Found a bug, have a question, or want to request a feature? Reach **virtualbjorn** on either Valheim modding Discord:

- **OdinPlus** — https://discord.gg/mbkPcvu9ax
- **ValheimModding** — https://discord.gg/MJWtxQs

Include your `BepInEx/LogOutput.log` when reporting an issue, or open one on the [GitHub repo](https://github.com/codewithbjim/ValheimMods).
