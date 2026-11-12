using System.Security.Cryptography;
using System.Text;

namespace Oblachka;

// Независимый запрет на время замены аренды. Не меняет пользовательский Lockdown
// и строгую защиту сервисов. При аварии остаётся до восстановления или явного Stop.
internal static class WfpHandover
{
    internal static readonly Guid Provider = new("cd540c56-e91a-44c9-8b81-9fb1ca50ec01");
    internal static readonly Guid Sublayer = new("cd540c56-e91a-44c9-8b81-9fb1ca50ec02");
    internal static Guid Key(int index) => new(SHA256.HashData(Encoding.ASCII.GetBytes("oblachka:handover:" + index)).AsSpan(0, 16));
    internal static IReadOnlyList<WfpFilter> Filters() => WfpLockdown.Filters(WfpLockdown.StrictPrograms(AppContext.BaseDirectory), false, true)
        .Select((filter, i) => filter with { Key = Key(i), Name = ServiceBrand.Name + " — переключение сервера", Persistent = true }).ToArray();
    internal static void Apply(IWfpEngine engine)
    {
        engine.Begin();
        try
        {
            Delete(engine);
            engine.AddProvider(Provider, ServiceBrand.Name + " — переключение", persistent: true);
            engine.AddSublayer(Sublayer, Provider, ServiceBrand.Name + " — переключение", WfpLockdown.StrictSublayerWeight, persistent: true);
            foreach (var filter in Filters()) engine.AddFilter(filter, Provider, Sublayer);
            engine.Commit();
        }
        catch { engine.Abort(); throw; }
        if (!engine.SublayerExists(Sublayer) || Filters().Any(f => !engine.FilterExists(f.Key)))
            throw new IOException("Не удалось удержать блокировку для переключения сервера.");
    }
    internal static void Remove(IWfpEngine engine)
    {
        if (!engine.SublayerExists(Sublayer)) return;
        engine.Begin();
        try { Delete(engine); engine.Commit(); }
        catch { engine.Abort(); throw; }
        if (engine.SublayerExists(Sublayer)) throw new IOException("Блокировка переключения не снята.");
    }
    private static void Delete(IWfpEngine engine)
    {
        if (!engine.SublayerExists(Sublayer)) return;
        for (var i = 0; i < WfpLockdown.MaxFilters; i++) engine.DeleteFilter(Key(i));
        engine.DeleteSublayer(Sublayer); engine.DeleteProvider(Provider);
    }
}
