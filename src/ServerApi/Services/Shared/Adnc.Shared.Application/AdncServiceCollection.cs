﻿using Adnc.Infra.Consul.Consumer;
using Adnc.Infra.EfCore.MySQL;
using Adnc.Infra.EventBus.RabbitMq;
using Adnc.Infra.Mongo.Configuration;
using Adnc.Infra.Mongo.Extensions;
using Adnc.Shared.ConfigModels;
using Adnc.Shared.RpcServices;
using DotNetCore.CAP;
using Microsoft.EntityFrameworkCore;
using Polly.Timeout;
using Refit;
using SkyApm.Diagnostics.CAP;
using System.Net.Http;

namespace Adnc.Shared.Application
{
    public abstract class AdncServiceCollection : IAdncServiceCollection
    {
        protected readonly IServiceCollection _services;
        protected readonly IConfiguration _configuration;
        protected readonly IServiceInfo _serviceInfo;
        public bool IsDevelopment { get; set; }

        protected AdncServiceCollection(IServiceCollection services)
        {
            _services = services;
            _configuration = services.GetConfiguration();
            _serviceInfo = services.GetServiceInfo();
            IsDevelopment = Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT").EqualsIgnoreCase("Development");
        }

        public abstract void AddAdncServices();

        /// <summary>
        /// 注册EFCoreContext
        /// </summary>
        protected virtual void AddEfCoreContext()
        {
            var mysqlConfig = _configuration.GetMysqlSection().Get<MysqlConfig>();
            var serverVersion = new MariaDbServerVersion(new Version(10, 5, 4));
            _services.AddDbContext<AdncDbContext>(options =>
            {
                options.UseMySql(mysqlConfig.ConnectionString, serverVersion, optionsBuilder =>
                {
                    optionsBuilder.MinBatchSize(4)
                                            .CommandTimeout(10)
                                            .MigrationsAssembly(_serviceInfo.AssemblyName.Replace("WebApi", "Migrations"))
                                            .UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                });

                if (IsDevelopment)
                {
                    //options.AddInterceptors(new DefaultDbCommandInterceptor())
                    options.LogTo(Console.WriteLine, LogLevel.Information)
                                .EnableSensitiveDataLogging()
                                .EnableDetailedErrors();
                }
                //替换默认查询sql生成器,如果通过mycat中间件实现读写分离需要替换默认SQL工厂。
                //options.ReplaceService<IQuerySqlGeneratorFactory, AdncMySqlQuerySqlGeneratorFactory>();
            });
        }

        /// <summary>
        /// 注册MongoContext
        /// </summary>
        protected virtual void AddMongoContext()
        {
            var mongoConfig = _configuration.GetMongoDbSection().Get<MongoConfig>();
            _services.AddMongo<MongoContext>(options =>
            {
                options.ConnectionString = mongoConfig.ConnectionString;
                options.PluralizeCollectionNames = mongoConfig.PluralizeCollectionNames;
                options.CollectionNamingConvention = (NamingConvention)mongoConfig.CollectionNamingConvention;
            });
        }

