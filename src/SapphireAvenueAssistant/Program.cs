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
builder.Services.AddHostedService<DiscordInboundPublisher>();

var app = builder.Build();
await app.Services.GetRequiredService<RelayStore>().InitializeAsync();

app.MapGet(
    "/",
    () => Results.Ok(new
    {
        service = "Sapphire Avenue Assistant",
        apiVersion = 1
    }));
app.MapGet(
    "/healthz",
    () => Results.Ok(new
    {
        status = sapphireOptions.Discord.CanVerifyInteractions &&
            sapphireOptions.Discord.CanPublish &&
            sapphireOptions.Relay.NodeTokens.Count > 0
                ? "ready"
                : "configuration-required",
        discordInteractions = sapphireOptions.Discord.CanVerifyInteractions,
        discordPublication = sapphireOptions.Discord.CanPublish,
        relayNodes = sapphireOptions.Relay.NodeTokens.Count
    }));
app.MapDiscordInteractions();
app.MapRelayEndpoints();

app.Run();

public partial class Program;
