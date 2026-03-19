using JetBrains.Annotations;
using Microsoft.AspNet.SignalR;
using NLog;
using PerfProblemSimulator.Models;
using PerfProblemSimulator.Services;
using System;
using System.Threading.Tasks;
using Unity;

namespace PerfProblemSimulator.Hubs
{
    /// <summary>
    /// WebSocket/SignalR hub for real-time metrics broadcasting to dashboard clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PURPOSE:</strong>
    /// Provides a persistent bidirectional communication channel between the server and
    /// connected browser clients. This enables real-time dashboard updates without polling.
    /// </para>
    /// <para>
    /// <strong>ALGORITHM:</strong>
    /// <list type="number">
    /// <item>Browser connects to /hubs/metrics endpoint (WebSocket preferred, with fallbacks)</item>
    /// <item>On connect: immediately send current metrics so client doesn't wait for next tick</item>
    /// <item>Every 1 second: MetricsBroadcastService pushes metrics to all connected clients</item>
    /// <item>On simulation events: push notifications so UI can update status indicators</item>
    /// <item>On disconnect: clean up resources (automatic via framework)</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>SIGNALR 2.x NOTES:</strong>
    /// <list type="bullet">
    /// <item>Uses Hub base class (not Hub&lt;T&gt; as in ASP.NET Core SignalR)</item>
    /// <item>OnConnected/OnDisconnected are synchronous (return Task but typically completed synchronously)</item>
    /// <item>Client method calls use dynamic Clients.Caller.methodName() pattern</item>
    /// </list>
    /// </para>
    /// </remarks>
    public class MetricsHub : Hub
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IMetricsCollector _metricsCollector;
        private readonly IIdleStateService _idleStateService;

        /// <summary>
        /// Initializes a new instance of the <see cref="MetricsHub"/> class.
        /// </summary>
        public MetricsHub(IMetricsCollector metricsCollector, IIdleStateService idleStateService)
        {
            _metricsCollector = metricsCollector ?? throw new ArgumentNullException(nameof(metricsCollector));
            _idleStateService = idleStateService ?? throw new ArgumentNullException(nameof(idleStateService));
        }

        /// <summary>
        /// Parameter-less constructor required for SignalR 2.x default activation.
        /// Services will be resolved from GlobalHost.DependencyResolver.
        /// </summary>
        public MetricsHub()
        {
            _metricsCollector = App_Start.UnityConfig.Container.Resolve<IMetricsCollector>();
            _idleStateService = App_Start.UnityConfig.Container.Resolve<IIdleStateService>();
        }

        /// <summary>
        /// Called when a client connects to the hub.
        /// </summary>
        public override Task OnConnected()
        {
            Logger.Info("Dashboard client connected: {0}", Context.ConnectionId);

            // NOTE: We do NOT auto-wake here. The client explicitly calls WakeUp() on page load.
            // This prevents SignalR auto-reconnects from waking the app unexpectedly.

            // Send current metrics immediately so client doesn't have to wait
            var currentSnapshot = _metricsCollector.LatestSnapshot;
            Clients.Caller.receiveMetrics(currentSnapshot);

            // Send current idle state to the connecting client
            var idleData = new IdleStateData
            {
                IsIdle = _idleStateService.IsIdle,
                Message = _idleStateService.IsIdle 
                    ? "Application is idle, no health probes being sent. There will be gaps in diagnostics and logs."
                    : "Application is active",
                Timestamp = DateTimeOffset.UtcNow
            };
            Clients.Caller.receiveIdleState(idleData);

            return base.OnConnected();
        }

        /// <summary>
        /// Called when a client disconnects from the hub.
        /// </summary>
        public override Task OnDisconnected(bool stopCalled)
        {
            if (!stopCalled)
            {
                Logger.Warn("Dashboard client disconnected unexpectedly: {0}", Context.ConnectionId);
            }
            else
            {
                Logger.Info("Dashboard client disconnected: {0}", Context.ConnectionId);
            }

            return base.OnDisconnected(stopCalled);
        }

        /// <summary>
        /// Client can request the latest metrics snapshot on demand.
        /// </summary>
        [UsedImplicitly]
        public void RequestMetrics()
        {
            // Record activity to prevent idle timeout
            _idleStateService.RecordActivity();
            
            var snapshot = _metricsCollector.LatestSnapshot;
            Clients.Caller.receiveMetrics(snapshot);
        }

        /// <summary>
        /// Client calls this to wake up the server from idle state.
        /// Used when the dashboard page loads or user interacts with it.
        /// </summary>
        [UsedImplicitly]
        public void WakeUp()
        {
            Logger.Debug("WakeUp called from client: {0}, current idle state: {1}", Context.ConnectionId, _idleStateService.IsIdle);
            
            var wasIdle = _idleStateService.WakeUp();
            
            // Always send direct response to the caller for immediate feedback
            // The broadcast service will also notify all clients via WakingUp event
            var idleData = new IdleStateData
            {
                IsIdle = false,
                Message = wasIdle 
                    ? "App waking up from idle state. There may be gaps in diagnostics and logs."
                    : "Application is active",
                Timestamp = DateTimeOffset.UtcNow
            };
            Clients.Caller.receiveIdleState(idleData);
            
            Logger.Debug("WakeUp response sent to client: {0}, wasIdle: {1}", Context.ConnectionId, wasIdle);
            
            if (wasIdle)
            {
                Logger.Info("Server woken up by client request from: {0}", Context.ConnectionId);
            }
        }
    }
}
