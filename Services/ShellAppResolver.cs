using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;

namespace TrayTrigger.Services;

/// <summary>What a shell item starts: an entry dragged from shell:AppsFolder, or the target of a shortcut with no file path.</summary>
/// <param name="Name">The shell's display name for it ("Xbox").</param>
/// <param name="FilePath">A real file on disk, to be handled like a dropped file.</param>
/// <param name="AppId">A Store app's app ID ("Microsoft.GamingApp_8wekyb3d8bbwe!Microsoft.Xbox.App").</param>
/// <param name="ProgramPath">A desktop app's .exe, resolved from its Start menu entry.</param>
public sealed record ShellApp(string Name, string? FilePath = null, string? AppId = null, string? ProgramPath = null)
{
    /// <summary>Nothing TrayTrigger can start was found: a Control Panel item, a website, a folder that isn't on disk.</summary>
    public bool IsUnresolved => FilePath == null && AppId == null && ProgramPath == null;
}

/// <summary>
/// Reads shell items: the ones Explorer hands over when something is dragged from shell:AppsFolder
/// (which offers no file path, so a plain file drop can't see them), the target of a shortcut that
/// points at one, and a Store app's icon. Everything here is the Windows shell's COM API.
/// </summary>
public static class ShellAppResolver
{
    /// <summary>The drag-and-drop format Explorer puts shell items in (CFSTR_SHELLIDLIST).</summary>
    public const string IdListFormat = "Shell IDList Array";

    /// <summary>More items than anyone drags at once; a larger count means the data is damaged.</summary>
    private const int MaxDroppedItems = 256;

    private const uint SIGDN_NORMALDISPLAY = 0x00000000;
    private const uint SIGDN_PARENTRELATIVEPARSING = 0x80018001;
    private const uint SIGDN_FILESYSPATH = 0x80058000;

    private const int SIIGBF_BIGGERSIZEOK = 0x1;
    private const int SIIGBF_ICONONLY = 0x4;

    /// <summary>PKEY_AppUserModel_ID: a Start menu entry's app ID.</summary>
    private static readonly PropertyKey AppUserModelIdKey = new(new Guid("9F4C2855-9F79-4B39-A8D0-E1D42DE1D5F3"), 5);

    /// <summary>PKEY_Link_TargetParsingPath: the file a desktop app's Start menu entry starts.</summary>
    private static readonly PropertyKey LinkTargetParsingPathKey = new(new Guid("B9B4B3FC-2B51-4A42-B5D8-324146AFCF25"), 2);

    /// <summary>Every item in a dropped "Shell IDList Array". Items the shell can't open are left out.</summary>
    public static List<ShellApp> FromIdListArray(byte[] data)
    {
        var apps = new List<ShellApp>();
        var offsets = ParseIdListOffsets(data);
        if (offsets == null)
        {
            LoggingService.Warn("ShellAppResolver", $"Ignored a dropped shell item list that isn't well formed ({data.Length} bytes).");
            return apps;
        }

        var handle = GCHandle.Alloc(data, GCHandleType.Pinned);
        try
        {
            IntPtr start = handle.AddrOfPinnedObject();
            IntPtr parent = start + offsets[0];
            for (int i = 1; i < offsets.Count; i++)
            {
                // Each item's ID list is relative to the parent folder's; the shell needs the whole path.
                IntPtr absolute = ILCombine(parent, start + offsets[i]);
                if (absolute == IntPtr.Zero) continue;
                try
                {
                    var app = FromIdList(absolute);
                    if (app != null) apps.Add(app);
                }
                finally
                {
                    ILFree(absolute);
                }
            }
        }
        finally
        {
            handle.Free();
        }
        return apps;
    }

