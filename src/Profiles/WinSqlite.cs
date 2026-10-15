using System.Runtime.InteropServices;
using System.Text;

namespace Oblachka;

// Чтение чужих баз SQLite (Throne, Hiddify, v2rayN) через winsqlite3.dll, который входит
// в Windows 10 и 11: своя копия SQLite и пакеты NuGet для этого не нужны.
//
// База копируется во временную папку вместе с журналом -wal: свежие записи клиента часто
// лежат только в журнале, а открывать оригинал, пока клиент работает, не стоит.
public static class WinSqlite
{
    private const string Library = "winsqlite3.dll";
    private const int Ok = 0, Row = 100, OpenReadWrite = 0x2;

    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_open_v2(byte[] filename, out IntPtr db, int flags, IntPtr vfs);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_close_v2(IntPtr db);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_prepare_v2(IntPtr db, byte[] sql, int bytes, out IntPtr statement, IntPtr tail);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_step(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_finalize(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_column_count(IntPtr statement);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern IntPtr sqlite3_column_text(IntPtr statement, int column);
    [DllImport(Library, CallingConvention = CallingConvention.StdCall)] private static extern int sqlite3_column_bytes(IntPtr statement, int column);

    // Строки результата как текст; null — базы нет, она не открылась или запрос не подошёл.
    public static IReadOnlyList<string?[]>? Query(string databasePath, string sql, int maxRows = 64)
    {
        if (!File.Exists(databasePath)) return null;
        var folder = Path.Combine(Path.GetTempPath(), "oblachka-import-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(folder);
            var copy = Path.Combine(folder, "source.db");
            CopyShared(databasePath, copy);
            if (File.Exists(databasePath + "-wal")) CopyShared(databasePath + "-wal", copy + "-wal");
            return Read(copy, sql, maxRows);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or DllNotFoundException or EntryPointNotFoundException) { return null; }
        finally { try { Directory.Delete(folder, true); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    private static void CopyShared(string source, string target)
    {
        using var input = new FileStream(source, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var output = File.Create(target);
        input.CopyTo(output);
    }

    private static IReadOnlyList<string?[]>? Read(string path, string sql, int maxRows)
    {
        if (sqlite3_open_v2(Utf8(path), out var db, OpenReadWrite, IntPtr.Zero) != Ok) { sqlite3_close_v2(db); return null; }
        try
        {
            if (sqlite3_prepare_v2(db, Utf8(sql), -1, out var statement, IntPtr.Zero) != Ok) return null;
            try
            {
                var rows = new List<string?[]>();
                var columns = sqlite3_column_count(statement);
                while (rows.Count < maxRows && sqlite3_step(statement) == Row)
                {
                    var row = new string?[columns];
                    for (var i = 0; i < columns; i++)
                    {
                        var text = sqlite3_column_text(statement, i);
                        row[i] = text == IntPtr.Zero ? null : Marshal.PtrToStringUTF8(text, sqlite3_column_bytes(statement, i));
                    }
                    rows.Add(row);
                }
                return rows;
            }
            finally { sqlite3_finalize(statement); }
        }
        finally { sqlite3_close_v2(db); }
    }

    private static byte[] Utf8(string text) => Encoding.UTF8.GetBytes(text + "\0");
}
