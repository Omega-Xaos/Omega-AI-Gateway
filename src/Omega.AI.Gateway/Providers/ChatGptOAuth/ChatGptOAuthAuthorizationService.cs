using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using Omega.AI.Gateway.Errors;

namespace Omega.AI.Gateway.Providers.ChatGptOAuth;

public sealed class ChatGptOAuthAuthorizationService : IAsyncDisposable
{
    private const string DynamicClientId = "dynamic_agent_client";
    private const string RequiredPlanScope = "chatgpt.tokens.use.direct";
    private readonly ChatGptOAuthOptions _options;
    private readonly IChatGptOAuthCredentialStore _credentials;
    private readonly IAuthorizationBrowser _browser;
    private readonly ChatGptOAuthTokenClient _tokens;
    private readonly ChatGptIdTokenValidator _idTokens;
    private readonly ConcurrentDictionary<string, Attempt> _attempts = new(StringComparer.Ordinal);

    public ChatGptOAuthAuthorizationService(
        ChatGptOAuthOptions options,
        IChatGptOAuthCredentialStore credentials,
        IAuthorizationBrowser browser,
        ChatGptOAuthTokenClient tokens,
        ChatGptIdTokenValidator idTokens)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _credentials = credentials ?? throw new ArgumentNullException(nameof(credentials));
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _tokens = tokens ?? throw new ArgumentNullException(nameof(tokens));
        _idTokens = idTokens ?? throw new ArgumentNullException(nameof(idTokens));
        _options.Validate();
    }

    public async Task<ChatGptOAuthSignIn> BeginSignInAsync(
        string profileId, string hostId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(profileId) || string.IsNullOrWhiteSpace(hostId))
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "Profile ID and stable host ID are required.");
        }

        var existing = await _credentials.GetAsync(profileId, cancellationToken).ConfigureAwait(false);
        var listener = LoopbackOAuthCallbackListener.Start();
        var state = RandomToken(32);
        var nonce = RandomToken(32);
        var verifier = RandomToken(48);
        var clientId = existing?.ClientId ?? DynamicClientId;
        var expiresAt = DateTimeOffset.UtcNow.Add(_options.SignInTimeout);
        var lifetime = new CancellationTokenSource();
        var attempt = new Attempt(profileId, hostId, listener, state, nonce, verifier, clientId,
            existing?.Subject, BuildAuthorizationUri(listener.CallbackUri, hostId, state, nonce, verifier, clientId, existing is null), expiresAt, lifetime);
        if (!_attempts.TryAdd(profileId, attempt))
        {
            lifetime.Dispose();
            await listener.DisposeAsync().ConfigureAwait(false);
            throw new AiGatewayException(AiErrorCode.Configuration, "A sign-in attempt is already active for this profile.");
        }

        try
        {
            _ = ExpireAttemptAsync(profileId, attempt);
            await _browser.OpenAsync(attempt.AuthorizationUri, cancellationToken).ConfigureAwait(false);
            if (!_attempts.ContainsKey(profileId))
            {
                throw new AiGatewayException(AiErrorCode.Timeout, "ChatGPT sign-in attempt expired before browser launch completed.");
            }

            return new ChatGptOAuthSignIn(profileId, attempt.AuthorizationUri, listener.CallbackUri, expiresAt);
        }
        catch
        {
            RemoveAttempt(profileId, attempt);
            await attempt.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public async Task<ChatGptOAuthCredential> WaitForSignInAsync(string profileId, CancellationToken cancellationToken = default)
    {
        if (!_attempts.TryGetValue(profileId, out var attempt) || Interlocked.CompareExchange(ref attempt.WaitClaimed, 1, 0) != 0)
        {
            throw new AiGatewayException(AiErrorCode.Configuration, "No ChatGPT sign-in attempt is active for this profile.");
        }

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, attempt.LifetimeToken);
            var remaining = attempt.ExpiresAtUtc - DateTimeOffset.UtcNow;
            if (remaining <= TimeSpan.Zero)
            {
                throw new AiGatewayException(AiErrorCode.Timeout, "ChatGPT sign-in attempt expired.");
            }

            timeout.CancelAfter(remaining);
            OAuthCallback callback;
            try
            {
                callback = await attempt.Listener.WaitAsync(timeout.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                throw new AiGatewayException(AiErrorCode.Timeout, "ChatGPT sign-in attempt expired.");
            }

            if (!FixedTimeEquals(callback.State, attempt.State))
            {
                throw new AiGatewayException(AiErrorCode.Authorization, "ChatGPT sign-in callback state did not match.");
            }

            if (!string.IsNullOrEmpty(callback.Error))
            {
                throw new AiGatewayException(AiErrorCode.Authorization,
                    callback.Error == "access_denied" ? "ChatGPT plan authorization was declined." : "ChatGPT authorization did not complete.");
            }

            var clientId = attempt.ClientId == DynamicClientId ? callback.ClientId : attempt.ClientId;
            if (string.IsNullOrWhiteSpace(clientId) || clientId == DynamicClientId ||
                callback.ClientId is not null && !string.Equals(callback.ClientId, clientId, StringComparison.Ordinal))
            {
                throw new AiGatewayException(AiErrorCode.Authorization, "ChatGPT did not return the expected registered client ID.");
            }

            if (string.IsNullOrWhiteSpace(callback.Code))
            {
                throw new AiGatewayException(AiErrorCode.Authorization, "ChatGPT authorization code is missing.");
            }

            var tokenSet = await _tokens.ExchangeCodeAsync(callback.Code, clientId, attempt.Verifier,
                attempt.Listener.CallbackUri, timeout.Token).ConfigureAwait(false);
            if (tokenSet.Scopes is null || !tokenSet.Scopes.Contains(RequiredPlanScope))
            {
                throw new AiGatewayException(AiErrorCode.Authorization, "ChatGPT account did not grant plan inference permission.");
            }

            var identity = await _idTokens.ValidateAsync(tokenSet.IdToken!, clientId, attempt.Nonce, timeout.Token).ConfigureAwait(false);
            if (attempt.ExpectedSubject is not null && !string.Equals(attempt.ExpectedSubject, identity.Subject, StringComparison.Ordinal))
            {
                throw new AiGatewayException(AiErrorCode.Authorization, "The signed-in ChatGPT account differs from the selected account.");
            }

            var credential = new ChatGptOAuthCredential(clientId, attempt.HostId, identity.Subject, identity.Email,
                tokenSet.AccessToken, tokenSet.RefreshToken!, tokenSet.IdToken!,
                DateTimeOffset.UtcNow.AddSeconds(tokenSet.ExpiresIn), tokenSet.Scopes);
            await using var credentialLock = await _credentials.AcquireRefreshLockAsync(profileId, CancellationToken.None).ConfigureAwait(false);
            var latest = await _credentials.GetAsync(profileId, CancellationToken.None).ConfigureAwait(false);
            if (attempt.ExpectedSubject is null && latest is not null ||
                attempt.ExpectedSubject is not null && (latest is null || latest.ClientId != attempt.ClientId || latest.Subject != attempt.ExpectedSubject))
            {
                throw new AiGatewayException(AiErrorCode.Authorization, "ChatGPT account registration changed during sign-in.");
            }

            await _credentials.SaveAsync(profileId, credential, CancellationToken.None).ConfigureAwait(false);
            return credential;
        }
        finally
        {
            RemoveAttempt(profileId, attempt);
            await attempt.DisposeAsync().ConfigureAwait(false);
        }
    }

    private async Task ExpireAttemptAsync(string profileId, Attempt attempt)
    {
        try
        {
            await Task.Delay(_options.SignInTimeout, attempt.LifetimeToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (attempt.Lifetime.IsCancellationRequested)
        {
            return;
        }

        if (RemoveAttempt(profileId, attempt))
        {
            await attempt.DisposeAsync().ConfigureAwait(false);
        }
    }

    private bool RemoveAttempt(string profileId, Attempt attempt) =>
        ((ICollection<KeyValuePair<string, Attempt>>)_attempts).Remove(new KeyValuePair<string, Attempt>(profileId, attempt));

    public async ValueTask DisposeAsync()
    {
        foreach (var pair in _attempts.ToArray())
        {
            if (RemoveAttempt(pair.Key, pair.Value))
            {
                await pair.Value.DisposeAsync().ConfigureAwait(false);
            }
        }
    }

    private Uri BuildAuthorizationUri(Uri redirectUri, string hostId, string state, string nonce, string verifier, string clientId, bool firstRegistration)
    {
        var parameters = System.Web.HttpUtility.ParseQueryString(string.Empty);
        parameters["client_id"] = clientId;
        if (firstRegistration)
        {
            parameters["agent_name_hint"] = _options.ApplicationName;
        }

        parameters["ext_agent_host_id"] = hostId;
        parameters["response_type"] = "code";
        parameters["redirect_uri"] = redirectUri.AbsoluteUri;
        parameters["scope"] = "openid profile email offline_access resource.invoke chatgpt.tokens.use.direct";
        parameters["resource"] = _options.Resource;
        parameters["state"] = state;
        parameters["nonce"] = nonce;
        parameters["code_challenge_method"] = "S256";
        parameters["code_challenge"] = PkceChallenge(verifier);
        var builder = new UriBuilder(_options.AuthorizationEndpoint) { Query = parameters.ToString() };
        return builder.Uri;
    }

    internal static string PkceChallenge(string verifier) =>
        Convert.ToBase64String(SHA256.HashData(Encoding.ASCII.GetBytes(verifier)))
            .TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string RandomToken(int bytes) =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(bytes)).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static bool FixedTimeEquals(string? left, string right)
    {
        if (left is null)
        {
            return false;
        }

        var leftBytes = Encoding.UTF8.GetBytes(left);
        var rightBytes = Encoding.UTF8.GetBytes(right);
        return leftBytes.Length == rightBytes.Length && CryptographicOperations.FixedTimeEquals(leftBytes, rightBytes);
    }

    private sealed class Attempt(
        string profileId, string hostId, LoopbackOAuthCallbackListener listener,
        string state, string nonce, string verifier, string clientId, string? expectedSubject,
        Uri authorizationUri, DateTimeOffset expiresAtUtc, CancellationTokenSource lifetime)
    {
        private int _disposed;
        public string ProfileId { get; } = profileId;
        public string HostId { get; } = hostId;
        public LoopbackOAuthCallbackListener Listener { get; } = listener;
        public string State { get; } = state;
        public string Nonce { get; } = nonce;
        public string Verifier { get; } = verifier;
        public string ClientId { get; } = clientId;
        public string? ExpectedSubject { get; } = expectedSubject;
        public Uri AuthorizationUri { get; } = authorizationUri;
        public DateTimeOffset ExpiresAtUtc { get; } = expiresAtUtc;
        public CancellationTokenSource Lifetime { get; } = lifetime;
        public CancellationToken LifetimeToken { get; } = lifetime.Token;
        public int WaitClaimed;

        public async ValueTask DisposeAsync()
        {
            if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
            Lifetime.Cancel();
            await Listener.DisposeAsync().ConfigureAwait(false);
            Lifetime.Dispose();
        }
    }
}
