using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace WhiteHoney.UI;

/// <summary>
/// A tiny HTTP/1.1 server on the loopback address only, for the page and its API. Every request must name this
/// server as its Host, and every API call must carry the token made at start, so other web pages open in the
/// player's browser can't drive the tool.
/// </summary>
sealed class HttpHost
{
    const int MaxHeader = 16 * 1024;
    const int MaxBody = 1024 * 1024;

    const string Csp = "default-src 'self'; script-src 'self' 'unsafe-inline'; style-src 'self' 'unsafe-inline'; img-src 'self' data:; "
        + "connect-src 'self'; base-uri 'none'; form-action 'none'; frame-ancestors 'none'";

    readonly App _app;
    readonly byte[] _token;
    readonly string _tokenText;
    readonly bool _devQuit;
    readonly byte[] _page;
    TcpListener? _listener;
    int _streams;
    long _lastStreamEnded = Environment.TickCount64;
    long _byeAt;

    public int Port { get; private set; }
    public string PageUrl => $"http://127.0.0.1:{Port}/?t={Uri.EscapeDataString(_tokenText)}";
    /// <summary>Open event streams, one per open page.</summary>
    public int Streams => Volatile.Read(ref _streams);
    public bool EverConnected { get; private set; }
    public TimeSpan SinceLastStream => TimeSpan.FromMilliseconds(Environment.TickCount64 - Interlocked.Read(ref _lastStreamEnded));
    /// <summary>
    /// The page said its window is closing (POST /api/bye) and no page has connected since. A page that just went
    /// quiet (the browser put a background window to sleep) never says this.
    /// </summary>
    public bool SaidBye
    {
        get
        {
            // Only a goodbye that came with the last page's connection closing counts: an older one (a reload, or
            // a second window that was closed earlier) says nothing about a page that went quiet later.
            long bye = Interlocked.Read(ref _byeAt);
            long gap = Interlocked.Read(ref _lastStreamEnded) - bye;
            return bye != 0 && gap is > -3000 and < 5000;
        }
    }
    /// <summary>Set by POST /api/quit (testing only).</summary>
    public ManualResetEventSlim QuitRequested { get; } = new();

    public HttpHost(App app, string token, bool devQuit)
    {
        _app = app;
        _tokenText = token;
        _token = Encoding.UTF8.GetBytes(token);
        _devQuit = devQuit;
        using var res = typeof(HttpHost).Assembly.GetManifestResourceStream("index.html")
            ?? throw new InvalidOperationException("The page is missing from the program.");
        using var reader = new StreamReader(res, Encoding.UTF8);
        _page = Encoding.UTF8.GetBytes(reader.ReadToEnd().Replace("{{VERSION}}", Report.Version));
    }

    public void Start(int port)
    {
        _listener = new TcpListener(IPAddress.Loopback, port);
        _listener.Start();
        Port = ((IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoop(_listener);
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { }
    }

    async Task AcceptLoop(TcpListener listener)
    {
        while (true)
        {
            TcpClient client;
            try { client = await listener.AcceptTcpClientAsync(); }
            catch (ObjectDisposedException) { return; }
            catch (InvalidOperationException) { return; }
            catch (SocketException) { continue; }
            _ = Task.Run(() => Serve(client));
        }
    }

    // ---------------------------------------------------------------- one connection

    sealed class Request
    {
        public string Method = "", Path = "";
        public Dictionary<string, string> Query = new(StringComparer.Ordinal);
        public Dictionary<string, string> Headers = new(StringComparer.OrdinalIgnoreCase);
        public byte[] Body = Array.Empty<byte>();
        public string? Header(string name) => Headers.GetValueOrDefault(name);
    }

    async Task Serve(TcpClient client)
    {
        using (client)
        {
            try
            {
                client.NoDelay = true;
                var stream = client.GetStream();
                Request? req;
                int error;
                using (var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15)))
                    (req, error) = await ReadRequest(stream, cts.Token);
                if (req == null)
                {
                    if (error > 0) await SendText(stream, error, "Bad request");
                    return;
                }
                await Handle(req, stream);
            }
            catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
            catch (Exception ex) { Program.Log($"Request failed: {ex}"); }
        }
    }

