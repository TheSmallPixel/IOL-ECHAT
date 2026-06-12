using ECHAT.Models.Enums;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Moq;

namespace ECHAT.Server.Core.Tests;

/// <summary>
/// Prova del fail-safe default (`_ => false`) di <see cref="PolicyEnforcer.AuthorizeAsync"/>: un
/// permesso non riconosciuto deve essere NEGATO anche all'Owner. Mutation testing aveva mostrato che
/// il ramo di default non era coperto: `_ => false` poteva diventare `_ => true` (fail-OPEN) senza
/// far fallire alcun test. Questo è un controllo di sicurezza, quindi va asserito esplicitamente.
/// </summary>
public class PolicyEnforcerDefaultDenyTests
{
    private readonly Mock<IMembershipReader> _membership = new();
    private readonly Mock<IUserStore> _users = new();
    private readonly Guid _userId = Guid.NewGuid();
    private readonly Guid _conversationId = Guid.NewGuid();

    [Fact]
    public async Task UnrecognizedPermission_IsDenied_EvenForOwner()
    {
        _membership.Setup(m => m.GetRoleAsync(_userId, _conversationId)).ReturnsAsync("Owner");
        var sut = new PolicyEnforcer(_membership.Object, _users.Object);

        // An out-of-range permission value must hit the default arm and fail closed.
        var result = await sut.AuthorizeAsync(_userId, _conversationId, (Permission)9999);

        result.Should().BeFalse("an unrecognized permission must default-deny, never default-allow");
    }
}