        /// <summary>
        /// 注册CAP组件的订阅者(实现事件总线及最终一致性（分布式事务）的一个开源的组件)
        /// </summary>
        /// <param name="tableNamePrefix">cap表面前缀</param>
        /// <param name="groupName">群组名子</param>
        /// <param name="func">回调函数</param>
        protected virtual void AddEventBusSubscribers<TSubscriber>()
            where TSubscriber : class, ICapSubscribe
        {
            var tableNamePrefix = "cap";
            var groupName = $"cap.{_serviceInfo.ShortName}.{System.Environment.GetEnvironmentVariable("ASPNETCORE_ENVIRONMENT").ToLower()}";

            //add skyamp
            _services.AddSkyApmExtensions().AddCap();

            _services.AddSingleton<TSubscriber>();

            var rabbitMqConfig = _configuration.GetRabbitMqSection().Get<RabbitMqConfig>();
            _services.AddCap(x =>
            {
                //如果你使用的 EF 进行数据操作，你需要添加如下配置：
                //可选项，你不需要再次配置 x.UseSqlServer 了
                //x.UseEntityFramework<AdncDbContext>(option =>
                //{
                //    option.TableNamePrefix = tableNamePrefix;
                //});
                var mysqlConfig = _configuration.GetMysqlSection().Get<MysqlConfig>();
                x.UseMySql(config =>
                {
                    config.ConnectionString = mysqlConfig.ConnectionString;
                    config.TableNamePrefix = tableNamePrefix;
                });

                //CAP支持 RabbitMQ、Kafka、AzureServiceBus 等作为MQ，根据使用选择配置：
                x.UseRabbitMQ(option =>
                {
                    option.HostName = rabbitMqConfig.HostName;
                    option.VirtualHost = rabbitMqConfig.VirtualHost;
                    option.Port = rabbitMqConfig.Port;
                    option.UserName = rabbitMqConfig.UserName;
                    option.Password = rabbitMqConfig.Password;
                });
                x.Version = _serviceInfo.Version;
                //默认值：cap.queue.{程序集名称},在 RabbitMQ 中映射到 Queue Names。
                x.DefaultGroupName = groupName;
                //默认值：60 秒,重试 & 间隔
                //在默认情况下，重试将在发送和消费消息失败的 4分钟后 开始，这是为了避免设置消息状态延迟导致可能出现的问题。
                //发送和消费消息的过程中失败会立即重试 3 次，在 3 次以后将进入重试轮询，此时 FailedRetryInterval 配置才会生效。
                x.FailedRetryInterval = 60;
                //默认值：50,重试的最大次数。当达到此设置值时，将不会再继续重试，通过改变此参数来设置重试的最大次数。
                x.FailedRetryCount = 50;
                //默认值：NULL,重试阈值的失败回调。当重试达到 FailedRetryCount 设置的值的时候，将调用此 Action 回调
                //，你可以通过指定此回调来接收失败达到最大的通知，以做出人工介入。例如发送邮件或者短信。
                x.FailedThresholdCallback = (failed) =>
                {
                    //todo
                };
                //默认值：24*3600 秒（1天后),成功消息的过期时间（秒）。
                //当消息发送或者消费成功时候，在时间达到 SucceedMessageExpiredAfter 秒时候将会从 Persistent 中删除，你可以通过指定此值来设置过期的时间。
                x.SucceedMessageExpiredAfter = 24 * 3600;
                //默认值：1,消费者线程并行处理消息的线程数，当这个值大于1时，将不能保证消息执行的顺序。
                x.ConsumerThreadCount = 1;
                x.UseDashboard(x =>
                {
                    x.PathMatch = $"/{_serviceInfo.ShortName}/cap";
                    x.UseAuth = false;
                });

                /* CAP目前不需要自动注册，先注释
                //必须是生产环境才注册cap服务到consul
                if ((_environment.IsProduction() || _environment.IsStaging()))
                {
                    x.UseDiscovery(discoverOptions =>
                    {
                        var consulConfig = _configuration.GetConsulSection().Get<ConsulConfig>();
                        var consulAdderss = new Uri(consulConfig.ConsulUrl);

                        var hostIps = NetworkInterface
                                                                        .GetAllNetworkInterfaces()
                                                                        .Where(network => network.OperationalStatus == OperationalStatus.Up)
                                                                        .Select(network => network.GetIPProperties())
                                                                        .OrderByDescending(properties => properties.GatewayAddresses.Count)
                                                                        .SelectMany(properties => properties.UnicastAddresses)
                                                                        .Where(address => !IPAddress.IsLoopback(address.Address) && address.Address.AddressFamily == AddressFamily.InterNetwork)
                                                                        .ToArray();

                        var currenServerAddress = hostIps.First().Address.MapToIPv4().ToString();

                        discoverOptions.DiscoveryServerHostName = consulAdderss.Host;
                        discoverOptions.DiscoveryServerPort = consulAdderss.Port;
                        discoverOptions.CurrentNodeHostName = currenServerAddress;
                        discoverOptions.CurrentNodePort = 80;
                        discoverOptions.NodeId = DateTime.Now.Ticks.ToString();
                        discoverOptions.NodeName = _serviceInfo.FullName.Replace("webapi", "cap");
                        discoverOptions.MatchPath = $"/{_serviceInfo.ShortName}/cap";
                    });
                }
                */
            });
        }

