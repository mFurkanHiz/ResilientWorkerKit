using System.Net;
using System.Text;
using System.Text.Json;

namespace ResilientWorkerKit.IntegrationTests.Infrastructure;

/// <summary>
/// A real in-process HTTP server (HttpListener) whose responses are scripted per request path.
/// Real sockets, real status codes, real headers — no HttpClient mocking.
/// </summary>
internal sealed class FakeApiServer : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Func<HttpListenerRequest, FakeApiResponse> _respond;
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _loop;
    private int _requestCount;

    public FakeApiServer(Func<HttpListenerRequest, FakeApiResponse> respond)
    {
        _respond = respond;
        var port = GetFreePort();
        BaseAddress = new Uri($"http://127.0.0.1:{port}/");
        _listener.Prefixes.Add(BaseAddress.ToString());
        _listener.Start();
        _loop = Task.Run(AcceptLoopAsync);
    }

    public Uri BaseAddress { get; }

    public int RequestCount => Volatile.Read(ref _requestCount);

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch (Exception) when (_cts.IsCancellationRequested)
            {
                return;
            }
            catch (HttpListenerException)
            {
                return;
            }

            Interlocked.Increment(ref _requestCount);

            try
            {
                var response = _respond(context.Request);
                context.Response.StatusCode = response.StatusCode;
                foreach (var (name, value) in response.Headers)
                {
                    context.Response.Headers[name] = value;
                }

                if (response.Body is { } body)
                {
                    context.Response.ContentType = response.ContentType;
                    var bytes = Encoding.UTF8.GetBytes(body);
                    context.Response.ContentLength64 = bytes.Length;
                    await context.Response.OutputStream.WriteAsync(bytes);
                }
            }
            catch (Exception)
            {
                context.Response.StatusCode = 500;
            }
            finally
            {
                context.Response.Close();
            }
        }
    }

    private static int GetFreePort()
    {
        using var socket = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
        socket.Start();
        var port = ((IPEndPoint)socket.LocalEndpoint).Port;
        socket.Stop();
        return port;
    }

    public async ValueTask DisposeAsync()
    {
        await _cts.CancelAsync();
        _listener.Stop();
        _listener.Close();
        try
        {
            await _loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        catch (TimeoutException)
        {
        }

        _cts.Dispose();
    }
}

/// <summary>A scripted response of <see cref="FakeApiServer"/>.</summary>
internal sealed record FakeApiResponse(
    int StatusCode,
    string? Body = null,
    string ContentType = "application/json",
    IReadOnlyList<KeyValuePair<string, string>>? HeaderList = null)
{
    public IReadOnlyList<KeyValuePair<string, string>> Headers => HeaderList ?? [];

    public static FakeApiResponse Json(object value)
        => new(200, JsonSerializer.Serialize(value));

    public static FakeApiResponse Status(int statusCode, params KeyValuePair<string, string>[] headers)
        => new(statusCode, HeaderList: headers);
}
