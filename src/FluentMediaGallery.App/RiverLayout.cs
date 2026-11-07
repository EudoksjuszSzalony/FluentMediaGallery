using System.Collections.Concurrent;
using System.Drawing;
using FluentMediaGallery.Core;

namespace FluentMediaGallery.App;

/// <summary>
/// Placement in one virtualized mosaic band. Each tile can have a different width,
/// height and vertical offset (not just a shared justified-row height).
/// </summary>
public sealed record RiverTile(MediaRow Media, double Left, double Top,
    double ImageWidth, double ImageHeight)
{
    public double Width => ImageWidth;
    public double Height => ImageHeight;
}

public sealed record RiverRow(string Header, IReadOnlyList<RiverTile> Tiles,
    double CanvasWidth, double CanvasHeight)
{
    public Microsoft.UI.Xaml.Visibility HeaderVisibility =>
        string.IsNullOrEmpty(Header)
            ? Microsoft.UI.Xaml.Visibility.Collapsed
            : Microsoft.UI.Xaml.Visibility.Visible;
}

/// <summary>
/// Photos-style mosaic geometry: alternating justified image rows and two-tier
/// bands with one enlarged image spanning both tiers. Geometric placement
/// retains photo aspect ratios, avoiding forced square thumbnails.
/// </summary>
public static class RiverLayout
{
    private const double Gap = 3;
    private const double TargetRowHeight = 192;
    private static readonly ConcurrentDictionary<string, double> Ratios =
        new(StringComparer.OrdinalIgnoreCase);

    private sealed record Placement(int Index, double Left, double Top,
        double Width, double Height);

    private sealed record Band(int Count, double Height, double Cost,
        IReadOnlyList<Placement> Placements);

    public static IReadOnlyList<RiverRow> Build(IReadOnlyList<MediaRow> items,
        double viewportWidth, string rangeCaption = "")
    {
        var result = new List<RiverRow>();
        if (items.Count == 0) return result;
        var width = Math.Max(320, viewportWidth - 16);
        var ratios = new double[items.Count];
        for (var i = 0; i < ratios.Length; i++)
            ratios[i] = GetRatio(items[i].Entry);

        for (var offset = 0; offset < items.Count;)
        {
            var regular = ChooseRegularRow(ratios, offset, width);
            var featured = ChooseFeaturedBand(ratios, offset, width);
            // A recurring two-tier band breaks the monotonous horizontal
            // stripes. Defer to regular rows if a featured band would require
            // unrealistic geometry for these specific photos.
            var useFeatured = featured is not null &&
                result.Count % 3 != 1 &&
                featured.Cost < 1.85 &&
                (regular is null || featured.Cost < regular.Cost + 1.0);

            var selected = useFeatured ? featured! : regular ?? featured;
            if (selected is null)
            {
                // Last, extremely portrait-oriented item: never discard it.
                var height = TargetRowHeight;
                selected = new Band(1, height, 2,
                    new[] { new Placement(0, 0, 0,
                        Math.Min(width, ratios[offset] * height), height) });
            }

            var tiles = selected.Placements.Select(p =>
                new RiverTile(items[offset + p.Index],
                    Math.Round(p.Left, 2), Math.Round(p.Top, 2),
                    Math.Max(1, Math.Round(p.Width, 2)),
                    Math.Max(1, Math.Round(p.Height, 2)))).ToArray();

            result.Add(new RiverRow(result.Count == 0 ? rangeCaption : "",
                tiles, width, Math.Round(selected.Height, 2)));
            offset += selected.Count;
        }
        return result;
    }

    private static Band? ChooseRegularRow(double[] ratios, int offset, double width)
    {
        Band? best = null;
        double sum = 0;
        var left = ratios.Length - offset;
        for (var count = 1; count <= Math.Min(8, left); count++)
        {
            sum += ratios[offset + count - 1];
            var space = width - (count - 1) * Gap;
            var h = space / sum;
            if (h < 105 || h > 370) continue;

            var atEnd = offset + count == ratios.Length;
            var displayedH = atEnd && h > TargetRowHeight * 1.12
                ? TargetRowHeight : h;
            var cost = Math.Abs(Math.Log(displayedH / TargetRowHeight));
            if (count == 1 && left > 1) cost += 0.6;
            if (displayedH > 290) cost += 0.5;
            if (displayedH < 130) cost += 0.5;

            if (best is not null && cost >= best.Cost) continue;
            double x = 0;
            var placements = new List<Placement>();
            for (var j = 0; j < count; j++)
            {
                var w = ratios[offset + j] * displayedH;
                if (j == count - 1 && displayedH == h)
                    w = width - x;
                placements.Add(new Placement(j, x, 0, Math.Max(1, w), displayedH));
                x += w + Gap;
            }
            best = new Band(count, displayedH, cost, placements);
        }
        return best;
    }

