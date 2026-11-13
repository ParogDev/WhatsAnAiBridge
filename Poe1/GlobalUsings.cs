// PoE1 (ExileCore) namespaces and type aliases for the shared sources in ..\Shared.
// Shared files must not import ExileCore/ExileCore2 directly - only through these.
global using ExileCore;
global using ExileCore.PoEMemory.Components;
global using ExileCore.PoEMemory.MemoryObjects;
global using ExileCore.PoEMemory.FilesInMemory;
global using ExileCore.Shared.Enums;
global using ExileCore.Shared.Attributes;
global using ExileCore.Shared.Interfaces;
global using ExileCore.Shared.Nodes;
global using Actor = ExileCore.PoEMemory.Components.Actor;
global using Color = SharpDX.Color;
global using RectangleF = SharpDX.RectangleF;
global using Vector2N = System.Numerics.Vector2;
global using FileInMemory = ExileCore.PoEMemory.FileInMemory;
global using UiElement = ExileCore.PoEMemory.Element;
global using NextFrameTask = ExileCore.Shared.NextFrameTask;
global using PluginManager = ExileCore.Shared.PluginManager;
global using PluginWrapper = ExileCore.Shared.PluginWrapper;
// ImGui is Hexa.NET.ImGui, imported by the .csproj (<Using Include="Hexa.NET.ImGui" />, poe1 only): that item keeps the
// PoE1 HUD's plugin compiler from adding an unconditional one. PoE2 imports ImGuiNET in Poe2\GlobalUsings.cs.
