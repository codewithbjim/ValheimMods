# Carve & Plunder

Creature corpses no longer auto-drop their loot. You have to kneel and **work the
body** — skin it, dismantle it, or loot it — for the full haul. Ignore it and it rots,
scattering only a fraction of what it carried.

A kill is no longer the end of the encounter; it's the start of the harvest.

---

## Highlights

- **Skin animals** with the vanilla **Knives** skill. A knife yields more than bare
  hands ("Dismantle"), and a sharper knife yields more still — knife damage drives the
  loot bonus, the Knives skill drives the speed.
- **Loot humanoids** (greydwarves, draugr, fulings, skeletons, …) with a custom
  **Looting** skill — its own name, icon, and in-game description in the skills menu.
  Hands only; the skill drives both speed and bonus loot.
- **Hold-to-work (or press-once).** Kneel and hold the Use key to channel the action —
  an on-screen progress bar fills as you work. Moving too far from the corpse, swinging a
  weapon or tool, or letting go of the key cancels it. Prefer a single tap? Flip
  **Hold To Work** off and one press runs the action on its own.
- **Corpses linger, then rot.** Worked corpses give their full loot (plus your skill
  bonus); ignored ones decay and drop only a fraction of it — half by default,
  configurable down to nothing. Humanoid and animal corpses have separate lifetimes.
- **Scoped and safe.** Only creatures that leave a ragdoll are affected. Bosses and
  non-ragdoll creatures (serpents, surtlings, …) keep vanilla instant-drop.

---

## Installation

Install with a mod manager and the dependency comes with it. To install manually, drop
`CarveAndPlunder.dll` into `BepInEx/plugins` and install the dependency below.

| Dependency | Why |
|---|---|
| [BepInExPack Valheim 5.4.2333](https://thunderstore.io/c/valheim/p/denikson/BepInExPack_Valheim/) | Mod loader. |
| [Jotunn 2.29.0](https://thunderstore.io/c/valheim/p/ValheimModding/Jotunn/) | Server-synced config (a mod manager pulls it in automatically). |

Install on every client. On a server, install it there too: gameplay settings are
**server-synced** — the server's values are pushed to clients and locked, so everyone
plays by the same rules. Your hold/press preference and logging stay local to you.

---

## How it works

1. Kill a creature. Its ragdoll drops to the ground but **keeps its loot**.
2. Walk up and look at the body — the hover shows **Skin** / **Dismantle** (animals) or
   **Loot** (humanoids).
3. **Hold the Use key** to kneel and work. The bar fills; when it completes, the full
   loot drops, scaled up by your skill (and, for animals, your knife).
4. Leave a body too long and it rots — dropping only a fraction of its loot (half by
   default).

The **Looting** skill raises both speed and yield. The **Knives** skill raises skinning
speed, while the knife's sharpness drives the animal yield — so skin with the sharpest
knife you have for the biggest bonus.

---

## Configuration

Every timer, skill rate, extra-loot bonus, the rot-loot fraction, and the creature
classification lists are configurable. See the
**[Wiki](https://github.com/codewithbjim/ValheimMods/blob/main/CarveAndPlunder/Thunderstore/WIKI.md)**
for the full reference, or edit `com.virtualbjorn.carveandplunder.cfg` in `BepInEx/config`.

---

## Support & questions

Found a bug, have a question, or want to request a feature? Reach **virtualbjorn** on
either Valheim modding Discord:

- **OdinPlus** — https://discord.gg/mbkPcvu9ax
- **ValheimModding** — https://discord.gg/MJWtxQs

Include your `BepInEx/LogOutput.log` when reporting an issue, or open one on the
[GitHub repo](https://github.com/codewithbjim/ValheimMods).
