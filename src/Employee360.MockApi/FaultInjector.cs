namespace Employee360.MockApi;

/// <param name="FailNext">How many upcoming /api requests to interfere with.</param>
/// <param name="StatusCode">Status to return; null means "delay only, then succeed".</param>
/// <param name="DelayMs">Delay before responding, used to trigger client timeouts.</param>
public sealed record FaultSettings(int FailNext, int? StatusCode, int DelayMs = 0);

public readonly record struct Fault(int? StatusCode, int DelayMs);

public sealed class FaultInjector
{
    private readonly object _gate = new();
    private FaultSettings _settings = new(0, null, 0);
    private int _remaining;

    public void Set(FaultSettings settings)
    {
        lock (_gate)
        {
            _settings = settings;
            _remaining = Math.Max(0, settings.FailNext);
        }
    }

    public bool TryTakeFault(out Fault fault)
    {
        lock (_gate)
        {
            if (_remaining <= 0)
            {
                fault = default;
                return false;
            }

            _remaining--;
            fault = new Fault(_settings.StatusCode, _settings.DelayMs);
            return true;
        }
    }

    public object Snapshot()
    {
        lock (_gate)
        {
            return new { remaining = _remaining, statusCode = _settings.StatusCode, delayMs = _settings.DelayMs };
        }
    }
}
