using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Text.RegularExpressions;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Searching and snapshotting the live object graph inside the bridge, in one request each (the MCP used to walk it with
/// one object.explore round trip per object):
/// - object.find {path, name?, value?, depth, maxNodes, timeMs, maxMatches, listLimit, components}: breadth-first under a
///   walker path for members whose name contains `name` and/or whose one-line value contains `value` (numbers match whole).
///   Each match carries its walker path and null-safe C#, like object.explore's children.
/// - object.snapshot {path, depth, maxNodes, maxLeaves, timeMs, listLimit, components}: every leaf under a path down to
///   `depth`, keyed by walker sub-path, for watch_object to diff (eval: stops at 2 levels).
/// Both read public members only (MemberIndex.Readable, compiled getters after 64 reads), skip types outside the walker's
/// namespaces, memory plumbing and Owner back-references, never open the same object twice (same reference, or same type
/// at the same Address: wrappers are recreated per getter call), and stop at the node / time / match / leaf budget, saying
/// which and where in `truncated`. A root path that does not resolve is refused with the walker's message (the segment).
/// Runs on the main thread like every request: the time budget bounds the frame it costs.
/// </summary>
public partial class WhatsAnAiBridge
{
    private static readonly HashSet<string> WalkPlumbingTypes = new(StringComparer.Ordinal) { "Memory", "IMemory", "GameController", "TheGame" };
    private const int WalkMaxErrors = 20;
    private const int WalkMaxAliases = 30;

    private string? ProcessObjectSearchMethod(string method, JToken? p) => method switch
    {
        "object.find" => ObjectFind(p),
        "object.snapshot" => ObjectSnapshot(p),
        _ => null,
    };

    // ── object.find ──────────────────────────────────────────────────

    private string ObjectFind(JToken? p)
    {
        var path = (p?["path"]?.Value<string>() ?? "GameController.Player").Trim();
        var name = NullIfBlank(p?["name"]?.Value<string>());
        var value = NullIfBlank(p?["value"]?.Type is JTokenType.Integer or JTokenType.Float
            ? p!["value"]!.ToString(Newtonsoft.Json.Formatting.None) : p?["value"]?.Value<string>());
        if (name == null && value == null)
            return Serialize(new ObjectPathErrorDto { Error = "bad_request", Message = "Pass name (a member name substring) and/or value (text the value contains).", Path = path });

        var walk = new ObjectWalk(
            depth: Math.Clamp(p?["depth"]?.Value<int>() ?? 4, 1, 8),
            maxNodes: Math.Clamp(p?["maxNodes"]?.Value<int>() ?? 5000, 1, 50_000),
            timeMs: Math.Clamp(p?["timeMs"]?.Value<int>() ?? 200, 10, 3000),
            listLimit: Math.Clamp(p?["listLimit"]?.Value<int>() ?? 200, 1, 2000),
            components: p?["components"]?.Value<bool>() ?? true);
        var maxMatches = Math.Clamp(p?["maxMatches"]?.Value<int>() ?? 100, 1, 500);
        var valueRx = value == null ? null : ValueMatcher(value);

        if (ResolveRoot(path, out var error) is not { } rootRef) return Serialize(error!);
        var result = new ObjectFindResponse { Root = path, Name = name, Value = value, Depth = walk.Depth, Game = GameId };

        walk.Run(rootRef.Value, path, p?["csharp"]?.Value<string>() ?? ToCSharp(path), child =>
        {
            var kind = child.Kind;
            bool nameHit = name != null && child.Name.Contains(name, StringComparison.OrdinalIgnoreCase);
            string? preview = null;
            bool valueHit = false;
            if (valueRx != null && !child.Blocked && kind is not ("object" or "list" or "dictionary" or "component" or "sequence" or "error"))
            {
                preview = child.Preview();
                valueHit = valueRx.IsMatch(preview);
            }
            if ((name == null || nameHit) && (valueRx == null || valueHit))
            {
                result.Matches.Add(new ObjectFindMatchDto
                {
                    Path = child.Path, Csharp = child.Csharp, Kind = kind, Level = child.Level,
                    Type = kind == "component" ? "component" : child.TypeName(),
                    Value = kind == "component" ? null : preview ?? child.Preview(),
                    By = nameHit && valueHit ? "name+value" : nameHit ? "name" : "value",
                });
                if (result.Matches.Count >= maxMatches) return "matches";
            }
            return null;
        });

        result.Budget = walk.Budget;
        result.Truncated = walk.Truncated;
        result.Errors = walk.Errors;
        result.Aliases = walk.Aliases;
        return Serialize(result);
    }

    /// <summary>Numbers match whole (356 not inside 3560 or 35.6); text matches as a case-insensitive substring.</summary>
    internal static Regex ValueMatcher(string text)
    {
        var numeric = double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out _);
        var esc = Regex.Escape(text);
        return new Regex(numeric ? $@"(?<![\d.]){esc}(?![\d.])" : esc, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    }

    // ── object.snapshot ──────────────────────────────────────────────

    private string ObjectSnapshot(JToken? p)
    {
        var path = (p?["path"]?.Value<string>() ?? "").Trim();
        if (path.Length == 0) return Serialize(new ObjectPathErrorDto { Error = "bad_request", Message = "Pass path (a walker path starting with GameController).", Path = path });
        var walk = new ObjectWalk(
            depth: Math.Clamp(p?["depth"]?.Value<int>() ?? 3, 1, 8),
            maxNodes: Math.Clamp(p?["maxNodes"]?.Value<int>() ?? 2000, 1, 20_000),
            timeMs: Math.Clamp(p?["timeMs"]?.Value<int>() ?? 100, 10, 1000),
            listLimit: Math.Clamp(p?["listLimit"]?.Value<int>() ?? 50, 1, 500),
            components: p?["components"]?.Value<bool>() ?? false);
        var maxLeaves = Math.Clamp(p?["maxLeaves"]?.Value<int>() ?? 5000, 1, 20_000);

        if (ResolveRoot(path, out var error) is not { } rootRef) return Serialize(error!);
        var root = rootRef.Value;
        var result = new ObjectSnapshotResponse { Path = path, Depth = walk.Depth, Game = GameId, Type = root == null ? "null" : ExpressionWalker.FormatTypeName(root.GetType()) };
        var leaves = result.Leaves;

        if (root == null || !ObjectWalk.IsExpandableValue(root))
        {
            leaves["value"] = ObjectWalk.LeafText(root);
            result.Budget = walk.Budget;
            return Serialize(result);
        }

        walk.Run(root, path, ToCSharp(path), child =>
        {
            if (child.Blocked || child.Kind == "component") return null;
            var key = child.Key;
            if (child.Error != null) leaves[key] = "(error: " + child.Error + ")";
            else if (child.Value is IDictionary d && !child.WillOpen) leaves[key + ".Count"] = d.Count.ToString(CultureInfo.InvariantCulture);
            else if (child.Value is ICollection c && !child.WillOpen) leaves[key + ".Count"] = c.Count.ToString(CultureInfo.InvariantCulture);
            else if (child.Value == null || !ObjectWalk.IsExpandableValue(child.Value)) leaves[key] = ObjectWalk.LeafText(child.Value);
            else if (!child.WillOpen && !child.Skipped) leaves[key] = child.Preview();
            return leaves.Count >= maxLeaves ? "leaves" : null;
        }, opened =>
        {
            // Collections opened: their count is a leaf too (items past listLimit are not).
            if (opened.Value is IDictionary d) leaves[ObjectWalk.Join(opened.Key, ".Count")] = d.Count.ToString(CultureInfo.InvariantCulture);
            else if (opened.Value is ICollection c) leaves[ObjectWalk.Join(opened.Key, ".Count")] = c.Count.ToString(CultureInfo.InvariantCulture);
        });

        result.Budget = walk.Budget;
        result.Truncated = walk.Truncated;
        result.Errors = walk.Errors;
        result.Aliases = walk.Aliases;
        return Serialize(result);
    }

    // ── Shared ───────────────────────────────────────────────────────

    private sealed class RootRef { public object? Value; }

    /// <summary>The root object, or null with the error naming the broken segment (the walker's message).</summary>
    private RootRef? ResolveRoot(string path, out ObjectPathErrorDto? error)
    {
        error = null;
        try
        {
            var value = new ExpressionWalker(GameController).Resolve(path, out var why);
            if (why != null) { error = new ObjectPathErrorDto { Error = "resolve_failed", Message = why, Path = path }; return null; }
            return new RootRef { Value = value };
        }
        catch (Exception ex)
        {
            error = new ObjectPathErrorDto { Error = "resolve_failed", Message = Message(ex), Path = path };
            return null;
        }
    }

    private static string? NullIfBlank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>One value met while walking: a member, item, entry or component of an opened node.</summary>
    private sealed class WalkChild
    {
        public string Name = "";
        /// <summary>Walker sub-path from the root ("Life.CurHP", "Buffs[0]", "Stats[\"MaximumLife\"]").</summary>
        public string Key = "";
        public string? Path;
        public string Csharp = "";
        public object? Value;
        public Type? Declared;
        public int Level;
        public bool Blocked;
        public bool IsComponent;
        public string? Error;
        /// <summary>Set by the walk before the visitor runs: this child will be opened (queued).</summary>
        public bool WillOpen;
        /// <summary>Expandable but deliberately not opened: plumbing, Owner, or already visited.</summary>
        public bool Skipped;

        public string Kind => IsComponent ? "component" : Blocked ? "blocked" : Error != null ? "error" : ObjectWalk.KindOf(Value);
        public string Preview() => Error != null ? "(error: " + Error + ")" : Blocked ? "" : Describe(Value, Declared)["preview"]?.ToString() ?? "";
        public string TypeName() => ExpressionWalker.FormatTypeName(Value?.GetType() ?? Declared ?? typeof(object));
    }

    /// <summary>Breadth-first walk with budgets; the visitor sees every child once and may stop the walk (returns a reason).</summary>
    private sealed class ObjectWalk
    {
        public readonly int Depth;
        private readonly int _maxNodes, _timeMs, _listLimit;
        private readonly bool _components;
        public readonly ObjectWalkBudgetDto Budget;
        public ObjectWalkTruncationDto? Truncated;
        public List<ObjectWalkErrorDto>? Errors;
        public List<ObjectWalkAliasDto>? Aliases;

        // Value -> where it was first met (walker path, else C#).
        private readonly Dictionary<object, string> _seenRefs = new(ReferenceEqualityComparer.Instance);
        private readonly Dictionary<(Type, long), string> _seenAddr = new();
        private readonly Stopwatch _sw = new();

        public ObjectWalk(int depth, int maxNodes, int timeMs, int listLimit, bool components)
        {
            Depth = depth; _maxNodes = maxNodes; _timeMs = timeMs; _listLimit = listLimit; _components = components;
            Budget = new ObjectWalkBudgetDto { MaxNodes = maxNodes, TimeMs = timeMs };
        }

        private sealed class Node { public object Value = null!; public string? Path; public string Csharp = ""; public string Key = ""; public int Level; }

        public void Run(object? root, string path, string csharp, Func<WalkChild, string?> visit, Action<WalkChild>? opened = null)
        {
            _sw.Start();
            try
            {
                if (root == null || !IsExpandableValue(root)) return;
                Remember(root, path, out _);
                var queue = new Queue<Node>();
                queue.Enqueue(new Node { Value = root, Path = path, Csharp = csharp, Key = "", Level = 0 });
                var kids = new List<WalkChild>();
                while (queue.Count > 0)
                {
                    var node = queue.Peek();
                    if (Budget.NodesOpened >= _maxNodes) { Stop("nodes", node, queue.Count, "raise maxNodes, lower depth or search a narrower root"); return; }
                    if (_sw.ElapsedMilliseconds > _timeMs) { Stop("time", node, queue.Count, "raise timeMs, lower depth or search a narrower root"); return; }
                    queue.Dequeue();
                    Budget.NodesOpened++;
                    if (node.Level > Budget.DeepestLevel) Budget.DeepestLevel = node.Level;
                    opened?.Invoke(new WalkChild { Key = node.Key, Value = node.Value, Path = node.Path, Level = node.Level });

                    kids.Clear();
                    if (!Open(node, kids)) { Stop("time", node, queue.Count + 1, "raise timeMs: this node's members were not all read"); return; }
                    foreach (var child in kids)
                    {
                        if (child.Error != null)
                        {
                            Budget.GetterErrors++;
                            if ((Errors ??= new()).Count < WalkMaxErrors) Errors.Add(new ObjectWalkErrorDto { At = child.Path ?? child.Csharp, Message = child.Error });
                        }
                        else if (child.Blocked) Budget.Blocked++;
                        else if (child.Value != null && IsExpandableValue(child.Value))
                        {
                            if (child.Name == "Owner" || WalkPlumbingTypes.Contains(child.Value.GetType().Name)) { Budget.Plumbing++; child.Skipped = true; }
                            else if (child.Level >= Depth) Budget.AtDepthLimit++;
                            else if (!Remember(child.Value, child.Path ?? child.Csharp, out var first))
                            {
                                Budget.Cycles++; child.Skipped = true;
                                Alias(child.Path ?? child.Csharp, first!);
                            }
                            else
                            {
                                child.WillOpen = true;
                                queue.Enqueue(new Node { Value = child.Value, Path = child.Path, Csharp = child.Csharp, Key = child.Key, Level = child.Level });
                            }
                        }
                        if (visit(child) is { } stop)
                        {
                            Stop(stop, node, queue.Count,stop == "matches" ? "raise maxMatches or narrow name/value" : "raise maxLeaves or watch a narrower path");
                            return;
                        }
                    }
                }
            }
            finally
            {
                Budget.ElapsedMs = Math.Round(_sw.Elapsed.TotalMilliseconds, 2);
            }
        }

        private void Stop(string reason, Node at, int pending, string hint)
            => Truncated = new ObjectWalkTruncationDto { Reason = reason, At = at.Path ?? at.Csharp, Pending = pending, Hint = hint };

        /// <summary>
        /// False when the value was queued before (same reference, or same type at the same nonzero Address); then
        /// <paramref name="first"/> is where. Reference types only: a boxed struct is a new box per read.
        /// </summary>
        private bool Remember(object value, string label, out string? first)
        {
            first = null;
            var t = value.GetType();
            if (t.IsValueType) return true;
            if (_seenRefs.TryGetValue(value, out first)) return false;
            if (value is not ICollection && MemoryObjects.Address(value) is { } a && a != 0)
            {
                if (_seenAddr.TryGetValue((t, a), out first)) return false;
                _seenAddr[(t, a)] = label;
            }
            _seenRefs[value] = label;
            return true;
        }

        /// <summary>A second path to an object already opened elsewhere (not a back-reference to an ancestor): worth naming.</summary>
        private void Alias(string label, string first)
        {
            if (label.StartsWith(first, StringComparison.Ordinal)) return;   // a member pointing back up its own chain
            if ((Aliases ??= new()).Count < WalkMaxAliases) Aliases.Add(new ObjectWalkAliasDto { Path = label, SameAs = first });
        }

        /// <summary>Reads the node's children into <paramref name="into"/>; false when the time budget ran out part way.</summary>
        private bool Open(Node n, List<WalkChild> into)
        {
            var value = n.Value;
            var type = value.GetType();
            var level = n.Level + 1;
            if (!ExpressionWalker.IsTypeAllowed(type)) return true;   // counted where it was met (Blocked) or a root outside: nothing to read

            if (value is IDictionary dict)
            {
                var keyType = type.IsGenericType ? type.GetGenericArguments()[0] : null;
                // Nested indexers (a["x"]["y"]) aren't walker syntax: entries of an indexed path get no path.
                var addressable = n.Path != null && !n.Path.EndsWith("]", StringComparison.Ordinal);
                var i = 0;
                try
                {
                    foreach (DictionaryEntry e in dict)
                    {
                        if (i++ >= _listLimit) { Budget.ItemsSkipped += dict.Count - _listLimit; break; }
                        if (_sw.ElapsedMilliseconds > _timeMs) return false;
                        var key = e.Key?.ToString() ?? "null";
                        var seg = ExpressionWalker.KeySegment(key);   // escaped: keys with dots, brackets or quotes read back
                        Budget.MembersRead++;
                        into.Add(new WalkChild
                        {
                            Name = "[" + key + "]", Key = n.Key + seg, Path = addressable ? n.Path + seg : null,
                            Csharp = $"{n.Csharp}?[{KeyLiteral(e.Key, keyType)}]", Value = e.Value, Declared = e.Value?.GetType(), Level = level,
                        });
                    }
                }
                catch (Exception ex) { into.Add(EnumerationError(n, type, i, ex, level)); }   // keep what was read, name where it broke
                return true;
            }
            if (value is IList list)
            {
                // Nested indexers (a[0][1]) aren't walker syntax: items of an indexed path get no path.
                var addressable = n.Path != null && !n.Path.EndsWith("]", StringComparison.Ordinal);
                int count;
                try { count = list.Count; } catch (Exception ex) { into.Add(new WalkChild { Name = "Count", Key = n.Key + ".Count", Csharp = n.Csharp + "?.Count", Error = Message(ex), Level = level }); return true; }
                for (int i = 0; i < count; i++)
                {
                    if (i >= _listLimit) { Budget.ItemsSkipped += count - _listLimit; break; }
                    if (_sw.ElapsedMilliseconds > _timeMs) return false;
                    var seg = "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                    var child = new WalkChild { Name = seg, Key = n.Key + seg, Path = addressable ? n.Path + seg : null, Csharp = $"{n.Csharp}?[{i}]", Level = level };
                    Budget.MembersRead++;
                    try { child.Value = list[i]; child.Declared = child.Value?.GetType(); } catch (Exception ex) { child.Error = Message(ex); }
                    into.Add(child);
                }
                return true;
            }
            if (value is IEnumerable seq and not string)
            {
                var i = 0;
                try
                {
                    foreach (var item in seq)
                    {
                        if (i >= _listLimit) { Budget.ItemsSkipped++; break; }   // a lazy sequence has no count: one "more" marker
                        if (_sw.ElapsedMilliseconds > _timeMs) return false;
                        var seg = "[" + i.ToString(CultureInfo.InvariantCulture) + "]";
                        Budget.MembersRead++;
                        into.Add(new WalkChild { Name = seg, Key = n.Key + seg, Path = null, Csharp = $"{n.Csharp}?.ElementAt({i})", Value = item, Declared = item?.GetType(), Level = level });
                        i++;
                    }
                }
                catch (Exception ex) { into.Add(EnumerationError(n, type, i, ex, level)); }
                return true;
            }

            // Object or struct: readable public members, one per name (most-derived), sorted.
            foreach (var member in MemberIndex.Readable(type))
            {
                if (_sw.ElapsedMilliseconds > _timeMs) return false;
                var seg = "." + member.Name;
                var child = new WalkChild
                {
                    Name = member.Name, Key = Join(n.Key, seg), Path = n.Path == null ? null : n.Path + seg,
                    // Members of a struct take '.': inside a ?. chain the struct isn't nullable ('?.' on it is CS0023).
                    Csharp = type.IsValueType ? $"{n.Csharp}.{member.Name}" : $"{n.Csharp}?.{member.Name}",
                    Declared = member.Type, Level = level,
                };
                var declared = member.Type;
                if (!ExpressionWalker.IsTypeAllowed(declared) && !declared.IsPrimitive && declared != typeof(string) && !declared.IsEnum)
                    child.Blocked = true;
                else
                {
                    Budget.MembersRead++;
                    try { child.Value = member.Get(value); } catch (Exception ex) { child.Error = Message(ex); }
                }
                into.Add(child);
            }
            if (_components) AddComponents(n, type, level, into);
            return true;
        }

        /// <summary>Entities: each component with a HUD type as a GetComponent&lt;T&gt;() child (read now: cached closed method, compiled call).</summary>
        private void AddComponents(Node n, Type type, int level, List<WalkChild> into)
        {
            if (type.Name != "Entity") return;
            IDictionary? names;
            try { names = MemberIndex.Find(type, "CacheComp", kinds: MemberIndex.Kinds.Property)?.Get(n.Value) as IDictionary; }
            catch (Exception ex) { into.Add(new WalkChild { Name = "CacheComp", Key = Join(n.Key, ".CacheComp"), Csharp = n.Csharp + "?.CacheComp", Error = Message(ex), Level = level }); return; }
            if (names == null) return;
            var compNames = new List<string>();
            foreach (var key in names.Keys) if (key?.ToString() is { Length: > 0 } s) compNames.Add(s);
            compNames.Sort(StringComparer.Ordinal);
            foreach (var name in compNames)
            {
                if (ExpressionWalker.ResolveComponentType(name) is not { } compType) continue;   // no HUD wrapper type
                var seg = $".GetComponent<{name}>()";
                var child = new WalkChild
                {
                    Name = name, Key = Join(n.Key, seg), Path = n.Path == null ? null : n.Path + seg,
                    Csharp = $"{n.Csharp}?.GetComponent<{name}>()", IsComponent = true, Declared = compType, Level = level,
                };
                Budget.MembersRead++;
                try
                {
                    if (MemberIndex.GenericMethod(type, "GetComponent", compType) is { } m) child.Value = MemberIndex.Call(m, n.Value);
                    else child.Error = $"No GetComponent<T> on {type.Name}";
                }
                catch (Exception ex) { child.Error = Message(ex); }
                into.Add(child);
            }
        }

        /// <summary>A collection's enumerator threw part way (modified while read, a broken lazy sequence): one error child naming it.</summary>
        private static WalkChild EnumerationError(Node n, Type type, int read, Exception ex, int level) => new()
        {
            Name = "(enumeration)", Key = n.Key, Path = n.Path, Csharp = n.Csharp, Level = level,
            Error = $"enumerating {ExpressionWalker.FormatTypeName(type)} threw after {read} item(s): {Message(ex)}",
        };

        internal static string Join(string key, string seg) => key.Length == 0 && seg.StartsWith('.') ? seg[1..] : key + seg;

        internal static bool IsExpandableValue(object v)
        {
            if (v is string || v is Enum || v is decimal) return false;
            var t = v.GetType();
            if (t.IsPrimitive) return false;
            if (v is ICollection c) return c.Count > 0;
            // System structs (TimeSpan, DateTime, Guid) read better as their text.
            return !(t.IsValueType && (t.Namespace == "System" || t.Namespace?.StartsWith("System.Threading", StringComparison.Ordinal) == true));
        }

        internal static string KindOf(object? v) => v switch
        {
            null => "null",
            string => "string",
            bool => "bool",
            Enum => "enum",
            IDictionary => "dictionary",
            ICollection => "list",
            _ when v.GetType().IsPrimitive || v is decimal => "number",
            IEnumerable => "sequence",
            _ when v.GetType().IsValueType => "struct",
            _ => "object",
        };

        /// <summary>A leaf's text: raw strings (up to 200 chars), invariant numbers, enum names, "null"; anything else its preview.</summary>
        internal static string LeafText(object? v) => v switch
        {
            null => "null",
            string s => s.Length > 200 ? s[..197] + "..." : s,
            bool b => b ? "true" : "false",
            Enum e => e.ToString(),
            _ when v.GetType().IsPrimitive || v is decimal => Number(v),
            _ => Describe(v, v.GetType())["preview"]?.ToString() ?? v.GetType().Name,
        };
    }
}
