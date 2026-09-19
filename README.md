# HL2RP

A Half-Life 2 roleplay gamemode for s&box, built on the
[Hexagon](https://github.com/noahsabaj/hexagon) roleplay framework.

This project is the setting and nothing else. It currently contains no code: everything that
runs is Hexagon, and HL2RP supplies assets.

| What | Where |
| --- | --- |
| Factions: Citizen, and Civil Protection, which needs an operator whitelist and may lock doors | `Assets/factions` |
| Items: ration, water, radio | `Assets/items` |
| The city: spawn, doors, game manager, HUD | `Assets/scenes/main.scene` |

Code belongs here only when it is about this setting, such as the Combine. Anything another
roleplay game would also want belongs in Hexagon.

## Setup

Hexagon is compiled into the game from a sibling checkout.

1. Clone `hexagon` next to this folder.
2. Link it in, from this folder:

```bash
pwsh -Command "New-Item -ItemType Junction -Path Libraries/hexagon -Target ../hexagon"
```

3. Open `hl2rp.sbproj` in the s&box editor.

## Checking it

The tools live in Hexagon and default to this game.

```bash
pwsh ../hexagon/tools/verify.ps1
```

```bash
pwsh ../hexagon/tools/playtest.ps1
```

The play test boots the real editor and plays this game: characters, the Civil Protection
whitelist, chat, inventory, doors, and a restart.