    /// <summary>
    /// The offsets in a CIDA ("Shell IDList Array"): the parent folder's ID list first, then one per
    /// item. Null when the count, an offset or an ID list runs past the data, so a damaged or hostile
    /// drop is never read out of bounds.
    /// </summary>
    internal static List<int>? ParseIdListOffsets(ReadOnlySpan<byte> data)
    {
        if (data.Length < 12) return null;
        uint count = BitConverter.ToUInt32(data[..4]);
        if (count == 0 || count > MaxDroppedItems) return null;

        long headerLength = 4L + 4L * (count + 1);
        if (headerLength > data.Length) return null;

        var offsets = new List<int>((int)count + 1);
        for (int i = 0; i <= count; i++)
        {
            uint offset = BitConverter.ToUInt32(data.Slice(4 + 4 * i, 4));
            if (offset < headerLength || !IsTerminatedIdList(data, offset)) return null;
            offsets.Add((int)offset);
        }
        return offsets;
    }

    /// <summary>An ID list is a run of items, each led by its own 16-bit size, ended by a zero size.</summary>
    private static bool IsTerminatedIdList(ReadOnlySpan<byte> data, long position)
    {
        while (position + 2 <= data.Length)
        {
            ushort size = BitConverter.ToUInt16(data.Slice((int)position, 2));
            if (size == 0) return true;
            if (size < 2) return false;
            position += size;
        }
        return false;
    }

