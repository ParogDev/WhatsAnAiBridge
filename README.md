# What's an AI Bridge?

> Development infrastructure for AI-assisted plugin development -- exposes live game state to Claude Code (and any MCP-compatible client) as callable tools.

Part of the **WhatsA** plugin family for ExileApi.

## What It Does

- Hosts a TCP JSON-RPC 2.0 server on localhost that the game plugin answers on the main thread
- Ships with a companion **MCP server** (`ExileBridgeMcp`) that exposes game state as Model Context Protocol tools so Claude Code can call them directly (`get_player`, `get_entities`, `deep_scan`, `eval_path`, etc.)
- Supports queries for player vitals, stats, entities, buffs, UI panels, NPC dialog, stash tabs, map data, and deep component dumps
- Includes a reflection-based expression walker (`eval_path` / `describe_type`) for ad-hoc object graph exploration without writing a plugin
- Records gameplay snapshots to JSONL files for offline analysis, with frame seek, search, and summary tools
- Shows a status HUD (TCP client count, query totals) and keeps a rotating query log

## Getting Started

### 1. Install the plugin
1. Download into `Plugins/Source/Whats An AI Bridge/`
2. HUD auto-compiles on next launch
3. Enable in the plugin list
4. On start the plugin writes `bridge-port.txt` and `bridge-token.txt` into the bridge directory (default `claude-bridge/` under the HUD install)

### 2. Wire up the MCP server (recommended)
The MCP server lives at `ExileBridgeMcp/` in the scaffolding workspace. Point your MCP client at it with a config like:

```json
{
  "mcpServers": {
    "exileapi": {
      "command": "dotnet",
      "args": ["run", "--project", "ExileBridgeMcp"],
      "env": {
        "BRIDGE_PORT": "50900",
        "BRIDGE_DIR": "C:\\Users\\You\\Documents\\PoeHelper\\claude-bridge"
      }
    }
  }
}
```

`BRIDGE_PORT` is the fallback; the MCP server prefers the actual port written to `bridge-port.txt` by the plugin (useful when you run ephemeral ports or multiple HUD instances). Auth tokens are read from `bridge-token.txt` automatically.

### 3. (Legacy) File IPC
File IPC is still supported behind the `Enable File IPC` toggle. AI tools write a query string to `request.txt` and the plugin writes the response to `response.json`. Prefer the MCP/TCP path for new work -- it's lower latency, supports concurrent requests, and doesn't race on file handles.

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Bridge Directory | `claude-bridge` | Path where token/port/request/response files live |
| Enable TCP | On | Start the JSON-RPC 2.0 TCP server |
| TCP Port | 50900 | Localhost port (49152-65535). Actual port is written to `bridge-port.txt` |
| Enable File IPC | On | Legacy request.txt/response.json polling |
| Poll Interval | 250ms | How often file IPC checks for new queries |
| Show Status HUD | On | Display bridge status indicator on screen |
| HUD Position X / Y | 10 / 200 | Status indicator screen position |
| Max Entity Range | 200 | Default distance limit for entity queries |
| Max Deep Stats | 80 | Cap on GameStat entries included per entity in `deep:` queries |
| Max UI Children | 300 | Cap on child elements walked during `ui` scans |
| Recording Interval | 200ms | Time between snapshot captures |
| Recording Entity Range | 200 | Distance limit for entities captured in recordings |
| Auto Deep Scan Bosses | On | Full component dump for Unique/Rare entities during recording |
| Recording Max Deep Stats | 200 | Deep-stat cap used while recording (higher than live default) |

<details>
<summary>MCP Tools</summary>

The MCP server registers three tool classes. Each tool is a thin wrapper that sends a JSON-RPC request to the plugin over TCP.

### Game state (`GameStateTools`)
- `get_all` -- player + area + entities + NPC dialog + map data (overview snapshot)
- `get_player` -- HP/ES/Mana, position, buffs, skills
- `get_area` -- zone name, level, act
- `get_entities(range, filter)` -- nearby entities; `filter` is `all`, `monsters`, or `items`
- `get_npc_dialog` -- NPC dialog visibility, lines, lore talk flag
- `get_map_data` -- map stats, quest flags (Djinn / Order of the Chalice / Faridun), dialog depth
- `get_ui_panels` -- visible UI panels with hierarchical child text
- `get_stash` -- stash tabs with name, type, color, flags (premium/public/remove-only/hidden), affinity
- `get_player_stats` -- full untruncated GameStat dictionary for build analysis
- `deep_scan(filter, range)` -- deep component dump for entities whose `Path` matches `filter` (Life, Actor, Buffs, StateMachine, Stats, Targetable, OMP, Render, Positioned, MinimapIcon, visual effect components)
- `get_bridge_status` -- connected clients, pending requests

