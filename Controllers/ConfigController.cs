using Microsoft.AspNetCore.Mvc;
using TuringTown.Services;

namespace TuringTown.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class ConfigController(SeasonProvider seasons) : ControllerBase
{
    private readonly record struct ConfigResp(string Season);

    /// <summary>What the client needs before it loads any art: the season ("default" or "christmas").</summary>
    [HttpGet]
    public IActionResult Get() => Ok(new ConfigResp(seasons.Current.ToString().ToLowerInvariant()));
}
