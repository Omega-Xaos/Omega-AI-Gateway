using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Omega.AI.Gateway.Contracts;
using Omega.AI.Gateway.Gateway;
using Omega.AI.Gateway.Providers.ChatGptOAuth;
using Omega.AI.Gateway.Providers.Anthropic;
using Omega.AI.Gateway.Providers.GoogleGemini;
using Omega.AI.Gateway.Providers.OpenAiCompatible;

namespace Omega.AI.Gateway.DependencyInjection;

public sealed class AiGatewayBuilder(IServiceCollection services)
{
    public IServiceCollection Services { get; } = services ?? throw new ArgumentNullException(nameof(services));

    public AiGatewayBuilder AddProvider<TProvider>() where TProvider : class, IAiProvider
    {
        Services.AddScoped<IAiProvider, TProvider>();
        return this;
    }

    public AiGatewayBuilder AddOpenAiCompatible()
    {
        Services.AddHttpClient<OpenAiCompatibleProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        Services.AddScoped<IAiProvider>(provider => provider.GetRequiredService<OpenAiCompatibleProvider>());
        return this;
    }

    public AiGatewayBuilder AddAnthropicMessages()
    {
        Services.AddHttpClient<AnthropicMessagesProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        Services.AddScoped<IAiProvider>(provider => provider.GetRequiredService<AnthropicMessagesProvider>());
        return this;
    }

    public AiGatewayBuilder AddGoogleGemini()
    {
        Services.AddHttpClient<GoogleGeminiProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        Services.AddScoped<IAiProvider>(provider => provider.GetRequiredService<GoogleGeminiProvider>());
        return this;
    }

    public AiGatewayBuilder AddChatGptOAuth(ChatGptOAuthOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        options.Validate();
        Services.AddSingleton(options);
        Services.TryAddSingleton<IAuthorizationBrowser, SystemAuthorizationBrowser>();
        Services.AddHttpClient<ChatGptOAuthTokenClient>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        Services.AddHttpClient<ChatGptIdTokenValidator>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        Services.AddHttpClient<ChatGptOAuthProvider>()
            .ConfigurePrimaryHttpMessageHandler(() => new SocketsHttpHandler { AllowAutoRedirect = false });
        Services.AddSingleton<ChatGptOAuthAuthorizationService>();
        Services.AddScoped<IAiProvider>(provider => provider.GetRequiredService<ChatGptOAuthProvider>());
        return this;
    }
}

public static class AiGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddOmegaAiGateway(
        this IServiceCollection services,
        Action<AiGatewayBuilder>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);
        var builder = new AiGatewayBuilder(services);
        configure?.Invoke(builder);
        services.AddScoped<IAiGateway, AiGateway>();
        return services;
    }
}


