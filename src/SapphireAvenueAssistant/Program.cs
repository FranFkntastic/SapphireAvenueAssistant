using System.Text.Json;
using System.Text.Json.Serialization;
using SapphireAvenueAssistant.Configuration;
using SapphireAvenueAssistant.Discord;
using SapphireAvenueAssistant.Relay;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(server => server.Limits.MaxRequestBodySize = 64 * 1024);
builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase)));

var sapphireOptions = builder.Configuration
    .GetSection("SapphireAvenue")
    .Get<SapphireAvenueOptions>() ?? new SapphireAvenueOptions();
builder.Services.AddSingleton(sapphireOptions);
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton<RelayStore>();
builder.Services.AddSingleton<RelayAuthenticator>();
builder.Services.AddSingleton<DiscordRequestVerifier>();
builder.Services.AddHttpClient<IDiscordApiClient, DiscordApiClient>(client =>
{
    client.BaseAddress = new Uri("https://discord.com/api/v10/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddHttpClient("DiscordCommandRegistration", client =>
{
    client.BaseAddress = new Uri("https://discord.com/api/v10/");
    client.Timeout = TimeSpan.FromSeconds(10);
});
builder.Services.AddSingleton(services => new DiscordCommandRegistrationService(
    services.GetRequiredService<IHttpClientFactory>().CreateClient("DiscordCommandRegistration"),
    services.GetRequiredService<SapphireAvenueOptions>(),
    services.GetRequiredService<TimeProvider>(),
    services.GetRequiredService<ILogger<DiscordCommandRegistrationService>>()));
builder.Services.AddHostedService(services => services.GetRequiredService<DiscordCommandRegistrationService>());
builder.Services.AddHostedService<DiscordInboundPublisher>();

var app = builder.Build();
await app.Services.GetRequiredService<RelayStore>().InitializeAsync();

app.MapGet(
    "/",
    () => Results.Ok(new
    {
        service = "Sapphire Avenue Discord Bridge",
        apiVersion = 1
    }));
app.MapGet(
    "/healthz",
    async Task<IResult> (
        RelayStore store,
        DiscordCommandRegistrationService commandRegistration,
        CancellationToken cancellationToken) =>
    {
        var relayConfiguration = await store.GetCommunityConfigurationAsync(
            sapphireOptions.Discord.GuildId,
            cancellationToken);
        var relayNodes = await store.CountActiveNodesAsync(cancellationToken);
        return Results.Ok(new
        {
            status = sapphireOptions.Discord.CanVerifyInteractions &&
            sapphireOptions.Discord.CanPublish &&
            relayConfiguration is not null &&
            relayNodes > 0 &&
            commandRegistration.Status == "ready"
                ? "ready"
                : "configuration-required",
            discordInteractions = sapphireOptions.Discord.CanVerifyInteractions,
            discordPublication = sapphireOptions.Discord.CanPublish,
            discordCommands = commandRegistration.Status,
            relayConfigured = relayConfiguration is not null,
            relayNodes
        });
    });
app.MapDiscordInteractions();
app.MapRelayEndpoints();

app.Run();

public partial class Program;
