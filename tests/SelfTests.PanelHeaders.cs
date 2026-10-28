namespace Oblachka;

public static partial class SelfTests
{
    // Заголовки панели Remnawave: срок и трафик, название, объявление, период обновления.
    // Значения приходят от сервера, поэтому проверяется и разбор, и очистка текста.
    private static async Task PanelHeaderChecksAsync(Func<string, Func<Task>, Task> check, string root)
    {
        await check("Panel text headers decode base64 and drop control characters", () => Sync(() =>
        {
            // «Облачка🌤» и объявление сервиса — ровно то, что отдаёт панель.
            Assert(SubscriptionMetadata.DecodeText("base64:0J7QsdC70LDRh9C60LDwn4yk") == "Облачка🌤");
            Assert(SubscriptionMetadata.DecodeText("Обычный текст") == "Обычный текст");
            Assert(SubscriptionMetadata.DecodeText("  Текст с пробелами  ") == "Текст с пробелами");
            Assert(SubscriptionMetadata.DecodeText(null) is null && SubscriptionMetadata.DecodeText("   ") is null);
            Assert(SubscriptionMetadata.DecodeText("base64:не-base64!") is null);
            // Управляющие символы и перевод строки в интерфейс не попадают.
            Assert(SubscriptionMetadata.DecodeText("Строка\r\nвторая") == "Строкавторая");
            // Длина ограничена: заголовок не растянет окно.
            Assert(SubscriptionMetadata.DecodeText(new string('я', 1000))!.Length == SubscriptionMetadata.MaxTextLength);
            Assert(SubscriptionMetadata.DecodeText(new string('я', 5000)) is null);
        }));
        await check("Update interval is taken from the panel within sane bounds", () => Sync(() =>
        {
            // Часы, как у Happ: у подписки сервиса profile-update-interval равен 3 — раз в 3 часа.
            Assert(SubscriptionMetadata.ParseUpdateInterval("3") == TimeSpan.FromHours(3));
            Assert(SubscriptionMetadata.ParseUpdateInterval("168") == TimeSpan.FromDays(7) && SubscriptionMetadata.ParseUpdateInterval("169") is null);
            Assert(SubscriptionMetadata.ParseUpdateInterval("0.5") == SubscriptionMetadata.MinUpdateInterval);
            // Слишком частое обновление поднимается до минимума, бессмысленное отбрасывается.
            Assert(SubscriptionMetadata.ParseUpdateInterval("0.001") == SubscriptionMetadata.MinUpdateInterval);
            Assert(SubscriptionMetadata.ParseUpdateInterval("0") is null);
            Assert(SubscriptionMetadata.ParseUpdateInterval("-1") is null);
            Assert(SubscriptionMetadata.ParseUpdateInterval("400") is null);
            Assert(SubscriptionMetadata.ParseUpdateInterval("каждый день") is null);
            Assert(SubscriptionMetadata.ParseUpdateInterval(null) is null);

            var schedule = new SubscriptionSchedule();
            var now = DateTimeOffset.UtcNow;
            schedule.Complete(now, true, TimeSpan.FromDays(3));
            Assert(!schedule.IsDue(now.AddDays(2)) && schedule.IsDue(now.AddDays(3).AddMinutes(1)));
            // Без заголовка остаётся прежний период в шесть часов.
            schedule.Complete(now, true);
            Assert(!schedule.IsDue(now.AddHours(5)) && schedule.IsDue(now.AddHours(7)));
        }));
        await check("Unlimited traffic is not shown as an exhausted plan", () => Sync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            // total=0 у Remnawave означает безлимит.
            var unlimited = new SubscriptionMetadata(0, 1403900, 0, now.AddYears(1));
            Assert(unlimited.Summary(now).Contains("без ограничения"));
            Assert(!unlimited.Summary(now).Contains("Осталось"));
            var limited = new SubscriptionMetadata(0, 0, 107374182400, now.AddYears(1));
            Assert(limited.Summary(now).Contains("Осталось 100 ГБ"));
        }));
        await check("Short plan summary tells the user how much time is left", () => Sync(() =>
        {
            var now = DateTimeOffset.UtcNow;
            Assert(new SubscriptionMetadata(null, null, null, now.AddDays(-1)).ShortSummary(now) == "Подписка истекла");
            Assert(new SubscriptionMetadata(null, null, null, now.AddHours(5)).ShortSummary(now) == "Подписка истекает сегодня");
            Assert(new SubscriptionMetadata(null, null, null, now.AddDays(5)).ShortSummary(now) == "Подписка ещё 5 дн.");
            Assert(new SubscriptionMetadata(null, null, null, now.AddYears(1)).ShortSummary(now).StartsWith("Подписка до "));
        }));
        await check("Saved state rejects oversized or control-character panel text", async () =>
        {
            var store = new ClientStateStore(Path.Combine(root, "headers-state.bin"));
            var saved = new[] { new SavedProfile("Узел", (System.Text.Json.Nodes.JsonObject)Profiles.Parse(link).Outbound.DeepClone()) };
            ClientSnapshot With(SubscriptionMetadata meta) =>
                new(1, saved, null, Array.Empty<string>(), 0, false, false) { Metadata = meta };
            store.Save(With(new SubscriptionMetadata(null, null, null, null) { Title = "Облачка", Announce = "Привет", UpdateInterval = TimeSpan.FromDays(3) }));
            var restored = store.Load()!;
            Assert(restored.Metadata!.Title == "Облачка" && restored.Metadata.UpdateInterval == TimeSpan.FromDays(3));
            ExpectUserError(() => store.Save(With(new SubscriptionMetadata(null, null, null, null) { Announce = new string('я', 400) })));
            ExpectUserError(() => store.Save(With(new SubscriptionMetadata(null, null, null, null) { Title = "Строка\nвторая" })));
            ExpectUserError(() => store.Save(With(new SubscriptionMetadata(null, null, null, null) { UpdateInterval = TimeSpan.FromDays(30) })));
            await Task.CompletedTask;
        });
    }
}
