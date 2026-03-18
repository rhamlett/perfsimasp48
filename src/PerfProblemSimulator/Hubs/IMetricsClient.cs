using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PerfProblemSimulator.Models;
using System;
using System.Threading.Tasks;

namespace PerfProblemSimulator.Hubs
{
    /// <summary>
    /// Contract defining real-time messages that can be pushed from server to connected dashboard clients.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PURPOSE:</strong>
    /// This interface defines the "server-to-client" message contract for the real-time communication
    /// channel. Each method represents a distinct message type that the server can push to browsers.
    /// The dashboard JavaScript registers handlers for each message type.
    /// </para>
    /// <para>
    /// <strong>NOTE FOR SIGNALR 2.x:</strong>
    /// This interface is for documentation purposes. SignalR 2.x uses dynamic method invocation
    /// via Clients.All.methodName(args) rather than typed interfaces.
    /// </para>
    /// </remarks>
    public interface IMetricsClient
    {
        /// <summary>
        /// Receives a metrics snapshot update.
        /// </summary>
        Task ReceiveMetrics(MetricsSnapshot snapshot);

        /// <summary>
        /// Receives a simulation started notification.
        /// </summary>
        Task SimulationStarted(string simulationType, Guid simulationId);

        /// <summary>
        /// Receives a simulation completed notification.
        /// </summary>
        Task SimulationCompleted(string simulationType, Guid simulationId);

        /// <summary>
        /// Receives a latency measurement from the server-side probe.
        /// </summary>
        Task ReceiveLatency(LatencyMeasurement measurement);

        /// <summary>
        /// Receives slow request latency data from the server.
        /// </summary>
        Task ReceiveSlowRequestLatency(SlowRequestLatencyData data);

        /// <summary>
        /// Receives load test statistics update for event log display.
        /// </summary>
        Task ReceiveLoadTestStats(LoadTestStatsData data);

        /// <summary>
        /// Notifies client that the application is going idle.
        /// </summary>
        Task ReceiveIdleState(IdleStateData data);
    }

    /// <summary>
    /// Data about the application's idle state change.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class IdleStateData
    {
        /// <summary>
        /// Whether the application is now idle.
        /// </summary>
        public bool IsIdle { get; set; }
        
        /// <summary>
        /// Message to display in the event log.
        /// </summary>
        public string Message { get; set; } = "";
        
        /// <summary>
        /// When this state change occurred.
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }
    }

    /// <summary>
    /// Data about a slow request's latency.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class SlowRequestLatencyData
    {
        /// <summary>
        /// The request number in the simulation.
        /// </summary>
        public int RequestNumber { get; set; }
        
        /// <summary>
        /// The scenario used for this request.
        /// </summary>
        public string Scenario { get; set; } = "";
        
        /// <summary>
        /// The measured latency in milliseconds.
        /// </summary>
        public double LatencyMs { get; set; }
        
        /// <summary>
        /// When this measurement was taken.
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// The expected duration of the request in milliseconds (Processing Time).
        /// </summary>
        public double ExpectedDurationMs { get; set; }

        /// <summary>
        /// Whether the request failed or timed out.
        /// </summary>
        public bool IsError { get; set; }

        /// <summary>
        /// Error message if the request failed.
        /// </summary>
        public string ErrorMessage { get; set; } = "";
    }

    /// <summary>
    /// Data about load test endpoint statistics for event log display.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class LoadTestStatsData
    {
        /// <summary>
        /// Current number of concurrent requests being processed.
        /// </summary>
        public int CurrentConcurrent { get; set; }
        
        /// <summary>
        /// Peak concurrent requests observed in this reporting period.
        /// </summary>
        public int PeakConcurrent { get; set; }
        
        /// <summary>
        /// Total requests completed in this reporting period.
        /// </summary>
        public long RequestsCompleted { get; set; }
        
        /// <summary>
        /// Average response time in milliseconds for this period.
        /// </summary>
        public double AvgResponseTimeMs { get; set; }
        
        /// <summary>
        /// Maximum response time observed in this period.
        /// </summary>
        public double MaxResponseTimeMs { get; set; }
        
        /// <summary>
        /// Requests per second throughput.
        /// </summary>
        public double RequestsPerSecond { get; set; }
        
        /// <summary>
        /// Number of exceptions during this period.
        /// </summary>
        public int ExceptionCount { get; set; }
        
        /// <summary>
        /// When this stats snapshot was captured.
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }
    }

    /// <summary>
    /// Represents a single latency measurement from the health probe or other sources.
    /// </summary>
    [JsonObject(NamingStrategyType = typeof(CamelCaseNamingStrategy))]
    public class LatencyMeasurement
    {
        /// <summary>
        /// When this measurement was taken.
        /// </summary>
        public DateTimeOffset Timestamp { get; set; }

        /// <summary>
        /// Measured latency in milliseconds.
        /// If the request timed out, this equals the timeout value.
        /// </summary>
        public long LatencyMs { get; set; }

        /// <summary>
        /// Whether the request timed out.
        /// </summary>
        public bool IsTimeout { get; set; }

        /// <summary>
        /// Whether the request failed with an error.
        /// </summary>
        public bool IsError { get; set; }

        /// <summary>
        /// Error message if IsError is true.
        /// </summary>
        public string ErrorMessage { get; set; }

        /// <summary>
        /// Source identifier for the latency measurement (e.g., "HealthProbe", "FailedRequest").
        /// </summary>
        public string Source { get; set; }
    }
}
