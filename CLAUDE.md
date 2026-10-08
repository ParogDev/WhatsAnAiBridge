# What's an AI Bridge?

ExileApi (PoE1) **and** ExileCore2 (PoE2) plugin that exposes live Path of Exile game state over TCP for AI assistants.

## Quick Start

Run `/setup-mcp` to automatically set up the companion MCP server.

## Layout: one project, two games

```
WhatsAnAiBridge.csproj   single project; compiles Shared\ + the current game's folder
Directory.Build.props    decides $(Game) and TargetFramework (see below)
Shared\                  game-agnostic code: TCP server, DTOs, queries, recording, expression walker
Poe1\                    PoE1 partial class + GlobalUsings (ExileCore, SharpDX, net10)
Poe2\                    PoE2 partial class + GlobalUsings (ExileCore2, System.Drawing, net8)
```

- **Game detection:** the HUD folder is three levels up from the plugin (`<HUD>\Plugins\Source\<Name>`). If it contains `ExileCore2.dll` the build is PoE2; if it contains `ExileCore.dll` it is PoE1. Outside a HUD, pass `-p:Game=poe1|poe2` (default poe1); references then come from `$(exapiPackage)` / `$(exilecore2Package)`.
- **Both HUDs point their `Plugins\Source\Whats An AI Bridge` junction at the repo root.** Build intermediates are split per game (`obj\poe1`, `obj\poe2`).
- **`TargetFramework` lives only in Directory.Build.props.** The PoE2 HUD rewrites any `<TargetFramework>` in a plugin .csproj to `net8.0-windows` when compiling, which would break PoE1.
- **Shared files never import `ExileCore*` directly.** Namespaces come from the per-game `GlobalUsings.cs`.
- **Anything with a different API or different game knowledge goes in the per-game partial** (`Poe1/WhatsAnAiBridge.Poe1.cs`, `Poe2/WhatsAnAiBridge.Poe2.cs`), and the two must declare the same members.
- **No fake values for what a game lacks.** Omit the field (nullable DTO property), call `MarkUnsupported("feature")`, and list it in `UnsupportedFeatures`. Responses carry `game` and `unsupported`, and `hello` reports both.

## In-HUD stats panel (`Shared\StatsPanel.cs`)

- An ImGui window ("Player Stats", id `###bridge_stats_panel`) drawn from `Render()`. It is one surface over the **shared stats view** (`StatsUiState` in the settings: pins, filter, category, selection, sort, `PanelOpen`), the same state the MCP App and agents use.
- **Reads** the state every frame and the 250 ms stats snapshot (`GetStatsSnapshot`); **writes only through the mutators** in `Shared\StatsFeature.cs` (`SetStatPinned`, `SetStatsFilter`, `SelectStat`, `SetStatsView`). Never add a second copy of the view state.
- Sync detection: the panel remembers the rev its own writes produced; any other rev change is "remote" (app/agent). A remote selection scrolls the row into view and pulses it; the header pill shows "Synced: <what>" for ~2.5 s.
- `PanelOpen` is the open flag: closing the window calls `SetStatsView(panelOpen: false)`; agents toggle it with `set_stats_view`; the settings UI has a "Show Player Stats Panel" pill. No hotkey.
- Panel-only preferences (section collapse, "Keys" label mode) are `StatsPanel*` ToggleNodes in the settings, not part of the shared view. Window position/size come from the HUD's `imgui.ini`.
- ASCII only in strings (the HUD font has no glyphs beyond it); icons are drawn with the draw list. Card fills derive from the live ImGui style (`SurfaceFill`); life/ES/mana and element hues are fixed. Filter/sort rows are rebuilt only when the snapshot or the view inputs change, and the table draws only the visible rows.

## Agent guide panel (`Shared\GuidePanel.cs`, state in `Shared\AgentGuide.cs`)

