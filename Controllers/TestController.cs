using Microsoft.AspNetCore.Mvc;

namespace TuringTown.Controllers;

[ApiController]
[Route("api/[controller]")]
public sealed class TestController : ControllerBase
{
    public class MessageWrapper
    {
        public required string Message { get; init; }
    }
    
    [HttpGet]
    public IActionResult TestGet()
    {
        return Ok(new MessageWrapper()
        {
            Message = "Hello World from wrapper"
        });
    }
}