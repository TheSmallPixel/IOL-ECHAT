using ECHAT.Client.Core.Interfaces;
using ECHAT.Client.Core.Services;
using ECHAT.Models.Events;
using FluentAssertions;

namespace ECHAT.Client.Core.Tests;

public class MigrationStateManagerTests
{
    private readonly MigrationStateManager _mgr = new();

    [Theory]
    [InlineData("Completed", MigrationPhase.Completed)]
    [InlineData("Cancelled", MigrationPhase.Cancelled)]
    [InlineData("Failed", MigrationPhase.Failed)]
    [InlineData("Running", MigrationPhase.Reencrypting)]
    [InlineData("", MigrationPhase.Reencrypting)]
    [InlineData("anything-else", MigrationPhase.Reencrypting)]
    public void MapRemoteStatusToPhase_MapsCorrectly(string status, MigrationPhase expected)
    {
        _mgr.MapRemoteStatusToPhase(status).Should().Be(expected);
    }

    [Theory]
    [InlineData(MigrationPhase.Starting, false)]
    [InlineData(MigrationPhase.Reencrypting, false)]
    [InlineData(MigrationPhase.Finalizing, false)]
    [InlineData(MigrationPhase.Completed, true)]
    [InlineData(MigrationPhase.Cancelled, true)]
    [InlineData(MigrationPhase.Failed, true)]
    public void IsTerminal_AllPhases(MigrationPhase phase, bool expected)
    {
        _mgr.IsTerminal(phase).Should().Be(expected);
    }

    [Fact]
    public void ShouldIgnoreRemote_TrueWhenLocallyDriven()
    {
        _mgr.ShouldIgnoreRemote(isLocallyDriven: true).Should().BeTrue();
    }

    [Fact]
    public void ShouldIgnoreRemote_FalseWhenNotLocallyDriven()
    {
        _mgr.ShouldIgnoreRemote(isLocallyDriven: false).Should().BeFalse();
    }

    [Fact]
    public void BuildRemoteProgress_UsesStatusPhaseAndPercent_WithNullTotal()
    {
        var evt = new JobProgressEvent
        {
            ConversationId = Guid.NewGuid(),
            Status = "Running",
            ProgressPercent = 42
        };

        var progress = _mgr.BuildRemoteProgress(evt);

        progress.Phase.Should().Be(MigrationPhase.Reencrypting);
        progress.Processed.Should().Be(42);
        progress.Total.Should().BeNull();
    }

    [Fact]
    public void ShouldClearTerminal_TrueWhenSnapshotUnchanged()
    {
        var snap = new MigrationProgress(MigrationPhase.Completed, 100);
        var current = new MigrationProgress(MigrationPhase.Completed, 100);

        _mgr.ShouldClearTerminal(current, snap).Should().BeTrue();
    }

    [Fact]
    public void ShouldClearTerminal_FalseWhenSnapshotChanged()
    {
        var snap = new MigrationProgress(MigrationPhase.Completed, 100);
        var current = new MigrationProgress(MigrationPhase.Starting);

        _mgr.ShouldClearTerminal(current, snap).Should().BeFalse();
    }

    [Fact]
    public void ShouldClearTerminal_FalseWhenCurrentNull()
    {
        var snap = new MigrationProgress(MigrationPhase.Completed, 100);

        _mgr.ShouldClearTerminal(null, snap).Should().BeFalse();
    }
}
