using ECHAT.Server.Core.Services;
using FluentAssertions;

namespace ECHAT.Server.Core.Tests;

public class BlobFileAssemblyServiceTests
{
    private readonly BlobFileAssemblyService _sut = new();

    [Fact]
    public void GetPartPath_ZeroPadsToFiveDigits()
    {
        _sut.GetPartPath("/tmp/x", 0).Should().Be(Path.Combine("/tmp/x", "part-00000"));
        _sut.GetPartPath("/tmp/x", 7).Should().Be(Path.Combine("/tmp/x", "part-00007"));
        _sut.GetPartPath("/tmp/x", 12345).Should().Be(Path.Combine("/tmp/x", "part-12345"));
    }

    [Fact]
    public void OrderPartPaths_SortsCorrectlyRegardlessOfInputOrder()
    {
        var dir = "/tmp/x";
        var unordered = new[]
        {
            _sut.GetPartPath(dir, 2),
            _sut.GetPartPath(dir, 0),
            _sut.GetPartPath(dir, 10),
            _sut.GetPartPath(dir, 1),
        };

        var ordered = _sut.OrderPartPaths(unordered);

        ordered.Should().ContainInOrder(
            _sut.GetPartPath(dir, 0),
            _sut.GetPartPath(dir, 1),
            _sut.GetPartPath(dir, 2),
            _sut.GetPartPath(dir, 10));
    }

    [Fact]
    public void OrderPartPaths_ZeroPaddingKeepsNumericOrderForManyParts()
    {
        var dir = "/tmp/x";
        // Without zero-padding "part-100" would sort before "part-9"; padding fixes that.
        var unordered = new[]
        {
            _sut.GetPartPath(dir, 100),
            _sut.GetPartPath(dir, 9),
            _sut.GetPartPath(dir, 99),
        };

        var ordered = _sut.OrderPartPaths(unordered);

        ordered.Should().ContainInOrder(
            _sut.GetPartPath(dir, 9),
            _sut.GetPartPath(dir, 99),
            _sut.GetPartPath(dir, 100));
    }

    [Fact]
    public void CalculateAssembledSize_SumsPartLengths()
    {
        var parts = new[]
        {
            new byte[] { 1, 2, 3 },
            new byte[] { 4, 5 },
            new byte[] { 6, 7, 8, 9 },
        };

        _sut.CalculateAssembledSize(parts).Should().Be(9);
    }

    [Fact]
    public void CalculateAssembledSize_EmptyInput_ReturnsZero()
    {
        _sut.CalculateAssembledSize(Array.Empty<byte[]>()).Should().Be(0);
    }
}
