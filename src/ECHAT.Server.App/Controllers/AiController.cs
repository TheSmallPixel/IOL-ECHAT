using ECHAT.Server.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECHAT.Server.App.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class AiController : ControllerBase
{
    private readonly IConfiguration _config;
    private readonly IHttpClientFactory _httpFactory;
    private readonly IAiReplyService _ai;
    private readonly ILogger<AiController> _logger;

    public AiController(IConfiguration config, IHttpClientFactory httpFactory, IAiReplyService ai, ILogger<AiController> logger)
    {
        _config = config;
        _httpFactory = httpFactory;
        _ai = ai;
        _logger = logger;
    }

    [HttpPost("reply")]
    public async Task<IActionResult> Reply([FromBody] AiReplyRequest request)
    {
        var apiKey = _config["OpenAI:ApiKey"];
        if (string.IsNullOrEmpty(apiKey))
            return BadRequest(new { error = "OpenAI API key not configured." });

        var options = new AiReplyOptions
        {
            ApiKey = apiKey,
            Model = _config["OpenAI:Model"] ?? "gpt-4o-mini"
        };

        try
        {
            var client = _httpFactory.CreateClient();
            var reply = await _ai.GetAiReplyAsync(client, options, request.Prompt);
            return Ok(new { reply });
        }
        catch (AiUpstreamException ex)
        {
            // Logga upstream status + corpo lato server; al client solo un errore generico,
            // mai il corpo grezzo di OpenAI (può contenere dettagli interni o sull'API key).
            _logger.LogError(ex, "AI upstream returned {StatusCode}: {Body}", (int)ex.StatusCode, ex.Body);
            return StatusCode(StatusCodes.Status502BadGateway, new { error = "AI reply failed." });
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "AI reply generation failed.");
            return StatusCode(StatusCodes.Status500InternalServerError, new { error = "AI reply failed." });
        }
    }
}

public class AiReplyRequest
{
    public string Prompt { get; set; } = "";
}
