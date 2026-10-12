namespace Oblachka;

// Дочерние процессы проверок запускает сам тестовый исполняемый файл, а не поставляемый клиент:
// точки входа --job-child, --helper-fixture и --helper-idle-fixture живут только здесь.
internal static class TestHost
{
    internal static string Executable => Path.Combine(AppContext.BaseDirectory, "Oblachka.Tests.exe");
}
