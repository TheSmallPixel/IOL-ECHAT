using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

public class QuotaServiceTests
{
    [Fact]
    public void TryConsume_WithinLimit_ShouldReturnTrue()
    {
        var service = new QuotaService(maxTokens: 10);

        var result = service.TryConsume("user1", 1);

        result.Should().BeTrue();
    }

    [Fact]
    public void TryConsume_ExceedingLimit_ShouldReturnFalse()
    {
        var service = new QuotaService(maxTokens: 3);

        service.TryConsume("user1", 1).Should().BeTrue();
        service.TryConsume("user1", 1).Should().BeTrue();
        service.TryConsume("user1", 1).Should().BeTrue();
        service.TryConsume("user1", 1).Should().BeFalse();
    }

    [Fact]
    public void TryConsume_DifferentKeys_ShouldBeIndependent()
    {
        var service = new QuotaService(maxTokens: 1);

        service.TryConsume("user1", 1).Should().BeTrue();
        service.TryConsume("user2", 1).Should().BeTrue();
        service.TryConsume("user1", 1).Should().BeFalse();
    }

    [Fact]
    public void TryConsume_MultipleTokens_ShouldConsumeCorrectly()
    {
        var service = new QuotaService(maxTokens: 5);

        service.TryConsume("user1", 3).Should().BeTrue();
        service.TryConsume("user1", 3).Should().BeFalse();
        service.TryConsume("user1", 2).Should().BeTrue();
    }
}
