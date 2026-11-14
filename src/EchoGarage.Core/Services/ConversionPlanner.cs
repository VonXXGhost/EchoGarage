using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

public sealed class ConversionPlanItem
{
    public required Track Track { get; init; }
    public string OutputPath { get; set; } = string.Empty;
    public bool Conflict { get; set; }
    public bool Skipped { get; set; }
    public string? SkipReason { get; set; }
}

public sealed class ConversionPlan
{
    public required ConversionPreset Preset { get; init; }
    public List<ConversionPlanItem> Items { get; } = [];
    public int ConflictCount => Items.Count(item => item.Conflict && !item.Skipped);
    public int ActiveCount => Items.Count(item => !item.Skipped);
}

/// <summary>
/// Pure batch planner: resolves output paths, detects conflicts and applies the
/// fixed conflict policies. The "Ask" policy leaves conflicts flagged so the UI can prompt once.
/// </summary>
public static class ConversionPlanner
{
    public static ConversionPlan Plan(IReadOnlyList<Track> tracks, ConversionPreset preset, Func<string, bool> fileExists)
    {
        var plan = new ConversionPlan { Preset = preset };
        var plannedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var track in tracks)
        {
            var item = new ConversionPlanItem { Track = track };
            plan.Items.Add(item);

            if (track.IsMissing)
            {
                item.Skipped = true;
                item.SkipReason = "源文件缺失";
                continue;
            }

            var outputPath = ConversionNaming.TryResolveOutputPath(preset, track);
            if (string.IsNullOrWhiteSpace(outputPath))
            {
                item.Skipped = true;
                item.SkipReason = "无法确定输出文件夹";
                continue;
            }

            item.OutputPath = outputPath;
            var conflict = fileExists(outputPath)
                || ConversionNaming.PathsEqual(outputPath, track.Path)
                || plannedPaths.Contains(outputPath);

            switch (preset.ConflictPolicy)
            {
                case ConversionConflictPolicy.Skip when conflict:
                    item.Skipped = true;
                    item.SkipReason = "目标文件已存在";
                    break;

                case ConversionConflictPolicy.Overwrite:
                    if (ConversionNaming.PathsEqual(outputPath, track.Path) || plannedPaths.Contains(outputPath))
                    {
                        item.OutputPath = ConversionNaming.MakeUniquePath(
                            outputPath,
                            candidate => fileExists(candidate)
                                || plannedPaths.Contains(candidate)
                                || ConversionNaming.PathsEqual(candidate, track.Path));
                    }

                    plannedPaths.Add(item.OutputPath);
                    break;

                case ConversionConflictPolicy.Rename:
                    item.OutputPath = ConversionNaming.MakeUniquePath(
                        outputPath,
                        candidate => fileExists(candidate) || plannedPaths.Contains(candidate));
                    plannedPaths.Add(item.OutputPath);
                    break;

                case ConversionConflictPolicy.Ask when conflict:
                    item.Conflict = true;
                    plannedPaths.Add(outputPath);
                    break;

                default:
                    plannedPaths.Add(outputPath);
                    break;
            }
        }

        return plan;
    }

    /// <summary>
    /// Applies the policy chosen in the batch conflict dialog to every flagged item.
    /// Overwrite keeps existing files, but never overwrites the source itself.
    /// </summary>
    public static void ApplyConflictPolicy(ConversionPlan plan, ConversionConflictPolicy policy, Func<string, bool> fileExists)
    {
        if (policy == ConversionConflictPolicy.Ask)
        {
            return;
        }

        var usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in plan.Items.Where(item => !item.Conflict && !item.Skipped))
        {
            usedPaths.Add(item.OutputPath);
        }

        foreach (var item in plan.Items.Where(item => item.Conflict && !item.Skipped))
        {
            item.Conflict = false;

            switch (policy)
            {
                case ConversionConflictPolicy.Skip:
                    item.Skipped = true;
                    item.SkipReason = "目标文件已存在";
                    break;

                case ConversionConflictPolicy.Overwrite:
                    if (ConversionNaming.PathsEqual(item.OutputPath, item.Track.Path) || usedPaths.Contains(item.OutputPath))
                    {
                        item.OutputPath = ConversionNaming.MakeUniquePath(
                            item.OutputPath,
                            candidate => fileExists(candidate) || usedPaths.Contains(candidate));
                    }

                    usedPaths.Add(item.OutputPath);
                    break;

                default:
                    item.OutputPath = ConversionNaming.MakeUniquePath(
                        item.OutputPath,
                        candidate => fileExists(candidate) || usedPaths.Contains(candidate));
                    usedPaths.Add(item.OutputPath);
                    break;
            }
        }
    }
}