    private static Band? ChooseFeaturedBand(double[] ratios, int offset, double width)
    {
        Band? best = null;
        var remaining = ratios.Length - offset;
        if (remaining < 3) return null;

        for (var featureOnLeft = 0; featureOnLeft <= 1; featureOnLeft++)
        for (var upperCount = 1; upperCount <= 3; upperCount++)
        for (var lowerCount = 1; lowerCount <= 3; lowerCount++)
        {
            var count = 1 + upperCount + lowerCount;
            if (count > remaining) continue;
            var featureIndex = featureOnLeft == 1 ? 0 : count - 1;
            var featureRatio = ratios[offset + featureIndex];
            if (featureRatio < 0.3 || featureRatio > 1.85) continue;

            var topStart = featureOnLeft == 1 ? 1 : 0;
            var bottomStart = topStart + upperCount;
            var sumTop = 0d;
            var sumBottom = 0d;
            for (var i = 0; i < upperCount; i++)
                sumTop += ratios[offset + topStart + i];
            for (var i = 0; i < lowerCount; i++)
                sumBottom += ratios[offset + bottomStart + i];

            var gapTop = (upperCount - 1) * Gap;
            var gapBottom = (lowerCount - 1) * Gap;
            var factor = 1d / sumTop + 1d / sumBottom;
            // Solve for two row heights and a two-row feature width so that
            // featureWidth / fullBandHeight == feature's natural aspect ratio.
            var height = ((width - Gap - gapTop) / sumTop
                        + (width - Gap - gapBottom) / sumBottom + Gap)
                        / (1 + featureRatio * factor);
            var featureWidth = featureRatio * height;
            var restWidth = width - featureWidth - Gap;
            var topHeight = (restWidth - gapTop) / sumTop;
            var bottomHeight = (restWidth - gapBottom) / sumBottom;

            if (!double.IsFinite(height) ||
                height < 285 || height > 525 ||
                featureWidth < width * 0.2 || featureWidth > width * 0.64 ||
                topHeight < 112 || topHeight > 300 ||
                bottomHeight < 112 || bottomHeight > 300 ||
                Math.Abs(height - topHeight - bottomHeight - Gap) > 2)
                continue;

            var cost = 0.52 * Math.Abs(Math.Log(height / 394d))
                     + 0.75 * Math.Abs(Math.Log(topHeight / TargetRowHeight))
                     + 0.75 * Math.Abs(Math.Log(bottomHeight / TargetRowHeight))
                     + 0.2 * Math.Abs(topHeight - bottomHeight) / TargetRowHeight;
            if (best is not null && cost >= best.Cost) continue;

            var result = new List<Placement>(count);
            var rightSide = featureOnLeft == 1;
            var remainingX = rightSide ? featureWidth + Gap : 0;
            result.Add(new Placement(featureIndex, rightSide ? 0 : width - featureWidth,
                0, featureWidth, height));

            var x = remainingX;
            for (var i = 0; i < upperCount; i++)
            {
                var w = ratios[offset + topStart + i] * topHeight;
                if (i == upperCount - 1) w = restWidth - (x - remainingX);
                result.Add(new Placement(topStart + i, x, 0, w, topHeight));
                x += w + Gap;
            }

            x = remainingX;
            for (var i = 0; i < lowerCount; i++)
            {
                var w = ratios[offset + bottomStart + i] * bottomHeight;
                if (i == lowerCount - 1) w = restWidth - (x - remainingX);
                result.Add(new Placement(bottomStart + i, x, topHeight + Gap,
                    w, bottomHeight));
                x += w + Gap;
            }

            // Keep the logical media/filmstrip order stable even when the
            // featured item is positioned to the right of two smaller rows.
            result.Sort((a, b) => a.Index.CompareTo(b.Index));
            best = new Band(count, height, cost, result);
        }
        return best;
    }

    private static double GetRatio(MediaEntry entry)
    {
        if (entry.Kind == MediaKind.Video) return 16d / 9;
        var key = entry.FullPath + "|" + entry.LastWriteUtcTicks;
        return Ratios.GetOrAdd(key, _ =>
        {
            try
            {
                using var img = Image.FromFile(entry.FullPath);
                if (img.Height > 0)
                    return Math.Clamp((double)img.Width / img.Height, 0.45, 4.0);
            }
            catch (Exception ex) when (
                ex is IOException or UnauthorizedAccessException or ArgumentException
                    or OutOfMemoryException or NotSupportedException
                    or System.Runtime.InteropServices.ExternalException)
            {
                // Offline share / unsupported decoder: display as a safe square.
            }
            return 1;
        });
    }
}
