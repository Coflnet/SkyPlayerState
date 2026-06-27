using System;
using System.IO;
using System.Reflection;
using Coflnet.Sky.PlayerState.Models;
using Coflnet.Sky.PlayerState.Services;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.OpenApi;
using Prometheus;
using Coflnet.Sky.EventBroker.Client.Api;
using Coflnet.Sky.PlayerName.Client.Api;
using Coflnet.Sky.Proxy.Client.Api;
using Coflnet.Sky.Items.Client.Api;
using Coflnet.Sky.Core;
using Cassandra;
using System.Security.Cryptography.X509Certificates;
using System.Security.Authentication;
using System.Linq;
using System.Collections.Generic;
using Coflnet.Sky.Bazaar.Client.Api;
using Coflnet.Sky.Api.Client.Api;
using Coflnet.Sky.Sniper.Client.Api;
using Coflnet.Sky.PlayerState.Bazaar;
using StackExchange.Redis;

namespace Coflnet.Sky.PlayerState;

public class Startup
{
    public Startup(IConfiguration configuration)
    {
        Configuration = configuration;
    }

    public IConfiguration Configuration { get; }

    // This method gets called by the runtime. Use this method to add services to the container.
    public void ConfigureServices(IServiceCollection services)
    {
        // give the background service room to flush all in-memory states to cassandra on a
        // planned restart before the host force-stops it. Kept below the k8s
        // terminationGracePeriodSeconds (30s) so the process can exit cleanly before SIGKILL.
        services.Configure<HostOptions>(o => o.ShutdownTimeout = TimeSpan.FromSeconds(25));
        services.AddControllers().AddJsonOptions(o => o.JsonSerializerOptions.IncludeFields = true);
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "SkyPlayerState", Version = "v1" });
            // Set the comments path for the Swagger JSON and UI.
            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            c.IncludeXmlComments(xmlPath);
        });

        if (Configuration["MIGRATOR"] == "true")
        {
            services.AddHostedService<MigrationService>();
        }
        else
            services.AddHostedService<PlayerStateBackgroundService>();
        services.AddJaeger(Configuration, 0.001);
        services.AddResponseCaching();
        services.AddResponseCompression();

        services.Configure<MongoSettings>(Configuration.GetSection("Mongo"));
        services.AddSingleton<IItemsService, ItemsService>();
        services.AddSingleton<Services.CoinParser>();
        services.AddSingleton<ITradeService, TradeService>();
        services.AddSingleton<Kafka.KafkaCreator>();
        services.AddSingleton<IPersistenceService, PersistenceService>();
        services.AddSingleton<ITransactionService, TransactionService>();
        services.AddSingleton<IShenStorage, ShenHistoryService>();
        services.AddSingleton<SkillService>();
        services.AddSingleton<SniperService>();
        services.AddSingleton<ISniperApi>(di => new SniperApi(Configuration["SNIPER_BASE_URL"]));
        services.AddSingleton<TrackedProfitService>();
        services.AddSingleton<MethodAggregateService>();
        services.AddSingleton<IBazaarProfitTracker, BazaarProfitTracker>();
        services.AddSingleton<IMayorAuraService, MayorAuraService>();
        services.AddSingleton<IBitService, BitService>();
        services.AddSingleton<IPlayerElectionService, PlayerElectionService>();
        services.AddSingleton<IMythologicalRitualService, MythologicalRitualService>();
        services.AddSingleton<RecipeService>();
        services.AddSingleton<RngMeterService>();
        services.AddSingleton<ItemDetails>();
        services.AddSingleton<StorageService>();
        services.AddSingleton<NBT>();
        services.AddSingleton<ICassandraService>(di => di.GetRequiredService<ITransactionService>() as ICassandraService
                    ?? throw new Exception("ITransactionService is not a ICassandraService"));
        services.AddSingleton<ICoinCounterService, CoinCounterService>();
        services.AddSingleton<IMessageApi>(sp => new MessageApi(Configuration["EVENTS_BASE_URL"]));
        services.AddSingleton<IScheduleApi>(sp => new ScheduleApi(Configuration["EVENTS_BASE_URL"]));
        services.AddSingleton<IPlayerNameApi>(sp => new PlayerNameApi(Configuration["PLAYERNAME_BASE_URL"]));
        services.AddSingleton<IBaseApi>(sp => new BaseApi(Configuration["PROXY_BASE_URL"]));
        services.AddSingleton<IOrderBookApi>(sp => new OrderBookApi(Configuration["BAZAAR_BASE_URL"]));
        services.AddSingleton<Sky.Bazaar.Client.Api.IBazaarApi>(sp => new Sky.Bazaar.Client.Api.BazaarApi(Configuration["BAZAAR_BASE_URL"]));
        services.AddSingleton<IConnectionMultiplexer>(_ => ConnectionMultiplexer.Connect(Configuration["REDIS_HOST"]));
        services.AddSingleton<BazaarSignalPublisher>();

        services.AddSingleton<IItemsApi>(context => new ItemsApi(Configuration["ITEMS_BASE_URL"]));
        services.AddSingleton<IAuctionsApi>(context => new AuctionsApi(Configuration["API_BASE_URL"]));
        services.AddSingleton<IPlayerApi>(context => new PlayerApi(Configuration["API_BASE_URL"]));
        services.AddSingleton<IPricesApi>(context => new PricesApi(Configuration["API_BASE_URL"]));
        RegisterScyllaSession(services);
    }

    // This method gets called by the runtime. Use this method to configure the HTTP request pipeline.
    public void Configure(IApplicationBuilder app, IWebHostEnvironment env)
    {
        app.UseExceptionHandler(errorApp =>
        {
            ErrorHandler.Add(errorApp, "playerstate");
        });
        if (env.IsDevelopment())
        {
            app.UseDeveloperExceptionPage();
        }
        app.UseSwagger();
        app.UseSwaggerUI(c =>
        {
            c.SwaggerEndpoint("/swagger/v1/swagger.json", "SkyPlayerState v1");
            c.RoutePrefix = "api";
        });

        app.UseResponseCaching();
        app.UseResponseCompression();

        app.UseRouting();

        app.UseAuthorization();

        app.UseEndpoints(endpoints =>
        {
            endpoints.MapMetrics();
            endpoints.MapControllers();
        });
    }
    private void RegisterScyllaSession(IServiceCollection services)
    {
        services.AddSingleton<ISession>(p =>
        {
            var logger = p.GetRequiredService<ILogger<Startup>>();
            logger.LogInformation("Connecting to Scylla...");
            var builder = Cluster.Builder().AddContactPoints(Configuration["SCYLLA:HOSTS"].Split(","))
                .WithLoadBalancingPolicy(new TokenAwarePolicy(new DCAwareRoundRobinPolicy()))
                .WithCredentials(Configuration["SCYLLA:USER"], Configuration["SCYLLA:PASSWORD"])
                .WithDefaultKeyspace(Configuration["SCYLLA:KEYSPACE"]);

            logger.LogDebug("Connecting to servers {hosts}", Configuration["SCYLLA:HOSTS"]);
            logger.LogDebug("Using keyspace {keyspace}", Configuration["SCYLLA:KEYSPACE"]);
            logger.LogDebug("Using replication class {replicationClass}", Configuration["SCYLLA:REPLICATION_CLASS"]);
            logger.LogDebug("Using replication factor {replicationFactor}", Configuration["SCYLLA:REPLICATION_FACTOR"]);
            logger.LogDebug("Using user {user}", Configuration["SCYLLA:USER"]);
            logger.LogDebug("Using password {password}...", Configuration["SCYLLA:PASSWORD"].Truncate(2));
            var certificatePaths = Configuration["SCYLLA:X509Certificate_PATHS"];
            logger.LogDebug("Using certificate paths {certificatePaths}", certificatePaths);
            logger.LogDebug("Using certificate password {certificatePassword}...", Configuration["SCYLLA:X509Certificate_PASSWORD"].Truncate(2));
            var validationCertificatePath = Configuration["SCYLLA:X509Certificate_VALIDATION_PATH"];
            if (!string.IsNullOrEmpty(certificatePaths))
            {
                var password = Configuration["SCYLLA:X509Certificate_PASSWORD"] ?? throw new InvalidOperationException("SCYLLA:X509Certificate_PASSWORD must be set if SCYLLA:X509Certificate_PATHS is set.");
                CustomRootCaCertificateValidator certificateValidator = null;
                if (!string.IsNullOrEmpty(validationCertificatePath))
                    certificateValidator = new CustomRootCaCertificateValidator(new X509Certificate2(validationCertificatePath, password));
                var sslOptions = new SSLOptions(
                    // TLSv1.2 is required as of October 9, 2019.
                    // See: https://www.instaclustr.com/removing-support-for-outdated-encryption-mechanisms/
                    SslProtocols.Tls12,
                    false,
                    // Custom validator avoids need to trust the CA system-wide.
                    (sender, certificate, chain, errors) => certificateValidator?.Validate(certificate, chain, errors) ?? true
                ).SetCertificateCollection(new(certificatePaths.Split(',').Select(p => new X509Certificate2(p, password)).ToArray()));
                builder.WithSSL(sslOptions);
            }
            var cluster = builder.Build();
            var session = cluster.Connect(null);
            var defaultKeyspace = cluster.Configuration.ClientOptions.DefaultKeyspace;
            if (!string.IsNullOrEmpty(defaultKeyspace))
            {
                var keyspaceExists = session.Execute(new SimpleStatement(
                    "SELECT keyspace_name FROM system_schema.keyspaces WHERE keyspace_name = ?",
                    defaultKeyspace)).Any();

                if (!keyspaceExists)
                {
                    try
                    {
                        session.CreateKeyspaceIfNotExists(defaultKeyspace, new Dictionary<string, string>()
                        {
                            {"class", Configuration["CASSANDRA:REPLICATION_CLASS"]},
                            {"replication_factor", Configuration["CASSANDRA:REPLICATION_FACTOR"]}
                        });
                        logger.LogInformation("Created cassandra keyspace");
                    }
                    catch (Exception exception)
                    {
                        logger.LogError(exception, "Startup migration for keyspace {keyspace} failed", defaultKeyspace);
                    }
                }

                session.ChangeKeyspace(defaultKeyspace);
            }
            return session;
        });
    }
}