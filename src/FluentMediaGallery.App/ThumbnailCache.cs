using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using FluentMediaGallery.Core;

namespace FluentMediaGallery.App;

/// <summary>
/// Bounded local PNG thumbnail cache. Extracts Windows Shell thumbnails on background threads,
/// never on UI. Cache only: no writes to any source media.
/// </summary>
public sealed class ShellThumbnailCache
{
    private const long MaxCacheBytes = 2L * 1024 * 1024 * 1024;
    private const int ImageEdge = 320;
    private const uint ThumbnailOnly = 0x00000008;
    private readonly SemaphoreSlim _workers = new(5, 5);
    private readonly SemaphoreSlim _pruneGuard = new(1, 1);
    private int _created;

    public string CacheRoot { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FluentMediaGallery", "Thumbnails", "v1");

    public async Task<string?> GetOrCreateAsync(MediaEntry item, CancellationToken ct = default)
    {
        var cacheKey = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            item.FullPath.ToUpperInvariant() + "|" + item.Size + "|" + item.LastWriteUtcTicks +
            "|" + ImageEdge)));
        var subfolder = Path.Combine(CacheRoot, cacheKey[..2]);
        var destination = Path.Combine(subfolder, cacheKey + ".png");

        if (File.Exists(destination)) return destination;

        await _workers.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (File.Exists(destination)) return destination;
            if (!File.Exists(item.FullPath)) return null;

            var result = await Task.Run(() => ExtractAndSave(item.FullPath, subfolder, destination), ct)
                .ConfigureAwait(false);
            if (result is not null && Interlocked.Increment(ref _created) % 128 == 0)
                _ = Task.Run(TrimCacheBestEffort);
            return result;
        }
        finally { _workers.Release(); }
    }

    private static string? ExtractAndSave(string mediaPath, string directory, string destination)
    {
        IShellItemImageFactory? factory = null;
        IntPtr bitmapHandle = IntPtr.Zero;
        string? temporaryFile = null;
        try
        {
            var iid = typeof(IShellItemImageFactory).GUID;
            int hr = SHCreateItemFromParsingName(mediaPath, IntPtr.Zero, ref iid, out factory);
            if (hr < 0 || factory is null) return null;

            hr = factory.GetImage(new ShellSize(ImageEdge, ImageEdge), ThumbnailOnly, out bitmapHandle);
            if (hr < 0 || bitmapHandle == IntPtr.Zero) return null;

            Directory.CreateDirectory(directory);
            temporaryFile = Path.Combine(directory, Guid.NewGuid().ToString("N") + ".tmp");
            using (var picture = Image.FromHbitmap(bitmapHandle))
                picture.Save(temporaryFile, ImageFormat.Png);

            try { File.Move(temporaryFile, destination); }
            catch (IOException) when (File.Exists(destination)) { /* same key generated twice */ }
            return destination;
        }
        catch (Exception ex) when (
            ex is IOException or UnauthorizedAccessException or COMException
               or System.Runtime.InteropServices.ExternalException or ArgumentException
               or NotSupportedException)
        {
            return null; // Thumbnail extraction is best effort; original media remains unchanged.
        }
        finally
        {
            if (bitmapHandle != IntPtr.Zero) DeleteObject(bitmapHandle);
            if (factory is not null) Marshal.ReleaseComObject(factory);
            if (temporaryFile is not null)
            {
                try { if (File.Exists(temporaryFile)) File.Delete(temporaryFile); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private void TrimCacheBestEffort()
    {
        if (!_pruneGuard.Wait(0)) return;
        try
        {
            if (!Directory.Exists(CacheRoot)) return;

            // Never delete outside this dedicated generated-thumbnail folder.
            var prefix = Path.GetFullPath(CacheRoot).TrimEnd(Path.DirectorySeparatorChar)
                         + Path.DirectorySeparatorChar;
            var items = Directory.EnumerateFiles(CacheRoot, "*.png", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .Where(f => f.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
                            && (f.Attributes & FileAttributes.ReparsePoint) == 0)
                .OrderBy(f => f.LastWriteTimeUtc)
                .ToList();

            long occupied = items.Sum(f => f.Length);
            foreach (var item in items)
            {
                if (occupied <= MaxCacheBytes) break;
                try
                {
                    var length = item.Length;
                    item.Delete();
                    occupied -= length;
                }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
        finally { _pruneGuard.Release(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct ShellSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    [ComImport]
    [Guid("BCC18B79-BA16-442F-80C4-8A59C30C463B")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig]
        int GetImage(ShellSize size, uint flags, out IntPtr bitmap);
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
    private static extern int SHCreateItemFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path, IntPtr bindContext,
        ref Guid iid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory result);

    [DllImport("gdi32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr bitmap);
}