- An ImGui window (id `###bridge_guide_panel`) drawn from `Render()` after the stats panel: the agent's current instruction for the user as a sticky card, with a compact "combat log" of what the agent is doing under it. Agents drive it with `guide.set {title?, instruction?, step?, steps?, status?, detail?, clear?}` and `guide.log {text, kind?: agent|step|result|warn}`; `guide.state` reads it back. State is in memory only.
- **Reads** `GuideSnapshot()` once per frame; the only write is `GuideDismiss()` (the card's x). Statuses: `waiting` (user must act: big text, pulsing accent frame, elapsed time), `detected` / `settling` (spinner, progress line), `captured` (green, shrinks to a one-line receipt after 4 s), `failed` ("TRY AGAIN", red, big text again), `info` / `done` (calm). A new instruction, or a return to waiting/failed, flashes the card.
- **Visibility:** `Settings.ShowAgentGuide` is the switch (Settings tab, "Show Agent Guide"). The panel hides itself when everything is quiet (idle/captured/done/info) and nothing changed for 2 minutes; waiting, failed and progress states stay. It reappears on the next rev change. The log keeps the last 40 lines and shows 6, newest at the bottom, older ones fading; its collapse state is the panel-only `GuideLogOpen` ToggleNode.
- **Stays out of the way:** 520 px wide, auto height, default top-centre under the skill bar (clear of the stash at x 0..665 and the inventory at 1080p); the user drags it anywhere and `imgui.ini` remembers. Flags `NoFocusOnAppearing | NoBringToFrontOnFocus | NoNav | NoScrollWithMouse`, no title bar, no background (the cards are drawn). It only takes the mouse over its own rectangle.
- Same rules as the stats panel: ASCII only (glyphs via the draw list), colours from the live ImGui style (`PanelTheme`, `SurfaceFill`) plus the fixed tones; no ExileCore* imports.

## Bridge protocol (v2)

- Newline-delimited JSON-RPC 2.0 on `127.0.0.1:<port>`. The port and a per-launch random token are written to `<HUD>\<BridgeDirectory>\bridge-port.txt` / `bridge-token.txt` (default `claude-bridge`).
- **Every method requires the token**, including `ping` and `status`. Loopback is reachable by every local account, including the separate `gaming` user.
- `method: "query"` with `params.type` = `hello | player | area | playerstats | ui | stash | entities[:range] | monsters | items | deep:<filter>[:range] | eval:<expr> | describe:<expr> | record:* | recording:* | snapshot`.
- Structured methods with named params: `stats.*` (shared stats view), `recording.*` (stateless playback), and `hud.*` (dev loop):
  - `hud.plugins` lists loaded and failed source plugins.
  - `hud.reload_plugin {name}` queues a recompile of one source plugin, like the menu's Reload button. It runs in `Render` on the main thread, and the HUD pauses while compiling. The bridge refuses to reload itself. The setting is `AllowPluginReload`.
  - `hud.reload_status` reports the queued reload and the last result. The watchdog answers it even while the HUD is compiling.
  - `PluginManager.ReloadSourcePlugin` is internal on both HUDs and is called by reflection. Check it with the MCP's `hud_type` after HUD updates.
  - **Reloading changed code needs the HUD setting Core → Plugin Settings → "Avoid locking plugin dlls" turned on.** It is off by default on both HUDs, and takes effect for plugins loaded after it is turned on, so restart the HUD once after ticking it.
    - When it's off, the HUD loads each plugin DLL from its file and keeps it locked. A recompile then can't replace the DLL, and the HUD has already unloaded the plugin, so it stays off until a restart.
    - So `hud.reload_plugin` refuses with `dll_locked` while the setting is off; `force=true` overrides that when the code didn't change. `hud.plugins` reports the setting as `avoidLockingDllFiles`.
- `script.run {code, thread: main|worker, timeoutMs}` and `script.result {id}` run C# inside the HUD using the Roslyn scripting DLLs both HUDs ship (`Shared/ScriptRunner.cs`).
  - **Off by default** (setting *Allow C# Scripts*): it's arbitrary code in the HUD process.
  - **Compile and run:** compiling happens on a worker; the run happens at the start of `Render` (main thread) or on the worker. A running script can't be aborted.
  - **What scripts get:** a prelude (`GameController`, `Log(object)`) and the game namespaces present in this build. The last expression is the result, serialized like `eval:` results.
  - **No globals type:** Roslyn needs a file-backed assembly for one, and plugin assemblies load from memory when the HUD avoids locking DLLs.
  - **Caching:** identical code reuses its compiled script.
  - **CI:** the runner's reference set includes the Roslyn DLLs (scaffolding `tools/ci/sync-hud-refs.ps1`).
- `object.explore {path, offset, limit, csharp?}` (`Shared/ObjectExplorer.cs`) returns one level of the object model at a walker path, for mapping data out:
  - **For the node and each child:** type, kind, a one-line preview (structs as `X=1 Y=2`, objects with their Name/RenderName and visibility), counts, the walker `path`, and null-safe `csharp`. Dictionaries with enum keys get typed keys (`Stats?[GameStat.MaximumLife]`).
  - **Entities** also list their components, as `GetComponent<T>()` paths.
  - **Paging:** collections are paged with `offset`/`limit`.
  - **Time budget:** reads run on the main thread under a 60 ms budget, and unread members are listed in `skipped`. Getters taking 5 ms or more get `slowMs`.
  - **Shared resolver:** `ExpressionWalker.Resolve` is the one path resolver behind `eval:`, `describe:` and explore.
- Quick manual test: `tools\bridge-query.ps1 -Game poe2 hello player` (in the scaffolding repo).
