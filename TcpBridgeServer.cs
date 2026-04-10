using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace WhatsAnAiBridge;

/// <summary>
/// TCP server that accepts JSON-RPC 2.0 requests on localhost and queues them
/// for processing on the main thread via Tick(). Responses are sent back
/// asynchronously to the requesting client.
/// </summary>
public class TcpBridgeServer : IDisposable
{
    public class PendingRequest
    {
        public required string Method { get; init; }
        public JToken? Params { get; init; }
        public required object Id { get; init; }
        public required TaskCompletionSource<string> Response { get; init; }
    }

    public ConcurrentQueue<PendingRequest> PendingRequests { get; } = new();

    public int ConnectedClients => _clientCount;
    public bool IsRunning => _listener != null;
    public int Port => _port;
    public string? AuthToken => _authToken;

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private int _port;
    private string? _authToken;
    private string? _tokenFilePath;
    private int _clientCount;
    private readonly Action<string> _log;
    private readonly Action<string> _logError;

    public TcpBridgeServer(Action<string> log, Action<string> logError)
    {
        _log = log;
        _logError = logError;
    }

    public void Start(int port, string bridgeDir)
    {
        if (_listener != null) return;

        _port = port;
        _cts = new CancellationTokenSource();

        // Generate auth token
        var tokenBytes = new byte[32];
        using (var rng = RandomNumberGenerator.Create())
            rng.GetBytes(tokenBytes);
        _authToken = Convert.ToBase64String(tokenBytes);

        // Write token file
        if (!Directory.Exists(bridgeDir))
            Directory.CreateDirectory(bridgeDir);
        _tokenFilePath = Path.Combine(bridgeDir, "bridge-token.txt");
        File.WriteAllText(_tokenFilePath, _authToken, Encoding.UTF8);

        // Write port file (for ephemeral port support)
        var portFilePath = Path.Combine(bridgeDir, "bridge-port.txt");
        File.WriteAllText(portFilePath, port.ToString(), Encoding.UTF8);

        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();

        _log($"TCP Bridge server started on 127.0.0.1:{port}");

        // Accept clients on background thread
        Task.Run(() => AcceptClientsAsync(_cts.Token), _cts.Token);
    }

    public void Stop()
    {
        _cts?.Cancel();
        try { _listener?.Stop(); } catch { }
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _clientCount = 0;

        // Clean up token file
        try { if (_tokenFilePath != null && File.Exists(_tokenFilePath)) File.Delete(_tokenFilePath); } catch { }

        _log("TCP Bridge server stopped");
    }

    private async Task AcceptClientsAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var client = await _listener!.AcceptTcpClientAsync(ct);
                client.NoDelay = true;
                _ = Task.Run(() => HandleClientAsync(client, ct), ct);
            }
            catch (OperationCanceledException) { break; }
            catch (ObjectDisposedException) { break; }
            catch (Exception ex) { _logError($"Accept error: {ex.Message}"); }
        }
    }

    private async Task HandleClientAsync(TcpClient client, CancellationToken ct)
    {
        Interlocked.Increment(ref _clientCount);
        var endpoint = client.Client.RemoteEndPoint?.ToString() ?? "unknown";
        _log($"Client connected: {endpoint}");

        try
        {
            using (client)
            await using (var stream = client.GetStream())
            using (var reader = new StreamReader(stream, Encoding.UTF8, leaveOpen: true))
            await using (var writer = new StreamWriter(stream, Encoding.UTF8, leaveOpen: true) { AutoFlush = true })
            {
                while (!ct.IsCancellationRequested && client.Connected)
                {
                    string? line;
                    try
                    {
                        line = await reader.ReadLineAsync(ct);
                    }
                    catch (OperationCanceledException) { break; }
                    catch (IOException) { break; }

                    if (line == null) break; // Client disconnected
                    if (string.IsNullOrWhiteSpace(line)) continue;

                    var response = await ProcessJsonRpcMessage(line);
                    if (response != null)
                    {
                        try
                        {
                            await writer.WriteLineAsync(response.AsMemory(), ct);
                        }
                        catch (IOException) { break; }
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logError($"Client error ({endpoint}): {ex.Message}");
        }
        finally
        {
            Interlocked.Decrement(ref _clientCount);
            _log($"Client disconnected: {endpoint}");
        }
    }

    private async Task<string?> ProcessJsonRpcMessage(string json)
    {
        JObject msg;
        try
        {
            msg = JObject.Parse(json);
        }
        catch
        {
            return JsonRpcError(null, -32700, "Parse error");
        }

        var id = msg["id"];
        var method = msg["method"]?.Value<string>();

        if (string.IsNullOrEmpty(method))
            return JsonRpcError(id, -32600, "Invalid request: missing method");

        // Auth check (skip for ping)
        if (method != "ping" && _authToken != null)
        {
            var token = msg["token"]?.Value<string>() ?? msg["params"]?["token"]?.Value<string>();
            if (token != _authToken)
                return JsonRpcError(id, -32001, "Authentication failed: invalid or missing token");
        }

        // Handle ping locally (no main thread needed)
        if (method == "ping")
        {
            return JsonRpcResult(id, JToken.FromObject("pong"));
        }

        // Handle status locally
        if (method == "status")
        {
            var status = new JObject
            {
                ["connected"] = true,
                ["pendingRequests"] = PendingRequests.Count,
                ["connectedClients"] = _clientCount,
            };
            return JsonRpcResult(id, status);
        }

        // Queue for main thread processing
        var tcs = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var pending = new PendingRequest
        {
            Method = method,
            Params = msg["params"],
            Id = id?.ToObject<object>() ?? 0,
            Response = tcs,
        };

        PendingRequests.Enqueue(pending);

        // Wait for main thread to process (with timeout)
        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            var result = await tcs.Task.WaitAsync(timeoutCts.Token);
            return JsonRpcResult(id, JToken.Parse(result));
        }
        catch (TimeoutException)
        {
            return JsonRpcError(id, -32002, "Request timed out waiting for main thread processing");
        }
        catch (Exception ex)
        {
            return JsonRpcError(id, -32603, $"Internal error: {ex.Message}");
        }
    }

    private static string JsonRpcResult(JToken? id, JToken result)
    {
        var response = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["result"] = result,
        };
        return response.ToString(Formatting.None);
    }

    private static string JsonRpcError(JToken? id, int code, string message)
    {
        var response = new JObject
        {
            ["jsonrpc"] = "2.0",
            ["id"] = id ?? JValue.CreateNull(),
            ["error"] = new JObject
            {
                ["code"] = code,
                ["message"] = message,
            },
        };
        return response.ToString(Formatting.None);
    }

    public void Dispose()
    {
        Stop();
    }
}
