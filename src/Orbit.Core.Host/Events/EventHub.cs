using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;

namespace Orbit.Core.Host.Events;

public sealed class OrbitEvent
{
    public required string Type { get; init; }

    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

    public object? Payload { get; init; }
}

/// <summary>
/// Fan-out event bus. Each subscriber gets every event (unlike a shared Channel
/// where competing readers would steal messages from each other).
/// </summary>
public sealed class EventHub
{
    private readonly ConcurrentDictionary<Guid, Channel<OrbitEvent>> _subscribers = new();

    public void Publish(OrbitEvent orbitEvent)
    {
        foreach (var channel in _subscribers.Values)
        {
            channel.Writer.TryWrite(orbitEvent);
        }
    }

    public async IAsyncEnumerable<OrbitEvent> SubscribeAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        var id = Guid.NewGuid();
        var channel = Channel.CreateBounded<OrbitEvent>(new BoundedChannelOptions(256)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false,
        });
        _subscribers[id] = channel;
        try
        {
            await foreach (var item in channel.Reader.ReadAllAsync(cancellationToken))
            {
                yield return item;
            }
        }
        finally
        {
            _subscribers.TryRemove(id, out _);
            channel.Writer.TryComplete();
        }
    }

    public static string Serialize(OrbitEvent orbitEvent) =>
        JsonSerializer.Serialize(orbitEvent);
}

public sealed class EventHeartbeatService : BackgroundService
{
    private readonly EventHub _hub;

    public EventHeartbeatService(EventHub hub) => _hub = hub;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            _hub.Publish(new OrbitEvent
            {
                Type = "heartbeat",
                Payload = new { status = "ok" },
            });
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
