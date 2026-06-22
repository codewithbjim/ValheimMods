# Changelog

## 0.1.0

Initial release.

- Corpses no longer auto-drop loot — work the body for the full haul, or it rots and
  drops only a fraction (half by default, configurable).
- **Skinning** (animals): vanilla **Knives** skill drives speed; knife physical damage
  drives a loot bonus. Bare-handed "Dismantle" is slower and gets no knife bonus.
- **Looting** (humanoids): custom **Looting** skill drives both speed and loot bonus,
  with its own localized name, icon, and in-game description.
- **Hold-to-work** channel with an on-screen progress bar; cancels on moving away,
  attacking, or releasing the key. Optional **press-once** mode (`Hold To Work` off)
  runs the action on a single tap.
- Separate corpse lifetimes for humanoids (5 min) and animals (1 min) before they rot.
- Configurable work times, skill XP rates, extra-loot bonuses, rot-loot fraction, max
  work distance, and the humanoid / excluded creature classification lists.
- **Server-synced settings** via Jotunn: gameplay values are pushed from the server and
  locked on clients; logging and the hold/press preference stay client-local.
- Bosses and non-ragdoll creatures keep vanilla instant-drop.
