# HL2RP v2 showcase

HL2RP is the game-owned reference schema for Hexagon v2. It is intentionally a
curated framework showcase rather than a compatibility port of the former
runtime. The package owns the startup scene and mounts a matching local Hexagon
source checkout during development and verification.

The game compiles under the s&box whitelist on every host, which is what makes
it publishable: streamed assemblies are access-controlled on dedicated servers
and remote clients alike. The whitelisted Hexagon library defines the v3
storage protocol; this game supplies one durable storage path shared by
dedicated servers, editor-hosted bootstrap sessions, and standalone builds: an
engine-neutral core (`HL2RPDurableStorageCore`) over the exact whitelist-safe
`BaseFileSystem` surface, with an exclusive-writer lease and staged, verified
immutable publication. Whitelist-safe code has no atomic rename and no
flush-to-disk, so a crash can leave one torn artifact at the final name — a
case the framework's recovery contract explicitly tolerates per WAL metadata
class — and durability is bounded by the operating-system cache rather than
the disk cache. Verification rejects reintroducing raw operating-system
storage and silently disabling the compile-time whitelist gate.

The schema demonstrates explicit modules, typed character and item state,
transactional inventory and economy operations, server-issued interaction
capabilities, immutable client snapshots, and host-authoritative combat,
restraint, scanner, and civic workflows.

## Repository layout

- `Code/Schema` registers the schema, its eight modules, and every stable public
  identifier.
- `Code/Domain` contains schema-owned persisted state and codecs.
- `Code/Features` contains host application services and item/command behavior.
- `Code/Runtime` adapts the Sandbox scene, networking, and world to the neutral
  Hexagon application layer.
- `Code/UI` contains the client-only Razor design system and immutable view
  models.
- `Assets/scenes/main.scene` is the only generic game-owned asset path. All
  other HL2RP assets live under `Assets/hl2rp`.
- `tests` contains Sandbox-independent .NET 10 conformance tests.

## Verification

From the sibling Hexagon checkout, run:

```powershell
./tools/verify.ps1 -SchemaRoot ../hl2rp-hexagon -SkipRemoteAcceptance
```

The verifier runs both neutral test projects, validates package and asset
ownership, regenerates and rebuilds the mounted s&box projects with warnings as
errors, and launches the game-owned scene to require explicit host and client
readiness. A real dedicated-server/two-client acceptance run is still required
for authenticated remote-client isolation and cannot be replaced by a single
Steam session. The command above is therefore the explicitly incomplete
local-only form; a release invocation supplies the source-bound manual evidence
manifest instead of `-SkipRemoteAcceptance`.

There is intentionally no migration path for v1 or existing v2 persistence.
Corrected data starts under `hexagon/persistence/v3/{schemaId}`; existing
`hexagon/v2/...` data remains untouched for rollback. The v3 store rejects
unknown persisted types, definitions, permissions, actions, and module dependencies.

## Account entitlements and first operator

Restricted faction creation is authorized by immutable
`HL2RPAccountEntitlementRecord` documents in the schema-owned
`hl2rp.account-entitlements` collection. The host derives the account from the
authenticated connection, re-reads the committed entitlement for every
creation, and sends receiver-specific creation availability to the client.
Client selections and replicated platform IDs never grant authority.

Configure the scene's `HL2RP Bootstrap Operators` component with one or more
authenticated unsigned account IDs before opening a live server. Those
host-authored IDs can use the **Access** workspace from character selection to
inspect, grant, or revoke the first Civil Protection, Overwatch, or City
Administration entitlement. City Administration characters may subsequently
use the same audited UI. Keep the component empty only when entitlement
administration is intentionally disabled until the scene is reconfigured.

Bootstrap operator configuration is non-persisted authority. Entitlements are
persisted transactionally with optimistic revisions, and grant/revoke audit
facts are published only after commit. Revocation blocks future restricted
character creation but intentionally does not rewrite or demote existing
characters.

### Fresh-store acceptance setup

The dedicated two-client acceptance run needs restricted characters, but the
tracked scene intentionally contains no personal account ID. Pre-seed those
entitlements through the real host before beginning the evidence window:

1. Record `git status --short` and require a clean HL2RP worktree. Open
   `Assets/scenes/main.scene` at the exact source revision that will be tested.
2. In the editor, temporarily enter the authenticated setup account's Steam
   ID64 in `HL2RP Bootstrap Operators`. Do **not** save this scene edit.
3. Keep the `Hexagon Bootstrap` persistence-root setting unchanged. The setup
   host and later dedicated server must use the same data directory and the same
   logical root, `hexagon/persistence/v3/hl2rp` (or the same explicit
   `PersistenceRootOverride` when isolation is required).
4. Start an editor-hosted session as that account. From character selection,
   use **Access** to grant the two remote test accounts the Civil Protection and
   City Administration entitlements required by the acceptance scenarios.
5. Stop the host cleanly and wait for both
   `HL2RP_RECOVERY_SNAPSHOT phase=pre_shutdown ...` and `HEXAGON_DRAINED host`.
   Do not delete, rename, or redirect the persistence root afterward.
6. Reload the scene and choose **Reload**/discard when prompted so the temporary
   operator ID is removed rather than written to `main.scene`; then close the
   editor. Require `git status --short` to be clean and verify that the tracked
   `AccountIds` value remains empty before launching the dedicated server.

This editor-hosted setup is not acceptance evidence. Start the timed run only
after the clean-status check, and capture evidence from a true dedicated-server
process plus two remote clients authenticated as distinct nonzero accounts. The
complete source-bound procedure is in the sibling Hexagon checkout at
`docs/testing.md#manual-dedicated-server-two-client-runbook`.
