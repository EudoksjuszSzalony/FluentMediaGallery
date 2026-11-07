using System.Collections.Concurrent;
using System.Drawing;
using FluentMediaGallery.Core;

namespace FluentMediaGallery.App;

public sealed record RiverTile(MediaRow Media, double ImageWidth, double ImageHeight)
{
    // Container includes a small gap; pixels and aspect ratio are otherwise preserved.
    public double Width => ImageWidth + 3;
    public double Height => ImageHeight;
}

public sealed record RiverRow(string Header, IReadOnlyList<RiverTile> Tiles);

/// <summary>
/// Justified image rows. Original dimensions are read from headers only and only
/// for currently requested SQLite batches. Never loads the full indexed library.
/// </summary>
public static class RiverLayout
{
    private static readonly ConcurrentDictionary<string, double> Ratios =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<RiverRow> Build(IReadOnlyList<MediaRow> items, double width)
    {
        var result = new List<RiverRow>();
        if (items.Count == 0) return result;
        width = Math.Max(350, width - 20);
        const double targetHeight = 168;
        const double gap = 3;

        var index = 0;
        string lastGroup = "";
        while (index < items.Count)
        {
            var batch = new List<MediaRow>();
            var ratios = new List<double>();
            double ratioTotal = 0;
            while (index < items.Count && (ratioTotal * targetHeight < width || batch.Count < 2))
            {
                var row = items[index++];
                var ratio = GetRatio(row.Entry);
                batch.Add(row);
                ratios.Add(ratio);
                ratioTotal += ratio;
                if (batch.Count >= 9) break;
            }

            double available = width - gap * Math.Max(0, batch.Count - 1);
            double height = Math.Clamp(available / ratioTotal, 112, 220);
            if (index == items.Count && height > targetHeight * 1.25)
                height = targetHeight; // don't stretch the incomplete final row
            var tiles = new List<RiverTile>(batch.Count);
            for (var i = 0; i < batch.Count; i++)
                tiles.Add(new RiverTile(batch[i], Math.Round(height * ratios[i]), Math.Round(height)));

            var first = batch[0].Entry;
            var group = new DateTime(first.LastWriteUtcTicks, DateTimeKind.Utc)
                .ToLocalTime().ToString("MMMM yyyy", System.Globalization.CultureInfo.GetCultureInfo("pl-PL"));
            var header = lastGroup == group ? "" : group;
            lastGroup = group;
            result.Add(new RiverRow(header, tiles));
        }
        return result;
    }

    private static double GetRatio(MediaEntry entry)
    {
        if (entry.Kind == MediaKind.Video) return 16d / 9;
        var key = entry.FullPath + "|" + entry.LastWriteUtcTicks;
        return Ratios.GetOrAdd(key, _ =>
        {
            try
            {
                using var image = Image.FromFile(entry.FullPath);
                if (image.Height > 0)
                    return Math.Clamp((double)image.Width / image.Height, 0.35, 5.0);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException
                    or OutOfMemoryException or System.Runtime.InteropServices.ExternalException)
            {
                // Unavailable NAS or unsupported image: square fallback; never remove media.
            }
            return 1;
        });
    }
}
