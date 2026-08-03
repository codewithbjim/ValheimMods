# Hack 'n Sap

**Get Hacking.** Stand directly beside a fellable tree or felled log, aim at it,
and press Use to begin chopping with your equipped axe. Hack 'n Sap repeats the
axe's normal primary attack, preserving Valheim's stamina, durability, Wood
Cutting skill, damage, effects, drops, and persistent target health.

## Features

- Adds a Use-key interaction to every discovered `TreeBase` that produces logs
  and to its `TreeLog` stages.
- Requires the player to be physically beside the exact hovered collider, so
  distant targets never start an empty-swing approach sequence.
- Cancelling never resets damage; resume later from the tree or log's remaining
  vanilla health.
- Supports ordinary trees and special fellable variants, including Ancient,
  mature Yggdrasil, and Ash trees using Valheim's standard felling pipeline.
- Records the felled tree's source prefab on its stump. After the configured
  delay, an unprotected stump becomes a natural sapling for that tree.
- Abandoned felled or partially cut logs decay away without drops after the
  configured inactivity period.
- Uses owner-authoritative ZDO timestamps and synchronized gameplay settings.
- Protects stumps and logs inside player-base effect areas by default.

## Controls

- **Use:** start or stop hacking while aiming at an adjacent tree or log.
- Moving, dodging, staggering, being knocked back, attacking manually, changing
  tools, or leaving the target ends the session.

An equipped axe with chop damage and remaining durability is required.

## Configuration

- Master enable switch and diagnostic logging
- Standing-tree and felled-log interactions
- Optional interaction-distance upper cap
- Stump renewal time in in-game days
- Abandoned-log decay time in in-game days
- Player-base protection

## Multiplayer

Install Hack 'n Sap and its dependencies on every peer and the server. Gameplay
configuration is synchronized through Jotunn.

## Build

Build `HackAndSap.csproj` with Visual Studio MSBuild. Debug and Release builds
deploy the DLL and PDB to the configured development installations; Release also
stages the DLL under `Thunderstore/plugins`.
