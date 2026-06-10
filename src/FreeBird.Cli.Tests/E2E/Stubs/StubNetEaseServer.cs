using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace FreeBird.Cli.Tests.E2E.Stubs;

/// <summary>
/// In-process HTTP stub that impersonates the NetEase /api/song/detail endpoint.
/// Binds an <see cref="HttpListener"/> to a loopback port (chosen by the OS via a
/// short-lived <see cref="TcpListener"/> probe — <c>HttpListener</c> itself does not
/// accept port 0) and replies to every request with the currently configured
/// <see cref="ResponseJson"/>, <see cref="StatusCode"/>, and <see cref="ResponseDelay"/>.
///
/// Designed for E2E tests of the v3 metadata pipeline: callers set
/// <c>FB_NETEASE_BASEURL=&lt;BaseUrl&gt;</c> before driving <c>ScanRunner.RunAsync</c>,
/// then assert on <see cref="RequestCount"/> / <see cref="ReceivedPaths"/> after the run.
///
/// Lifecycle: the listener loop starts in the ctor; <see cref="Dispose"/> stops the
/// listener and waits briefly for the loop task to drain.
/// </summary>
public sealed class StubNetEaseServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly CancellationTokenSource _stopCts = new();
    private readonly Task _loopTask;
    private int _requestCount;
    private readonly List<string> _receivedPaths = new();
    private readonly object _pathsLock = new();

    /// <summary>Base URL (scheme + host + port, no trailing slash) the stub listens on.</summary>
    public Uri BaseUrl { get; }

    /// <summary>JSON body returned for every request. Mutate before driving the CLI.</summary>
    public string ResponseJson { get; set; } = """{"songs":[],"code":200}""";

    /// <summary>HTTP status code returned for every request.</summary>
    public int StatusCode { get; set; } = 200;

    /// <summary>Per-request artificial delay (applied before the response is written).</summary>
    public TimeSpan ResponseDelay { get; set; } = TimeSpan.Zero;

    /// <summary>Number of requests the listener has fully accepted (incremented on each context).</summary>
    public int RequestCount => Volatile.Read(ref _requestCount);

    /// <summary>Path + query string for each received request, in order.</summary>
    public IReadOnlyList<string> ReceivedPaths
    {
        get
        {
            lock (_pathsLock) { return _receivedPaths.ToArray(); }
        }
    }

    public StubNetEaseServer()
    {
        var port = ReserveLoopbackPort();
        BaseUrl = new Uri($"http://127.0.0.1:{port}");
        _listener = new HttpListener();
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _loopTask = Task.Run(LoopAsync);
    }

    private static int ReserveLoopbackPort()
    {
        // HttpListener does not accept port 0. Bind a TcpListener to port 0,
        // capture the OS-assigned port, then immediately release it. There is a
        // small TOCTOU window before HttpListener binds — in practice the OS
        // does not reuse a just-freed ephemeral port that quickly under test load.
        var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        try
        {
            return ((IPEndPoint)probe.LocalEndpoint).Port;
        }
        finally
        {
            probe.Stop();
        }
    }

    private async Task LoopAsync()
    {
        while (!_stopCts.IsCancellationRequested)
        {
            HttpListenerContext ctx;
            try
            {
                ctx = await _listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                // Listener was stopped — exit cleanly.
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }

            try
            {
                Interlocked.Increment(ref _requestCount);
                lock (_pathsLock)
                {
                    _receivedPaths.Add(ctx.Request.Url?.PathAndQuery ?? string.Empty);
                }

                if (ResponseDelay > TimeSpan.Zero)
                {
                    try
                    {
                        await Task.Delay(ResponseDelay, _stopCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        // Dispose during delay: abort the response and exit.
                        try { ctx.Response.Abort(); } catch { /* best-effort */ }
                        break;
                    }
                }

                ctx.Response.StatusCode = StatusCode;
                ctx.Response.ContentType = "application/json";
                var payload = Encoding.UTF8.GetBytes(ResponseJson);
                ctx.Response.ContentLength64 = payload.Length;
                await ctx.Response.OutputStream.WriteAsync(payload).ConfigureAwait(false);
            }
            catch (Exception)
            {
                // Best-effort: a client may have disconnected. Don't crash the loop.
            }
            finally
            {
                try { ctx.Response.Close(); } catch { /* best-effort */ }
            }
        }
    }

    public void Dispose()
    {
        try { _stopCts.Cancel(); } catch { }
        try { _listener.Stop(); } catch { }
        try { _listener.Close(); } catch { }
        try { _loopTask.Wait(TimeSpan.FromSeconds(2)); } catch { /* best-effort */ }
        _stopCts.Dispose();
    }
}
