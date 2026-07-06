namespace DeviceSync.Application;

public sealed class DeviceSessionRegistry
{
    private readonly object _gate = new();
    private DeviceSessionInfo? _activeSession;

    public DeviceSessionInfo? ActiveSession
    {
        get
        {
            lock (_gate)
            {
                return _activeSession;
            }
        }
    }

    public bool TryReplaceOrAdd(DeviceSessionInfo session, out DeviceSessionInfo? replacedSession)
    {
        lock (_gate)
        {
            if (_activeSession is not null && _activeSession.DeviceId != session.DeviceId)
            {
                replacedSession = null;
                return false;
            }

            replacedSession = _activeSession;
            _activeSession = session;
            return true;
        }
    }

    public bool Remove(string deviceId)
    {
        lock (_gate)
        {
            if (_activeSession?.DeviceId != deviceId)
            {
                return false;
            }

            _activeSession = null;
            return true;
        }
    }

    public bool Remove(DeviceSessionInfo session)
    {
        lock (_gate)
        {
            if (_activeSession != session)
            {
                return false;
            }

            _activeSession = null;
            return true;
        }
    }

    public void Clear()
    {
        lock (_gate)
        {
            _activeSession = null;
        }
    }
}
