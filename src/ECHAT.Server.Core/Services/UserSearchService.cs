using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Interfaces;

namespace ECHAT.Server.Core.Services;

/// <inheritdoc cref="IUserSearchService"/>
public class UserSearchService : IUserSearchService
{
    private readonly IUserStore _users;

    public UserSearchService(IUserStore users)
    {
        _users = users;
    }

    public async Task<List<UserDto>> SearchAsync(Guid currentUserId, string query)
    {
        if (string.IsNullOrWhiteSpace(query) || query.Length < 2)
            return new List<UserDto>();

        var candidates = await _users.SearchUsersAsync(currentUserId, query);

        return candidates
            .Where(u => u.IsActive
                        && u.Id != currentUserId
                        && (u.Email.Contains(query) || u.DisplayName.Contains(query)))
            .Take(20)
            .Select(u => new UserDto
            {
                Id = u.Id,
                Email = u.Email,
                DisplayName = u.DisplayName,
                PictureUrl = u.PictureUrl
            })
            .ToList();
    }
}
