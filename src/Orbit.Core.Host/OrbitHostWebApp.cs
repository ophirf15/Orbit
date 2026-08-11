using Orbit.Core.Host;
using Orbit.Core.Host.Api;
using Orbit.Core.Host.Auth;
using Orbit.Core.Host.Events;
using Orbit.Core.Host.Hosting;
using Orbit.Core.Host.Security;
using Orbit.Core.Sync;
using Orbit.Infrastructure.Calendar;
using Orbit.Infrastructure.Changes;
using Orbit.Infrastructure.Contacts;
using Orbit.Infrastructure.Context;
using Orbit.Infrastructure.Data;
using Orbit.Infrastructure.Diagnostics;
using Orbit.Infrastructure.Email;
using Orbit.Infrastructure.Files;
using Orbit.Infrastructure.Malleability;
using Orbit.Infrastructure.Search;
using Orbit.Infrastructure.Operator;
using Orbit.Infrastructure.Pulse;
using Orbit.Infrastructure.Suggestions;
using Orbit.Infrastructure.Sync;

namespace Orbit.Core.Host;

/// <summary>
/// Shared web application composition for the executable and integration tests.
/// </summary>
public static class OrbitHostWebApp
{
    public static WebApplicationBuilder CreateBuilder(HostOptions options, string[]? args = null)
    {
        var builder = WebApplication.CreateBuilder(new WebApplicationOptions
        {
            Args = args ?? [],
            ContentRootPath = AppContext.BaseDirectory,
        });

        builder.WebHost.ConfigureKestrel(kestrel =>
        {
            // Always expose loopback so local clients (Outlook web add-in, App on same PC)
            // keep working even when settings bind Core Host to a LAN address.
            var primary = System.Net.IPAddress.Parse(NormalizeListenAddress(options.BindAddress));
            kestrel.Listen(primary, options.Port);
            if (!System.Net.IPAddress.IsLoopback(primary))
            {
                kestrel.Listen(System.Net.IPAddress.Loopback, options.Port);
            }
        });

        builder.WebHost.UseUrls();

        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<EventHub>();
        builder.Services.AddHostedService<EventHeartbeatService>();
        builder.Services.AddHostedService<AgentEventWorker>();
        builder.Services.AddSingleton<OperatorWakeService>();
        builder.Services.AddHostedService(sp => sp.GetRequiredService<OperatorWakeService>());
        builder.Services.AddHostedService<AmbientPulseService>();
        builder.Services.AddHostedService<CalendarAmbientSyncService>();
        builder.Services.AddHostedService<ChangeLogRecorderService>();

        return builder;
    }

    public static WebApplication BuildApp(WebApplicationBuilder builder, HostOptions options)
    {
        // Integration tests may not register a database; register an ephemeral one when missing.
        if (builder.Services.All(d => d.ServiceType != typeof(OrbitDatabase)))
        {
            var db = OrbitDatabase.Open(options.LocalDataRoot);
            builder.Services.AddSingleton(db);
            builder.Services.AddSingleton(db.Factory);
            builder.Services.AddSingleton(new ProjectReadStore(db.Factory));
            builder.Services.AddSingleton(new WorkbenchReadStore(db.Factory));
            builder.Services.AddSingleton(new ProjectContextReadStore(db.Factory));
        }

        EnsureWorkbenchStores(builder);
        EnsureFileServices(builder);
        EnsureContactServices(builder);
        EnsureOperatorServices(builder);
        EnsureSuggestionServices(builder);
        EnsureCalendarServices(builder);
        EnsureContextServices(builder);
        EnsureSearchServices(builder);
        EnsureEmailServices(builder);
        EnsureConversationServices(builder);
        EnsureSyncServices(builder, options);
        EnsureMalleabilityServices(builder);
        EnsureDiagnosticsServices(builder, options);
        EnsureOrbitPulseServices(builder);

        builder.Services.AddCors(cors =>
        {
            cors.AddPolicy("OutlookWebAddIn", policy =>
            {
                policy
                    .SetIsOriginAllowed(origin =>
                    {
                        if (string.IsNullOrWhiteSpace(origin))
                        {
                            return false;
                        }

                        if (!Uri.TryCreate(origin, UriKind.Absolute, out var uri))
                        {
                            return false;
                        }

                        return string.Equals(uri.Host, "localhost", StringComparison.OrdinalIgnoreCase)
                            || string.Equals(uri.Host, "127.0.0.1", StringComparison.OrdinalIgnoreCase);
                    })
                    .AllowAnyHeader()
                    .AllowAnyMethod();
            });
        });

        var app = builder.Build();
        app.UseCors("OutlookWebAddIn");
        app.UseMiddleware<ApiKeyMiddleware>();
        app.MapMetaEndpoints();
        app.MapWorkbenchEndpoints();
        app.MapContextBundleEndpoints();
        app.MapProjectFolderEndpoints();
        app.MapEmailEndpoints();
        app.MapCalendarEndpoints();
        app.MapAgentMonitorEndpoints();
        app.MapContactEndpoints();
        app.MapSuggestionEndpoints();
        app.MapOperatorEndpoints();
        app.MapPulseEndpoints();
        app.MapCapabilityStubEndpoints();
        app.MapSearchEndpoints();
        app.MapFileEndpoints();
        app.MapAgentToolEndpoints();
        app.MapMalleabilityEndpoints();
        app.MapUnknownAgentToolEndpoints();
        app.MapConversationActivityEndpoints();
        app.MapSyncEndpoints();
        app.MapDiagnosticsEndpoints();
        app.MapEventEndpoints();
        return app;
    }

