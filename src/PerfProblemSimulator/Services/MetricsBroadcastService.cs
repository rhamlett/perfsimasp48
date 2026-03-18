using Microsoft.AspNet.SignalR;
using NLog;
using PerfProblemSimulator.Hubs;
using PerfProblemSimulator.Models;
using System;
using System.Collections.Concurrent;
using System.Threading;

namespace PerfProblemSimulator.Services
{
    /// <summary>
    /// Background service that broadcasts metrics to SignalR clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PURPOSE:</strong>
    /// This service acts as a bridge between the MetricsCollector and SignalR.
    /// It subscribes to metrics events and broadcasts them to all connected clients.
    /// </para>
    /// <para>
    /// <strong>Thread Pool Independence (Critical for Load Testing):</strong>
    /// When the load test endpoint exhausts the thread pool, SignalR broadcasts would
    /// normally freeze because they rely on thread pool threads. To prevent this, we use:
    /// - A dedicated broadcast thread (not from thread pool)
    /// - A message queue (BlockingCollection) for thread-safe message passing
    /// - Fire-and-forget semantics that don't await thread pool continuations
    /// </para>
    /// </remarks>
    public class MetricsBroadcastService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private readonly IMetricsCollector _metricsCollector;
        private readonly ISimulationTracker _simulationTracker;
        private readonly IIdleStateService _idleStateService;

        // Message queue for thread-pool-independent broadcasting
        private readonly BlockingCollection<BroadcastMessage> _messageQueue = new BlockingCollection<BroadcastMessage>(100);
        private Thread _broadcastThread;
        private volatile bool _running;

        /// <summary>
        /// Initializes a new instance of the <see cref="MetricsBroadcastService"/> class.
        /// </summary>
        public MetricsBroadcastService(
            IMetricsCollector metricsCollector,
            ISimulationTracker simulationTracker,
            IIdleStateService idleStateService)
        {
            if (metricsCollector == null) throw new ArgumentNullException("metricsCollector");
            if (simulationTracker == null) throw new ArgumentNullException("simulationTracker");
            if (idleStateService == null) throw new ArgumentNullException("idleStateService");

            _metricsCollector = metricsCollector;
            _simulationTracker = simulationTracker;
            _idleStateService = idleStateService;
        }

        /// <summary>
        /// Starts the metrics broadcast service.
        /// </summary>
        public void Start()
        {
            _running = true;

            // Start dedicated broadcast thread (not from thread pool)
            _broadcastThread = new Thread(BroadcastLoop)
            {
                Name = "SignalR-Broadcast",
                IsBackground = true,
                Priority = ThreadPriority.AboveNormal
            };
            _broadcastThread.Start();

            _metricsCollector.MetricsCollected += OnMetricsCollected;
            _simulationTracker.SimulationStarted += OnSimulationStarted;
            _simulationTracker.SimulationCompleted += OnSimulationCompleted;
            _idleStateService.GoingIdle += OnGoingIdle;
            _idleStateService.WakingUp += OnWakingUp;
            _metricsCollector.Start();

            Logger.Info("Metrics broadcast service started with dedicated broadcast thread");
        }

        /// <summary>
        /// Stops the metrics broadcast service.
        /// </summary>
        public void Stop()
        {
            _running = false;
            _messageQueue.CompleteAdding();

            _metricsCollector.MetricsCollected -= OnMetricsCollected;
            _simulationTracker.SimulationStarted -= OnSimulationStarted;
            _simulationTracker.SimulationCompleted -= OnSimulationCompleted;
            _idleStateService.GoingIdle -= OnGoingIdle;
            _idleStateService.WakingUp -= OnWakingUp;
            _metricsCollector.Stop();

            // Wait for broadcast thread to finish (with timeout)
            if (_broadcastThread != null)
            {
                _broadcastThread.Join(TimeSpan.FromSeconds(5));
            }

            Logger.Info("Metrics broadcast service stopped");
        }

        /// <summary>
        /// Dedicated thread loop that processes broadcast messages.
        /// </summary>
        private void BroadcastLoop()
        {
            Logger.Debug("Broadcast thread started");

            while (_running || _messageQueue.Count > 0)
            {
                try
                {
                    BroadcastMessage message;
                    if (_messageQueue.TryTake(out message, TimeSpan.FromMilliseconds(100)))
                    {
                        ProcessMessage(message);
                    }
                }
                catch (InvalidOperationException)
                {
                    // Collection was marked as complete - exit loop
                    break;
                }
                catch (Exception ex)
                {
                    Logger.Error(ex, "Error in broadcast loop");
                }
            }

            Logger.Debug("Broadcast thread exiting");
        }

