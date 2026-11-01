using System.Diagnostics;

namespace Oblachka;

internal static class BinaryRuleSets
{
    internal static void Compile(string folder, string sourceJson, string outputPath)
    {
        var executable = Path.Combine(AppContext.BaseDirectory, "tools", "sing-box.exe");
        var sourcePath = outputPath + ".source-" + Guid.NewGuid().ToString("N") + ".json";
        var temporaryPath = outputPath + ".tmp-" + Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(folder);
        try
        {
            File.WriteAllText(sourcePath, sourceJson);
            var start = new ProcessStartInfo(executable)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(executable)!
            };
            start.ArgumentList.Add("rule-set");
            start.ArgumentList.Add("compile");
            start.ArgumentList.Add("--output");
            start.ArgumentList.Add(temporaryPath);
            start.ArgumentList.Add(sourcePath);
            using var process = new Process { StartInfo = start };
            using var job = new ProcessJob();
            process.Start();
            job.Attach(process);
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(15000))
            {
                process.Kill(true);
                process.WaitForExit();
                throw new UserError("Не удалось подготовить списки маршрутизации за 15 секунд.");
            }
            Task.WhenAll(output, error).GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !Valid(temporaryPath))
                throw new UserError("Не удалось подготовить списки маршрутизации для ядра.");
            File.Move(temporaryPath, outputPath, true);
        }
        finally
        {
            TryDelete(sourcePath);
            TryDelete(temporaryPath);
        }
    }

    private static bool Valid(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.Length > 4 && stream.ReadByte() == 'S' && stream.ReadByte() == 'R' && stream.ReadByte() == 'S' && stream.ReadByte() > 0;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return false; }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
