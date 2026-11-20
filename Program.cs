using TuringTown.Models;
using TuringTown.Services;
using TuringTown.Services.Ai;
using TuringTown.Services.Games;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers()
    .AddJsonOptions(o => o.JsonSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));
builder.Services.AddHealthChecks();
builder.Services.AddSignalR()
    .AddJsonProtocol(o => o.PayloadSerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter()));

builder.Services.AddOptions<GameOptions>()
    .Bind(builder.Configuration.GetSection(GameOptions.SectionName))
    .ValidateDataAnnotations()
    .ValidateOnStart();

builder.Services.AddSingleton<RoomManager>();

// Turing Town: the game engine, its timings, and the AI behind the bots (the scripted agent works offline).
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.Configure<TuringTownOptions>(builder.Configuration.GetSection(TuringTownOptions.SectionName));
builder.Services.AddSingleton<IBotAgent, ScriptedBotAgent>();
builder.Services.AddSingleton<IContentFilter, AllowAllContentFilter>();
builder.Services.AddSingleton<IRoomBroadcaster, SignalRRoomBroadcaster>();
builder.Services.AddSingleton<GameCoordinator>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

// This middleware ensures that ONLY GET method requests can be cached by the browser.
// This absolutely 100% prevents any other http methods from being stored and recycled from a browser cache.
// I'm not sure if this is necessary, but I think it's a good catch-all to prevent us scratching our heads
// in frustration down the line.
app.Use(async (context, next) =>
{
    if (context.Request.Method != "GET")
    {
        context.Response.OnStarting(() =>
        {
            context.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate, max-age=0";
            context.Response.Headers.Pragma = "no-cache";
            context.Response.Headers.Expires = "0";
            return Task.CompletedTask;
        });
    }

    await next();
});

app.UseDefaultFiles();
app.UseStaticFiles();

app.UseRouting();

//app.UseHttpsRedirection();

app.MapControllers();
app.MapHub<TuringTown.Hubs.GameHub>("/hubs/game");

// Liveness check used by the CI/deploy smoke tests. Available in all environments.
app.MapHealthChecks("/api/test");

app.Run();

// Lets the integration tests start the whole app (WebApplicationFactory<Program>).
public partial class Program;
