using System.Diagnostics;

namespace Orbit_App.Services;

/// <summary>
/// Cross-process handoff that does not depend on WinUI AppInstance redirect
/// (unreliable when Orbit is started via <c>dotnet run</c> vs protocol-launched exe).
/// Outlook launcher writes a request file + pulses a named event; the running App
/// waits on the event and also polls on a UI timer.
/// </summary>
public static class OrbitPushSignal
{
    public const string RequestFileName = "push-outlook.request";
    public const string EventName = @"Local\Orbit.PushOutlook";

    public static string CommandsDirectory =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Orbit",
            "commands");

    public static string RequestPath => Path.Combine(CommandsDirectory, RequestFileName);

    public static void WritePushOutlookRequest(string source = "unknown")
    {
        try
        {
            Directory.CreateDirectory(CommandsDirectory);
            var payload = $"{DateTimeOffset.UtcNow:O}\nsource={source}\n";
            File.WriteAllText(RequestPath, payload);
        }
        catch (Exception ex)
        {
            Debug.WriteLine("OrbitPushSignal.Write failed: " + ex.Message);
        }

        PulseEvent();
    }

    public static void PulseEvent()
    {
        try
        {
            using var ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            ev.Set();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("OrbitPushSignal.Pulse failed: " + ex.Message);
        }
    }

    public static bool RequestPending()
    {
        try
        {
            return File.Exists(RequestPath);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Background wait on the named event + file poll. Invokes <paramref name="tryAccept"/>;
    /// the signal file is deleted only when that returns true.
    /// </summary>
    public static IDisposable StartWatcher(Func<bool> tryAccept)
    {
        Directory.CreateDirectory(CommandsDirectory);
        var cts = new CancellationTokenSource();
        var gate = new object();
        var lastAttemptTicks = 0L;

        void Attempt()
        {
            lock (gate)
            {
                if (!RequestPending())
                {
                    return;
                }

                var now = Environment.TickCount64;
                if (now - lastAttemptTicks < 400)
                {
                    return;
                }

                lastAttemptTicks = now;
            }

            try
            {
                if (tryAccept())
                {
                    TryDeleteRequest();
                }
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OrbitPushSignal accept: " + ex.Message);
            }
        }

        FileSystemWatcher? watcher = null;
        try
        {
            watcher = new FileSystemWatcher(CommandsDirectory)
            {
                Filter = RequestFileName,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite | NotifyFilters.CreationTime,
                EnableRaisingEvents = true,
            };
            watcher.Created += (_, _) => Attempt();
            watcher.Changed += (_, _) => Attempt();
        }
        catch (Exception ex)
        {
            Debug.WriteLine("OrbitPushSignal.StartWatcher FSW failed: " + ex.Message);
        }

        var thread = new Thread(() =>
        {
            EventWaitHandle? ev = null;
            try
            {
                ev = new EventWaitHandle(false, EventResetMode.AutoReset, EventName);
            }
            catch (Exception ex)
            {
                Debug.WriteLine("OrbitPushSignal event open failed: " + ex.Message);
            }

            try
            {
                while (!cts.IsCancellationRequested)
                {
                    var signaled = ev?.WaitOne(500) == true;
                    if (signaled || RequestPending())
                    {
                        Attempt();
                    }
                }
            }
            finally
            {
                ev?.Dispose();
            }
        })
        {
            IsBackground = true,
            Name = "Orbit-PushSignal",
        };
        thread.Start();

        return new PushSignalSubscription(watcher, cts, thread);
    }

    private sealed class PushSignalSubscription : IDisposable
    {
        private readonly FileSystemWatcher? _watcher;
        private readonly CancellationTokenSource _cts;
        private readonly Thread _thread;

        public PushSignalSubscription(FileSystemWatcher? watcher, CancellationTokenSource cts, Thread thread)
        {
            _watcher = watcher;
            _cts = cts;
            _thread = thread;
        }

        public void Dispose()
        {
            try
            {
                _cts.Cancel();
                PulseEvent();
                if (!_thread.Join(1000))
                {
                    Debug.WriteLine("OrbitPushSignal watcher thread did not stop cleanly.");
                }
            }
            catch
            {
                // ignore
            }
            finally
            {
                _watcher?.Dispose();
                _cts.Dispose();
            }
        }
    }

    public static void TryDeleteRequest()
    {
        try
        {
            if (File.Exists(RequestPath))
            {
                File.Delete(RequestPath);
            }
        }
        catch
        {
            // ignore
        }
    }
}