        /// <summary>
        /// 注册Rpc服务(跨微服务之间的同步通讯)
        /// </summary>
        /// <typeparam name="TRpcService">Rpc服务接口</typeparam>
        /// <param name="serviceName">在注册中心注册的服务名称，或者服务的Url</param>
        /// <param name="policies">Polly策略</param>
        protected virtual void AddRpcService<TRpcService>(string serviceName
        , List<IAsyncPolicy<HttpResponseMessage>> policies)
         where TRpcService : class, IRpcService
        {
            var prefix = serviceName.Substring(0, 7);
            bool isConsulAdderss = prefix != "http://" && prefix != "https:/";

            var refitSettings = new RefitSettings(new SystemTextJsonContentSerializer(SystemTextJson.GetAdncDefaultOptions()));
            //注册RefitClient,设置httpclient生命周期时间，默认也是2分钟。
            var clientbuilder = _services.AddRefitClient<TRpcService>(refitSettings)
                                                         .SetHandlerLifetime(TimeSpan.FromMinutes(2));
            //如果参数是服务名字，那么需要从consul获取地址
            if (isConsulAdderss)
                clientbuilder.ConfigureHttpClient(client => client.BaseAddress = new Uri($"http://{serviceName}"))
                                    .AddHttpMessageHandler<ConsulDiscoverDelegatingHandler>();
            else
                clientbuilder.ConfigureHttpClient(client => client.BaseAddress = new Uri(serviceName))
                                    .AddHttpMessageHandler<SimpleDiscoveryDelegatingHandler>();

            //添加polly相关策略
            policies?.ForEach(policy => clientbuilder.AddPolicyHandler(policy));
        }

        /// <summary>
        /// 生成默认的Polly策略
        /// </summary>
        /// <returns></returns>
        protected virtual List<IAsyncPolicy<HttpResponseMessage>> GenerateDefaultRefitPolicies()
        {
            //隔离策略
            //var bulkheadPolicy = Policy.BulkheadAsync<HttpResponseMessage>(10, 100);

            //回退策略
            //回退也称服务降级，用来指定发生故障时的备用方案。
            //目前用不上
            //var fallbackPolicy = Policy<string>.Handle<HttpRequestException>().FallbackAsync("substitute data");

            //缓存策略
            //缓存策略无效
            //https://github.com/App-vNext/Polly/wiki/Polly-and-HttpClientFactory?WT.mc_id=-blog-scottha#user-content-use-case-cachep
            //var cache = new MemoryCache(Options.Create(new MemoryCacheOptions()));
            //var cacheProvider = new MemoryCacheProvider(cache);
            //var cachePolicy = Policy.CacheAsync<HttpResponseMessage>(cacheProvider, TimeSpan.FromSeconds(100));

            //重试策略,超时或者API返回>500的错误,重试3次。
            //重试次数会统计到失败次数
            var retryPolicy = Policy.Handle<TimeoutRejectedException>()
                                    .OrResult<HttpResponseMessage>(response => (int)response.StatusCode >= 500)
                                    .WaitAndRetryAsync(new[]
                                    {
                                    TimeSpan.FromSeconds(1),
                                    TimeSpan.FromSeconds(2),
                                    TimeSpan.FromSeconds(4)
                                    });
            //超时策略
            var timeoutPolicy = Policy.TimeoutAsync<HttpResponseMessage>(IsDevelopment ? 10 : 3);

            //熔断策略
            //如下，如果我们的业务代码连续失败50次，就触发熔断(onBreak),就不会再调用我们的业务代码，而是直接抛出BrokenCircuitException异常。
            //当熔断时间10分钟后(durationOfBreak)，切换为HalfOpen状态，触发onHalfOpen事件，此时会再调用一次我们的业务代码
            //，如果调用成功，则触发onReset事件，并解除熔断，恢复初始状态，否则立即切回熔断状态。
            var circuitBreakerPolicy = Policy.Handle<Exception>()
                                             .CircuitBreakerAsync
                                             (
                                                 // 熔断前允许出现几次错误
                                                 exceptionsAllowedBeforeBreaking: 10
                                                 ,
                                                 // 熔断时间,熔断10分钟
                                                 durationOfBreak: TimeSpan.FromMinutes(3)
                                                 ,
                                                 // 熔断时触发
                                                 onBreak: (ex, breakDelay) =>
                                                 {
                                                     //todo
                                                     var e = ex;
                                                     var delay = breakDelay;
                                                 }
                                                 ,
                                                 //熔断恢复时触发
                                                 onReset: () =>
                                                 {
                                                     //todo
                                                 }
                                                 ,
                                                 //在熔断时间到了之后触发
                                                 onHalfOpen: () =>
                                                 {
                                                     //todo
                                                 }
                                             );

            return new List<IAsyncPolicy<HttpResponseMessage>>()
        {
            retryPolicy
           ,timeoutPolicy
           ,circuitBreakerPolicy.AsAsyncPolicy<HttpResponseMessage>()
        };
        }

