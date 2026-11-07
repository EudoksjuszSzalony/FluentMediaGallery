using System.Runtime.InteropServices;

namespace FluentMediaGallery.Core;

/// <summary>
/// Reads the same Windows System.Rating shown as "Classification" in Explorer.
/// Does not use Microsoft Photos' private database; DOES NOT modify source files.
/// </summary>
public sealed class ShellRatingReader
{
    private const ushort VtUi1 = 17;
    private const ushort VtUi2 = 18;
    private const ushort VtUi4 = 19;
    private static readonly PropertyKey RatingKey = new(new Guid("64440492-4C8B-11D1-8B70-080036B11A03"), 9);
    private static readonly PropertyKey SimpleRatingKey = new(new Guid("A09F084E-AD41-489F-8076-AA5BE3082BCA"), 100);

    public int? TryRead(string path)
    {
        if (!OperatingSystem.IsWindows() || !File.Exists(path)) return null;
        IPropertyStore? store = null;
        try
        {
            var iid = typeof(IPropertyStore).GUID;
            var hr = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero, 0, ref iid, out store);
            if (hr < 0 || store is null) return null;

            var rating = TryGetUInt(store, RatingKey);
            if (rating is >= 0 and <= 99) return rating;
            var simple = TryGetUInt(store, SimpleRatingKey);
            return simple switch { 1 => 1, 2 => 25, 3 => 50, 4 => 75, 5 => 99, _ => null };
        }
        catch (COMException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
        catch (IOException) { return null; }
        finally { if (store is not null) Marshal.ReleaseComObject(store); }
    }

    private static int? TryGetUInt(IPropertyStore store, PropertyKey key)
    {
        var variant = default(PropVariant);
        try
        {
            var hr = store.GetValue(ref key, out variant);
            if (hr < 0) return null;
            return variant.Type switch
            {
                VtUi1 => variant.ByteValue,
                VtUi2 => variant.ShortValue,
                VtUi4 when variant.UIntValue <= int.MaxValue => (int)variant.UIntValue,
                _ => null
            };
        }
        finally { PropVariantClear(ref variant); }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid id, uint pid)
    {
        public Guid FormatId = id;
        public uint PropertyId = pid;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
        [FieldOffset(8)] public byte ByteValue;
        [FieldOffset(8)] public ushort ShortValue;
        [FieldOffset(8)] public uint UIntValue;
    }

    [ComImport, Guid("886D8EEB-8CF2-4446-8D02-CDBA1DBDCF99"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IPropertyStore
    {
        [PreserveSig] int GetCount(out uint count);
        [PreserveSig] int GetAt(uint index, out PropertyKey key);
        [PreserveSig] int GetValue(ref PropertyKey key, out PropVariant value);
        [PreserveSig] int SetValue(ref PropertyKey key, ref PropVariant value);
        [PreserveSig] int Commit();
    }

    [DllImport("shell32.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
    private static extern int SHGetPropertyStoreFromParsingName(
        [MarshalAs(UnmanagedType.LPWStr)] string path,
        IntPtr bindContext, uint flags, ref Guid iid,
        [MarshalAs(UnmanagedType.Interface)] out IPropertyStore store);

    [DllImport("ole32.dll")]
    private static extern int PropVariantClear(ref PropVariant variant);
}
