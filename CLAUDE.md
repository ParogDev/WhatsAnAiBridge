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
- Quick manual test: `tools\bridge-query.ps1 -Game poe2 hello player` (in the scaffolding repo).
