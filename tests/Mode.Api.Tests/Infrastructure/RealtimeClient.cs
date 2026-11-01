using System.Text.Json;
using Microsoft.AspNetCore.Http.Connections;
using Microsoft.AspNetCore.SignalR.Client;
using Mode.Api.Realtime;

namespace Mode.Api.Tests.Infrastructure;

/// <summary>A SignalR client that records every server event for assertions.</summary>
internal sealed class RealtimeClient : IAsyncDisposable
{
    private static readonly string[] EventNames =
    [
        RealtimeEvents.MessageCreated, RealtimeEvents.MessageEdited, RealtimeEvents.MessageDeleted, RealtimeEvents.ReactionChanged,
        RealtimeEvents.Typing, RealtimeEvents.ActivityCreated, RealtimeEvents.RoomAccessRevoked, RealtimeEvents.FileUpdated,
        RealtimeEvents.Notification, RealtimeEvents.PresenceChanged, RealtimeEvents.ReadMarkerChanged, RealtimeEvents.ActivityRead,
    ];

    private readonly HubConnection _connection;
    private readonly List<(string Name, JsonElement Payload)> _events = [];
    private readonly SemaphoreSlim _arrived = new(0);

    private RealtimeClient(HubConnection connection) => _connection = connection;

    public static async Task<RealtimeClient> ConnectAsync(ApiFixture api, string userId)
    {
        var token = TestAuth.CreateToken(userId);
        var connection = new HubConnectionBuilder()
            .WithUrl(new Uri(api.Factory.Server.BaseAddress, "hubs/mode"), options =>
            {
                options.HttpMessageHandlerFactory = _ => api.Factory.Server.CreateHandler();
                options.Transports = HttpTransportType.LongPolling;
                options.AccessTokenProvider = () => Task.FromResult<string?>(token);
            })
            .Build();

        var client = new RealtimeClient(connection);
        foreach (var name in EventNames)
            connection.On<JsonElement>(name, payload => client.Record(name, payload));
        await connection.StartAsync();
        return client;
    }

    public Task StopAsync() => _connection.StopAsync();

    public async Task<JsonElement> JoinAsync(string roomType, Guid roomId, long lastSequence = 0)
    {
        var results = await _connection.InvokeAsync<JsonElement>(
            "JoinRooms", new[] { new { room = new { type = roomType, id = roomId }, lastSequence } });
        return results[0];
    }

    public Task TypingAsync(string roomType, Guid roomId) =>
        _connection.InvokeAsync("Typing", new { type = roomType, id = roomId }, (Guid?)null);

    public Task<JsonElement> SubscribePresenceAsync(params string[] userIds) =>
        _connection.InvokeAsync<JsonElement>("SubscribePresence", userIds);

    public Task HeartbeatAsync(bool active) => _connection.InvokeAsync("Heartbeat", active);

    public IReadOnlyList<JsonElement> Events(string name)
    {
        lock (_events) return _events.Where(e => e.Name == name).Select(e => e.Payload).ToList();
    }

    public async Task<JsonElement> WaitForAsync(string name, Predicate<JsonElement>? match = null, TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(15));
        while (true)
        {
            var found = Events(name).Where(e => match?.Invoke(e) ?? true).ToList();
            if (found.Count > 0) return found[0];

            var remaining = deadline - DateTime.UtcNow;
            if (remaining <= TimeSpan.Zero || !await _arrived.WaitAsync(remaining))
                throw new TimeoutException($"No '{name}' event arrived in time.");
        }
    }

    private void Record(string name, JsonElement payload)
    {
        lock (_events) _events.Add((name, payload.Clone()));
        _arrived.Release();
    }

    public async ValueTask DisposeAsync() => await _connection.DisposeAsync();
}
