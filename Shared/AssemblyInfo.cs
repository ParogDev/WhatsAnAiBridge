using System.Diagnostics;

// The HUD compiles source plugins in Debug: Roslyn then marks the assembly DisableOptimizations, and the JIT runs every
// method unoptimized (measured in Whats A Health Bar: Render 450 -> 101 us, Tick 43 -> 14 us). Roslyn only synthesizes that attribute when the
// assembly doesn't declare one, so declaring it here without DisableOptimizations lets the JIT optimize this plugin.
// Breakpoints still bind; locals may show as optimized away in a debugger.
[assembly: Debuggable(DebuggableAttribute.DebuggingModes.IgnoreSymbolStoreSequencePoints)]
