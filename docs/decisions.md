# Decisions

One paragraph each, newest last. A decision records what was chosen and why, so it can be
revisited when the reason stops being true.

## 2026-09-18: Rewrite game-first, in one project

The previous code was a general roleplay framework in one repository and a game in another. It
reached 83,000 lines with a schema compiler, a module graph and a policy pipeline, while
restraining a player, flying the scanner and opening the search panel did not work. The framework
had been designed before a game existed to tell it what it needed. This rewrite builds the game
directly. A framework can be extracted later from whatever gets written twice. The old code is on
the `agent/v2-audit-remediation` branch, and the old framework repository is untouched.

## 2026-09-18: Use the engine, not a layer over it

Public state is `[Sync( SyncFlags.FromHost )]`. Private state goes to its owner with `[Rpc.Owner]`.
Client requests are `[Rpc.Host]` methods. Movement is `PlayerController`. Doors are `IPressable`.
Items and factions are `GameResource` assets. Operator tools are `[ConCmd]`. The old code wrapped
each of these in its own abstraction, including a hand-written snapshot wire format. Every wrapper
was a place for the two sides to disagree, and one such disagreement is why the search panel never
opened.

## 2026-09-18: One JSON file per document, no database and no write-ahead log

A roleplay server holds thousands of characters at most and changes a handful per second. The
sandboxed filesystem has no rename, so a write cannot be atomic. `DocumentStore` writes a complete
sibling copy first, then the real file, then removes the sibling. A crash at any point leaves at
least one complete copy, and loading prefers the real file and falls back to the sibling. Tests
tear each write in turn and reboot. What this gives up is atomicity across documents. Nothing
needs that yet. The first feature that does, most likely a trade between two characters, should
put both sides in one document rather than bring back a transaction log.

## 2026-09-18: The host trusts the connection and its own measurements, nothing else

Every request starts in `Player.Authorize`: the caller must own the pawn it is calling, and must be
within a request budget. Character ownership is checked against the caller's Steam id, and "not
yours" gets the same answer as "does not exist" so ids cannot be probed. A door measures the
distance to the caller's pawn itself. Chat is sent only to the connections in range.

## 2026-09-18: Position is owner-simulated and not yet validated

`PlayerController` runs on the owning client, so the host reads a position the client reported. A
modified client could stand next to a door it is far from. The old code had a windowed movement
audit for this, and it is the right next piece of host authority to bring over. It is left out of
the first milestone because nothing here is worth cheating for yet, and it is recorded so nobody
mistakes the reach check for proof.

## 2026-09-18: Test by playing

Every broken feature in the old code was hidden by a test that faked the exact piece that was
wrong. Here, pure rules get unit tests, and everything else is checked by `tools/playtest.ps1`,
which plays the real game in the real editor through the editor's own control endpoint. The
`DevDriver` component exists only for that. It is never placed in a scene, refuses to run outside
the editor, and calls the same client requests the HUD calls, so it cannot pass a rule the real
path would fail.

## 2026-09-18: Carried over unchanged

The character-name work: NFKC normalization, the single-script rule, and the Unicode confusable
skeleton with its generated table. It held up under review and is the hardest part to get right.

## Not here yet

Death and combat, restraints, the scanner, vendors and currency spending, world item drops,
clothing, a second-client play test, and continuous integration. Each should arrive as a playable
slice with its own play-test section.
