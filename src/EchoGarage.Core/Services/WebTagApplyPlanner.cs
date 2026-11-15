using EchoGarage.Core.Models;

namespace EchoGarage.Core.Services;

/// <summary>
/// 把“勾选字段 + 匹配结果”转换成每个本地文件的 <see cref="TagBatchEdit"/>（ADR-022 §4）。
/// 专辑级字段写入全部所选文件；歌曲级字段只写匹配成功的文件；空值/非法值不写。
/// </summary>
public static class WebTagApplyPlanner
{
    public static WebTagApplyPlan BuildPlan(
        IReadOnlyList<WebTagMatch> matches,
        WebTagRelease release,
        IReadOnlySet<string> selectedAlbumFields,
        IReadOnlySet<string> selectedTrackFields,
        bool includeCover,
        WebTagCover? cover)
    {
        var edits = new List<WebTagFileEdit>();
        foreach (var match in matches)
        {
            var edit = new TagBatchEdit();

            AddAlbumFields(edit, release, selectedAlbumFields);
            if (match.RemoteTrack is not null)
            {
                AddTrackFields(edit, match.RemoteTrack, selectedTrackFields);
            }

            if (includeCover && cover is { Bytes.Length: > 0 })
            {
                edit.CoverChanged = true;
                edit.NewCoverBytes = cover.Bytes;
                edit.NewCoverMime = cover.Mime;
            }

            // 没有任何字段变化的文件不进入写入列表，避免无意义地重写文件。
            if (edit.Fields.Count > 0 || edit.CoverChanged)
            {
                edits.Add(new WebTagFileEdit(match.LocalTrack, edit));
            }
        }

        return new WebTagApplyPlan(edits);
    }

    private static void AddAlbumFields(
        TagBatchEdit edit,
        WebTagRelease release,
        IReadOnlySet<string> selected)
    {
        AddText(edit, release.AlbumFields, selected, WebTagFieldKeys.Album, TagFieldKeys.Album);
        AddText(edit, release.AlbumFields, selected, WebTagFieldKeys.AlbumArtist, TagFieldKeys.AlbumArtist);
        AddNumber(edit, release.AlbumFields, selected, WebTagFieldKeys.Year, TagFieldKeys.Year);
        AddText(edit, release.AlbumFields, selected, WebTagFieldKeys.Genre, TagFieldKeys.Genre);
    }

    private static void AddTrackFields(
        TagBatchEdit edit,
        WebTagTrack track,
        IReadOnlySet<string> selected)
    {
        AddText(edit, track.Fields, selected, WebTagFieldKeys.Title, TagFieldKeys.Title);
        AddText(edit, track.Fields, selected, WebTagFieldKeys.Artist, TagFieldKeys.Artist);
        AddText(edit, track.Fields, selected, WebTagFieldKeys.Composer, TagFieldKeys.Composer);
        AddNumber(edit, track.Fields, selected, WebTagFieldKeys.Track, TagFieldKeys.TrackNumber);
        AddNumber(edit, track.Fields, selected, WebTagFieldKeys.Disc, TagFieldKeys.DiscNumber);
    }

    private static void AddText(
        TagBatchEdit edit,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlySet<string> selected,
        string webKey,
        string tagKey)
    {
        if (!selected.Contains(webKey) ||
            !fields.TryGetValue(webKey, out var value) ||
            string.IsNullOrWhiteSpace(value))
        {
            return;
        }

        edit.Fields[tagKey] = value.Trim();
    }

    private static void AddNumber(
        TagBatchEdit edit,
        IReadOnlyDictionary<string, string> fields,
        IReadOnlySet<string> selected,
        string webKey,
        string tagKey)
    {
        if (!selected.Contains(webKey) ||
            !fields.TryGetValue(webKey, out var text) ||
            !int.TryParse(text, out var value) ||
            value <= 0)
        {
            return;
        }

        if (tagKey == TagFieldKeys.Year && value > 9999)
        {
            return;
        }

        edit.Fields[tagKey] = value.ToString();
    }
}
