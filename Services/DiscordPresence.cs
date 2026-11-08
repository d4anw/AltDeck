using System.IO.Pipes;
using System.Text;
using System.Text.Json.Nodes;

namespace RoDeck.Services;

/// <summary>
/// Shows "Playing RoDeck" and how many alts run on the user's Discord profile (Discord "Rich Presence").
/// Talks to the Discord desktop app over its local pipe (discord-ipc-0..9). Nothing leaves the PC except through Discord itself.
/// All pipe work runs on a background thread; Update() only decides whether something needs sending.
/// </summary>
public sealed class DiscordPresence
{
    /// <summary>RoDeck's own Discord application (public ID, not a secret). Its name "RoDeck" and the "altdeck_logo"
    /// art asset are what Discord shows, so nobody has to make their own app.</summary>
    public const string AppId = "1566917960700797029";

    private static readonly TimeSpan MinGap = TimeSpan.FromSeconds(15);   // Discord limits how often presence may change
    private static readonly TimeSpan Timeout3s = TimeSpan.FromSeconds(3);

    private readonly long _startUnix = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    private NamedPipeClientStream? _pipe;
    private string _connectedId = "";
    private volatile string _sentKey = "";          // what Discord shows right now ("" = nothing)
    private volatile string _status = "";
    private DateTime _nextTry = DateTime.MinValue;
    private int _busy;

    /// <summary>One short line for the Settings page: what is going on with Discord.</summary>
    public string Status => _status;

    /// <summary>Try again right away (used when the switch changed).</summary>
    public void Reset() { _nextTry = DateTime.MinValue; _sentKey = _pipe == null ? "" : "\0"; }

    /// <summary>Call every few seconds from the UI thread.</summary>
    public void Update(bool enabled, int running)
    {
        const string appId = AppId;

        if (!enabled)
        {
            _status = "";
            if (_pipe != null || _sentKey != "") Run(() => { Clear(); return Task.CompletedTask; });
            return;
        }

        string key = running.ToString();
        if (key == _sentKey && _pipe is { IsConnected: true } && _connectedId == appId) return;
        if (DateTime.UtcNow < _nextTry) return;
        Run(() => SendAsync(appId, key, running));
    }

    /// <summary>Removes the presence and closes the pipe (when RoDeck closes). Waits at most a moment.</summary>
    public void Close()
    {
        try { Task.Run(Clear).Wait(1500); } catch { }
    }

    // ------------------------------------------------------------------

    private void Run(Func<Task> work)
    {
        if (Interlocked.Exchange(ref _busy, 1) == 1) return;
        Task.Run(async () =>
        {
            try { await work(); }
            catch { }
            finally { Interlocked.Exchange(ref _busy, 0); }
        });
    }

    private async Task SendAsync(string appId, string key, int running)
    {
        try
        {
            await EnsureConnectedAsync(appId);

            var activity = new JsonObject
            {
                ["details"] = running == 0 ? "Idle, nothing running" : $"{running} client{(running == 1 ? "" : "s")} running",
                ["state"] = "All-in-one Roblox app",
                ["timestamps"] = new JsonObject { ["start"] = _startUnix },
                // the big picture on the card: an art asset named "altdeck_logo" uploaded to the Discord app
                // (Rich Presence > Art Assets). If it doesn't exist, Discord just shows its "?" placeholder.
                ["assets"] = new JsonObject { ["large_image"] = "altdeck_logo", ["large_text"] = "RoDeck" }
            };
            await CommandAsync(activity);

            _sentKey = key;
            _status = "Showing on your Discord profile.";
            _nextTry = DateTime.UtcNow + MinGap;
        }
        catch (Exception ex)
        {
            Disconnect();
            _sentKey = "";
            _status = ex is InvalidOperationException or IOException or TimeoutException or OperationCanceledException
                ? ex.Message
                : "Couldn't reach Discord: " + ex.Message;
            _nextTry = DateTime.UtcNow + MinGap;
        }
    }

