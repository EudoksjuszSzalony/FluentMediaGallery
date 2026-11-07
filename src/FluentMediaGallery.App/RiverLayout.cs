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

    /// <summary>
    /// Pack an uninterrupted media stream into justified rows, independent of
    /// month changes. The range caption is drawn once above the first row;
    /// unlike the former month-splitting layout this avoids single-image strips.
    /// All geometry is computed off the UI thread and never reads more than
    /// the currently loaded SQLite batch.
    /// </summary>
    public static IReadOnlyList<RiverRow> Build(
        IReadOnlyList<MediaRow> items, double width, string rangeCaption = "")
    {
        var result = new List<RiverRow>();
        if (items.Count == 0) return result;

        var availableWidth = Math.Max(320, width - 18);
        const double gap = 3;
        const double preferredHeight = 194;
        const double minimumHeight = 128;
        const double maximumHeight = 275;
        var ratios = new double[items.Count];
        for (var i = 0; i < ratios.Length; i++)
            ratios[i] = GetRatio(items[i].Entry);

        for (var start = 0; start < items.Count;)
        {
            // Select a row length whose natural height is closest to the
            // Photos-style target without excessive cropping or dead space.
            var max = Math.Min(10, items.Count - start);
            var bestCount = 1;
            var bestCost = double.PositiveInfinity;
            var sum = 0d;

            for (var count = 1; count <= max; count++)
            {
                sum += ratios[start + count - 1];
                var rowWidth = availableWidth - (count - 1) * gap;
                if (rowWidth <= 0) break;
                var height = rowWidth / sum;
                var cost = Math.Abs(Math.Log(height / preferredHeight));
                if (height < minimumHeight)
                    cost += 1.4 * (minimumHeight - height) / minimumHeight;
                if (height > maximumHeight)
                    cost += 1.4 * (height - maximumHeight) / maximumHeight;
                // Avoid one giant landscape logo if more thumbnails are available.
                if (count == 1 && max > 1) cost += 0.32;
                if (cost < bestCost)
                {
                    bestCost = cost;
                    bestCount = count;
                }
                if (height < minimumHeight * 0.75) break;
            }

            var rowSum = 0d;
            for (var i = 0; i < bestCount; i++) rowSum += ratios[start + i];
            var usable = availableWidth - gap * (bestCount - 1);
            var targetHeight = usable / rowSum;

            // The last incomplete row remains left-aligned at natural tile size
            // instead of stretching two images to absurd proportions.
            if (start + bestCount == items.Count && targetHeight > preferredHeight * 1.15)
                targetHeight = preferredHeight;

            targetHeight = Math.Clamp(targetHeight, minimumHeight, maximumHeight);
            var tiles = new List<RiverTile>(bestCount);
            var remainingWidth = usable;
            for (var i = 0; i < bestCount; i++)
            {
                var tileWidth = Math.Round(targetHeight * ratios[start + i]);
                // Compensate pixel rounding on normally justified (non-final)
                // rows so the last tile reaches the container's right edge.
                if (i == bestCount - 1 && targetHeight == usable / rowSum)
                    tileWidth = Math.Max(1, Math.Round(remainingWidth));
                else
                    tileWidth = Math.Min(tileWidth, Math.Max(1, remainingWidth));
                remainingWidth -= tileWidth;

                tiles.Add(new RiverTile(items[start + i], Math.Max(1, tileWidth),
                    Math.Round(targetHeight)));
            }

            result.Add(new RiverRow(result.Count == 0 ? rangeCaption : "", tiles));
            start += bestCount;
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
                    return Math.Clamp((double)image.Width / image.Height, 0.55, 3.6);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException
                    or OutOfMemoryException or NotSupportedException or System.Runtime.InteropServices.ExternalException)
            {
                // Unavailable NAS or unsupported image: square fallback; never remove media.
            }
            return 1;
        });
    }
}
