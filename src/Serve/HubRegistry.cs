using System.Collections.Concurrent;

namespace KY.AI.Serve;

// A registered dev-server supervisor: its project name and the loopback URL of its
// local REST control API.
internal sealed record Registration(string Name, string ControlUrl);

// Body of the hub register/deregister requests.
internal sealed record RegisterRequest(string Name, string ControlUrl);

// The hub's in-memory registry of running supervisors, keyed by project name.
internal sealed class HubRegistry
{
    private readonly ConcurrentDictionary<string, Registration> _map = new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(Registration r) => _map[r.Name] = r;
    public void Remove(string name) => _map.TryRemove(name, out _);
    public Registration? Get(string name) => _map.TryGetValue(name, out var r) ? r : null;
    public IReadOnlyCollection<Registration> All() => _map.Values.ToList();
}
