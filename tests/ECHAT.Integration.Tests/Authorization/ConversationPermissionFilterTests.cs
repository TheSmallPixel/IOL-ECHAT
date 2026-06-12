using System.Security.Claims;
using ECHAT.Models.Enums;
using ECHAT.Server.App.Authorization;
using ECHAT.Server.Core.Interfaces;
using FluentAssertions;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Moq;

namespace ECHAT.Integration.Tests.Authorization;

/// <summary>
/// Verifica la colla MVC di <see cref="RequireConversationPermissionAttribute"/>: estrazione di
/// userId (claim) e conversationId (route), delega corretta a IPolicyEnforcer, e short-circuit
/// (Forbid/Unauthorized) PRIMA che l'action venga eseguita. La logica ruolopermesso è testata
/// a parte in PolicyEnforcerTests.
/// </summary>
public class ConversationPermissionFilterTests
{
    private readonly Mock<IPolicyEnforcer> _policy = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _conversationId = Guid.NewGuid();

    private IAsyncAuthorizationFilter CreateFilter(Permission permission)
    {
        var attr = new RequireConversationPermissionAttribute(permission);
        var services = new ServiceCollection();
        services.AddSingleton(_policy.Object);
        return (IAsyncAuthorizationFilter)attr.CreateInstance(services.BuildServiceProvider());
    }

    private static AuthorizationFilterContext BuildContext(ClaimsPrincipal user, RouteValueDictionary route)
    {
        var httpContext = new DefaultHttpContext { User = user };
        var actionContext = new ActionContext(httpContext, new RouteData(route), new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
    }

    private ClaimsPrincipal AuthedUser() => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, _userId.ToString()) }, "test"));

    private RouteValueDictionary WithConversation() =>
        new() { ["conversationId"] = _conversationId.ToString() };

    [Fact]
    public async Task Allows_WhenPolicyGrants()
    {
        _policy.Setup(p => p.AuthorizeAsync(_userId, _conversationId, Permission.AddMember)).ReturnsAsync(true);
        var ctx = BuildContext(AuthedUser(), WithConversation());

        await CreateFilter(Permission.AddMember).OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull(); // nessuno short-circuit: l'action può procedere
    }

    [Fact]
    public async Task Forbids_WhenPolicyDenies()
    {
        _policy.Setup(p => p.AuthorizeAsync(_userId, _conversationId, Permission.AddMember)).ReturnsAsync(false);
        var ctx = BuildContext(AuthedUser(), WithConversation());

        await CreateFilter(Permission.AddMember).OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Unauthorized_WhenNoUserIdClaim()
    {
        var anonymous = new ClaimsPrincipal(new ClaimsIdentity());
        var ctx = BuildContext(anonymous, WithConversation());

        await CreateFilter(Permission.Read).OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<UnauthorizedResult>();
    }

    [Fact]
    public async Task Forbids_WhenConversationIdMissingFromRoute()
    {
        var ctx = BuildContext(AuthedUser(), new RouteValueDictionary());

        await CreateFilter(Permission.Read).OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task PassesClaimUserIdAndRouteConversationId_ToPolicy()
    {
        _policy.Setup(p => p.AuthorizeAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Permission>()))
            .ReturnsAsync(true);
        var ctx = BuildContext(AuthedUser(), WithConversation());

        await CreateFilter(Permission.RemoveMember).OnAuthorizationAsync(ctx);

        _policy.Verify(p => p.AuthorizeAsync(_userId, _conversationId, Permission.RemoveMember), Times.Once);
    }
}

/// <summary>
/// Verifica la colla MVC di <see cref="RequirePlatformAdminAttribute"/>.
/// </summary>
public class PlatformAdminFilterTests
{
    private readonly Mock<IPolicyEnforcer> _policy = new();
    private readonly Guid _userId = Guid.NewGuid();

    private IAsyncAuthorizationFilter CreateFilter()
    {
        var attr = new RequirePlatformAdminAttribute();
        var services = new ServiceCollection();
        services.AddSingleton(_policy.Object);
        return (IAsyncAuthorizationFilter)attr.CreateInstance(services.BuildServiceProvider());
    }

    private static AuthorizationFilterContext BuildContext(ClaimsPrincipal user)
    {
        var httpContext = new DefaultHttpContext { User = user };
        var actionContext = new ActionContext(httpContext, new RouteData(), new ActionDescriptor());
        return new AuthorizationFilterContext(actionContext, new List<IFilterMetadata>());
    }

    private ClaimsPrincipal AuthedUser() => new(new ClaimsIdentity(
        new[] { new Claim(ClaimTypes.NameIdentifier, _userId.ToString()) }, "test"));

    [Fact]
    public async Task Allows_WhenPlatformAdmin()
    {
        _policy.Setup(p => p.AuthorizePlatformAsync(_userId)).ReturnsAsync(true);
        var ctx = BuildContext(AuthedUser());

        await CreateFilter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeNull();
    }

    [Fact]
    public async Task Forbids_WhenNotPlatformAdmin()
    {
        _policy.Setup(p => p.AuthorizePlatformAsync(_userId)).ReturnsAsync(false);
        var ctx = BuildContext(AuthedUser());

        await CreateFilter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<ForbidResult>();
    }

    [Fact]
    public async Task Unauthorized_WhenNoUserIdClaim()
    {
        var ctx = BuildContext(new ClaimsPrincipal(new ClaimsIdentity()));

        await CreateFilter().OnAuthorizationAsync(ctx);

        ctx.Result.Should().BeOfType<UnauthorizedResult>();
    }
}
