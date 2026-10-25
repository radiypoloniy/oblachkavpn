using System.Text.Json;
using System.Text.Json.Nodes;

namespace Oblachka;

// Редактор своего сервера («Для продвинутых», П2): настройка подключения («Вручную») — JSON
// outbound, как его видит ядро. Правка проходит тот же разбор, что импорт (белые списки полей,
// обязательный TLS), и проверку ядром — сломанная настройка не сохраняется.
public partial class MainWindow
{
    private static readonly JsonSerializerOptions Indented = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private async Task EditServerAsync(Profile item)
    {
        if (operation is not null || item.Group != ProfileGroup.ManualId) return;
        var shown = (JsonObject)item.Outbound.DeepClone();
        shown.Remove("tag");
        var text = AppDialog.Ask(this, "Настройка «" + item.DisplayName + "»", "Подключение в формате " + (item.Core == CoreKind.SingBox ? "sing-box" : "Xray") +
            ". Перед сохранением настройку проверит ядро.", "", "Сохранить", shown.ToJsonString(Indented), multiline: true, maxLength: Profiles.MaxBytes, height: 300);
        if (text is null) return;
        operation = new CancellationTokenSource(); UpdateControls();
        try
        {
            var edited = ParseEdited(text, item);
            var adapter = new CoreAdapter(edited.Core, Path.Combine(AppContext.BaseDirectory, "tools", CoreAdapter.FileFor(edited)));
            await CoreConfigurationCheck.ValidateAsync(adapter, RoutingPlan.Compile(adapter, edited, 10808, []), operation.Token);
            if (closed) return;
            var manual = ProfilesOf(ProfileGroup.ManualId).Select(p => ReferenceEquals(p, item) ? edited : p).ToArray();
            SetGroup(groups.First(g => g.IsManual), manual);
            if (profile is not null && ReferenceEquals(profile, item)) profile = manual.First(p => p.Name == edited.Name);
            AfterServerListChange(false);
            DetailText.Text = runningProfile is not null && FavoriteKey(runningProfile) == FavoriteKey(item)
                ? "Настройка сохранена. Она вступит в силу при следующем подключении." : "Настройка «" + edited.DisplayName + "» сохранена.";
        }
        catch (OperationCanceledException) { }
        catch (UserError ex) { if (!closed) DetailText.Text = "Настройка не сохранена: " + ex.Message; }
        finally { operation.Dispose(); operation = null; if (!closed) UpdateControls(); }
    }

    // Тот же разбор, что у импорта; имя и группа — прежние.
    internal static Profile ParseEdited(string text, Profile item)
    {
        try
        {
            var outbound = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip }) as JsonObject
                ?? throw new UserError("Нужен один JSON-объект подключения.");
            if (outbound["type"] is not null != (item.Core == CoreKind.SingBox))
                throw new UserError("Формат подключения сменился: добавьте его как новый сервер.");
            var parsed = item.Core == CoreKind.SingBox ? SingBoxProfiles.Parse(outbound, item.Name) : Profiles.FromOutbound(outbound, item.Name);
            return parsed with { Name = item.Name, Source = item.Source, Group = item.Group };
        }
        // Повтор ключа JsonNode замечает только при обходе — ArgumentException, а не JsonException.
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or FormatException)
        { throw new UserError("JSON не разобран — проверьте скобки, запятые и повторы ключей."); }
    }
}
