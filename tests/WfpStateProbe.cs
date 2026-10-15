using System.Diagnostics;
using System.Xml.Linq;

namespace Oblachka;

// Что Windows видит из нашей защиты. netsh называет слои и условия символическими
// именами только тогда, когда ключ ему известен, — так проверяется, что ключи из
// fwpmu.h переписаны в WfpEngine верно. Пустой список после остановки — что снято всё.
internal static class WfpStateProbe
{
    internal static string Describe()
    {
        var file = Path.Combine(Path.GetTempPath(), "oblachka-wfp-" + Guid.NewGuid().ToString("N") + ".xml");
        try
        {
            var info = new ProcessStartInfo("netsh") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
            foreach (var a in new[] { "wfp", "show", "filters", "file=" + file }) info.ArgumentList.Add(a);
            using (var p = Process.Start(info)!) { p.StandardOutput.ReadToEnd(); p.WaitForExit(30000); }
            var ours = XDocument.Load(file).Descendants("item")
                .Where(i => (string?)i.Element("subLayerKey") is { } key &&
                    key.Contains(WfpProtectionBackend.SublayerKey.ToString(), StringComparison.OrdinalIgnoreCase)).ToArray();
            if (ours.Length == 0) return "наших фильтров WFP: 0";
            // Тип интерфейса туннеля: фильтр пакетов полагается на то, что это 53.
            var types = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.Name.StartsWith(WindowsTunnelBackend.InterfacePrefix, StringComparison.Ordinal) &&
                    n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up)
                .Select(n => (int)n.NetworkInterfaceType).Distinct();
            var layers = ours.Select(i => (string?)i.Element("layerKey")).Distinct().OrderBy(x => x);
            var fields = ours.SelectMany(i => i.Descendants("fieldKey")).Select(f => f.Value).Distinct().OrderBy(x => x);
            var actions = ours.Select(i => (string?)i.Element("action")?.Element("type")).GroupBy(x => x).Select(g => g.Key + "×" + g.Count());
            return "наших фильтров WFP: " + ours.Length + "; слои: " + string.Join(", ", layers) +
                "; условия: " + string.Join(", ", fields) + "; действия: " + string.Join(", ", actions) +
                "; тип интерфейса туннеля: " + string.Join(",", types);
        }
        catch (Exception ex) { return "фильтры WFP прочитать не удалось (" + ex.GetType().Name + ")"; }
        finally { try { File.Delete(file); } catch (IOException) { } }
    }
}
