using System.Runtime.InteropServices;
using System.Runtime.InteropServices.ComTypes;
using System.Text;

namespace Oblachka;

// Чтение ярлыков .lnk через IShellLinkW: путь и аргументы. Позднее связывание WScript.Shell
// из .NET не работает надёжно, а этот интерфейс есть в любой Windows.
internal static class ShellLinks
{
    public static (string? Target, string Arguments) Read(string link)
    {
        var shell = (IShellLinkW)new ShellLink();
        try
        {
            ((IPersistFile)shell).Load(link, 0);
            var path = new StringBuilder(1024);
            shell.GetPath(path, path.Capacity, IntPtr.Zero, 0);
            var arguments = new StringBuilder(2048);
            shell.GetArguments(arguments, arguments.Capacity);
            return (path.Length == 0 ? null : path.ToString(), arguments.ToString());
        }
        finally { Marshal.FinalReleaseComObject(shell); }
    }

    [ComImport, Guid("00021401-0000-0000-C000-000000000046")]
    private class ShellLink { }

    // Порядок методов — как в таблице интерфейса; объявлены только нужные и предшествующие им.
    [ComImport, InterfaceType(ComInterfaceType.InterfaceIsIUnknown), Guid("000214F9-0000-0000-C000-000000000046")]
    private interface IShellLinkW
    {
        void GetPath([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder file, int size, IntPtr data, uint flags);
        void GetIDList(out IntPtr list);
        void SetIDList(IntPtr list);
        void GetDescription([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder name, int size);
        void SetDescription([MarshalAs(UnmanagedType.LPWStr)] string name);
        void GetWorkingDirectory([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder dir, int size);
        void SetWorkingDirectory([MarshalAs(UnmanagedType.LPWStr)] string dir);
        void GetArguments([Out, MarshalAs(UnmanagedType.LPWStr)] StringBuilder args, int size);
    }
}
