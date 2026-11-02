using System.Net;
using System.Net.Sockets;
using System.Text;

namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

public sealed class LoopbackOAuthCallbackListener : IAsyncDisposable
{
    private readonly HttpListener _listener = new();
    private readonly TaskCompletionSource<OAuthCallback> _completion = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly CancellationTokenSource _stop = new();
    private readonly Task _acceptTask;

    private LoopbackOAuthCallbackListener(int port)
    {
        CallbackUri = new Uri($"http://127.0.0.1:{port}/auth/callback");
        _listener.Prefixes.Add($"http://127.0.0.1:{port}/");
        _listener.Start();
        _acceptTask = AcceptAsync();
    }

    public Uri CallbackUri { get; }

    public static LoopbackOAuthCallbackListener Start()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        var port = ((IPEndPoint)probe.LocalEndpoint).Port;
        probe.Stop();
        return new LoopbackOAuthCallbackListener(port);
    }

    internal Task<OAuthCallback> WaitAsync(CancellationToken cancellationToken) =>
        _completion.Task.WaitAsync(cancellationToken);

    private async Task AcceptAsync()
    {
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                var context = await _listener.GetContextAsync().WaitAsync(_stop.Token).ConfigureAwait(false);
                var request = context.Request;
                var remote = request.RemoteEndPoint?.Address;
                if (remote is null || !IPAddress.IsLoopback(remote) || request.HttpMethod != "GET" ||
                    !string.Equals(request.Url?.AbsolutePath, "/auth/callback", StringComparison.Ordinal))
                {
                    context.Response.StatusCode = (int)HttpStatusCode.NotFound;
                    context.Response.Close();
                    continue;
                }

                var query = System.Web.HttpUtility.ParseQueryString(request.Url!.Query);
                await WriteResponseAsync(context.Response).ConfigureAwait(false);
                _completion.TrySetResult(new OAuthCallback(
                    query["code"], query["state"], query["client_id"], query["error"], query["error_description"]));
                return;
            }
        }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested)
        {
        }
        catch (HttpListenerException) when (_stop.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            _completion.TrySetException(new InvalidOperationException("OAuth callback listener failed."));
        }
    }

    private static async Task WriteResponseAsync(HttpListenerResponse response)
    {
        const string page = "<!doctype html><meta charset=utf-8><title>Sign-in complete</title><p>Sign-in returned to the app. You may close this tab.</p>";
        var bytes = Encoding.UTF8.GetBytes(page);
        response.ContentType = "text/html; charset=utf-8";
        response.ContentLength64 = bytes.Length;
        await response.OutputStream.WriteAsync(bytes).ConfigureAwait(false);
        response.Close();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        _listener.Close();
        try { await _acceptTask.ConfigureAwait(false); } catch (Exception) { }
        _stop.Dispose();
    }
}
