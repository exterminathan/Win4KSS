using System.Runtime.InteropServices;

namespace Win4K.Core.Playback;

/// <summary>
/// Raw P/Invoke surface for libmpv (client API 2.5). Everything here is a direct mapping of
/// mpv/client.h; the usable wrapper is <see cref="MpvPlayer"/>.
///
/// libmpv is cdecl and uses UTF-8 for every string, so strings are marshalled by hand rather
/// than with the default UTF-16 marshaller.
/// </summary>
internal static partial class MpvInterop
{
    internal const string Library = "libmpv-2";

    // ---- lifecycle -------------------------------------------------------------------

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial ulong mpv_client_api_version();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr mpv_create();

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int mpv_initialize(IntPtr ctx);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void mpv_terminate_destroy(IntPtr ctx);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void mpv_wakeup(IntPtr ctx);

    // ---- options and properties ------------------------------------------------------

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int mpv_set_option_string(IntPtr ctx, string name, string data);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int mpv_set_property_string(IntPtr ctx, string name, string data);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr mpv_get_property_string(IntPtr ctx, string name);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int mpv_get_property(IntPtr ctx, string name, MpvFormat format, out long data);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int mpv_observe_property(IntPtr ctx, ulong replyUserdata, string name, MpvFormat format);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void mpv_free(IntPtr data);

    // ---- commands and events ---------------------------------------------------------

    /// <summary>
    /// Null-terminated argv form. Preferred over mpv_command_string because arguments are not
    /// re-parsed — file paths containing spaces or quotes would otherwise need escaping.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int mpv_command(IntPtr ctx, IntPtr[] args);

    /// <summary>
    /// argv form with a result node. Used for loadfile, which returns the new entry's
    /// playlist_entry_id — the only reliable way to map later end-file events back to a clip.
    /// </summary>
    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int mpv_command_ret(IntPtr ctx, IntPtr[] args, out MpvNode result);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial void mpv_free_node_contents(ref MpvNode node);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial int mpv_request_log_messages(IntPtr ctx, string minLevel);

    [LibraryImport(Library)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr mpv_wait_event(IntPtr ctx, double timeout);

    [LibraryImport(Library, StringMarshalling = StringMarshalling.Utf8)]
    [UnmanagedCallConv(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    internal static partial IntPtr mpv_error_string(int error);

    // ---- helpers ---------------------------------------------------------------------

    internal static string? PtrToStringUtf8(IntPtr ptr) =>
        ptr == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(ptr);

    internal static string ErrorText(int code)
    {
        var text = PtrToStringUtf8(mpv_error_string(code));
        return string.IsNullOrEmpty(text) ? $"mpv error {code}" : $"{text} ({code})";
    }
}

internal enum MpvFormat
{
    None = 0,
    String = 1,
    OsdString = 2,
    Flag = 3,
    Int64 = 4,
    Double = 5,
    Node = 6,
    NodeArray = 7,
    NodeMap = 8,
    ByteArray = 9,
}

internal enum MpvEventId
{
    None = 0,
    Shutdown = 1,
    LogMessage = 2,
    GetPropertyReply = 3,
    SetPropertyReply = 4,
    CommandReply = 5,
    StartFile = 6,
    EndFile = 7,
    FileLoaded = 8,
    /// <summary>
    /// Deprecated upstream in favour of observing "idle-active", which we also do; handled
    /// anyway because it costs nothing and some builds still send it.
    /// </summary>
    Idle = 11,
    ClientMessage = 16,
    VideoReconfig = 17,
    AudioReconfig = 18,
    Seek = 20,
    PlaybackRestart = 21,
    PropertyChange = 22,
    QueueOverflow = 24,
    Hook = 25,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEvent
{
    public MpvEventId EventId;
    public int Error;
    public ulong ReplyUserdata;
    public IntPtr Data;
}

internal enum MpvEndFileReason
{
    Eof = 0,
    Stop = 2,
    Quit = 3,
    Error = 4,
    Redirect = 5,
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventEndFile
{
    public MpvEndFileReason Reason;
    public int Error;
    public long PlaylistEntryId;
    public long PlaylistInsertId;
    public int PlaylistInsertNumEntries;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventStartFile
{
    public long PlaylistEntryId;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventLogMessage
{
    public IntPtr Prefix;
    public IntPtr Level;
    public IntPtr Text;
    public int LogLevel;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvEventProperty
{
    public IntPtr Name;
    public MpvFormat Format;
    public IntPtr Data;
}

/// <summary>
/// mpv_node. The C union is 8 bytes on x64 (char*, int, int64, double or list pointer), so it
/// is carried as one IntPtr and reinterpreted according to <see cref="Format"/>.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
internal struct MpvNode
{
    public IntPtr U;
    public MpvFormat Format;
}

[StructLayout(LayoutKind.Sequential)]
internal struct MpvNodeList
{
    public int Num;
    public IntPtr Values;
    public IntPtr Keys;
}
