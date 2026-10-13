using System.Reflection;
using System.Runtime.InteropServices;

namespace Oblachka;

public enum FirewallAction { Block = 0, Allow = 1 }

// Одно разрешение. Протокол 256 означает «любой», и при нём порты задавать нельзя.
public sealed record FirewallAllowRule(
    string Name, int Protocol = WindowsFirewallPolicy.AnyProtocol,
    string? LocalAddresses = null, string? RemoteAddresses = null,
    string? LocalPorts = null, string? RemotePorts = null);

// Брандмауэр, каким его видит kill switch. Отдельный интерфейс нужен затем, чтобы
// проверки шли на подменном брандмауэре, а не на сети компьютера.
public interface IFirewallPolicy
{
    bool IsEnabled(int profile);
    FirewallAction OutboundAction(int profile);
    void SetOutboundAction(int profile, FirewallAction action);
    void AddAllowRule(FirewallAllowRule rule);
    bool RuleExists(string name);
    void RemoveRule(string name);
}

// Брандмауэр Windows через COM-объект HNetCfg.FwPolicy2.
//
// Взят COM, а не netsh: вывод netsh локализован, и разбор его строк на русской Windows
// держался бы на переводе. COM отдаёт значения числами.
//
// Вызовы идут через InvokeMember, без ссылки на библиотеку типов: у FwPolicy2 часть
// свойств параметризована профилем, и такой вызов — единственный способ обратиться к ним
// без генерации обёрток на сборке.
public sealed class WindowsFirewallPolicy : IFirewallPolicy
{
    public const int AnyProtocol = 256;
    public const int Udp = 17;
    // Профили: домен, частный, публичный. Трогаются все три: профиль меняется вместе с
    // сетью, и смена сети посреди сессии не должна открывать трафик.
    public static readonly int[] AllProfiles = { 1, 2, 4 };

    private const int DirectionOutbound = 2;
    private const int ActionAllow = 1;
    private const int ProfileAll = 0x7fffffff;

    private readonly object policy;
    private readonly Type type;

    private WindowsFirewallPolicy(object instance)
    {
        policy = instance;
        type = instance.GetType();
    }

    public static WindowsFirewallPolicy Open()
    {
        var progId = Type.GetTypeFromProgID("HNetCfg.FwPolicy2", throwOnError: false)
            ?? throw new UserError("Брандмауэр Windows недоступен: блокировать трафик мимо VPN нечем.");
        object instance;
        try { instance = Activator.CreateInstance(progId) ?? throw new UserError("Брандмауэр Windows не открылся."); }
        catch (COMException) { throw new UserError("Брандмауэр Windows не открылся. Проверьте, запущена ли его служба."); }
        return new WindowsFirewallPolicy(instance);
    }

    public bool IsEnabled(int profile) => Convert.ToBoolean(Read("FirewallEnabled", profile));

    public FirewallAction OutboundAction(int profile)
    {
        var raw = Convert.ToInt32(Read("DefaultOutboundAction", profile));
        if (!Enum.IsDefined(typeof(FirewallAction), raw))
            throw new IOException("Брандмауэр вернул неизвестное исходящее действие: " + raw);
        return (FirewallAction)raw;
    }

    public void SetOutboundAction(int profile, FirewallAction action) =>
        Write("DefaultOutboundAction", profile, (int)action);

    public void AddAllowRule(FirewallAllowRule rule)
    {
        var ruleType = Type.GetTypeFromProgID("HNetCfg.FWRule", throwOnError: false)
            ?? throw new UserError("Брандмауэр Windows не позволяет создать правило.");
        var created = Activator.CreateInstance(ruleType) ?? throw new UserError("Правило брандмауэра не создалось.");
        Guarded(() =>
        {
            SetOn(created, "Name", rule.Name);
            SetOn(created, "Description", ServiceBrand.Name + ": разрешение на время работы системного режима.");
            SetOn(created, "Direction", DirectionOutbound);
            SetOn(created, "Action", ActionAllow);
            SetOn(created, "Protocol", rule.Protocol);
            // Адреса и порты задаются только заданные: пустая строка для COM — не «любой».
            if (rule.LocalAddresses is not null) SetOn(created, "LocalAddresses", rule.LocalAddresses);
            if (rule.RemoteAddresses is not null) SetOn(created, "RemoteAddresses", rule.RemoteAddresses);
            if (rule.LocalPorts is not null) SetOn(created, "LocalPorts", rule.LocalPorts);
            if (rule.RemotePorts is not null) SetOn(created, "RemotePorts", rule.RemotePorts);
            SetOn(created, "Profiles", ProfileAll);
            SetOn(created, "Enabled", true);
            var rules = Read("Rules") ?? throw new IOException("Брандмауэр не отдал список правил.");
            Call(rules, "Add", created);
        });
    }

    public bool RuleExists(string name)
    {
        var rules = Read("Rules") ?? throw new IOException("Брандмауэр не отдал список правил.");
        try
        {
            return Call(rules, "Item", name) is not null;
        }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException or ArgumentException)
        {
            // Отсутствующее правило COM отдаёт исключением, а не пустым значением.
            return false;
        }
    }

    public void RemoveRule(string name)
    {
        var rules = Read("Rules") ?? throw new IOException("Брандмауэр не отдал список правил.");
        try { Call(rules, "Remove", name); }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException or ArgumentException)
        {
            // Удалять нечего — это и есть нужный итог.
        }
    }

    private object? Read(string name, params object[] args) =>
        Guarded(() => type.InvokeMember(name, BindingFlags.GetProperty, null, policy, args));

    private void Write(string name, params object[] args) =>
        Guarded(() => type.InvokeMember(name, BindingFlags.SetProperty, null, policy, args));

    private static void SetOn(object target, string name, object value) =>
        target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, new[] { value });

    private static object? Call(object target, string name, params object[] args) =>
        target.GetType().InvokeMember(name, BindingFlags.InvokeMethod | BindingFlags.GetProperty, null, target, args);

    // Отказы COM приводятся к тем типам, которые сессия считает ожидаемыми: иначе
    // восстановление не отработает, а журнал останется без отметки.
    private static T Guarded<T>(Func<T> action)
    {
        try { return action(); }
        catch (TargetInvocationException ex) when (ex.InnerException is UnauthorizedAccessException inner)
        { throw new UnauthorizedAccessException(inner.Message, inner); }
        catch (TargetInvocationException ex) when (ex.InnerException is COMException inner)
        { throw new IOException("Брандмауэр Windows отказал в операции.", inner); }
        catch (COMException ex) { throw new IOException("Брандмауэр Windows отказал в операции.", ex); }
    }

    private static void Guarded(Action action) => Guarded<object?>(() => { action(); return null; });
}
