# Carve & Plunder — Wiki

Full documentation and configuration reference.

---

## How corpses are handled

When a creature dies, Valheim spawns a **ragdoll** and would normally drop its loot
immediately. Carve & Plunder intercepts that drop:

- The corpse keeps its loot and gains a **Skin / Dismantle / Loot** interaction.
- A despawn timer is (re)started so the corpse lingers long enough to work, then rots.
- If you work the corpse, the **full** loot drops — scaled up by your skill (and knife,
  for animals).
- If you ignore it, the corpse rots and drops only a **fraction** of its loot (half by
  default; configurable from full down to nothing).

Only creatures that leave a ragdoll are affected. **Bosses** and creatures with no
ragdoll (serpents, surtlings, gjall, …) keep vanilla instant-drop.

### Animals vs. humanoids

- **Animals** → **Skin** (with a knife) or **Dismantle** (bare-handed). Uses the vanilla
  **Knives** skill. Knife physical damage (slash + pierce + blunt, including upgrades)
  drives the loot bonus; the Knives skill drives the speed.
- **Humanoids** → **Loot**, hands only. Uses the custom **Looting** skill — with its own
  localized name, icon, and description in the skills menu — which drives both speed and
  bonus.

Which creatures count as humanoid is controlled by the **Humanoid Creatures** list; the
**Excluded Creatures** list keeps creatures on vanilla instant-drop (bosses by default).
Everything else with a ragdoll is treated as an animal.

### The work pose

Working a corpse plays Valheim's **kneel** emote over the body. It cancels the moment you
move out of range or swing a weapon or tool. How the Use key behaves depends on the
**Hold To Work** setting:

- **On (default)** — you hold the Use key; releasing cancels.
- **Off** — a single Use press starts the work and it continues on its own; press Use
  again (or move away / attack) to cancel.

---

## Configuration reference

Edit `BepInEx/config/com.virtualbjorn.carveandplunder.cfg`, or use a config manager
in-game. Defaults shown in brackets.

### Server sync

The mod depends on **Jotunn**, whose synchronization manager makes the gameplay settings
**server-authoritative**. When you join a server, its values for those settings are
pushed to your client and locked — only a server admin can change them, and everyone
plays by the same rules. The settings marked **Client-only** below are never synced; each
player keeps their own:

- **Enable Logs** — diagnostic output, per machine.
- **Hold To Work** — your hold-vs-press input preference.

Everything else (timers, skill rates, bonuses, the rot-loot fraction, and the
classification lists) is synced from the server.

### General

| Setting | Default | Notes |
|---|---|---|
| Enabled | `true` | Master switch. When off, corpses drop loot the vanilla way. |
| Enable Logs | `false` | Print info/warning diagnostics to the BepInEx console and `Player.log`. |

### Interaction

| Setting | Default | Notes |
|---|---|---|
| Hold To Work | `true` | On: hold Use to work, releasing cancels. Off: one Use press runs the action on its own (press again / move / attack to cancel). Client-only. |
| Max Work Distance | `3.0` | Move further than this from the corpse while working and the action cancels. |
| Humanoid Corpse Lifetime | `300` | Seconds a humanoid corpse lingers before it rots. |
| Animal Corpse Lifetime | `60` | Seconds an animal carcass lingers before it rots. |
| Expiry Loot Fraction | `0.5` | Fraction of its loot a corpse still drops if it rots un-worked (stacks scaled by this, minimum 1 of each item). `0` = drop nothing, `1` = full loot. |

### Skinning (animals — Knives skill)

| Setting | Default | Notes |
|---|---|---|
| Skin Time Base | `2.0` | Seconds to skin at Knives 0 (with a knife). |
| Skin Time Reduction | `1.0` | Seconds removed from skin time at Knives 100 (linear). |
| Dismantle Time | `4.0` | Seconds to dismantle bare-handed. Knives skill does not help. |
| Knife Bonus Reference Damage | `50` | Knife physical damage that maps to the full skin bonus. |
| Skin Extra Max | `2` | Extra items added per drop stack with a knife at/above the reference damage. `0` = no bonus. |
| Knives XP Per Skin | `1.0` | Knives skill XP per successful skinning. |

### Looting (humanoids — custom Looting skill)

| Setting | Default | Notes |
|---|---|---|
| Loot Time Base | `2.5` | Seconds to loot at Looting 0. |
| Loot Time Reduction | `1.5` | Seconds removed from loot time at Looting 100 (linear). |
| Loot Extra Max | `2` | Extra items added per drop stack at Looting 100. `0` = no bonus. Also stated in the skill's in-game description. |
| Looting XP Per Loot | `1.0` | Looting skill XP per successful loot. |

### Classification

| Setting | Default | Notes |
|---|---|---|
| Humanoid Creatures | Greydwarf, Greydwarf_Elite, Greydwarf_Shaman, Skeleton, Skeleton_Poison, Draugr, Draugr_Elite, Draugr_Ranged, Goblin, GoblinBrute, GoblinShaman, Fuling, Cultist, Dverger | Comma-separated base names treated as humanoid (Loot). Use the name without the `(Clone)` / `_ragdoll` suffix. |
| Excluded Creatures | Eikthyr, gd_king, Bonemass, Dragon, GoblinKing, SeekerQueen, Fader | Comma-separated creatures that keep vanilla instant-drop (bosses by default). |

---

## Tips

- Carry the sharpest knife you have when skinning — knife damage, not just the Knives
  skill, drives the animal loot bonus.
- The humanoid corpse timer is generous (5 minutes) so a busy fight doesn't cost you
  loot; animals rot faster (1 minute). Both are configurable.
- Want the old "work it or lose it all" stakes? Set **Expiry Loot Fraction** to `0`.
  Prefer a softer mod? Raise it toward `1`.

---

## Troubleshooting

- **Nothing happens on a corpse.** Confirm the mod is enabled and the creature actually
  leaves a ragdoll (bosses and serpents/surtlings don't). Enable **Enable Logs** to see
  classification and interaction diagnostics in `BepInEx/LogOutput.log`.
- **Loot was reduced.** The corpse rotted before it was worked, so it dropped only its
  **Expiry Loot Fraction** (half by default) — work corpses promptly, or raise the
  lifetime settings.
- **Report issues** with your `BepInEx/LogOutput.log` to **virtualbjorn** on the OdinPlus
  or ValheimModding Discord, or on the [GitHub repo](https://github.com/codewithbjim/ValheimMods).
