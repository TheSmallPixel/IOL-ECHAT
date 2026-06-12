using ECHAT.Client.Core.Services;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class RandomDelayProviderTests
{
    [Fact]
    public async Task DelayRandomAsync_SmallRange_CompletesWithoutThrowing()
    {
        var sut = new RandomDelayProvider();

        // Range 1..2ms: il delay reale completa praticamente subito.
        Func<Task> act = () => sut.DelayRandomAsync(1, 2, CancellationToken.None);

        await act.Should().NotThrowAsync();
    }

    [Fact]
    public async Task DelayRandomAsync_AlreadyCancelledToken_ThrowsOperationCanceled()
    {
        var sut = new RandomDelayProvider();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => sut.DelayRandomAsync(1, 2, cts.Token);

        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