        /// <summary>
        /// 添加任务调度
        /// </summary>
        /// <typeparam name="TSchedulingJob"></typeparam>
        //public virtual void AddSchedulingJobs<TSchedulingJob>()
        //    where TSchedulingJob : class, IRecurringSchedulingJobs
        //{
        //    _schedulingJobs = _schedulingJobs.Concat(new[] { typeof(TSchedulingJob) });
        //}

        /// <summary>
        ///  注册 Hangfire 任务调度
        /// </summary>
        //public virtual void AddHangfire()
        //{
        //    var hangfireConfig = _configuration.GetHangfireSection().Get<HangfireConfig>();

        //    var mongoUrlBuilder = new MongoUrlBuilder(hangfireConfig.ConnectionString);
        //    var mongoClient = new MongoClient(mongoUrlBuilder.ToMongoUrl());

        //    _services.AddHangfire(config =>
        //    {
        //        config.SetDataCompatibilityLevel(CompatibilityLevel.Version_170);
        //        config.UseSimpleAssemblyNameTypeSerializer();
        //        config.UseRecommendedSerializerSettings();
        //        // 设置 MongoDB 为持久化存储器
        //        config.UseMongoStorage(mongoClient, mongoUrlBuilder.DatabaseName, new MongoStorageOptions
        //        {
        //            MigrationOptions = new MongoMigrationOptions
        //            {
        //                MigrationStrategy = new MigrateMongoMigrationStrategy(),
        //                BackupStrategy = new CollectionMongoBackupStrategy()
        //            },
        //            ConnectionCheckTimeout = TimeSpan.FromMinutes(hangfireConfig.ConnectionCheckTimeout),
        //            QueuePollInterval = TimeSpan.FromMinutes(hangfireConfig.QueuePollInterval),
        //            CheckConnection = true,
        //        })
        //        // 任务超时时间
        //        .JobExpirationTimeout = TimeSpan.FromMinutes(hangfireConfig.JobTimeout);
        //        // 打印日志到控制面板和在线进度条展示
        //        config.UseConsole();
        //        config.UseRecurringJob(_schedulingJobs.ToArray());
        //    });
        //    // 将 Hangfire 服务添加为后台托管服务
        //    _services.AddHangfireServer((service, options) =>
        //    {
        //        options.ServerName = $"{_serviceInfo.FullName.Replace(".", "/")}";
        //        options.ShutdownTimeout = TimeSpan.FromMinutes(10);
        //        // 最大 Job 处理并发数量的阈值，根据机子处理器数量设置或默认20
        //        options.WorkerCount = Math.Max(Environment.ProcessorCount, 20);
        //    });
        //}
    }
}