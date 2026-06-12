using ECHAT.Models.Dtos;
using ECHAT.Server.Core.Interfaces;
using ECHAT.Server.Core.Services;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace ECHAT.Server.Core.Tests;

/// <summary>
/// Verifica che <see cref="KeyAccessService.ResolveKeysAsync"/> interroghi il DEVICE esplicitamente
/// richiesto (quando passato), non l'utente chiamante. Mutation testing aveva mostrato che il
/// <c>deviceId ?? userId</c> poteva collassare a <c>userId</c> senza far fallire alcun test, un
/// percorso rilevante per il controllo d'accesso (di quale device si restituiscono i wrap).
/// </summary>
public class KeyAccessServiceResolveTests
{
    private readonly Mock<IKeyEnvelopeStore> _keyStore = new();
    private readonly Mock<IMemberStore> _members = new();
    private KeyAccessService Sut() => new(_keyStore.Object, _members.Object, NullLogger<KeyAccessService>.Instance);

    [Fact]
    public async Task ResolveKeys_ExplicitDeviceId_QueriesThatDevice_NotTheCaller()
    {
        var conv = Guid.NewGuid();
        var caller = Guid.NewGuid();
        var device = Guid.NewGuid();
        _keyStore.Setup(s => s.GetKeysAsync(conv, It.IsAny<int?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new List<WrappedKey> { new() { DeviceId = device } });

        await Sut().ResolveKeysAsync(conv, caller, epochId: 3, deviceId: device);

        _keyStore.Verify(s => s.GetKeysAsync(conv, 3, device), Times.Once);   // explicit device used
        _keyStore.Verify(s => s.GetKeysAsync(conv, It.IsAny<int?>(), caller), Times.Never); // NOT the caller
    }

    [Fact]
    public async Task ResolveKeys_NullDeviceId_FallsBackToCaller()
    {
        var conv = Guid.NewGuid();
        var caller = Guid.NewGuid();
        _keyStore.Setup(s => s.GetKeysAsync(conv, It.IsAny<int?>(), It.IsAny<Guid?>()))
            .ReturnsAsync(new List<WrappedKey>());

        await Sut().ResolveKeysAsync(conv, caller, epochId: null, deviceId: null);

        _keyStore.Verify(s => s.GetKeysAsync(conv, null, caller), Times.Once);
    }
}
