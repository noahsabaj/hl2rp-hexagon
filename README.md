# HL2RP v2 showcase

HL2RP is the game-owned reference schema for Hexagon v2. It is intentionally a
curated framework showcase rather than a compatibility port of the former
runtime. The package owns the startup scene and mounts a matching local Hexagon
source checkout during development and verification.

The game is intentionally configured as a standalone-only s&box project. The
whitelisted Hexagon library defines the v3 storage protocol; this game supplies
its production OS adapter, including exclusive leases, write-through durability,
and atomic file publication. Verification rejects moving those raw operations
back into the library or silently downgrading the standalone compiler boundary.

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

There is no v1 migration path. The v2 store uses a distinct format root and
rejects unknown persisted types, definitions, permissions, actions, and module
dependencies.

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
