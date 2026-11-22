using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class OutputDeviceSelectorTests
{
    private static AudioDeviceInfo Device(string id, string name, bool isDefault = false) =>
        new(id, name, isDefault);

    private static List<AudioDeviceInfo> Devices() =>
    [
        Device("{0.0.0.00000000}.{speakers}", "扬声器 (Realtek)", isDefault: true),
        Device("{0.0.0.00000000}.{headphones}", "耳机 (USB Audio)"),
        Device("{0.0.0.00000000}.{hdmi}", "显示器 (HDMI)")
    ];

    [Fact]
    public void Select_PreferredDevicePresent_UsesIt()
    {
        var devices = Devices();

        var selection = OutputDeviceSelector.Select(devices, devices[1].Id);

        Assert.Same(devices[1], selection.Device);
        Assert.True(selection.HasPreference);
        Assert.True(selection.IsPreferredAvailable);
        Assert.False(selection.IsFallback);
        Assert.False(selection.IsFollowingDefault);
    }

    [Fact]
    public void Select_PreferredDeviceIdMatchIsCaseInsensitive()
    {
        var devices = Devices();

        var selection = OutputDeviceSelector.Select(devices, devices[2].Id.ToUpperInvariant());

        Assert.Same(devices[2], selection.Device);
        Assert.True(selection.IsPreferredAvailable);
    }

    [Fact]
    public void Select_PreferredDeviceMissing_FallsBackToDefaultAndFlagsIt()
    {
        var devices = Devices();

        var selection = OutputDeviceSelector.Select(devices, "{0.0.0.00000000}.{unplugged}");

        Assert.Same(devices[0], selection.Device);
        Assert.False(selection.IsPreferredAvailable);
        Assert.True(selection.IsFallback);
        Assert.True(selection.HasPreference);
    }

    [Fact]
    public void Select_PreferredDeviceMissingWithoutDefault_FallsBackToFirst()
    {
        List<AudioDeviceInfo> devices =
        [
            Device("{id-a}", "设备 A"),
            Device("{id-b}", "设备 B")
        ];

        var selection = OutputDeviceSelector.Select(devices, "{id-missing}");

        Assert.Same(devices[0], selection.Device);
        Assert.True(selection.IsFallback);
    }

    [Fact]
    public void Select_NoPreference_FollowsSystemDefault()
    {
        var devices = Devices();

        var selection = OutputDeviceSelector.Select(devices, null);

        Assert.Same(devices[0], selection.Device);
        Assert.True(selection.IsFollowingDefault);
        Assert.False(selection.IsFallback);
    }

    [Fact]
    public void Select_NoPreferenceWithoutDefault_UsesFirstDevice()
    {
        List<AudioDeviceInfo> devices =
        [
            Device("{id-a}", "设备 A"),
            Device("{id-b}", "设备 B")
        ];

        var selection = OutputDeviceSelector.Select(devices, null);

        Assert.Same(devices[0], selection.Device);
        Assert.True(selection.IsFollowingDefault);
    }

    [Fact]
    public void Select_BlankPreference_IsTreatedAsNoPreference()
    {
        var devices = Devices();

        var selection = OutputDeviceSelector.Select(devices, "   ");

        Assert.Same(devices[0], selection.Device);
        Assert.False(selection.HasPreference);
        Assert.False(selection.IsFallback);
        Assert.True(selection.IsFollowingDefault);
    }

    [Fact]
    public void Select_NoDevices_ReturnsNullSelection()
    {
        var selection = OutputDeviceSelector.Select([], "{id-a}");

        Assert.Null(selection.Device);
        Assert.False(selection.IsFallback);
        Assert.True(selection.HasPreference);
    }

    [Fact]
    public void Select_NullDevicesList_IsSafe()
    {
        var selection = OutputDeviceSelector.Select(null, null);

        Assert.Null(selection.Device);
        Assert.False(selection.IsFallback);
    }
}
