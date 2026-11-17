using TuringTown.Services;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddOpenApi();
builder.Services.AddControllers();

builder.Services.AddSingleton<RoomManager>();

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

app.Run();