# Hack 'n Sap

**Get Hacking.** Stand directly beside a tree or log, aim at it, and press Use
to start chopping with your equipped axe.

Hack 'n Sap uses the axe's normal primary attack. Stamina, durability, Wood
Cutting skill, damage, effects, drops, and target health all remain vanilla.
Cancel whenever you like—the next session resumes from the tree or log's actual
remaining health.

## What it does

- Adds an adjacent Use-key interaction to fellable trees and felled logs.
- Repeats ordinary axe swings without replacing Valheim's damage pipeline.
- Supports every discovered tree that produces logs, including Ancient, mature
  Yggdrasil, and Ash trees using the standard `TreeBase`/`TreeLog` pipeline.
- Turns old, unprotected stumps into natural saplings of their source tree.
- Decays abandoned logs away without drops after a configurable delay.
- Protects stumps and logs inside player-base areas by default.
- Synchronizes gameplay configuration through Jotunn.

## Controls

- **Use:** start or stop hacking while aiming at an adjacent tree or log.
- Moving, dodging, staggering, being knocked back, attacking manually, changing
  tools, or leaving the target stops the session.

An equipped axe with chop damage and remaining durability is required.

## Configuration

You can independently configure tree and log interactions, an optional distance
cap, stump renewal days, log decay days, base protection, and diagnostic logs.

## Installation and multiplayer

Install with r2modman or Thunderstore Mod Manager, or place the DLL in
`BepInEx/plugins` after installing BepInEx and Jotunn. Every multiplayer peer and
the server should install the mod.
