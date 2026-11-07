using System.Runtime.InteropServices;

namespace FluentMediaGallery.Core;

/// <summary>
/// Writes System.Rating via the Shell property handler and verifies by reopening.
/// Does NOT modify files if no compatible Windows property handler is available.
/// Some videos and formats expose read-only metadata; caller must keep SQLite pending state.
/// </summary>
public sealed class ShellRatingWriter
{
    private const uint GpsReadWrite = 0x00000002;
    private const ushort VtUi4 = 19;
    private static readonly PropertyKey RatingKey = new(
        new Guid("64440492-4C8B-11D1-8B70-080036B11A03"), 9);
    private readonly ShellRatingReader _reader = new();

    public RatingWriteResult TryWrite(string path, int rating)
    {
        if (!OperatingSystem.IsWindows())
            return new(false, null, "Windows Property System is unavailable.");
        if (rating is not (1 or 75 or 99))
            return new(false, null, "Only 1-, 4- and 5-star values can be written.");
        if (!File.Exists(path))
            return new(false, null, "File is missing or source is offline.");

        IPropertyStore? store = null;
        try
        {
            if ((File.GetAttributes(path) & FileAttributes.ReadOnly) != 0)
                return new(false, null, "File is read-only.");

            var iid = typeof(IPropertyStore).GUID;
            var openHr = SHGetPropertyStoreFromParsingName(path, IntPtr.Zero,
                GpsReadWrite, ref iid, out store);
            if (openHr < 0 || store is null)
                return new(false, null, "No writable Shell property store: " + DescribeHr(openHr));

            var key = RatingKey;
            var value = new PropVariant { Type = VtUi4, UIntValue = (uint)rating };
            var setHr = store.SetValue(ref key, ref value);
            if (setHr < 0)
                return new(false, null, "System.Rating is not writable: " + DescribeHr(setHr));
            var commitHr = store.Commit();
            if (commitHr < 0)
                return new(false, null, "Unable to commit System.Rating: " + DescribeHr(commitHr));
        }
        catch (COMException ex) { return new(false, null, ex.Message); }
        catch (UnauthorizedAccessException ex) { return new(false, null, ex.Message); }
        catch (IOException ex) { return new(false, null, ex.Message); }
        finally
        {
            if (store is not null) Marshal.ReleaseComObject(store);
        }

        // Never report success only because SetValue/Commit returned S_OK.
        // A fresh property-store read confirms the rating is visible to Windows.
        var readBack = _reader.TryRead(path);
        if (readBack != rating)
            return new(false, readBack, "Read-back verification failed; handler may not persist rating.");
        return new(true, readBack, null);
    }

    private static string DescribeHr(int hr) => "0x" + unchecked((uint)hr).ToString("X8");

    [StructLayout(LayoutKind.Sequential)]
    private struct PropertyKey(Guid formatId, uint propertyId)
    {
        public Guid FormatId = formatId;
        public uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Explicit, Size = 24)]
    private struct PropVariant
    {
        [FieldOffset(0)] public ushort Type;
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
}
