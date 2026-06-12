using System.Security.Claims;
using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Interfaces;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;

namespace ECHAT.Server.App.Controllers;

[Authorize]
[ApiController]
[Route("api/[controller]")]
public class UsersController : ControllerBase
{
    private readonly IUserSearchService _search;
    private readonly ILogger<UsersController> _logger;

    public UsersController(IUserSearchService search, ILogger<UsersController> logger)
    {
        _search = search;
        _logger = logger;
    }

    [HttpGet("search")]
    public async Task<ActionResult<List<UserDto>>> Search([FromQuery] string q)
    {
        var currentUserId = Guid.Parse(User.FindFirst(ClaimTypes.NameIdentifier)!.Value);

        var users = await _search.SearchAsync(currentUserId, q);

        _logger.LogInformation(
            "User search executed: requesterId={RequesterId} queryLength={QueryLength} resultCount={ResultCount}",
            currentUserId, q?.Length ?? 0, users.Count);

        return Ok(users);
    }
}
