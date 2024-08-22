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
using Microsoft.OpenApi.Models;
using Prometheus;
using MongoDB.Driver;
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
        services.AddControllers().AddNewtonsoftJson();
        services.AddSwaggerGen(c =>
        {
            c.SwaggerDoc("v1", new OpenApiInfo { Title = "SkyPlayerState", Version = "v1" });
            // Set the comments path for the Swagger JSON and UI.
            var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
            var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
            c.IncludeXmlComments(xmlPath);
        });

        // Replace with your server version and type.
        // Use 'MariaDbServerVersion' for MariaDB.
        // Alternatively, use 'ServerVersion.AutoDetect(connectionString)'.
        // For common usages, see pull request #1233.
        //var serverVersion = new MariaDbServerVersion(new Version(Configuration["MARIADB_VERSION"]));

        // Replace 'YourDbContext' with the name of your own DbContext derived class.
        /*services.AddDbContext<PlayerStateDbContext>(
            dbContextOptions => dbContextOptions
                .UseMySql(Configuration["DB_CONNECTION"], serverVersion)
                .EnableSensitiveDataLogging() // <-- These two calls are optional but help
                .EnableDetailedErrors()       // <-- with debugging (remove for production).
        );*/
        services.AddSingleton(a => new MongoClient(
            Configuration["Mongo:ConnectionString"]
        ));

        if (Configuration["MIGRATOR"] == "true")
        {
            services.AddHostedService<MigrationService>();
        }
        else
            services.AddHostedService<PlayerStateBackgroundService>();
        services.AddJaeger(Configuration);
        services.AddResponseCaching();
        services.AddResponseCompression();

        services.Configure<MongoSettings>(Configuration.GetSection("Mongo"));
        services.AddSingleton<IItemsService, ItemsService>();
        services.AddSingleton<CoinParser>();
        services.AddSingleton<ITradeService, TradeService>();
        services.AddSingleton<Kafka.KafkaCreator>();
        services.AddSingleton<IPersistenceService, PersistenceService>();
        services.AddSingleton<ITransactionService, TransactionService>();
        services.AddSingleton<ICassandraService>(di => di.GetRequiredService<ITransactionService>() as ICassandraService 
                    ?? throw new Exception("ITransactionService is not a ICassandraService"));
        services.AddSingleton<IMessageApi>(sp => new MessageApi(Configuration["EVENTS_BASE_URL"]));
        services.AddSingleton<IScheduleApi>(sp => new ScheduleApi(Configuration["EVENTS_BASE_URL"]));
        services.AddSingleton<IPlayerNameApi>(sp => new PlayerNameApi(Configuration["PLAYERNAME_BASE_URL"]));
        services.AddSingleton<IBaseApi>(sp => new BaseApi(Configuration["PROXY_BASE_URL"]));
        services.AddSingleton<IOrderBookApi>(sp => new OrderBookApi(Configuration["BAZAAR_BASE_URL"]));

        services.AddSingleton<IItemsApi>(context => new ItemsApi(Configuration["ITEMS_BASE_URL"]));
        services.AddSingleton<IAuctionsApi>(context => new AuctionsApi(Configuration["API_BASE_URL"]));
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
            Console.WriteLine("Connecting to Scylla...");
            var builder = Cluster.Builder().AddContactPoints(Configuration["SCYLLA:HOSTS"].Split(","))
                .WithLoadBalancingPolicy(new TokenAwarePolicy(new DCAwareRoundRobinPolicy()))
                .WithCredentials(Configuration["SCYLLA:USER"], Configuration["SCYLLA:PASSWORD"])
                .WithDefaultKeyspace(Configuration["SCYLLA:KEYSPACE"]);

            Console.WriteLine("Connecting to servers " + Configuration["SCYLLA:HOSTS"]);
            Console.WriteLine("Using keyspace " + Configuration["SCYLLA:KEYSPACE"]);
            Console.WriteLine("Using replication class " + Configuration["SCYLLA:REPLICATION_CLASS"]);
            Console.WriteLine("Using replication factor " + Configuration["SCYLLA:REPLICATION_FACTOR"]);
            Console.WriteLine("Using user " + Configuration["SCYLLA:USER"]);
            Console.WriteLine("Using password " + Configuration["SCYLLA:PASSWORD"].Truncate(2) + "...");
            var certificatePaths = Configuration["SCYLLA:X509Certificate_PATHS"];
            Console.WriteLine("Using certificate paths " + certificatePaths);
            Console.WriteLine("Using certificate password " + Configuration["SCYLLA:X509Certificate_PASSWORD"].Truncate(2) + "...");
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
            try
            {
                session.CreateKeyspaceIfNotExists(defaultKeyspace, new Dictionary<string, string>()
                {
                    {"class", Configuration["CASSANDRA:REPLICATION_CLASS"]},
                    {"replication_factor", Configuration["CASSANDRA:REPLICATION_FACTOR"]}
                });
                session.ChangeKeyspace(defaultKeyspace);
                Console.WriteLine("Created cassandra keyspace");
            }
            catch (UnauthorizedException)
            {
                Console.WriteLine("User unauthorized to create keyspace, trying to connect directly");
            }
            finally
            {
                session.ChangeKeyspace(defaultKeyspace);
            }
            return session;
        });
    }
}