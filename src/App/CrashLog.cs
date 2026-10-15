namespace Oblachka;

// Последняя внутренняя ошибка окна: тип и стек вызовов, без текста сообщения — в нём могли бы
// оказаться ключ или адреса. Без этого аварийное завершение у пользователя не разобрать.
public static class CrashLog
{
    public static string PathName => Path.Combine(ClientDataPaths.Root, "crash.txt");

    public static void Write(Exception error)
    {
        try
        {
            Directory.CreateDirectory(ClientDataPaths.Root);
            var lines = new List<string> { DateTimeOffset.Now.ToString("u") + " " + BuildIdentity.Label };
            for (var e = error; e is not null; e = e.InnerException)
                lines.Add(e.GetType().FullName + Environment.NewLine + e.StackTrace);
            File.WriteAllLines(PathName, lines);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
