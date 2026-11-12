namespace Oblachka;

public sealed partial class NetworkHelperClient
{
    public async Task<bool> SupportsHandoverAsync(CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var response = await pipe.SendAsync(HelperOperation.Heartbeat, null, token, version: 2).ConfigureAwait(false);
            return response.Failure == HelperFailure.None && response.Blocking == true;
        }
        finally { operations.Release(); }
    }
    public async Task SetHandoverAsync(bool on, CancellationToken token)
    {
        await operations.WaitAsync(token).ConfigureAwait(false);
        try
        {
            var response = await pipe.SendAsync(HelperOperation.Handover, null, token, lockdown: new(on, false)).ConfigureAwait(false);
            if (response.Failure != HelperFailure.None) throw new UserError("Служба не подтвердила блокировку переключения. Обновите сетевую службу.");
        }
        finally { operations.Release(); }
    }
}
