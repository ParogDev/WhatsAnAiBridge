using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// Stateless recording access: every call names its recording file (the file name is the handle),
/// so callers never depend on a hidden "currently loaded" recording. Frame indexes are cached per
/// file, so repeated calls stay cheap. The legacy "recording:load" + "recording:frame:N" string
/// commands still work and share this cache.
/// </summary>
public partial class WhatsAnAiBridge
{
    private readonly Dictionary<string, (DateTime mtime, long size, List<long> offsets)> _frameIndexCache = new();

    /// <summary>Routes "recording.*" JSON-RPC methods. Returns null when the method isn't one of them.</summary>
    private string? ProcessRecordingMethod(string method, JToken? p)
    {
        if (!method.StartsWith("recording.")) return null;

        var file = p?["file"]?.Value<string>() ?? "";
        if (!TryResolveRecording(file, out var path, out var error))
            return Serialize(new ErrorResponse { Error = error, File = file });

        // Point the legacy single-recording state at this file too, so both APIs agree.
        _loadedRecordingPath = path;
        _loadedFrameOffsets = GetFrameIndex(path);

        switch (method)
        {
            case "recording.info":
                return Serialize(new RecordingLoadResponse { File = file, Frames = _loadedFrameOffsets.Count });

            case "recording.frame":
            {
                var n = p?["frame"]?.Value<int?>() ?? 0;
                if (n < 0 || n >= _loadedFrameOffsets.Count)
                    return Serialize(new ErrorResponse { Error = $"Frame {n} out of range", TotalFrames = _loadedFrameOffsets.Count, File = file });
                return ReadFrame(n);
            }

            case "recording.range":
            {
                var from = Math.Max(0, p?["from"]?.Value<int?>() ?? 0);
                var to = Math.Min(_loadedFrameOffsets.Count - 1, p?["to"]?.Value<int?>() ?? from);
                if (to - from > 50) to = from + 50;
                var frames = new List<object>();
                for (var i = from; i <= to; i++) frames.Add(ReadFrame(i));
                return Serialize(new RecordingFramesResponse { Frames = frames });
            }

            case "recording.search":
            {
                var term = p?["term"]?.Value<string>() ?? "";
                var matches = SearchFrames(term);
                return Serialize(new RecordingSearchResponse { Term = term, MatchCount = matches.Count, Frames = matches });
            }

            case "recording.summary":
                return SummarizeRecording();

            default:
                return Serialize(new ErrorResponse { Error = $"Unknown recording method '{method}'" });
        }
    }

    /// <summary>Accepts a bare file name inside the recordings folder only (no paths, no traversal).</summary>
    private bool TryResolveRecording(string file, out string path, out string error)
    {
        path = "";
        error = "";
        if (string.IsNullOrWhiteSpace(file) || file != Path.GetFileName(file) || file.Contains("..")
            || !file.EndsWith(".jsonl", StringComparison.OrdinalIgnoreCase))
        {
            error = "file must be a recording file name like 'rec_20260410_135500.jsonl' (see recording:list)";
            return false;
        }

        path = Path.Combine(_bridgeDir, "recordings", file);
        if (!File.Exists(path))
        {
            error = "File not found";
            return false;
        }
        return true;
    }

    private List<long> GetFrameIndex(string path)
    {
        var fi = new FileInfo(path);
        if (_frameIndexCache.TryGetValue(path, out var cached) && cached.mtime == fi.LastWriteTimeUtc && cached.size == fi.Length)
            return cached.offsets;

        LoadRecording(path); // fills _loadedFrameOffsets
        var offsets = _loadedFrameOffsets ?? new List<long>();
        if (_frameIndexCache.Count > 8) _frameIndexCache.Clear();
        _frameIndexCache[path] = (fi.LastWriteTimeUtc, fi.Length, offsets);
        return offsets;
    }
}