        /// <summary>
        /// Process a single broadcast message.
        /// </summary>
        private void ProcessMessage(BroadcastMessage message)
        {
            try
            {
                var hubContext = GlobalHost.ConnectionManager.GetHubContext<MetricsHub>();

                switch (message.Type)
                {
                    case BroadcastType.Metrics:
                        hubContext.Clients.All.receiveMetrics((MetricsSnapshot)message.Data);
                        break;

                    case BroadcastType.SimulationStarted:
                        var startArgs = (SimulationEventArgs)message.Data;
                        Logger.Debug("Broadcast SimulationStarted: {0} {1}", startArgs.Type, startArgs.SimulationId);
                        hubContext.Clients.All.simulationStarted(startArgs.Type.ToString(), startArgs.SimulationId);
                        break;

                    case BroadcastType.SimulationCompleted:
                        var completeArgs = (SimulationEventArgs)message.Data;
                        Logger.Debug("Broadcast SimulationCompleted: {0} {1}", completeArgs.Type, completeArgs.SimulationId);
                        hubContext.Clients.All.simulationCompleted(completeArgs.Type.ToString(), completeArgs.SimulationId);
                        break;

                    case BroadcastType.Latency:
                        hubContext.Clients.All.receiveLatency((LatencyMeasurement)message.Data);
                        break;

                    case BroadcastType.SlowRequestLatency:
                        hubContext.Clients.All.receiveSlowRequestLatency((SlowRequestLatencyData)message.Data);
                        break;

                    case BroadcastType.LoadTestStats:
                        hubContext.Clients.All.receiveLoadTestStats((LoadTestStatsData)message.Data);
                        break;

                    case BroadcastType.IdleState:
                        hubContext.Clients.All.receiveIdleState((IdleStateData)message.Data);
                        break;
                }
            }
            catch (Exception ex)
            {
                Logger.Warn(ex, "Error initiating broadcast for {0} message", message.Type);
            }
        }

        private void OnMetricsCollected(object sender, MetricsSnapshot snapshot)
        {
            _messageQueue.TryAdd(new BroadcastMessage(BroadcastType.Metrics, snapshot));
        }

        private void OnSimulationStarted(object sender, SimulationEventArgs e)
        {
            _messageQueue.TryAdd(new BroadcastMessage(BroadcastType.SimulationStarted, e));
        }

        private void OnSimulationCompleted(object sender, SimulationEventArgs e)
        {
            _messageQueue.TryAdd(new BroadcastMessage(BroadcastType.SimulationCompleted, e));
        }

        private void OnGoingIdle(object sender, EventArgs e)
        {
            var idleData = new IdleStateData
            {
                IsIdle = true,
                Message = "Application going idle, no health probes being sent. There will be gaps in diagnostics and logs.",
                Timestamp = DateTimeOffset.UtcNow
            };
            _messageQueue.TryAdd(new BroadcastMessage(BroadcastType.IdleState, idleData));
        }

        private void OnWakingUp(object sender, EventArgs e)
        {
            var idleData = new IdleStateData
            {
                IsIdle = false,
                Message = "App waking up from idle state. There may be gaps in diagnostics and logs.",
                Timestamp = DateTimeOffset.UtcNow
            };
            _messageQueue.TryAdd(new BroadcastMessage(BroadcastType.IdleState, idleData));
        }

        /// <summary>
        /// Message types for the broadcast queue.
        /// </summary>
        private enum BroadcastType
        {
            Metrics,
            SimulationStarted,
            SimulationCompleted,
            Latency,
            SlowRequestLatency,
            LoadTestStats,
            IdleState
        }

        /// <summary>
        /// Wrapper for broadcast messages in the queue.
        /// </summary>
        private class BroadcastMessage
        {
            public BroadcastType Type { get; private set; }
            public object Data { get; private set; }

            public BroadcastMessage(BroadcastType type, object data)
            {
                Type = type;
                Data = data;
            }
        }

        /// <summary>
        /// Queues a latency measurement for broadcast.
        /// </summary>
        public void QueueLatencyBroadcast(LatencyMeasurement measurement)
        {
            _messageQueue.TryAdd(new BroadcastMessage(BroadcastType.Latency, measurement));
        }

        /// <summary>
        /// Queues slow request latency data for broadcast.
        /// </summary>
        public void QueueSlowRequestLatencyBroadcast(SlowRequestLatencyData data)
        {
            _messageQueue.TryAdd(new BroadcastMessage(BroadcastType.SlowRequestLatency, data));
        }

        /// <summary>
        /// Queues load test stats for broadcast.
        /// </summary>
        public void QueueLoadTestStatsBroadcast(LoadTestStatsData data)
        {
            _messageQueue.TryAdd(new BroadcastMessage(BroadcastType.LoadTestStats, data));
        }
    }
}
