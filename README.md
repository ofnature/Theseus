# Theseus

**Runs dungeons for your whole fleet — and picks up where it left off.**

A Dalamud plugin for FFXIV that paths through a dungeon, engages trash, hands bosses to BossMod
Reborn and resumes pathing afterwards. Built for multibox fleets: every character runs the route
itself and they stay in step with each other, so one client lagging, dying, or being restarted
doesn't derail the run.

Theseus went into the labyrinth Daedalus built, killed what was inside, and used Ariadne's thread
to find his way back out. This plugin does the first two, and treats the third as the feature
that actually matters.

> **Status: early development.** The framework is in place; the run engine is being built. Not
> yet usable for real runs.

## What makes it different

**It resumes.** If a run is interrupted — a crash, a disconnect, a plugin reload — Theseus picks
up where it stopped instead of starting the dungeon over. It reads the game's own Duty
Information objectives to work out which part of the dungeon you're in, then your position to
find the exact spot. Because the objectives come from the game rather than from a saved file,
they're still correct after a client restart.

**Every character paths itself.** Coordination happens at duty objectives, not by one client
telling the others where to walk. A character that falls behind, dies, or restarts rejoins where
the group actually is.

**It does the chores.** Repair and materia extraction run on every character between runs, not
just one.

## Requirements

| Plugin | Why |
|---|---|
| [vnavmesh](https://github.com/awgil/ffxiv_navmesh) | Pathfinding and movement |
| [BossMod Reborn](https://github.com/FFXIV-CombatReborn/BossmodReborn) | Boss mechanics and dodging |
| [Daedalus](https://github.com/ofnature/Daedalus) | Combat rotations and fleet coordination |
| [Charon](https://github.com/ofnature/Charon) | Optional — equip upgrades between runs |

## Installation

Add this third-party repository in Dalamud settings:

```
https://raw.githubusercontent.com/ofnature/Daedalus/main/repo.json
```

That feed carries Theseus alongside the rest of the suite.

## Usage

| Command | Does |
|---|---|
| `/theseus` | Open the run window |
| `/ts` | Short alias |
| `/theseus config` | Open settings |

Theseus ships disabled. Turn it on in **Settings → Enable Theseus** once the dependencies above
show green in the footer.

## The suite

- **[Daedalus](https://github.com/ofnature/Daedalus)** — rotation assistant for all combat jobs
- **[Charon](https://github.com/ofnature/Charon)** — auto pillion, gear equipping, party invites
- **[Caduceus](https://github.com/ofnature/Caduceus)** — manual mouseover healing
- **[SealBreaker](https://github.com/ofnature/SealBreaker)** — duty farming, GC turn-ins
- **Theseus** — dungeon running

## License

Personal project, no license granted. Provided as-is.
