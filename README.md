# What's an AI Bridge?

> Give your AI eyes into your game -- live Path of Exile game state as callable tools for Claude Code, VS Code Copilot, or any MCP-compatible client.

Part of the **WhatsA** plugin family for ExileApi.

This started as a private tool for building plugins with AI. It turns out giving an AI assistant direct access to game state is useful for anyone getting started with ExileApi -- so it's going public. If you've ever wanted an AI to help you build a plugin but got stuck explaining what's on screen, this is for you.

## What It Does

- Lets your AI query your character, nearby monsters, items, UI panels, stash tabs, and NPC dialog in real time
- Includes a reflection walker so the AI can explore the full ExileApi object graph without writing code
- Records gameplay snapshots for offline analysis -- useful for debugging when you can't reproduce a situation
- Shows a status HUD on screen so you can see what the AI is asking and when

## Getting Started

### 1. Install the plugin
1. Download into `Plugins/Source/Whats An AI Bridge/`
2. HUD auto-compiles on next launch
3. Enable in the plugin list
4. On start the plugin writes `bridge-port.txt` and `bridge-token.txt` into the bridge directory (default `claude-bridge/` under the HUD install)

> **Important:** The plugin runs on the game's main thread. If ExileApi is not in the foreground, the game thread may be throttled or paused, which means queries won't be answered and game state won't update. Either keep ExileApi in the foreground while querying, or enable **Core > Force Foreground** in ExileApi's settings.

### 2. Set up the MCP server
The companion MCP server is a separate project: **[ExileApiMcp](https://github.com/ParogDev/ExileApiMcp)**. It translates between MCP tools and the plugin's TCP server.

```bash
git clone https://github.com/ParogDev/ExileApiMcp.git
```

You'll need the [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0) (preview) to build it.

Then add to your MCP client config (e.g. `.mcp.json` in your project root for Claude Code):

```json
{
  "mcpServers": {
    "exileapi": {
      "command": "dotnet",
      "args": ["run", "--project", "C:\\path\\to\\ExileApiMcp"],
      "env": {
        "BRIDGE_DIR": "C:\\path\\to\\your\\HUD\\claude-bridge"
      }
    }
  }
}
```

See the [ExileApiMcp README](https://github.com/ParogDev/ExileApiMcp) for full setup, VS Code config, and troubleshooting.

### 3. Give your AI this prompt

Once the plugin is running and the MCP server is configured, paste this into your AI to verify everything works:

> I have ExileApiMcp configured as an MCP server. It connects to Path of Exile via the ExileApi HUD overlay. Use the `get_bridge_status` tool to check the connection, then `get_all` to see my current game state. If tools hang or return errors, the HUD might not be in the foreground -- remind me to enable "Force Foreground" in ExileApi's Core settings.

From there, the AI can see your character, inspect entities, explore the object graph, and help you build plugins with live data.

## Settings

| Setting | Default | Description |
|---------|---------|-------------|
| Bridge Directory | `claude-bridge` | Where token, port, and IPC files are stored |
| Enable TCP | On | Start the TCP server for MCP connections |
| TCP Port | 50900 | Localhost port for the TCP server |
| Show Status HUD | On | Display connection status and query activity on screen |
| HUD Position X / Y | 10 / 200 | Screen position for the status indicator |
| Max Entity Range | 200 | How far to scan for entities (grid units) |
| Recording Interval | 200ms | Time between snapshot captures during recording |
| Auto Deep Scan Bosses | On | Automatically capture full component data for Unique/Rare entities |

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

### Additional settings
| Setting | Default | Description |
|---------|---------|-------------|
| Enable File IPC | On | Legacy request.txt/response.json polling |
| Poll Interval | 250ms | How often file IPC checks for new queries |
| Max Deep Stats | 80 | Cap on GameStat entries per entity in deep queries |
| Max UI Children | 300 | Cap on child elements walked during UI scans |
| Recording Entity Range | 200 | Distance limit for entities captured in recordings |
| Recording Max Deep Stats | 200 | Deep-stat cap used while recording |

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
| **What's an AI Bridge?** | Live game state bridge for AI-assisted plugin development |
| [What's an Unbound Avatar?](https://github.com/ParogDev/WhatsAnUnboundAvatar) | Auto-activation for Avatar of the Wilds at 100 fury |

Built with ExileApiScaffolding (private development workspace)
