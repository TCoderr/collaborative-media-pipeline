using System.Collections.Concurrent;

namespace MediaPipeline.Api.Realtime;

/// <summary>Replica-local connection subscriptions, reauthorised against PostgreSQL before every broadcast.</summary>
public sealed class ProjectSubscriptions
{
    private readonly ConcurrentDictionary<(Guid Project, string Connection), Guid> _subscriptions = new();
    public void Add(Guid project, string connection, Guid user) => _subscriptions[(project, connection)] = user;
    public void Remove(Guid project, string connection) => _subscriptions.TryRemove((project, connection), out _);
    public void Disconnect(string connection)
    {
        foreach (var key in _subscriptions.Keys.Where(k => k.Connection == connection)) _subscriptions.TryRemove(key, out _);
    }
    public string[] AuthorisedConnections(Guid project, HashSet<Guid> members) => _subscriptions
        .Where(s => s.Key.Project == project && members.Contains(s.Value)).Select(s => s.Key.Connection).ToArray();
}
