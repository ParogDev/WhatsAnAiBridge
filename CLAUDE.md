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
- Quick manual test: `tools\bridge-query.ps1 -Game poe2 hello player` (in the scaffolding repo).
