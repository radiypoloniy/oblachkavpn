using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Serialization;

namespace Oblachka;

public sealed record ClientServerChoice(
    [property: JsonRequired] string Id, [property: JsonRequired] string Name,
    [property: JsonRequired] bool Selected, [property: JsonRequired] bool Running, [property: JsonRequired] bool Free);
public sealed record ClientServerPage(
    [property: JsonRequired] int Offset, [property: JsonRequired] int Total,
    [property: JsonRequired] ClientServerChoice[] Items);

// Только идентификаторы и подписи. Конфигурации и адреса подписок остаются у владельца.
public static class ClientServerCatalog
{
    public const int PageSize = 32;
    public static string? Id(Profile? profile) => profile is null ? null : Convert.ToHexString(
        SHA256.HashData(Encoding.UTF8.GetBytes(ProfileSelection.Identity(profile))));
    internal static bool ValidId(string? id) => id is { Length: 64 } && id.All(Uri.IsHexDigit);
    internal static string Title(Profile profile) => profile.DisplayName is { } name && name.Length > 256 ? name[..256] : profile.DisplayName;
    internal static Profile? Find(ClientRuntime owner, string id) => owner.Profiles.Imported.FirstOrDefault(
        p => string.Equals(Id(p), id, StringComparison.OrdinalIgnoreCase));
    internal static ClientServerPage Page(ClientRuntime owner, int offset)
    {
        var data = owner.Profiles; offset = Math.Min(offset, data.Imported.Count);
        var selected = Id(data.Selected); var running = Id(data.Running);
        return new(offset, data.Imported.Count, data.Imported.Skip(offset).Take(PageSize).Select(p =>
        {
            var id = Id(p)!;
            return new ClientServerChoice(id, Title(p), id == selected, id == running, p.IsFree);
        }).ToArray());
    }
}
