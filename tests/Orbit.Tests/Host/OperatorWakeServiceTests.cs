using System.Net;
using System.Net.Sockets;
using System.Reflection;
using Microsoft.AspNetCore.Builder;
using Microsoft.Extensions.DependencyInjection;
using Orbit.Core.Host;
using Orbit.Core.Host.Events;
using Orbit.Core.Host.Hosting;
using Orbit.Core.Operator;
using Orbit.Infrastructure.Operator;

namespace Orbit.Tests.Host;

/// <summary>
/// ADR 0028: Host no longer pokes Hermes on a calendar.soon/duty.scan timer. These tests spin up
/// the real Host app (Hermes unconfigured, so wakes resolve to Skipped without a network call) and
/// assert only real graph events reach <see cref="OperatorRunStore"/>.
/// </summary>
public sealed class OperatorWakeServiceTests : IAsyncLifetime
{
    private WebApplication? _app;
    private string? _tempRoot;

    public async Task InitializeAsync()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "OrbitWakeTests", Guid.NewGuid().ToString("N"));
        var generated = Path.Combine(_tempRoot, "generated");
        var data = Path.Combine(_tempRoot, "data");
        Directory.CreateDirectory(generated);
        Directory.CreateDirectory(data);

        var options = new HostOptions
        {
            BindAddress = "127.0.0.1",
            Port = GetFreePort(),
            ApiKey = null,
            LocalDataRoot = data,
            GeneratedFilesRoot = generated,
            HermesBaseUrl = null,
        };

        HostStartupGuard.EnsureMayListen(options);
        var builder = OrbitHostWebApp.CreateBuilder(options);
        _app = OrbitHostWebApp.BuildApp(builder, options);
        await _app.StartAsync();
    }

    public async Task DisposeAsync()
    {
        if (_app is not null)
        {
            await _app.StopAsync();
            await _app.DisposeAsync();
        }

        if (_tempRoot is not null && Directory.Exists(_tempRoot))
        {
            try
            {
                Directory.Delete(_tempRoot, recursive: true);
            }
            catch (IOException)
            {
                // best-effort
            }
        }
    }

    [Fact]
    public async Task CalendarSynced_DoesNotEnqueueOperatorRun()
    {
        var hub = _app!.Services.GetRequiredService<EventHub>();
        var runs = _app.Services.GetRequiredService<OperatorRunStore>();

        // Simulates what CalendarAmbientSyncService publishes on its 15-min data-sync tick.
        hub.Publish(new OrbitEvent
        {
            Type = "calendar.synced",
            Payload = new { sourcesUpserted = 1, eventsUpserted = 3, linksCreated = 0 },
        });

        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        Assert.Empty(runs.ListRecent(10));
    }

    [Fact]
    public async Task EmailIngested_StillEnqueuesOperatorRun()
    {
        // Mirrors EmailEndpoints.cs: ingestion calls OperatorWakeService.RequestWake directly
        // (not just an EventHub publish) precisely because EventHub has competing subscribers.
        var wake = _app!.Services.GetRequiredService<OperatorWakeService>();
        var runs = _app.Services.GetRequiredService<OperatorRunStore>();

        wake.RequestWake(OperatorTriggers.EmailIngested, """{"emailId":"missing-email-id"}""");

        await Task.Delay(TimeSpan.FromMilliseconds(1500));

        Assert.Contains(runs.ListRecent(10), r => r.TriggerKind == OperatorTriggers.EmailIngested);
    }

    [Fact]
    public void OperatorWakeService_HasNoCalendarSoonPollingLoop()
    {
        var method = typeof(OperatorWakeService).GetMethod(
            "CalendarSoonLoopAsync",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Assert.Null(method);
    }

    [Fact]
    public void AmbientPulseService_TakesNoOperatorWakeDependency()
    {
        var ctor = Assert.Single(typeof(AmbientPulseService).GetConstructors());
        Assert.DoesNotContain(ctor.GetParameters(), p => p.ParameterType == typeof(OperatorWakeService));
    }

    private static int GetFreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
