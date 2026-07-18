using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class TransportSessionCoordinator
{
    private readonly object _gate = new();
    private ClientSession? _active;

    public bool TryActivate(ClientSession candidate, out ClientSession? replaced)
    {
        lock (_gate)
        {
            replaced = null;
            if (_active is not null && _active.DeviceId != candidate.DeviceId) return false;
            if (_active is not null)
            {
                var currentPriority = DeviceTransportProfile.For(_active.TransportKind).Priority;
                var candidatePriority = DeviceTransportProfile.For(candidate.TransportKind).Priority;
                if (candidatePriority < currentPriority) return false;
                replaced = _active;
            }
            _active = candidate;
            return true;
        }
    }

    public void Release(ClientSession candidate)
    {
        lock (_gate)
        {
            if (ReferenceEquals(_active, candidate)) _active = null;
        }
    }

    public ClientSession? Active
    {
        get { lock (_gate) return _active; }
    }
}
