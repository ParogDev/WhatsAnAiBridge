# What's an AI Bridge?

> Development infrastructure for AI-assisted plugin development -- you need this if you're building plugins with AI tools.

Part of the **WhatsA** plugin family for ExileApi.

## What It Does

- Lets AI tools (like Claude Code) query live game state by reading/writing files in a bridge directory
- Supports queries for player stats, entities, buffs, UI panels, NPC dialog, stash tabs, and deep component dumps
- Records gameplay snapshots to JSONL files for offline AI analysis and debugging
- Shows a status HUD and maintains a query log so you can see what the AI is asking

## Getting Started

1. Download and place in `Plugins/Source/What's an AI Bridge/`
2. HUD auto-compiles on next launch
3. Enable in plugin list
4. The bridge directory defaults to `claude-bridge/` inside your HUD installation
5. AI tools write queries to `request.txt`, the plugin responds in `response.json`

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Bridge Directory | `claude-bridge/` | Path where request/response files live |
| Poll Interval | 250ms | How often to check for new queries |
| Show Status HUD | On | Display bridge status indicator on screen |
| HUD Position X / Y | 10 / 200 | Status indicator screen position |
| Max Entity Range | 200 | Default distance limit for entity queries |
| Recording Interval | 200ms | Time between snapshot captures |
| Auto Deep Scan Bosses | On | Full component dump for Unique/Rare entities during recording |

<details>
<summary>Technical Details</summary>

### Query Types

**Basic queries:**
- `all` -- Full dump of player + area + NPC dialog + map data + entities
- `player` -- Player vitals (HP, ES, Mana), position, buffs
- `area` -- Current zone name, level, act
- `entities[:range]` -- All nearby entities (default 200 range)
- `monsters` -- Alive hostile monsters only
- `items` -- World items (drops on ground)
- `npcdialog` -- NPC dialog state, visibility, depth, dialog lines
- `mapdata` -- Map stats, Djinn quest flags, dialog depth tracking
- `ui` -- Scan all visible UI panels with hierarchical child text (2 levels deep)
- `stash` -- All stash tabs with name, type, visibleIndex, color, flags (premium, public, remove-only, hidden)

**Deep analysis:**
- `deep:Filter[:range]` -- Deep component dump for entities matching path filter
  - Includes: Life, Actor, Buffs, StateMachine, Stats, Targetable, OMP, Render, Positioned, MinimapIcon
  - Captures visual effects (Beam, GroundEffect, EffectPack, AnimationController)

### Recording & Playback

**Capture commands:**
- `record:start[:interval]` -- Begin recording snapshots
- `record:stop` -- End recording, returns frame count and file path
- `record:status` -- Check current recording state
- `snapshot` -- Capture single frame

**Playback commands:**
- `recording:list` -- List saved `.jsonl` recording files
- `recording:load:filename` -- Load a recording for analysis
- `recording:frame:N` -- Read frame N
- `recording:range:N:M` -- Read frames N through M
- `recording:search:term` -- Find frames containing term
- `recording:summary` -- Analyze loaded recording: entity counts, buff timeline

### Snapshot Data Structure

Each frame captures:
- **Player**: HP/ES/Mana, position, rotation, actor state, buffs with stacks/maxTime/source
- **Entities (adaptive depth)**:
  - Unique/Rare: Full deep scan (Life, Actor, Buffs, StateMachine, Stats, visual effects)
  - Normal/Magic: Lightweight (id, type, path, name, alive, hostile, rarity, hp)
  - Entities with visual effects auto-promoted to deep scan

### Architecture

- File-based IPC: polls for `request.txt`, writes `response.json`
- UTF-8 JSON with proper escaping
- JSONL format for recordings (one JSON object per line)
- Max 50 queries in the rotating log
- Status colors: green (idle), yellow (processing), red (error)
- Custom tabbed settings UI with 5 tabs: Status, Settings, Query Log, Recording, Guide

</details>

## About This Project

These plugins are built with AI-assisted development using Claude Code and the
ExileApiScaffolding (private development workspace) workspace.

The developer works professionally in cybersecurity and high-risk software --
AI compensates for a C# knowledge gap specifically, not engineering judgment.
Plugin data comes from the PoE Wiki and PoEDB data mining.

The focus is on UX: friction points and missing expected features that the
existing plugin ecosystem doesn't address. Every hour spent developing is an
hour not spent on league progression, so feedback is the best way to support
the project.

## WhatsA Plugin Family

| Plugin | Description |
|--------|-------------|
| [What's a Breakpoint?](https://github.com/ParogDev/WhatsABreakpoint) | Kinetic Fusillade attack speed breakpoint visualizer |
| [What's a Crowd Control?](https://github.com/ParogDev/WhatsACrowdControl) | OmniCC-style CC effect overlay with timers |
| [What's a Mirage?](https://github.com/ParogDev/WhatsAMirage) | League mechanic overlay for spawners, chests, and wishes |
| [What's a Tincture?](https://github.com/ParogDev/WhatsATincture) | Automated tincture management with burn stack tracking |
| [What's a Tooltip?](https://github.com/ParogDev/WhatsATooltip) | Shared rich tooltip service for WhatsA plugins |
| **What's an AI Bridge?** | File-based IPC for AI-assisted plugin development |
| [What's an Unbound Avatar?](https://github.com/ParogDev/WhatsAnUnboundAvatar) | Auto-activation for Avatar of the Wilds at 100 fury |

Built with ExileApiScaffolding (private development workspace)
