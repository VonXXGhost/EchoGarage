using EchoGarage.Core.Models;
using EchoGarage.Core.Services;

namespace EchoGarage.Tests;

public sealed class ConversionPlannerTests
{
    private static readonly HashSet<string> NoFiles = new(StringComparer.OrdinalIgnoreCase);

    [Fact]
    public void Plan_ComputesOutputPathsForEachTrack()
    {
        var plan = ConversionPlanner.Plan(
            [MakeTrack(@"C:\music\a.mp3", "A"), MakeTrack(@"C:\music\b.mp3", "B")],
            MakePreset(),
            NoFiles.Contains);

        Assert.Equal(2, plan.Items.Count);
        Assert.Equal(Path.Combine(@"C:\music", "Artist - A.mp3"), plan.Items[0].OutputPath);
        Assert.Equal(Path.Combine(@"C:\music", "Artist - B.mp3"), plan.Items[1].OutputPath);
        Assert.Equal(0, plan.ConflictCount);
    }

    [Fact]
    public void Plan_AskPolicyFlagsExistingFiles()
    {
        var preset = MakePreset(ConversionConflictPolicy.Ask);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\music", "Artist - A.mp3")
        };

        var plan = ConversionPlanner.Plan([MakeTrack(@"C:\music\a.mp3", "A")], preset, existing.Contains);

        Assert.True(plan.Items[0].Conflict);
        Assert.Equal(1, plan.ConflictCount);
    }

    [Fact]
    public void Plan_SkipPolicySkipsExistingFiles()
    {
        var preset = MakePreset(ConversionConflictPolicy.Skip);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\music", "Artist - A.mp3")
        };

        var plan = ConversionPlanner.Plan([MakeTrack(@"C:\music\a.mp3", "A")], preset, existing.Contains);

        Assert.True(plan.Items[0].Skipped);
        Assert.Equal("目标文件已存在", plan.Items[0].SkipReason);
    }

    [Fact]
    public void Plan_OverwriteNeverTargetsTheSourceFile()
    {
        var preset = MakePreset(ConversionConflictPolicy.Overwrite);
        preset.Format = ConversionFormat.Wav;
        preset.FileNameTemplate = "%filename%";
        var track = MakeTrack(@"C:\music\a.wav", "A");

        var plan = ConversionPlanner.Plan([track], preset, NoFiles.Contains);

        Assert.NotEqual(track.Path, plan.Items[0].OutputPath, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(@"C:\music", "a (2).wav"), plan.Items[0].OutputPath);
    }

    [Fact]
    public void Plan_RenamePolicyAvoidsExistingAndDuplicatePaths()
    {
        var preset = MakePreset(ConversionConflictPolicy.Rename);
        preset.FileNameTemplate = "%artist%";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\music", "Artist.mp3")
        };

        var plan = ConversionPlanner.Plan(
            [MakeTrack(@"C:\music\a.mp3", "A"), MakeTrack(@"C:\music\b.mp3", "B")],
            preset,
            existing.Contains);

        Assert.Equal(Path.Combine(@"C:\music", "Artist (2).mp3"), plan.Items[0].OutputPath);
        Assert.Equal(Path.Combine(@"C:\music", "Artist (3).mp3"), plan.Items[1].OutputPath);
    }

    [Fact]
    public void ApplyConflictPolicy_OverwriteKeepsTargetsAndRenamesSourceCollisions()
    {
        var preset = MakePreset(ConversionConflictPolicy.Ask);
        preset.Format = ConversionFormat.Wav;
        preset.FileNameTemplate = "%filename%";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\music", "b.wav")
        };

        var plan = ConversionPlanner.Plan(
            [MakeTrack(@"C:\music\a.wav", "A"), MakeTrack(@"C:\music\b.wav", "B")],
            preset,
            existing.Contains);

        ConversionPlanner.ApplyConflictPolicy(plan, ConversionConflictPolicy.Overwrite, existing.Contains);

        Assert.Equal(Path.Combine(@"C:\music", "a.wav"), plan.Items[0].OutputPath);
        Assert.Equal(Path.Combine(@"C:\music", "b (2).wav"), plan.Items[1].OutputPath);
    }

    [Fact]
    public void ApplyConflictPolicy_SkipMarksConflictsSkipped()
    {
        var preset = MakePreset(ConversionConflictPolicy.Ask);
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            Path.Combine(@"C:\music", "Artist - A.mp3")
        };

        var plan = ConversionPlanner.Plan([MakeTrack(@"C:\music\a.mp3", "A")], preset, existing.Contains);
        ConversionPlanner.ApplyConflictPolicy(plan, ConversionConflictPolicy.Skip, existing.Contains);

        Assert.True(plan.Items[0].Skipped);
        Assert.False(plan.Items[0].Conflict);
    }

    private static ConversionPreset MakePreset(ConversionConflictPolicy policy = ConversionConflictPolicy.Ask) => new()
    {
        Format = ConversionFormat.Mp3,
        FileNameTemplate = "%artist% - %title%",
        ConflictPolicy = policy
    };

    private static Track MakeTrack(string path, string title) => new()
    {
        Path = path,
        Title = title,
        Artist = "Artist"
    };
}