    private void Clear()
    {
        try
        {
            if (_pipe is { IsConnected: true })
                CommandAsync(null).Wait(1500);
        }
        catch { }
        Disconnect();
        _sentKey = "";
    }

    private async Task EnsureConnectedAsync(string appId)
    {
        if (_pipe is { IsConnected: true } && _connectedId == appId) return;
        Disconnect();

        for (int i = 0; i < 10; i++)
        {
            var pipe = new NamedPipeClientStream(".", $"discord-ipc-{i}", PipeDirection.InOut, PipeOptions.Asynchronous);
            try
            {
                pipe.Connect(250);
                var hello = new JsonObject { ["v"] = 1, ["client_id"] = appId }.ToJsonString();
                await WriteFrameAsync(pipe, 0, hello);

                var (op, json) = await ReadFrameAsync(pipe);
                if (op == 2 || json.Contains("\"ERROR\"", StringComparison.Ordinal))
                    throw new InvalidOperationException("Discord didn't accept RoDeck's application. Try again later.");

                _pipe = pipe;
                _connectedId = appId;
                return;
            }
            catch (InvalidOperationException)
            {
                pipe.Dispose();
                throw;
            }
            catch
            {
                pipe.Dispose();   // this pipe isn't Discord or isn't free: try the next one
            }
        }
        throw new IOException("Discord isn't open. Start the Discord desktop app (not the browser version).");
    }

    /// <summary>Sets the activity, or removes it when activity is null.</summary>
    private async Task CommandAsync(JsonObject? activity)
    {
        var pipe = _pipe ?? throw new IOException("Not connected to Discord.");
        var payload = new JsonObject
        {
            ["cmd"] = "SET_ACTIVITY",
            ["args"] = new JsonObject { ["pid"] = Environment.ProcessId, ["activity"] = activity },
            ["nonce"] = Guid.NewGuid().ToString()
        };
        await WriteFrameAsync(pipe, 1, payload.ToJsonString());

        var (_, json) = await ReadFrameAsync(pipe);
        try
        {
            var reply = JsonNode.Parse(json);
            if ((string?)reply?["evt"] == "ERROR")
                throw new InvalidOperationException("Discord said: " + ((string?)reply?["data"]?["message"] ?? "error"));
        }
        catch (System.Text.Json.JsonException) { }
    }

    private void Disconnect()
    {
        try { _pipe?.Dispose(); } catch { }
        _pipe = null;
        _connectedId = "";
    }

    // ---- frames: 4 bytes opcode, 4 bytes length (both little-endian), then JSON

    private static async Task WriteFrameAsync(Stream s, int opcode, string json)
    {
        var body = Encoding.UTF8.GetBytes(json);
        var frame = new byte[8 + body.Length];
        BitConverter.TryWriteBytes(frame.AsSpan(0, 4), opcode);
        BitConverter.TryWriteBytes(frame.AsSpan(4, 4), body.Length);
        body.CopyTo(frame, 8);
        using var cts = new CancellationTokenSource(Timeout3s);
        await s.WriteAsync(frame, cts.Token);
        await s.FlushAsync(cts.Token);
    }

    private static async Task<(int Opcode, string Json)> ReadFrameAsync(Stream s)
    {
        using var cts = new CancellationTokenSource(Timeout3s);
        var head = new byte[8];
        await ReadExactAsync(s, head, cts.Token);
        int opcode = BitConverter.ToInt32(head, 0);
        int length = BitConverter.ToInt32(head, 4);
        if (length < 0 || length > 1_000_000) throw new IOException("Discord sent something unexpected.");
        var body = new byte[length];
        await ReadExactAsync(s, body, cts.Token);
        return (opcode, Encoding.UTF8.GetString(body));
    }

    private static async Task ReadExactAsync(Stream s, byte[] buffer, CancellationToken ct)
    {
        int got = 0;
        while (got < buffer.Length)
        {
            int n = await s.ReadAsync(buffer.AsMemory(got), ct);
            if (n == 0) throw new IOException("Discord closed the connection.");
            got += n;
        }
    }
}