    private static void EnsureDiagnosticsServices(WebApplicationBuilder builder, HostOptions options)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(HermesHealthStatusStore)))
        {
            builder.Services.AddSingleton<HermesHealthStatusStore>();
        }

        if (builder.Services.All(d => d.ServiceType != typeof(DiagnosticsBundleBuilder)))
        {
            builder.Services.AddSingleton(sp =>
            {
                var opts = sp.GetRequiredService<HostOptions>();
                return new DiagnosticsBundleBuilder(
                    sp.GetRequiredService<SqliteConnectionFactory>(),
                    sp.GetRequiredService<SnapshotService>(),
                    sp.GetRequiredService<CalendarReadStore>(),
                    sp.GetRequiredService<HermesHealthStatusStore>(),
                    opts.LocalDataRoot,
                    opts.GeneratedFilesRoot,
                    sp.GetService<OperatorRunStore>());
            });
        }
    }

    private static void EnsureMalleabilityServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(CustomFieldStore)))
        {
            builder.Services.AddSingleton(sp => new CustomFieldStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(LayoutStore)))
        {
            builder.Services.AddSingleton(sp => new LayoutStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(DeveloperSourceService)))
        {
            builder.Services.AddSingleton(sp =>
            {
                var options = sp.GetRequiredService<HostOptions>();
                var folders = sp.GetService<ProjectFolderStore>();
                return new DeveloperSourceService(
                    () => options.DeveloperMode,
                    () => options.SourceRepoRoot,
                    () => options.DeveloperRemoteOverride,
                    () => folders?.ListActiveRootPaths() ?? Array.Empty<string>());
            });
        }
    }

    private static void EnsureSyncServices(WebApplicationBuilder builder, HostOptions options)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(SnapshotSyncOptions)))
        {
            builder.Services.AddSingleton(new SnapshotSyncOptions());
        }

        if (builder.Services.All(d => d.ServiceType != typeof(SyncLineageStore)))
        {
            builder.Services.AddSingleton(_ => new SyncLineageStore(options.LocalDataRoot));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(SnapshotService)))
        {
            builder.Services.AddSingleton(sp =>
            {
                var opts = sp.GetRequiredService<HostOptions>();
                return new SnapshotService(
                    sp.GetRequiredService<SqliteConnectionFactory>(),
                    sp.GetRequiredService<SyncLineageStore>(),
                    opts.LocalDataRoot,
                    opts.DeviceId,
                    opts.DeviceName,
                    () => opts.OneDriveSnapshotFolder,
                    sp.GetService<SnapshotSyncOptions>());
            });
        }

        if (builder.Services.All(d => d.ServiceType != typeof(SnapshotHostedService)))
        {
            builder.Services.AddHostedService<SnapshotHostedService>();
        }
    }

    private static void EnsureConversationServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(ConversationStore)))
        {
            builder.Services.AddSingleton(sp => new ConversationStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(RemoteActivityStore)))
        {
            builder.Services.AddSingleton(sp => new RemoteActivityStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }
    }

    private static void EnsureWorkbenchStores(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(WorkbenchReadStore)))
        {
            builder.Services.AddSingleton(sp => new WorkbenchReadStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(NoteWriteStore)))
        {
            builder.Services.AddSingleton(sp => new NoteWriteStore(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetService<SyncLineageStore>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(ProjectWriteStore)))
        {
            builder.Services.AddSingleton(sp => new ProjectWriteStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(ProjectMergeStore)))
        {
            builder.Services.AddSingleton(sp => new ProjectMergeStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(WorkbenchLayoutStore)))
        {
            builder.Services.AddSingleton(sp => new WorkbenchLayoutStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(ProjectContextReadStore)))
        {
            builder.Services.AddSingleton(sp => new ProjectContextReadStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }
    }

    private static void EnsureFileServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(ProjectFolderStore)))
        {
            builder.Services.AddSingleton(sp => new ProjectFolderStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(IExternalFileCapability)))
        {
            builder.Services.AddSingleton<IExternalFileCapability>(sp =>
            {
                var folders = sp.GetRequiredService<ProjectFolderStore>();
                return new ExternalFileService(() => folders.ListActiveRootPaths());
            });
        }

        if (builder.Services.All(d => d.ServiceType != typeof(FileIndexService)))
        {
            builder.Services.AddSingleton(sp => new FileIndexService(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<ProjectFolderStore>(),
                sp.GetRequiredService<IExternalFileCapability>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(PathGuard)))
        {
            builder.Services.AddSingleton(sp => new PathGuard(
                sp.GetRequiredService<HostOptions>(),
                sp.GetRequiredService<ProjectFolderStore>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(FolderWatchHostedService)))
        {
            builder.Services.AddHostedService<FolderWatchHostedService>();
        }
    }

    private static void EnsureEmailServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(MsgEmailParser)))
        {
            builder.Services.AddSingleton<MsgEmailParser>();
        }

        if (builder.Services.All(d => d.ServiceType != typeof(EmailArtifactStore)))
        {
            builder.Services.AddSingleton(sp => new EmailArtifactStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(EmailIngestionService)))
        {
            builder.Services.AddSingleton(sp => new EmailIngestionService(
                sp.GetRequiredService<EmailArtifactStore>(),
                sp.GetRequiredService<MsgEmailParser>(),
                sp.GetRequiredService<HostOptions>().GeneratedFilesRoot,
                sp.GetRequiredService<EmailContactEnricher>(),
                sp.GetRequiredService<MultiProjectClaimSplitter>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(TaskEmailThreadStore)))
        {
            builder.Services.AddSingleton(sp => new TaskEmailThreadStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(EmailDutyEnsureService)))
        {
            builder.Services.AddSingleton(sp => new EmailDutyEnsureService(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<EmailArtifactStore>(),
                sp.GetRequiredService<TaskEmailThreadStore>(),
                sp.GetRequiredService<OrbitMutationStore>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(IOutlookMsgExport)))
        {
            builder.Services.AddSingleton<IOutlookMsgExport, OutlookMsgExport>();
        }
    }

    private static void EnsureSearchServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(GlobalSearchService)))
        {
            builder.Services.AddSingleton(sp => new GlobalSearchService(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(EvidenceService)))
        {
            builder.Services.AddSingleton(sp => new EvidenceService(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<ContextBundleService>()));
        }
    }

    private static void EnsureContextServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(ContextBundleService)))
        {
            builder.Services.AddSingleton(sp => new ContextBundleService(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<CalendarReadStore>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(MultiProjectClaimSplitter)))
        {
            builder.Services.AddSingleton(sp => new MultiProjectClaimSplitter(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<SuggestionStore>()));
        }
    }

    private static void EnsureCalendarServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(CalendarReadStore)))
        {
            builder.Services.AddSingleton(sp => new CalendarReadStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(MeetingProjectLinker)))
        {
            builder.Services.AddSingleton(sp => new MeetingProjectLinker(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(AttentionScorer)))
        {
            builder.Services.AddSingleton(sp => new AttentionScorer(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(CalendarSyncService)))
        {
            builder.Services.AddSingleton(sp =>
            {
                var factory = sp.GetRequiredService<SqliteConnectionFactory>();
                var options = sp.GetRequiredService<HostOptions>();
                return new CalendarSyncService(
                    factory,
                    sp.GetRequiredService<MeetingProjectLinker>(),
                    sp.GetRequiredService<AttentionScorer>(),
                    () =>
                    {
                        var providers = new List<ICalendarProvider>
                        {
                            new OutlookCalendarProvider(),
                            new GraphCalendarProvider(),
                        };
                        if (!string.IsNullOrWhiteSpace(options.CalendarIcsPath))
                        {
                            providers.Add(new IcsCalendarProvider(options.CalendarIcsPath));
                        }

                        return providers;
                    });
            });
        }
    }

    private static void EnsureContactServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(ContactStore)))
        {
            builder.Services.AddSingleton(sp => new ContactStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(EmailContactEnricher)))
        {
            builder.Services.AddSingleton(sp => new EmailContactEnricher(sp.GetRequiredService<ContactStore>()));
        }
    }

    private static void EnsureSuggestionServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(SuggestionStore)))
        {
            builder.Services.AddSingleton(sp => new SuggestionStore(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<OperatorMemoryStore>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(SuggestionEngine)))
        {
            builder.Services.AddSingleton(sp => new SuggestionEngine(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<SuggestionStore>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(OrbitMutationStore)))
        {
            builder.Services.AddSingleton(sp => new OrbitMutationStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(TaskDependencyStore)))
        {
            builder.Services.AddSingleton(sp => new TaskDependencyStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(TaskRelationshipEngine)))
        {
            builder.Services.AddSingleton(sp => new TaskRelationshipEngine(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<SuggestionStore>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(ContactRelationEngine)))
        {
            builder.Services.AddSingleton(sp => new ContactRelationEngine(
                sp.GetRequiredService<SqliteConnectionFactory>(),
                sp.GetRequiredService<SuggestionStore>()));
        }
    }

    private static void EnsureOrbitPulseServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(PulseReadStore)))
        {
            builder.Services.AddSingleton(sp => new PulseReadStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(ChangeLogStore)))
        {
            builder.Services.AddSingleton(sp => new ChangeLogStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(OrbitIgnitionService)))
        {
            builder.Services.AddSingleton(sp => new OrbitIgnitionService(
                sp.GetRequiredService<PulseReadStore>(),
                sp.GetRequiredService<ProjectWriteStore>(),
                sp.GetService<ProjectFolderStore>()));
        }
    }

    private static void EnsureOperatorServices(WebApplicationBuilder builder)
    {
        if (builder.Services.All(d => d.ServiceType != typeof(StandingRulesStore)))
        {
            builder.Services.AddSingleton(sp => new StandingRulesStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(OperatorMemoryStore)))
        {
            builder.Services.AddSingleton(sp => new OperatorMemoryStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(OperatorRunStore)))
        {
            builder.Services.AddSingleton(sp => new OperatorRunStore(sp.GetRequiredService<SqliteConnectionFactory>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(StandingRuleEngine)))
        {
            builder.Services.AddSingleton(sp => new StandingRuleEngine(
                sp.GetRequiredService<StandingRulesStore>(),
                sp.GetRequiredService<OrbitMutationStore>(),
                sp.GetRequiredService<TaskEmailThreadStore>(),
                sp.GetRequiredService<NoteWriteStore>()));
        }

        if (builder.Services.All(d => d.ServiceType != typeof(HermesHealthStatusStoreBridge)))
        {
            builder.Services.AddSingleton<HermesHealthStatusStoreBridge>();
        }
    }

    private static string NormalizeListenAddress(string bindAddress)
    {
        if (string.Equals(bindAddress, "localhost", StringComparison.OrdinalIgnoreCase))
        {
            return "127.0.0.1";
        }

        return bindAddress;
    }
}
