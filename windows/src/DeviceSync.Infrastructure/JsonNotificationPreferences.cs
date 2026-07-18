using System.Text.Json;
using DeviceSync.Application;

namespace DeviceSync.Infrastructure;

public sealed class JsonNotificationPreferences : INotificationPreferences
{
    private readonly object _gate = new();
    private readonly string _path;
    private State _state;

    public JsonNotificationPreferences()
        : this(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "DeviceSync",
            "notification-settings.json"))
    {
    }

    internal JsonNotificationPreferences(string path)
    {
        _path = path;
        _state = Load(path);
    }

    public bool ToastsEnabled
    {
        get { lock (_gate) return _state.ToastsEnabled; }
        set => Mutate(state => state with { ToastsEnabled = value });
    }

    public bool HideSensitiveWhenLocked
    {
        get { lock (_gate) return _state.HideSensitiveWhenLocked; }
        set => Mutate(state => state with { HideSensitiveWhenLocked = value });
    }

    public TimeOnly? QuietHoursStart
    {
        get { lock (_gate) return Parse(_state.QuietHoursStart); }
        set => Mutate(state => state with { QuietHoursStart = value?.ToString("HH:mm") });
    }

    public TimeOnly? QuietHoursEnd
    {
        get { lock (_gate) return Parse(_state.QuietHoursEnd); }
        set => Mutate(state => state with { QuietHoursEnd = value?.ToString("HH:mm") });
    }

    public IReadOnlySet<string> BlockedPackages
    {
        get { lock (_gate) return _state.BlockedPackages.ToHashSet(StringComparer.Ordinal); }
    }

    public bool IsAppAllowed(string packageName)
    {
        lock (_gate) return !_state.BlockedPackages.Contains(packageName, StringComparer.Ordinal);
    }

    public void SetAppAllowed(string packageName, bool allowed)
    {
        Mutate(state =>
        {
            var blocked = state.BlockedPackages.ToHashSet(StringComparer.Ordinal);
            if (allowed) blocked.Remove(packageName);
            else blocked.Add(packageName);
            return state with { BlockedPackages = blocked.Order(StringComparer.Ordinal).ToArray() };
        });
    }

    private void Mutate(Func<State, State> update)
    {
        lock (_gate)
        {
            _state = update(_state);
            Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
            var temporary = _path + ".tmp";
            File.WriteAllText(temporary, JsonSerializer.Serialize(_state));
            File.Move(temporary, _path, true);
        }
    }

    private static State Load(string path)
    {
        try
        {
            return File.Exists(path)
                ? JsonSerializer.Deserialize<State>(File.ReadAllText(path)) ?? new()
                : new();
        }
        catch (JsonException)
        {
            return new();
        }
    }

    private static TimeOnly? Parse(string? value) =>
        TimeOnly.TryParseExact(value, "HH:mm", out var parsed) ? parsed : null;

    private sealed record State
    {
        public bool ToastsEnabled { get; init; } = true;
        public bool HideSensitiveWhenLocked { get; init; } = true;
        public string? QuietHoursStart { get; init; }
        public string? QuietHoursEnd { get; init; }
        public IReadOnlyList<string> BlockedPackages { get; init; } = [];
    }
}
