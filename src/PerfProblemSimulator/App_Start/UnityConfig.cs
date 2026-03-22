using Microsoft.AspNet.SignalR;
using NLog;
using PerfProblemSimulator.Hubs;
using PerfProblemSimulator.Services;
using System.Web.Http;
using Unity;
using Unity.Lifetime;
using Unity.WebApi;

namespace PerfProblemSimulator.App_Start
{
    /// <summary>
    /// Unity dependency injection configuration.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PORTING NOTE from ASP.NET Core:</strong>
    /// This replaces the builder.Services.Add* calls in Program.cs.
    /// 
    /// Lifetime mappings:
    /// - ASP.NET Core AddSingleton() → Unity ContainerControlledLifetimeManager
    /// - ASP.NET Core AddTransient() → Unity TransientLifetimeManager
    /// - ASP.NET Core AddScoped() → Unity HierarchicalLifetimeManager
    /// </para>
    /// </remarks>
    public static class UnityConfig
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly IUnityContainer ContainerInstance = new UnityContainer();

        /// <summary>
        /// Gets the Unity container instance.
        /// </summary>
        public static IUnityContainer Container => ContainerInstance;

        /// <summary>
        /// Configures Unity for Web API dependency injection.
        /// </summary>
        public static void ConfigureWebApi(HttpConfiguration config)
        {
            RegisterServices(ContainerInstance);
            config.DependencyResolver = new UnityDependencyResolver(ContainerInstance);
            Logger.Info("Unity configured for Web API");
        }

        /// <summary>
        /// Configures Unity for SignalR dependency injection.
        /// </summary>
        public static void ConfigureSignalR(IDependencyResolver resolver)
        {
            // Register SignalR hub with Unity container
            // SignalR will use the default resolver but we provide our services
            Logger.Info("Unity configured for SignalR");
        }

        /// <summary>
        /// Registers all application services with the Unity container.
        /// </summary>
        private static void RegisterServices(IUnityContainer container)
        {
            Logger.Info("Registering services with Unity container...");

            // -----------------------------------------------------------------------------
            // Singleton Services (one instance for app lifetime)
            // -----------------------------------------------------------------------------

            // SimulationTelemetry - Tracks simulation events to Application Insights
            // Auto-configures from APPLICATIONINSIGHTS_CONNECTION_STRING if available
            container.RegisterType<ISimulationTelemetry, SimulationTelemetry>(
                new ContainerControlledLifetimeManager());

            // IdleStateService - Manages application idle state
            container.RegisterType<IIdleStateService, IdleStateService>(
                new ContainerControlledLifetimeManager());

            // SimulationTracker - Tracks all active simulations
            container.RegisterType<ISimulationTracker, SimulationTracker>(
                new ContainerControlledLifetimeManager());

            // MemoryPressureService - Manages memory allocations (must be singleton to hold memory)
            container.RegisterType<IMemoryPressureService, MemoryPressureService>(
                new ContainerControlledLifetimeManager());

            // SlowRequestService - Manages slow request simulations
            container.RegisterType<ISlowRequestService, SlowRequestService>(
                new ContainerControlledLifetimeManager());

            // LoadTestService - Manages load test state
            container.RegisterType<ILoadTestService, LoadTestService>(
                new ContainerControlledLifetimeManager());

            // FailedRequestService - Manages failed request simulations
            container.RegisterType<IFailedRequestService, FailedRequestService>(
                new ContainerControlledLifetimeManager());

            // MetricsCollector - Collects system metrics on dedicated thread
            container.RegisterType<IMetricsCollector, MetricsCollector>(
                new ContainerControlledLifetimeManager());

            // MetricsBroadcastService - Broadcasts metrics to SignalR clients
            container.RegisterType<MetricsBroadcastService>(
                new ContainerControlledLifetimeManager());

            // LatencyProbeService - Measures request latency
            container.RegisterType<LatencyProbeService>(
                new ContainerControlledLifetimeManager());

            // -----------------------------------------------------------------------------
            // Transient Services (new instance per request)
            // -----------------------------------------------------------------------------

            // CpuStressService - Each request gets its own instance
            container.RegisterType<ICpuStressService, CpuStressService>(
                new TransientLifetimeManager());

            // ThreadBlockService - Each request gets its own instance
            container.RegisterType<IThreadBlockService, ThreadBlockService>(
                new TransientLifetimeManager());

            // CrashService - Each request gets its own instance
            container.RegisterType<ICrashService, CrashService>(
                new TransientLifetimeManager());

            // -----------------------------------------------------------------------------
            // SignalR Hub Context
            // -----------------------------------------------------------------------------
            // Register a factory for getting the SignalR hub context
            container.RegisterFactory<IHubContext<MetricsHub>>(
                _ => GlobalHost.ConnectionManager.GetHubContext<MetricsHub>(),
                new ContainerControlledLifetimeManager());

            Logger.Info("All services registered with Unity container");
        }
    }
}
