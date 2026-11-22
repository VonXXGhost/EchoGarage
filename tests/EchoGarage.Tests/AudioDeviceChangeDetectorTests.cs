using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class AudioDeviceChangeDetectorTests
{
    private static AudioDeviceInfo Device(string id, string name, bool isDefault = false) =>
        new(id, name, isDefault);

    [Fact]
    public void Compare_IdenticalSnapshots_ReportsNoChanges()
    {
        List<AudioDeviceInfo> snapshot =
        [
            Device("{a}", "扬声器", isDefault: true),
            Device("{b}", "耳机")
        ];

        var changes = AudioDeviceChangeDetector.Compare(snapshot, snapshot.ToList());

        Assert.False(changes.HasChanges);
        Assert.Empty(changes.Added);
        Assert.Empty(changes.Removed);
        Assert.Empty(changes.Renamed);
        Assert.False(changes.DefaultChanged);
    }

    [Fact]
    public void Compare_NewDevice_IsReportedAsAdded()
    {
        List<AudioDeviceInfo> previous = [Device("{a}", "扬声器", isDefault: true)];
        List<AudioDeviceInfo> current = [Device("{a}", "扬声器", isDefault: true), Device("{b}", "耳机")];

        var changes = AudioDeviceChangeDetector.Compare(previous, current);

        Assert.True(changes.HasChanges);
        Assert.Single(changes.Added);
        Assert.Equal("{b}", changes.Added[0].Id);
        Assert.Empty(changes.Removed);
    }

    [Fact]
    public void Compare_RemovedDevice_IsReported_WithPreviousDefault()
    {
        List<AudioDeviceInfo> previous = [Device("{a}", "扬声器", isDefault: true), Device("{b}", "耳机")];
        List<AudioDeviceInfo> current = [Device("{b}", "耳机", isDefault: true)];

        var changes = AudioDeviceChangeDetector.Compare(previous, current);

        Assert.True(changes.HasChanges);
        Assert.Single(changes.Removed);
        Assert.Equal("{a}", changes.Removed[0].Id);
        Assert.Equal("{a}", changes.PreviousDefault?.Id);
        Assert.Equal("{b}", changes.CurrentDefault?.Id);
        Assert.True(changes.DefaultChanged);
    }

    [Fact]
    public void Compare_DefaultSwitchOnly_IsReported()
    {
        List<AudioDeviceInfo> previous = [Device("{a}", "扬声器", isDefault: true), Device("{b}", "耳机")];
        List<AudioDeviceInfo> current = [Device("{a}", "扬声器"), Device("{b}", "耳机", isDefault: true)];

        var changes = AudioDeviceChangeDetector.Compare(previous, current);

        Assert.True(changes.DefaultChanged);
        Assert.Equal("{a}", changes.PreviousDefault?.Id);
        Assert.Equal("{b}", changes.CurrentDefault?.Id);
        Assert.Empty(changes.Added);
        Assert.Empty(changes.Removed);
    }

    [Fact]
    public void Compare_RenamedDevice_IsReported()
    {
        List<AudioDeviceInfo> previous = [Device("{a}", "扬声器 (已禁用)")];
        List<AudioDeviceInfo> current = [Device("{a}", "扬声器 (Realtek)")];

        var changes = AudioDeviceChangeDetector.Compare(previous, current);

        Assert.Single(changes.Renamed);
        Assert.Equal("扬声器 (Realtek)", changes.Renamed[0].FriendlyName);
        Assert.Empty(changes.Added);
        Assert.Empty(changes.Removed);
    }

    [Fact]
    public void Compare_BothEmpty_ReturnsEmpty()
    {
        var changes = AudioDeviceChangeDetector.Compare([], []);

        Assert.False(changes.HasChanges);
        Assert.Empty(changes.Added);
        Assert.Empty(changes.Removed);
    }

    [Fact]
    public void Compare_NullSnapshots_AreSafe()
    {
        var changes = AudioDeviceChangeDetector.Compare(null, null);

        Assert.False(changes.HasChanges);
    }

    [Fact]
    public void Compare_FirstSnapshotWithoutPrevious_ReportsAllAsAdded()
    {
        List<AudioDeviceInfo> current = [Device("{a}", "扬声器", isDefault: true), Device("{b}", "耳机")];

        var changes = AudioDeviceChangeDetector.Compare([], current);

        Assert.Equal(2, changes.Added.Count);
        Assert.True(changes.DefaultChanged);
        Assert.Equal("{a}", changes.CurrentDefault?.Id);
    }
}