### Reflection (`EvalTools`)
- `eval_path(expression)` -- walk the ExileApi object graph by dotted path starting from `GameController`. Supports property/field access, `GetComponent<T>()`, `[N]` indexing, `["key"]` lookup, `GetChildAtIndex(N)`, `ToString()`. Read-only, public members only, 150ms timeout.
- `describe_type(expression)` -- list public properties and methods at a path so you can discover what's reachable before calling `eval_path`

### Recording (`RecordingTools`)
- `record_start(intervalMs)` / `record_stop` / `record_status`
- `snapshot` -- capture a single frame immediately
- `recording_list` -- saved `.jsonl` files with size and mtime
- `recording_load(filename)` -- load a recording for playback
- `recording_frame(n)` -- read frame N
- `recording_search(term)` -- find frames containing a substring
- `recording_summary` -- unique paths, unique buff names, frame count, time range

</details>

<details>
<summary>Technical Details</summary>

### Transports
- **TCP JSON-RPC 2.0** (primary). Listener binds `127.0.0.1` only. Clients send newline-delimited JSON-RPC requests; the plugin drains up to 5 pending requests per `Tick()` under a 150ms budget so the game stays responsive. Requests time out after 10 seconds if they can't get main-thread time. `ping` and `status` are handled off-thread for cheap health checks.
- **File IPC** (legacy). Polled at `PollIntervalMs`; one-shot request/response.

### Authentication
On start the plugin generates a 256-bit random token, writes it to `bridge-token.txt`, and requires it on every RPC except `ping`. Clients read the file to authenticate; the token is regenerated each plugin start/restart so stale clients get kicked.

### Raw query strings
The plugin accepts the JSON-RPC `method` directly as a query name (`player`, `area`, `stash`, `ui`, ...) or via `method: "query", params: { type: "..." }`. This is what the MCP tools use internally, and it's also how the legacy file IPC works -- the query string is the same in both transports.

Parameterized forms:
- `entities[:range]`, `monsters`, `items`
- `deep:Filter[:range]` -- Path-substring filter with optional range override
- `eval:<expression>` / `describe:<expression>`
- `record:start[:interval]`, `record:stop`, `record:status`, `snapshot`
- `recording:list`, `recording:load:<file>`, `recording:frame:<N>`, `recording:range:<N>:<M>`, `recording:search:<term>`, `recording:summary`

### Snapshot data structure
Each recording frame captures:
- **Player**: HP/ES/Mana, position, rotation, actor state, buffs with stacks / maxTime / source
- **Entities (adaptive depth)**:
  - Unique/Rare: full deep scan (Life, Actor, Buffs, StateMachine, Stats, visual effects)
  - Normal/Magic: lightweight (id, type, path, name, alive, hostile, rarity, hp)
  - Entities with visual effects auto-promoted to deep scan

Recordings are JSONL (one JSON object per line) so frames can be streamed and seeked by byte offset. The plugin keeps a frame-offset index in memory for the currently loaded recording.

### Status HUD
- Green dot: TCP server up, idle
- Yellow dot: processing a query
- Grey dot: TCP disabled
- Trailing `TCP:N` shows connected client count

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
| [What's a Blade Vortex?](https://github.com/ParogDev/WhatsABladeVortex) | Blade Vortex stack tracker with Minion Pact snapshot detection |
| [What's a Breakpoint?](https://github.com/ParogDev/WhatsABreakpoint) | Kinetic Fusillade attack speed breakpoint visualizer |
| [What's a Crowd Control?](https://github.com/ParogDev/WhatsACrowdControl) | OmniCC-style CC effect overlay with timers |
| [What's a Mirage?](https://github.com/ParogDev/WhatsAMirage) | League mechanic overlay for spawners, chests, and wishes |
| [What's a Tincture?](https://github.com/ParogDev/WhatsATincture) | Automated tincture management with burn stack tracking |
| [What's a Tooltip?](https://github.com/ParogDev/WhatsATooltip) | Shared rich tooltip service for WhatsA plugins |
| **What's an AI Bridge?** | MCP + TCP JSON-RPC bridge for AI-assisted plugin development |
| [What's an Unbound Avatar?](https://github.com/ParogDev/WhatsAnUnboundAvatar) | Auto-activation for Avatar of the Wilds at 100 fury |

Built with ExileApiScaffolding (private development workspace)