    static async Task<(Request?, int)> ReadRequest(Stream s, CancellationToken ct)
    {
        var buf = new byte[MaxHeader];
        int len = 0, end = -1;
        while (end < 0)
        {
            if (len == buf.Length) return (null, 431);
            int n = await s.ReadAsync(buf.AsMemory(len), ct);
            if (n == 0) return (null, 0);
            int from = Math.Max(0, len - 3);
            len += n;
            end = buf.AsSpan(from, len - from).IndexOf("\r\n\r\n"u8);
            if (end >= 0) end += from;
        }

        var lines = Encoding.Latin1.GetString(buf, 0, end).Split("\r\n");
        var first = lines[0].Split(' ');
        if (first.Length != 3 || !first[2].StartsWith("HTTP/1.", StringComparison.Ordinal) || !first[1].StartsWith('/')) return (null, 400);
        var req = new Request { Method = first[0] };
        int q = first[1].IndexOf('?');
        req.Path = q < 0 ? first[1] : first[1][..q];
        if (q >= 0)
            foreach (var pair in first[1][(q + 1)..].Split('&', StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = pair.IndexOf('=');
                string Unescape(string x) { try { return Uri.UnescapeDataString(x.Replace('+', ' ')); } catch { return x; } }
                req.Query[Unescape(eq < 0 ? pair : pair[..eq])] = eq < 0 ? "" : Unescape(pair[(eq + 1)..]);
            }
        for (int i = 1; i < lines.Length; i++)
        {
            int colon = lines[i].IndexOf(':');
            if (colon <= 0) return (null, 400);
            var name = lines[i][..colon].Trim();
            var value = lines[i][(colon + 1)..].Trim();
            // Two Host headers could make the check below look at the wrong one.
            if (req.Headers.ContainsKey(name) && name.Equals("Host", StringComparison.OrdinalIgnoreCase)) return (null, 400);
            req.Headers[name] = value;
        }
        if (req.Headers.ContainsKey("Transfer-Encoding")) return (null, 411);

        long length = 0;
        if (req.Header("Content-Length") is { } cl && !long.TryParse(cl, NumberStyles.None, CultureInfo.InvariantCulture, out length)) return (null, 400);
        if (length > MaxBody) return (null, 413);
        var body = new byte[length];
        int have = (int)Math.Min(len - (end + 4), length);
        Array.Copy(buf, end + 4, body, 0, have);
        while (have < length)
        {
            int n = await s.ReadAsync(body.AsMemory(have), ct);
            if (n == 0) return (null, 400);
            have += n;
        }
        req.Body = body;
        return (req, 0);
    }

    bool TokenOk(string? given) =>
        given != null && CryptographicOperations.FixedTimeEquals(Encoding.UTF8.GetBytes(given), _token);

    bool HostOk(Request req) =>
        req.Header("Host") is { } host
        && (host.Equals($"127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase) || host.Equals($"localhost:{Port}", StringComparison.OrdinalIgnoreCase));

    bool OriginOk(Request req) =>
        req.Header("Origin") is not { } origin
        || origin.Equals($"http://127.0.0.1:{Port}", StringComparison.OrdinalIgnoreCase) || origin.Equals($"http://localhost:{Port}", StringComparison.OrdinalIgnoreCase);

    async Task Handle(Request req, Stream s)
    {
        // Not this server by name (DNS rebinding), or called from another site: no action at all.
        if (!HostOk(req) || !OriginOk(req) || req.Header("Sec-Fetch-Site") == "cross-site")
        {
            await SendText(s, 403, "Forbidden");
            return;
        }

        if (req.Path == "/")
        {
            if (req.Method != "GET") { await SendText(s, 405, "Method not allowed"); return; }
            if (!TokenOk(req.Query.GetValueOrDefault("t"))) { await SendText(s, 403, "Forbidden"); return; }
            await Send(s, 200, "text/html; charset=utf-8", _page);
            return;
        }
        if (!req.Path.StartsWith("/api/", StringComparison.Ordinal))
        {
            await SendText(s, 404, "Not found");
            return;
        }

        // The event stream and the goodbye beacon can't send headers (EventSource, sendBeacon), so they carry the
        // token in the address.
        bool events = req.Path == "/api/events";
        bool tokenInAddress = events || req.Path == "/api/bye";
        if (!TokenOk(req.Header("X-WH-Token") ?? (tokenInAddress ? req.Query.GetValueOrDefault("t") : null)))
        {
            await SendText(s, 403, "Forbidden");
            return;
        }

        if (req.Path is "/api/state" or "/api/events")
        {
            if (req.Method != "GET") { await SendText(s, 405, "Method not allowed"); return; }
            if (events) await Events(s);
            else await Send(s, 200, "application/json; charset=utf-8", _app.StateJson());
            return;
        }

        if (req.Method != "POST") { await SendText(s, 405, "Method not allowed"); return; }
        if (req.Path == "/api/bye")
        {
            Interlocked.Exchange(ref _byeAt, Environment.TickCount64);
            await Reply(s, App.Reply.Ok());
            return;
        }
        JsonElement body;
        try
        {
            using var doc = JsonDocument.Parse(req.Body.Length == 0 ? "{}"u8.ToArray() : req.Body);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) { await Reply(s, App.Reply.Fail(400, "Bad request.")); return; }
            body = doc.RootElement.Clone();
        }
        catch (JsonException)
        {
            await Reply(s, App.Reply.Fail(400, "Bad request."));
            return;
        }

        App.Reply reply;
        try
        {
            reply = req.Path switch
            {
                "/api/scan" => await _app.StartScan(),
                "/api/exclude" => await _app.SetTrim(Str(body, "mod"), Bool(body, "trim")),
                "/api/new-items" => await _app.SetNewItems(Str(body, "choice")),
                "/api/apply" => await _app.StartApply(),
                "/api/undo" => await _app.StartUndo(),
                "/api/report" => await _app.OpenReport(),
                "/api/show-fix" => _app.ShowFixFolder(),
                "/api/game" => await _app.SetGameFromUser(Str(body, "path")),
                "/api/browse" => await _app.Browse(Str(body, "for")),
                "/api/mo2" => _app.AnswerMo2(Str(body, "path"), Bool(body, "skip") == true),
                "/api/measure/start" => _app.StartMeasure(Str(body, "label")),
                "/api/measure/stop" => _app.StopMeasure(),
                "/api/quit" when _devQuit => Quit(),
                _ => App.Reply.Fail(404, "Not found."),
            };
        }
        catch (Exception ex)
        {
            Program.Log($"{req.Path} failed: {ex}");
            reply = App.Reply.Fail(500, "Something went wrong inside White Honey: " + ex.Message + " The details were saved in White-Honey-Crash.log next to the tool.");
        }
        await Reply(s, reply);
    }

    App.Reply Quit()
    {
        _ = Task.Delay(200).ContinueWith(_ => QuitRequested.Set());
        return App.Reply.Ok();
    }

    static string? Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    static bool? Bool(JsonElement o, string name) =>
        o.TryGetProperty(name, out var v) ? v.ValueKind switch { JsonValueKind.True => true, JsonValueKind.False => false, _ => null } : null;

    // ---------------------------------------------------------------- server-sent events

    /// <summary>Pushes the whole state whenever it changes (at most about 8 times a second), and a comment every 15 s.</summary>
    async Task Events(Stream s)
    {
        var head = new StringBuilder();
        head.Append("HTTP/1.1 200 OK\r\nContent-Type: text/event-stream; charset=utf-8\r\n");
        CommonHeaders(head);
        head.Append("Connection: close\r\n\r\n");
        await s.WriteAsync(Encoding.ASCII.GetBytes(head.ToString()));

        Interlocked.Increment(ref _streams);
        EverConnected = true;
        using var gone = new CancellationTokenSource();
        _ = WatchForClose(s, gone);
        try
        {
            await s.WriteAsync("retry: 1000\n\n"u8.ToArray(), gone.Token);
            long sent = -1;
            var sinceWrite = Stopwatch.StartNew();
            while (!gone.IsCancellationRequested)
            {
                var (version, changed) = _app.Watch();
                if (version != sent)
                {
                    sent = version;
                    var json = _app.StateJson();
                    var msg = new byte[json.Length + 8];
                    "data: "u8.CopyTo(msg);
                    json.CopyTo(msg, 6);
                    msg[^2] = (byte)'\n';
                    msg[^1] = (byte)'\n';
                    await s.WriteAsync(msg, gone.Token);
                    await s.FlushAsync(gone.Token);
                    sinceWrite.Restart();
                    await Task.Delay(125, gone.Token);
                    continue;
                }
                var wait = TimeSpan.FromSeconds(15) - sinceWrite.Elapsed;
                if (wait <= TimeSpan.Zero)
                {
                    await s.WriteAsync(": keep-alive\n\n"u8.ToArray(), gone.Token);
                    sinceWrite.Restart();
                    continue;
                }
                await Task.WhenAny(changed, Task.Delay(wait, gone.Token));
            }
        }
        catch (Exception ex) when (ex is IOException or SocketException or OperationCanceledException or ObjectDisposedException) { }
        finally
        {
            Interlocked.Exchange(ref _lastStreamEnded, Environment.TickCount64);
            Interlocked.Decrement(ref _streams);
            gone.Cancel();
        }
    }

    /// <summary>The page never sends anything on its event stream; a read that ends means the page is gone.</summary>
    static async Task WatchForClose(Stream s, CancellationTokenSource gone)
    {
        try
        {
            var buf = new byte[256];
            while (await s.ReadAsync(buf, gone.Token) > 0) { }
        }
        catch { }
        finally
        {
            try { gone.Cancel(); } catch (ObjectDisposedException) { }
        }
    }

    // ---------------------------------------------------------------- responses

    static void CommonHeaders(StringBuilder h)
    {
        h.Append("Cache-Control: no-store\r\n");
        h.Append("X-Content-Type-Options: nosniff\r\n");
        h.Append("Content-Security-Policy: ").Append(Csp).Append("\r\n");
        h.Append("Referrer-Policy: no-referrer\r\n");
        h.Append("X-Frame-Options: DENY\r\n");
        h.Append("Cross-Origin-Resource-Policy: same-origin\r\n");
    }

    static Task Reply(Stream s, App.Reply r) => Send(s, r.Status, "application/json; charset=utf-8", r.Body);

    static Task SendText(Stream s, int status, string text) => Send(s, status, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));

    static async Task Send(Stream s, int status, string contentType, byte[] body)
    {
        var h = new StringBuilder();
        h.Append("HTTP/1.1 ").Append(status).Append(' ').Append(Reason(status)).Append("\r\n");
        h.Append("Content-Type: ").Append(contentType).Append("\r\n");
        h.Append("Content-Length: ").Append(body.Length).Append("\r\n");
        CommonHeaders(h);
        h.Append("Connection: close\r\n\r\n");
        var head = Encoding.ASCII.GetBytes(h.ToString());
        var all = new byte[head.Length + body.Length];
        head.CopyTo(all, 0);
        body.CopyTo(all, head.Length);
        await s.WriteAsync(all);
        await s.FlushAsync();
    }

    static string Reason(int status) => status switch
    {
        200 => "OK",
        400 => "Bad Request",
        403 => "Forbidden",
        404 => "Not Found",
        405 => "Method Not Allowed",
        409 => "Conflict",
        411 => "Length Required",
        413 => "Payload Too Large",
        431 => "Request Header Fields Too Large",
        500 => "Internal Server Error",
        _ => "Status",
    };
}
