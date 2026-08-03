# ValheimMods
BepInEx plugins for Valheim — EL-NoAdventureMapFix and more

## Local Valheim installations

- `F:\SteamLibrary\steamapps\common\Valheim` is the normal-play Steam installation.
  Projects read its current managed Unity assemblies, but build targets never deploy
  development DLLs into it.
- `E:\ValheimMods\Valheim v0.221.12` is the isolated development installation. Every
  build deploys DLL/PDB files into its `BepInEx\scripts` directory; Debug builds also
  deploy to the active r2modmanPlus `ModdingDevelopment` profile.
- Publicized compile-time assemblies live in the ignored `.dependencies` cache.
