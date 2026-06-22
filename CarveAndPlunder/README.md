# Carve & Plunder

Creature corpses no longer auto-drop loot. You must kneel and **work the body** —
or the loot rots away with the corpse.

- **Animals → Skin** (vanilla **Knives** skill). A knife yields more; bare hands
  "Dismantle" slower for base loot. Knife sharpness drives the bonus; Knives skill
  drives speed.
- **Humanoids → Loot** (custom **Looting** skill). Hands only. Looting skill drives
  both speed and bonus.
- Kneel + **hold-E** channel. Moving, attacking, releasing E, taking damage, or
  walking away cancels it.
- **Scope:** only creatures that spawn a ragdoll. Bosses and non-ragdoll creatures
  (serpents, surtlings) keep vanilla instant-drop.

## How it hooks the game

| File | Role |
|------|------|
| `CorpsePatches.cs` | `Ragdoll.Awake` postfix attaches `CorpseInteraction`; `Ragdoll.SpawnLoot` prefix blocks the TTL auto-drop until the corpse is worked. |
| `CorpseInteraction.cs` | `Hoverable` + `Interactable` on the corpse; classifies it and starts a work session. |
| `CorpseWorkSession.cs` | The hold timer, kneel pose, cancel checks, and owner-authoritative scaled loot spawn. |
| `CorpseClassifier.cs` | Animal vs humanoid vs passthrough, from the ragdoll prefab name + config lists. |
| `LootingSkill.cs` / `SkillPatches.cs` | The custom Looting skill. |
| `WorkProgressUI.cs` | Hold-progress bar cloned from Valheim's native stamina bar. |
| `ModConfig.cs` | All tunables. |

## Integration seams (still to finish)

1. **Custom skill → Smoothbrain SkillManager.** `LootingSkill.cs` currently uses a
   self-contained native-Skills implementation so the project compiles and the skill
   works (XP accrues, persists, scales). To get a localized name, a real icon, and
   per-skill XP-rate config, drop **SkillManager.cs** into the project and reimplement
   `LootingSkill` against it. All callers go through `LootingSkill.Raise()` /
   `LootingSkill.GetFactor()`, so the swap stays isolated to `LootingSkill.cs` +
   `SkillPatches.cs`. Until then the skill shows as a raw `$skill_<hash>` token in the
   skills UI.

2. **Work animation.** `CorpseWorkSession.StartWorkPose` plays the `kneel` emote.
   A crafting motion can't be layered on top — the emote and the `crafting` animator
   state both occupy the base body layer, so they're mutually exclusive. To change the
   pose, swap the emote (or drop in a custom AssetBundle clip) — contained entirely in
   `StartWorkPose`/`StopWorkPose`.

3. **Tuning.** Times and extra-loot bonuses in `ModConfig.cs` are first-guess defaults;
   tune against real skill levels.

## Build

Visual Studio, `net481`. References resolve from the paths in
`CarveAndPlunder.csproj` (Valheim publicized assemblies, BepInEx, Unity modules).
Debug builds copy the DLL into the active r2modmanPlus dev profile.
