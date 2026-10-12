namespace Oblachka;

public partial class MainWindow
{
    private async Task CheckRememberedSubscriptionAsync(List<string> checks)
    {
        var root = Path.Combine(AppContext.BaseDirectory, "test-data", Guid.NewGuid().ToString("N"));
        var path = Path.Combine(root, "remembered.bin");
        var store = new ClientStateStore(path);
        var fresh = new MainWindow(false, store);
        try
        {
            var imported = Subscriptions.Parse("vless://11111111-1111-4111-8111-111111111111@example.invalid:443?security=tls#%F0%9F%87%AA%F0%9F%87%BA%D0%90%D0%B2%D1%82%D0%BE");
            // Адрес сервиса: именно этот путь проходит пользователь, вводя ключ подписки.
            var sources = new[] { "https://" + ServiceBrand.SubscriptionHosts[0] + ServiceBrand.SubscriptionPath + "remembered-token" };
            await fresh.LoadSubscriptionAsync(sources, 0, (_, _, _) => Task.FromResult(new MirrorResult(imported, 0, 1)));
            fresh.FlushStateSave();
            if (!File.Exists(path)) throw new InvalidOperationException("Subscription requires a separate manual save");
            var reloaded = new MainWindow(false, new ClientStateStore(path));
            try
            {
                reloaded.RestoreSavedState();
                if (reloaded.profile?.DisplayName != "Авто" || !reloaded.subscriptionSources.SequenceEqual(sources) ||
                    reloaded.profile is not { IsService: true } ||
                    reloaded.Onboarding.Visibility == System.Windows.Visibility.Visible || reloaded.engine.ProcessId is not null)
                    throw new InvalidOperationException("Next launch requires the subscription again or starts the core automatically");
            }
            finally { reloaded.Close(); }
            var temporaryPath = Path.Combine(root, "not-remembered.bin");
            var temporary = new MainWindow(false, new ClientStateStore(temporaryPath));
            try
            {
                temporary.Onboarding.RememberSubscription.IsChecked = false;
                await temporary.LoadSubscriptionAsync(sources, 0, (_, _, _) => Task.FromResult(new MirrorResult(imported, 0, 1)));
                temporary.FlushStateSave();
                if (File.Exists(temporaryPath)) throw new InvalidOperationException("Opt-out still saves subscription secrets");
            }
            finally { temporary.Close(); }
            checks.Add("Adding a subscription remembers it without an extra save click; next launch restores it and opt-out writes no state");
        }
        finally { fresh.Close(); }
    }
}
