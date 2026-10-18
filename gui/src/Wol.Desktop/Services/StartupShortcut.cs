using System.Runtime.InteropServices;
using System.Runtime.InteropServices.Marshalling;

namespace Wol.Desktop.Services;

/// <summary>
/// "Start with Windows": a shortcut in the user's Startup folder (<c>shell:startup</c>) that
/// opens Wol in the notification area. No registry writes; deleting the shortcut undoes it.
/// </summary>
internal static partial class StartupShortcut
{
    /// <summary>
    /// Command-line argument opening Wol in the notification area.
    /// </summary>
    public const string TrayArgument = "--tray";

    /// <summary>
    /// Path of the shortcut.
    /// </summary>
    public static string Path { get; } = System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Startup),
        "Wol.lnk");

    /// <summary>
    /// Whether the shortcut exists.
    /// </summary>
    public static bool Exists => File.Exists(Path);

    /// <summary>
    /// Creates or deletes the shortcut.
    /// </summary>
    /// <param name="enabled">Whether Wol starts with Windows.</param>
    /// <returns>True when the shortcut now matches the request.</returns>
    public static bool Set(bool enabled)
    {
        try
        {
            if (!enabled)
            {
                File.Delete(Path);
                return true;
            }

            var executable = AppFolder.Launcher;
            var clsid = new Guid("00021401-0000-0000-C000-000000000046");
            var iid = new Guid("00000000-0000-0000-C000-000000000046");
            Marshal.ThrowExceptionForHR(CoCreateInstance(in clsid, 0, CLSCTX_INPROC_SERVER, in iid, out var unknown));
            try
            {
                var link = (IShellLinkW)new StrategyBasedComWrappers().GetOrCreateObjectForComInstance(unknown, CreateObjectFlags.None);
                link.SetPath(executable);
                link.SetArguments(TrayArgument);
                link.SetWorkingDirectory(System.IO.Path.GetDirectoryName(executable)!);
                link.SetDescription("Wol");
                ((IPersistFile)link).Save(Path, true);
            }
            finally
            {
                Marshal.Release(unknown);
            }
            return true;
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException or COMException)
        {
            return false;
        }
    }

    /// <summary>
    /// <c>CLSCTX_INPROC_SERVER</c>: the COM object runs in this process.
    /// </summary>
    private const uint CLSCTX_INPROC_SERVER = 1;

    /// <summary>
    /// Win32 <c>CoCreateInstance</c> (ole32.dll).
    /// </summary>
    /// <param name="clsid">Class of the object.</param>
    /// <param name="outer">Aggregating object; none.</param>
    /// <param name="context">Where the object runs.</param>
    /// <param name="iid">Interface asked for.</param>
    /// <param name="instance">The object.</param>
    /// <returns>An HRESULT.</returns>
    [LibraryImport("ole32.dll")]
    private static partial int CoCreateInstance(in Guid clsid, nint outer, uint context, in Guid iid, out nint instance);

    /// <summary>
    /// <c>IShellLinkW</c> (shobjidl_core.h): every method in vtable order, only setters used.
    /// </summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("000214F9-0000-0000-C000-000000000046")]
    internal partial interface IShellLinkW
    {
        /// <summary>
        /// Gets the target path (unused).
        /// </summary>
        /// <param name="file">Buffer.</param>
        /// <param name="length">Buffer length.</param>
        /// <param name="data">Find data.</param>
        /// <param name="flags">Flags.</param>
        void GetPath(nint file, int length, nint data, uint flags);

        /// <summary>
        /// Gets the target item list (unused).
        /// </summary>
        /// <param name="list">Item list.</param>
        void GetIDList(out nint list);

        /// <summary>
        /// Sets the target item list (unused).
        /// </summary>
        /// <param name="list">Item list.</param>
        void SetIDList(nint list);

        /// <summary>
        /// Gets the description (unused).
        /// </summary>
        /// <param name="name">Buffer.</param>
        /// <param name="length">Buffer length.</param>
        void GetDescription(nint name, int length);

        /// <summary>
        /// Sets the description.
        /// </summary>
        /// <param name="name">Description.</param>
        void SetDescription(string name);

        /// <summary>
        /// Gets the working directory (unused).
        /// </summary>
        /// <param name="directory">Buffer.</param>
        /// <param name="length">Buffer length.</param>
        void GetWorkingDirectory(nint directory, int length);

        /// <summary>
        /// Sets the working directory.
        /// </summary>
        /// <param name="directory">Directory.</param>
        void SetWorkingDirectory(string directory);

        /// <summary>
        /// Gets the arguments (unused).
        /// </summary>
        /// <param name="arguments">Buffer.</param>
        /// <param name="length">Buffer length.</param>
        void GetArguments(nint arguments, int length);

        /// <summary>
        /// Sets the arguments.
        /// </summary>
        /// <param name="arguments">Arguments.</param>
        void SetArguments(string arguments);

        /// <summary>
        /// Gets the hotkey (unused).
        /// </summary>
        /// <param name="hotkey">Hotkey.</param>
        void GetHotkey(out ushort hotkey);

        /// <summary>
        /// Sets the hotkey (unused).
        /// </summary>
        /// <param name="hotkey">Hotkey.</param>
        void SetHotkey(ushort hotkey);

        /// <summary>
        /// Gets the show command (unused).
        /// </summary>
        /// <param name="command">Show command.</param>
        void GetShowCmd(out int command);

        /// <summary>
        /// Sets the show command (unused).
        /// </summary>
        /// <param name="command">Show command.</param>
        void SetShowCmd(int command);

        /// <summary>
        /// Gets the icon location (unused).
        /// </summary>
        /// <param name="path">Buffer.</param>
        /// <param name="length">Buffer length.</param>
        /// <param name="index">Icon index.</param>
        void GetIconLocation(nint path, int length, out int index);

        /// <summary>
        /// Sets the icon location (unused).
        /// </summary>
        /// <param name="path">Icon file.</param>
        /// <param name="index">Icon index.</param>
        void SetIconLocation(string path, int index);

        /// <summary>
        /// Sets the relative path (unused).
        /// </summary>
        /// <param name="path">Relative path.</param>
        /// <param name="reserved">Reserved.</param>
        void SetRelativePath(string path, uint reserved);

        /// <summary>
        /// Resolves the target (unused).
        /// </summary>
        /// <param name="window">Owner window.</param>
        /// <param name="flags">Flags.</param>
        void Resolve(nint window, uint flags);

        /// <summary>
        /// Sets the target path.
        /// </summary>
        /// <param name="file">Target path.</param>
        void SetPath(string file);
    }

    /// <summary>
    /// <c>IPersistFile</c> (objidl.h): saves the shortcut.
    /// </summary>
    [GeneratedComInterface(StringMarshalling = StringMarshalling.Utf16)]
    [Guid("0000010b-0000-0000-C000-000000000046")]
    internal partial interface IPersistFile
    {
        /// <summary>
        /// Gets the class (unused, from <c>IPersist</c>).
        /// </summary>
        /// <param name="clsid">Class.</param>
        void GetClassID(out Guid clsid);

        /// <summary>
        /// Tells whether the object changed (unused).
        /// </summary>
        /// <returns>An HRESULT.</returns>
        [PreserveSig]
        int IsDirty();

        /// <summary>
        /// Loads a file (unused).
        /// </summary>
        /// <param name="file">File path.</param>
        /// <param name="mode">Access mode.</param>
        void Load(string file, uint mode);

        /// <summary>
        /// Saves the object to a file.
        /// </summary>
        /// <param name="file">File path.</param>
        /// <param name="remember">Whether the file becomes the current one.</param>
        void Save(string file, [MarshalAs(UnmanagedType.Bool)] bool remember);

        /// <summary>
        /// Tells that saving is complete (unused).
        /// </summary>
        /// <param name="file">File path.</param>
        void SaveCompleted(string file);

        /// <summary>
        /// Gets the current file (unused).
        /// </summary>
        /// <param name="file">File path.</param>
        void GetCurFile(out nint file);
    }
}
