# Showcase architecture

HL2RP supplies eight explicit schema modules: civic identity, Combine,
communications, commerce, documents, restraint, scanner, and combat. Module
dependencies are compiled deterministically by Hexagon; there is no reflected
plugin discovery or name-parsing authorization.

## Authority boundaries

The authenticated account is always derived from the calling Sandbox
connection. Clients submit intent and creation values only. Factions, classes,
whitelists, generated Combine service names, CIDs, balances, inventory access,
damage, ammunition, scanner state, and entity state are recalculated or checked
on the host.

Restricted creation uses a schema-owned account entitlement repository keyed by
the authenticated `AccountId`. Unknown flags, malformed documents, wrong
document keys, stale revisions, and unknown target accounts fail closed. The
editor-authored bootstrap-operator component is the only non-persisted seed
authority; it cannot be changed by a client. Bootstrap operators and active City
Administration characters use the same host-authorized Access panel, while the
creation menu consumes receiver-specific enabled rows authored by the host.
The synthetic smoke-test account is an in-memory verification overlay and is
never written to the real entitlement collection.

For a fresh-store dedicated acceptance run, seed the two test accounts before
the evidence window. Temporarily place the authenticated setup account's Steam
ID64 in the scene's `HL2RP Bootstrap Operators` component without saving the
scene, run the real Access workspace, and commit the required Civil Protection
and City Administration grants. Shut that editor-hosted instance down cleanly,
preserve the same `hexagon/persistence/v3/hl2rp` root (or identical explicit
root override), reload/discard the scene edit, and require a clean Git status
with the tracked `AccountIds` still empty. Only the subsequent dedicated-server
run with two distinct remote authenticated clients is acceptance evidence; the
full procedure and artifact requirements are documented in the sibling
Hexagon `docs/testing.md` runbook.

Character, inventory, item, world-item, economy, and persistent scene-entity
changes commit through one unit of work. Notifications and snapshots are sent
only after a successful commit. Continuing interaction access is represented by
server-only, connection- and character-bound sessions; it is never persisted on
an inventory.

Restrained-character search is additionally gated by one shared host-side
authorizer used by both generic character interaction and the search service.
It requires a committed Civil Protection or Overwatch character plus the
restraint permission in the current active-character authority snapshot.
Continuation rechecks that authority; changing role or active character closes
the session and revokes its transient inventory grant.

HL2RP declares the exact nested persistence contract for every curated item,
reference category, and world-state kind. Hexagon validates that contract before
constructing the host application. Character inventory dimensions, interaction
idle timeout, and global chat rate limits are typed durable configuration values;
their shipped defaults remain 8 by 6 slots, 60 seconds, and four messages per
five seconds. Validators permit only bounded or stricter values, and recovery
evidence includes the canonical configuration snapshot.

Communications use a host-published active-character authority snapshot.
Request traffic can originate only from the validated request-device action:
the transaction rechecks device membership, power, policy, and cooldown, then a
post-commit handler delivers to the sender and current Civil Protection,
Overwatch, or dispatch-authorized officials. Generic request chat is denied.
Dispatch authorization comes from the current official role and permission,
never from a possessed item. Radio recipients are resolved from one canonical
live-inventory snapshot and must hold a powered radio on the matching
frequency. Generic chat and transactional request actions reserve the same
per-connection global rate-limit bucket, and a failed commit releases its
reservation without publishing a request or consuming the device cooldown.

Entitlement revocation applies to future character creation. Existing
characters retain their creation-time schema state and faction so a revocation
cannot silently rewrite live domain aggregates.

## Curated content

The schema registers Citizen, Civil Protection, Overwatch, and City
Administration factions, with Recruit, Unit, Elite, and Scanner Civil
Protection classes. It registers only the seventeen documented showcase items.
Every droppable item has a validated world model; definitions without one are
explicitly non-droppable.

The main scene demonstrates the Hexagon bootstrap and spawn, an ownable door and
Combine lock, storage, permit-gated vendor, ration and vending machines,
forcefield, scanner dock and drone, combat target, and world-item drop/pickup
area. Each persistent world example has one editor-authored stable identity.

Persisted world state is also live scene behavior, not display-only metadata.
Door open/close commits before rotating the authored object and changing its
collider. A current door session exposes immutable ownership status and exact
claim/release preconditions to the Operations UI; the controls disappear as
soon as the host revokes that session. Forcefield visibility and solidity are
derived from committed `Enabled` and `CombineOnly` state, with Combine passage
implemented through host-authored body tags and collision rules. Machine
cooldowns use the value persisted from each machine component rather than a
runtime constant.

World-item persistence is the desired-state authority. Host publication and
destruction run through a single-flight reconciler; successful handles remain
cached until destruction succeeds or invalidity is confirmed. A transient
post-commit engine failure is reported as committed but pending reconciliation
and retried with coalesced 250 ms-to-30 s backoff. Startup does not announce
readiness unless every durable world item converges.

Every scanner dock declares the stable identity of exactly one drone. Startup
requires reciprocal persisted dock/drone links, and only the drone may own a
pilot session. The drone's authored speed and acceleration limits are resolved
on the host for every accepted input. Input changes a capped target velocity;
continuous time-based integration applies acceleration independently of RPC or
render frequency and stops stale input. Replay protection updates in memory as
soon as input is accepted, while only the newest sequence is persisted through
one 250 ms write-behind window per session (at most four sequence commits per
second). Spotlight, photo, session transition, cleanup, and shutdown commits
absorb or flush the newest pending sequence. A process crash may lose no more
than the final 250 ms sequence window; startup clears scanner sessions that did
not survive the process.

## Client boundary

Razor panels render immutable public/private snapshots and invoke typed client
controllers. They do not query repositories, read server aggregates, or mutate
Sandbox world components. Listen hosts still construct a separate client store
and UI scope.

## Deliberately omitted legacy behavior

There are no duplicate weapon, ammunition, water, or consumable variants; no
pager/static-radio split; no book catalog; no arbitrary screenshot upload; and
no compatibility facade over old manager, event, receiver, persistence, or RPC
APIs.