    /// <summary>What the shell item at an absolute ID list starts, or null when the shell can't open it.</summary>
    public static ShellApp? FromIdList(IntPtr pidl)
    {
        if (pidl == IntPtr.Zero) return null;
        IShellItem2? item = null;
        try
        {
            Guid iid = typeof(IShellItem2).GUID;
            if (SHCreateItemFromIDList(pidl, ref iid, out item) < 0 || item == null) return null;
            var app = Resolve(item);
            LoggingService.Verbose("ShellAppResolver", $"Shell item '{app.Name}': file '{app.FilePath}', app ID '{app.AppId}', program '{app.ProgramPath}'.");
            return app;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("ShellAppResolver", $"Could not read a shell item: {ex.Message}");
            return null;
        }
        finally
        {
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    private static ShellApp Resolve(IShellItem2 item)
    {
        string name = GetDisplayName(item, SIGDN_NORMALDISPLAY)?.Trim() ?? string.Empty;

        string? filePath = GetDisplayName(item, SIGDN_FILESYSPATH);
        if (!string.IsNullOrWhiteSpace(filePath)) return new ShellApp(name, FilePath: filePath);

        // A shell:AppsFolder entry's own parsing name is its app ID; the property covers other folders.
        string? parsingName = GetDisplayName(item, SIGDN_PARENTRELATIVEPARSING);
        string? appId = GetString(item, AppUserModelIdKey);
        if (ToolCatalog.ValidateAppId(appId) != null) appId = parsingName;
        if (ToolCatalog.ValidateAppId(appId) == null) return new ShellApp(name, AppId: appId!.Trim());

        // A desktop app's entry: the .exe its Start menu shortcut starts, or, when the entry is the
        // program itself, its parsing name - a full path, or known-folder-relative ("{1AC14E77-...}\cmd.exe").
        string? program = AsProgramPath(GetString(item, LinkTargetParsingPathKey))
            ?? AsProgramPath(parsingName)
            ?? AsProgramPath(ExpandKnownFolder(parsingName, KnownFolderPath));
        return new ShellApp(name, ProgramPath: program);
    }

    private static string? AsProgramPath(string? path)
    {
        string candidate = path?.Trim() ?? string.Empty;
        return candidate.Length > 0 && Path.IsPathFullyQualified(candidate)
            && string.Equals(Path.GetExtension(candidate), ".exe", StringComparison.OrdinalIgnoreCase)
            ? candidate
            : null;
    }

    /// <summary>"{KNOWNFOLDERID}\relative\path" with the known folder filled in, or null for any other parsing name.</summary>
    internal static string? ExpandKnownFolder(string? parsingName, Func<Guid, string?> knownFolderPath)
    {
        if (string.IsNullOrEmpty(parsingName) || parsingName[0] != '{') return null;
        int close = parsingName.IndexOf("}\\", StringComparison.Ordinal);
        if (close < 0 || !Guid.TryParse(parsingName.AsSpan(0, close + 1), out Guid folderId)) return null;

        string relative = parsingName[(close + 2)..];
        string? folder = knownFolderPath(folderId);
        if (string.IsNullOrEmpty(folder) || relative.Length == 0 || Path.IsPathRooted(relative)) return null;
        try
        {
            return Path.GetFullPath(Path.Combine(folder, relative));
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string? KnownFolderPath(Guid folderId)
    {
        if (SHGetKnownFolderPath(ref folderId, 0, IntPtr.Zero, out IntPtr path) < 0) return null;
        try
        {
            return Marshal.PtrToStringUni(path);
        }
        finally
        {
            Marshal.FreeCoTaskMem(path);
        }
    }

    /// <summary>
    /// Saves a Store app's icon as a PNG, as large as the shell has it up to <paramref name="size"/>.
    /// False when the app or its icon can't be read; the caller falls back to its own icon.
    /// </summary>
    public static bool TrySaveIcon(string appId, string pngPath, int size = 256)
    {
        if (ToolCatalog.ValidateAppId(appId) != null) return false;

        IShellItem2? item = null;
        IntPtr hbitmap = IntPtr.Zero;
        try
        {
            Guid iid = typeof(IShellItem2).GUID;
            if (SHCreateItemFromParsingName(ToolCatalog.AppsFolderPath(appId), IntPtr.Zero, ref iid, out item) < 0 || item == null) return false;

            var factory = (IShellItemImageFactory)item;
            if (factory.GetImage(new NativeSize(size, size), SIIGBF_BIGGERSIZEOK | SIIGBF_ICONONLY, out hbitmap) < 0 || hbitmap == IntPtr.Zero) return false;

            using var bitmap = ToBitmap(hbitmap);
            bitmap.Save(pngPath, ImageFormat.Png);
            return true;
        }
        catch (Exception ex)
        {
            LoggingService.Verbose("ShellAppResolver", $"Could not read the icon of Store app '{appId}': {ex.Message}");
            return false;
        }
        finally
        {
            if (hbitmap != IntPtr.Zero) DeleteObject(hbitmap);
            if (item != null) Marshal.ReleaseComObject(item);
        }
    }

    /// <summary>
    /// A copy of a shell image that keeps its transparency. Image.FromHbitmap drops the alpha channel,
    /// so a 32-bit image's pixels are copied row by row instead, top-down whichever way it is stored.
    /// </summary>
    private static unsafe Bitmap ToBitmap(IntPtr hbitmap)
    {
        int dibSize = Marshal.SizeOf<DibSection>();
        if (GetObject(hbitmap, dibSize, out DibSection dib) != dibSize || dib.BitsPerPixel != 32 || dib.Bits == IntPtr.Zero)
        {
            // Not a 32-bit image: there's no transparency to keep.
            return Image.FromHbitmap(hbitmap);
        }

        int width = dib.Width;
        int height = dib.Height;
        int stride = dib.WidthBytes;
        byte* bits = (byte*)dib.Bits;

        // Some images leave alpha at zero (opaque by convention); most are premultiplied, a few aren't.
        bool hasAlpha = false;
        bool premultiplied = true;
        for (long i = 0, end = (long)stride * height; i < end; i += 4)
        {
            byte alpha = bits[i + 3];
            if (alpha != 0) hasAlpha = true;
            if (bits[i] > alpha || bits[i + 1] > alpha || bits[i + 2] > alpha) premultiplied = false;
        }
        var format = !hasAlpha ? PixelFormat.Format32bppRgb : premultiplied ? PixelFormat.Format32bppPArgb : PixelFormat.Format32bppArgb;

        var bitmap = new Bitmap(width, height, format);
        var locked = bitmap.LockBits(new Rectangle(0, 0, width, height), ImageLockMode.WriteOnly, format);
        try
        {
            bool bottomUp = dib.HeaderHeight > 0;
            for (int y = 0; y < height; y++)
            {
                byte* source = bits + (long)(bottomUp ? height - 1 - y : y) * stride;
                byte* destination = (byte*)locked.Scan0 + (long)y * locked.Stride;
                Buffer.MemoryCopy(source, destination, locked.Stride, Math.Min(stride, locked.Stride));
            }
        }
        finally
        {
            bitmap.UnlockBits(locked);
        }
        return bitmap;
    }

    // ------------------------------------------------------------------ interop

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct PropertyKey(Guid formatId, uint propertyId)
    {
        public readonly Guid FormatId = formatId;
        public readonly uint PropertyId = propertyId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private readonly struct NativeSize(int width, int height)
    {
        public readonly int Width = width;
        public readonly int Height = height;
    }

    /// <summary>DIBSECTION: a BITMAP followed by its BITMAPINFOHEADER, whose height sign says which way the rows run.</summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct DibSection
    {
        public int Type;
        public int Width;
        public int Height;
        public int WidthBytes;
        public ushort Planes;
        public ushort BitsPerPixel;
        public IntPtr Bits;
        public uint HeaderSize;
        public int HeaderWidth;
        public int HeaderHeight;
        public ushort HeaderPlanes;
        public ushort HeaderBitCount;
        public uint Compression;
        public uint SizeImage;
        public int XPelsPerMeter;
        public int YPelsPerMeter;
        public uint ColorsUsed;
        public uint ColorsImportant;
        public uint Bitfield0;
        public uint Bitfield1;
        public uint Bitfield2;
        public IntPtr Section;
        public uint Offset;
    }

    /// <summary>IShellItem2, with IShellItem's methods first as the vtable has them. Only the ones used return HRESULTs.</summary>
    [ComImport, Guid("7e9fb0d3-919f-4307-ab2e-9b1860310c93"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItem2
    {
        void BindToHandler(IntPtr pbc, ref Guid bhid, ref Guid riid, out IntPtr ppv);
        void GetParent(out IntPtr ppsi);
        [PreserveSig] int GetDisplayName(uint sigdnName, out IntPtr ppszName);
        void GetAttributes(uint sfgaoMask, out uint psfgaoAttribs);
        void Compare(IntPtr psi, uint hint, out int piOrder);
        void GetPropertyStore(int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreWithCreateObject(int flags, IntPtr punkCreateObject, ref Guid riid, out IntPtr ppv);
        void GetPropertyStoreForKeys(IntPtr rgKeys, uint cKeys, int flags, ref Guid riid, out IntPtr ppv);
        void GetPropertyDescriptionList(ref PropertyKey keyType, ref Guid riid, out IntPtr ppv);
        void Update(IntPtr pbc);
        void GetProperty(ref PropertyKey key, IntPtr ppropvar);
        void GetCLSID(ref PropertyKey key, out Guid pclsid);
        void GetFileTime(ref PropertyKey key, out long pft);
        void GetInt32(ref PropertyKey key, out int pi);
        [PreserveSig] int GetString(ref PropertyKey key, out IntPtr ppsz);
    }

    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IShellItemImageFactory
    {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr phbm);
    }

    private static string? GetDisplayName(IShellItem2 item, uint sigdn)
    {
        if (item.GetDisplayName(sigdn, out IntPtr text) < 0 || text == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    private static string? GetString(IShellItem2 item, PropertyKey key)
    {
        if (item.GetString(ref key, out IntPtr text) < 0 || text == IntPtr.Zero) return null;
        try
        {
            return Marshal.PtrToStringUni(text);
        }
        finally
        {
            Marshal.FreeCoTaskMem(text);
        }
    }

    [DllImport("shell32.dll")]
    private static extern int SHCreateItemFromIDList(IntPtr pidl, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2? ppv);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern int SHCreateItemFromParsingName(string pszPath, IntPtr pbc, ref Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItem2? ppv);

    [DllImport("shell32.dll")]
    private static extern IntPtr ILCombine(IntPtr pidl1, IntPtr pidl2);

    [DllImport("shell32.dll")]
    private static extern void ILFree(IntPtr pidl);

    [DllImport("shell32.dll")]
    private static extern int SHGetKnownFolderPath(ref Guid rfid, uint dwFlags, IntPtr hToken, out IntPtr ppszPath);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll", EntryPoint = "GetObjectW")]
    private static extern int GetObject(IntPtr hObject, int cbBuffer, out DibSection dib);
}
