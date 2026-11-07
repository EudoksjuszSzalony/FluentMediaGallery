using System.Collections.Concurrent;
using System.Drawing;
using FluentMediaGallery.Core;

namespace FluentMediaGallery.App;

public sealed record RiverTile(MediaRow Media, double ImageWidth, double ImageHeight)
{
    // Container includes a small gap; pixels and aspect ratio are otherwise preserved.
    public double Width => ImageWidth;
    public double Height => ImageHeight;
}

public sealed record RiverRow(string Header, IReadOnlyList<RiverTile> Tiles)
{
    public Microsoft.UI.Xaml.Visibility HeaderVisibility =>
        string.IsNullOrEmpty(Header)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
}

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

        // Target a photo-like mosaic with predictable row height; prevent a very
        // wide logo from forcing the whole row to become a tiny ribbon.
        width = Math.Max(350, width - 16);
        const double targetHeight = 202;
        const double gap = 3;
        var culture = System.Globalization.CultureInfo.GetCultureInfo("pl-PL");
        var index = 0;
        string? lastGroup = null;

        while (index < items.Count)
        {
            var first = items[index].Entry;
            var group = new DateTime(first.LastWriteUtcTicks, DateTimeKind.Utc)
                .ToLocalTime().ToString("MMMM yyyy", culture);
            var batch = new List<MediaRow>();
            var ratios = new List<double>();
            double ratioTotal = 0;

            // Never mix two date groups in one justified row.
            while (index < items.Count)
            {
                var candidate = items[index].Entry;
                var candidateGroup = new DateTime(candidate.LastWriteUtcTicks, DateTimeKind.Utc)
                    .ToLocalTime().ToString("MMMM yyyy", culture);
                if (batch.Count > 0 && candidateGroup != group) break;

                var ratio = GetRatio(candidate);
                batch.Add(items[index++]);
                ratios.Add(ratio);
                ratioTotal += ratio;
                if (ratioTotal * targetHeight >= width - gap * batch.Count ||
                    batch.Count >= 9) break;
            }

            var available = width - gap * Math.Max(0, batch.Count - 1);
            var naturalHeight = available / ratioTotal;
            var height = Math.Clamp(naturalHeight, 145, 240);
            // Last row of a group is allowed to leave unused horizontal space.
            var tiles = new List<RiverTile>(batch.Count);
            for (var i = 0; i < batch.Count; i++)
                tiles.Add(new RiverTile(batch[i],
                    Math.Round(height * ratios[i]), Math.Round(height)));

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
                    return Math.Clamp((double)image.Width / image.Height, 0.65, 2.8);
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
