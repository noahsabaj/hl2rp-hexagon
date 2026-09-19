# HL2RP

A Half-Life 2 roleplay gamemode for s&box. This branch is a from-scratch rewrite. It is one
project, written against the engine's own features, and built game-first: a small loop that
plays, then more on top of it.

## What plays today

- Connect, and the host gives you a pawn driven by the engine's `PlayerController`.
- Create characters. Names are checked for look-alikes, mixed scripts and invisible characters.
- Factions are assets. Civil Protection needs an operator whitelist; Citizen does not.
- Enter the city where your character last stood, with the starting items its faction grants.
- Talk: say, `/w` whisper, `/y` yell, `/me`, and `//` out-of-character. Only players in range hear
  in-character speech, and nobody out of range learns a message existed.
- A grid inventory: move and discard items.
- Doors: anyone in reach opens them, a locked door refuses, and only factions allowed to may lock.
- Everything survives a restart.

## How it is built

| Part | Where | How it is checked |
| --- | --- | --- |
| Rules with no engine in them: names, inventory grid, chat parsing, rate limit, storage | `Code/Logic` | Unit tests in `Tests`, compiled from the same files |
| Components: game manager, player, door, chat, operator commands | `Code` | Compiled against the installed engine, warnings as errors |
| HUD | `Code/UI` | Same compile, then looked at in the play test |
| The game as a whole | the scene and assets | `tools/playtest.ps1` plays it in the real editor |

State reaches clients the engine's way. Public state is `[Sync( SyncFlags.FromHost )]` on
components. Private state goes to its owner by `[Rpc.Owner]`. A client changes nothing directly:
it calls a `[Rpc.Host]` request, and the host re-derives who is asking from the connection,
measures distances itself, and saves before it replies.

## Commands

Run both from this folder with the s&box editor closed.

```bash
pwsh tools/verify.ps1
```

Runs the logic tests, has s&box generate the project, and compiles it against the engine.

```bash
pwsh tools/playtest.ps1
```

Boots the editor, enters play mode, and plays the scenario above through the real RPC path,
including a restart, in a throwaway data folder.

## Operator commands

Typed in the host's console.

| Command | Effect |
| --- | --- |
| `hl2rp_whitelist <steamid64> <faction>` | Allow an account to create characters in a whitelisted faction |
| `hl2rp_unwhitelist <steamid64> <faction>` | Remove that permission |
| `hl2rp_give "<character name>" <item>` | Give an item to a character who is in the city |

See [docs/decisions.md](docs/decisions.md) for why it is shaped this way and what is deliberately
not here yet.
