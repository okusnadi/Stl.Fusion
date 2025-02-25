using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Channels;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Stl.DependencyInjection;
using Stl.IO;
using Stl.Fusion.Bridge;
using Stl.Fusion.Bridge.Messages;
using Stl.Fusion.Client;
using Stl.Fusion.EntityFramework;
using Stl.Fusion.Tests.Model;
using Stl.Fusion.Tests.Services;
using Stl.Fusion.Tests.UIModels;
using Stl.Fusion.Internal;
using Stl.Fusion.Server;
using Stl.Testing;
using Stl.Testing.Internal;
using Xunit;
using Xunit.Abstractions;
using Xunit.DependencyInjection.Logging;

namespace Stl.Fusion.Tests
{
    public class FusionTestOptions
    {
        public bool UseInMemoryDatabase { get; set; }
    }

    public class FusionTestBase : TestBase, IAsyncLifetime
    {
        public FusionTestOptions Options { get; }
        public bool IsLoggingEnabled { get; set; } = true;
        public PathString DbPath { get; protected set; }
        public FusionTestWebHost WebHost { get; }
        public IServiceProvider Services { get; }
        public IServiceProvider WebServices { get; }
        public IServiceProvider ClientServices { get; }
        public ILogger Log { get; }

        public FusionTestBase(ITestOutputHelper @out, FusionTestOptions? options = null) : base(@out)
        {
            Options = options ?? new FusionTestOptions();
            // ReSharper disable once VirtualMemberCallInConstructor
            Services = CreateServices();
            WebHost = Services.GetRequiredService<FusionTestWebHost>();
            WebServices = WebHost.Services;
            ClientServices = CreateServices(true);
            Log = (ILogger) Services.GetRequiredService(typeof(ILogger<>).MakeGenericType(GetType()));
        }

        public virtual async Task InitializeAsync()
        {
            if (File.Exists(DbPath))
                File.Delete(DbPath);
            await using var dbContext = CreateDbContext();
            await dbContext.Database.EnsureCreatedAsync();
        }

        public virtual Task DisposeAsync()
        {
            if (Services is IDisposable d1)
                d1.Dispose();
            if (ClientServices is IDisposable d2)
                d2.Dispose();
            return Task.CompletedTask;
        }

        protected override void Dispose(bool disposing)
            => DisposeAsync().Wait();

        protected IServiceProvider CreateServices(bool isClient = false)
        {
            var services = (IServiceCollection) new ServiceCollection();
            ConfigureServices(services, isClient);
            return services.BuildServiceProvider();
        }

        protected virtual void ConfigureServices(IServiceCollection services, bool isClient = false)
        {
            services.AddSingleton(Out);

            // Logging
            services.AddLogging(logging => {
                var debugCategories = new List<string> {
                    "Stl.Fusion",
                    "Stl.CommandR",
                    "Stl.Tests.Fusion",
                    // DbLoggerCategory.Database.Transaction.Name,
                    // DbLoggerCategory.Database.Connection.Name,
                    // DbLoggerCategory.Database.Command.Name,
                    // DbLoggerCategory.Query.Name,
                    // DbLoggerCategory.Update.Name,
                };

                bool LogFilter(string category, LogLevel level)
                    => IsLoggingEnabled &&
                        debugCategories.Any(category.StartsWith)
                        && level >= LogLevel.Debug;

                logging.ClearProviders();
                logging.SetMinimumLevel(LogLevel.Debug);
                logging.AddFilter(LogFilter);
                logging.AddDebug();
                // XUnit logging requires weird setup b/c otherwise it filters out
                // everything below LogLevel.Information
                logging.AddProvider(new XunitTestOutputLoggerProvider(
                    new TestOutputHelperAccessor(Out),
                    LogFilter));
            });

            // Auto-discovered services
            var testType = GetType();
            services.AttributeScanner()
                .WithTypeFilter(testType.Namespace!)
                .AddServicesFrom(testType.Assembly);

            // Core Fusion services
            var fusion = services.AddFusion();

            if (!isClient) {
                // Configuring Services and ServerServices
                services.AttributeScanner(ServiceScope.Services)
                    .WithTypeFilter(testType.Namespace!)
                    .AddServicesFrom(testType.Assembly);

                // DbContext & related services
                var appTempDir = PathEx.GetApplicationTempDirectory("", true);
                DbPath = appTempDir & PathEx.GetHashedName($"{testType.Name}_{testType.Namespace}.db");
                services.AddPooledDbContextFactory<TestDbContext>(builder => {
                    if (Options.UseInMemoryDatabase)
                        builder.UseInMemoryDatabase(DbPath)
                            .ConfigureWarnings(w => {
                                w.Ignore(InMemoryEventId.TransactionIgnoredWarning);
                            });
                    else
                        builder.UseSqlite($"Data Source={DbPath}", sqlite => { });
                }, 256);
                services.AddDbContextServices<TestDbContext>(b => {
                    b.AddEntityResolver<long, User>();
                    b.AddOperations();
                });

                // WebHost
                var webHost = (FusionTestWebHost?) WebHost;
                services.AddSingleton(c => webHost ?? new FusionTestWebHost(services));
            }
            else {
                // Configuring ClientServices
                services.AttributeScanner(ServiceScope.ClientServices)
                    .WithTypeFilter(testType.Namespace!)
                    .AddServicesFrom(testType.Assembly);

                // Fusion client
                var fusionClient = fusion.AddRestEaseClient(
                    (c, options) => {
                        options.BaseUri = WebHost.ServerUri;
                        options.MessageLogLevel = LogLevel.Information;
                    }).ConfigureHttpClientFactory(
                    (c, name, options) => {
                        var baseUri = WebHost.ServerUri;
                        var apiUri = new Uri($"{baseUri}api/");
                        var isFusionService = !(name ?? "").Contains("Tests");
                        var clientBaseUri = isFusionService ? baseUri : apiUri;
                        options.HttpClientActions.Add(c => c.BaseAddress = clientBaseUri);
                    });
                fusion.AddAuthentication(auth => auth.AddRestEaseClient());

                // Custom live state
                fusion.AddState(c => c.StateFactory().NewLive<ServerTimeModel2>(
                    async (state, cancellationToken) => {
                        var client = c.GetRequiredService<IClientTimeService>();
                        var time = await client.GetTimeAsync(cancellationToken).ConfigureAwait(false);
                        return new ServerTimeModel2(time);
                    }));
            }
        }

        protected TestDbContext CreateDbContext()
            => Services.GetRequiredService<IDbContextFactory<TestDbContext>>().CreateDbContext();

        protected Task<Channel<BridgeMessage>> ConnectToPublisherAsync(CancellationToken cancellationToken = default)
        {
            var publisher = WebServices.GetRequiredService<IPublisher>();
            var channelProvider = ClientServices.GetRequiredService<IChannelProvider>();
            return channelProvider.CreateChannelAsync(publisher.Id, cancellationToken);
        }

        protected virtual TestChannelPair<BridgeMessage> CreateChannelPair(
            string name, bool dump = true)
            => new(name, dump ? Out : null);

        protected virtual Task DelayAsync(double seconds)
            => Timeouts.Clock.DelayAsync(TimeSpan.FromSeconds(seconds));

        protected void GCCollect()
        {
            for (var i = 0; i < 3; i++) {
                GC.Collect();
                Thread.Sleep(10);
            }
        }
    }
}
