using System.Collections.Concurrent;

namespace KY.AI.Serve;

// A registered dev-server supervisor: its project name and the loopback URL of its
// local REST control API.
internal sealed record Registration(string Name, string ControlUrl);

// Body of the hub register/deregister requests.
internal sealed record RegisterRequest(string Name, string ControlUrl);

// Body of the bridge heartbeat/deregister requests (Id identifies one `<tool> connect` process).
internal sealed record BridgeRequest(string Id);

// The hub's in-memory registry of running supervisors, keyed by project name.
internal sealed class HubRegistry
{
    private readonly ConcurrentDictionary<string, Registration> _map = new(StringComparer.OrdinalIgnoreCase);

    public void Upsert(Registration r) => _map[r.Name] = r;
    public void Remove(string name) => _map.TryRemove(name, out _);
    public Registration? Get(string name) => _map.TryGetValue(name, out var r) ? r : null;
    public IReadOnlyCollection<Registration> All() => _map.Values.ToList();
}

// The hub's in-memory registry of attached stdio bridges (`<tool> connect`), keyed by bridge id.
// A bridge has no listener of its own, so it makes itself known by heartbeating; the hub counts a
// live bridge as a reason to stay up, exactly like a registered supervisor. That's what lets the
// idle-exit be unconditional: a hub lives as long as SOMETHING needs it (a dev server, or an MCP
// client holding it open) and winds itself down the moment nothing does.
internal sealed class BridgeRegistry
{
    private readonly ConcurrentDictionary<string, DateTimeOffset> _seen = new(StringComparer.Ordinal);

    public void Heartbeat(string id) => _seen[id] = DateTimeOffset.UtcNow;
    public void Remove(string id) => _seen.TryRemove(id, out _);

    // Bridges whose last heartbeat falls inside `window`. Stale entries are dropped as we count, so
    // a hard-killed bridge (one that never said goodbye) stops holding the hub open on its own.
    public int LiveCount(TimeSpan window)
    {
        var cutoff = DateTimeOffset.UtcNow - window;
        foreach (var (id, at) in _seen)
            if (at < cutoff) _seen.TryRemove(id, out _);
        return _seen.Count;
    }
}
