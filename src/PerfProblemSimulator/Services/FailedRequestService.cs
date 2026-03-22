using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;
using Microsoft.AspNet.SignalR.Hubs;
using Newtonsoft.Json.Linq;
using NLog;
using PerfProblemSimulator.Hubs;
using PerfProblemSimulator.Models;

namespace PerfProblemSimulator.Services
{

/// <summary>
/// Service that generates failed HTTP requests (5xx responses) for AppLens testing.
/// </summary>
/// <remarks>
/// <para>
/// <strong>PURPOSE:</strong>
/// Generates HTTP 500 errors that appear in Azure AppLens and Application Insights.
/// This is useful for testing error detection, alerting, and diagnosis workflows.
/// </para>
/// <para>
/// <strong>ALGORITHM:</strong>
/// <list type="number">
/// <item>Start() creates a dedicated spawner thread (not thread pool)</item>
/// <item>For each requested failure, make an HTTP POST to /api/loadtest</item>
/// <item>Load test parameters are configured for guaranteed failure:
///   <list type="bullet">
///     <item>baselineDelayMs: 1500 (ensures request takes >1s)</item>
///     <item>errorAfterSeconds: 1 (start error checks after 1 second)</item>
///     <item>errorPercent: 100 (100% probability = guaranteed failure)</item>
///   </list>
/// </item>
/// <item>The load test endpoint throws a random exception → HTTP 500</item>
/// <item>Each failure is logged and tracked for status reporting</item>
/// </list>
/// </para>
/// <para>
/// <strong>WHY USE LOAD TEST ENDPOINT:</strong>
/// The load test endpoint already has error injection logic. By setting errorPercent=100,
/// we guarantee that every request will fail with an exception. This produces authentic
/// HTTP 500 responses that pass through the full ASP.NET pipeline and appear correctly
/// in all Azure monitoring tools.
/// </para>
/// </remarks>
public class FailedRequestService : IFailedRequestService, IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
        private static readonly HttpClient HttpClientInstance = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        private readonly ISimulationTracker _simulationTracker;
        private readonly ISimulationTelemetry _telemetry;

        private CancellationTokenSource _cts;
        private Thread _requestSpawnerThread;
        private volatile bool _isRunning;
        private int _requestsSent;
        private int _requestsCompleted;
        private int _requestsInProgress;
        private int _targetCount;
        private DateTimeOffset? _startedAt;
        private Guid _simulationId;
        private string _baseUrl;

        public bool IsRunning => _isRunning;

        public FailedRequestService(ISimulationTracker simulationTracker, ISimulationTelemetry telemetry)
        {
            if (simulationTracker == null) throw new ArgumentNullException(nameof(simulationTracker));
            _simulationTracker = simulationTracker;
            _telemetry = telemetry;
        }

        public SimulationResult Start(int requestCount)
        {
            if (_isRunning)
            {
                return new SimulationResult
                {
                    SimulationId = _simulationId,
                    Type = SimulationType.FailedRequest,
                    Status = "AlreadyRunning",
                    Message = "Failed request simulation is already running. Stop it first or wait for completion."
                };
            }

                        _simulationId = Guid.NewGuid();
            
            // Set Activity tag for Application Insights correlation (if enabled via Azure portal)
            Activity.Current?.SetTag("SimulationId", _simulationId.ToString());
            
            _cts = new CancellationTokenSource();
            _requestsSent = 0;
            _requestsCompleted = 0;
            _requestsInProgress = 0;
            _targetCount = Math.Max(1, requestCount);
            _startedAt = DateTimeOffset.UtcNow;
            _isRunning = true;

            // Get the base URL for HTTP calls
            _baseUrl = GetBaseUrl();

            var parameters = new Dictionary<string, object>
            {
                ["TargetCount"] = _targetCount
            };

            _simulationTracker.RegisterSimulation(_simulationId, SimulationType.FailedRequest, parameters, _cts);

            // Track simulation start in Application Insights (if configured)
            _telemetry?.TrackSimulationStarted(_simulationId, SimulationType.FailedRequest, parameters);

            // Use dedicated thread (not thread pool) to spawn requests
            _requestSpawnerThread = new Thread(() => SpawnFailedRequestsLoop(_cts.Token))
            {
                Name = string.Format("FailedRequestSpawner-{0:N}", _simulationId),
                IsBackground = true
            };
            _requestSpawnerThread.Start();

            Logger.Warn(
                "Failed request simulation started: {0}. Generating {1} HTTP 500 errors at {2}/api/loadtest",
                _simulationId, _targetCount, _baseUrl);

            return new SimulationResult
            {
                SimulationId = _simulationId,
                Type = SimulationType.FailedRequest,
                Status = "Started",
                Message = string.Format("Generating {0} failed requests (HTTP 500 errors). " +
                          "These will appear in AppLens and Application Insights failure metrics. " +
                          "Each request takes ~1.5 seconds before failing.", _targetCount),
                ActualParameters = parameters,
                StartedAt = _startedAt.Value
            };
        }

    public SimulationResult Stop()
    {
        if (!_isRunning)
        {
            return new SimulationResult
            {
                SimulationId = Guid.Empty,
                Type = SimulationType.FailedRequest,
                Status = "NotRunning",
                Message = "No failed request simulation is running."
            };
        }

        if (_cts != null) _cts.Cancel();
            _isRunning = false;
            _simulationTracker.UnregisterSimulation(_simulationId);

            // Track simulation end in Application Insights (if configured)
            _telemetry?.TrackSimulationEnded(_simulationId, SimulationType.FailedRequest, "Stopped");

            Logger.Info(
                "Failed request simulation stopped: {0}. Sent={1}, Completed={2}, InProgress={3}",
                _simulationId, _requestsSent, _requestsCompleted, _requestsInProgress);

            return new SimulationResult
            {
                SimulationId = _simulationId,
                Type = SimulationType.FailedRequest,
                Status = "Stopped",
                Message = string.Format("Failed request simulation stopped. Sent: {0}, Completed: {1}, Still in progress: {2}",
                    _requestsSent, _requestsCompleted, _requestsInProgress),
                ActualParameters = new Dictionary<string, object>
                {
                    ["RequestsSent"] = _requestsSent,
                    ["RequestsCompleted"] = _requestsCompleted,
                    ["TargetCount"] = _targetCount
                }
            };
        }

        public FailedRequestStatus GetStatus()
        {
            return new FailedRequestStatus
            {
                IsRunning = _isRunning,
                RequestsSent = _requestsSent,
                RequestsCompleted = _requestsCompleted,
                RequestsInProgress = _requestsInProgress,
                TargetCount = _targetCount,
                StartedAt = _startedAt
            };
        }

        /// <summary>
        /// Gets the base URL of the current application for self-calls.
        /// </summary>
        private string GetBaseUrl()
        {
            var websiteHostname = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
            if (!string.IsNullOrEmpty(websiteHostname))
            {
                return "https://" + websiteHostname;
            }

            // Fallback to localhost
            return "http://localhost";
        }

        /// <summary>
        /// Main loop that spawns failed requests.
        /// </summary>
        private void SpawnFailedRequestsLoop(CancellationToken cancellationToken)
        {
            try
            {
                Logger.Info("Failed request spawner started for simulation {0}", _simulationId);

                // Short initial delay to let any startup settle
                Thread.Sleep(500);

                while (!cancellationToken.IsCancellationRequested && _requestsSent < _targetCount)
                {
                    // Fire the request asynchronously and continue to next
                    Task.Run(() => SendFailedRequest(cancellationToken), cancellationToken);

                    Interlocked.Increment(ref _requestsSent);
                    Interlocked.Increment(ref _requestsInProgress);

                    // Small delay between requests to spread them out
                    // This makes them more visible as individual data points
                    Thread.Sleep(200);
                }

                // Wait for in-progress requests to complete (with timeout)
                var waitStart = DateTime.UtcNow;
                while (_requestsInProgress > 0 && (DateTime.UtcNow - waitStart).TotalSeconds < 30)
                {
                    Thread.Sleep(500);
                }

                Logger.Info(
                    "Failed request simulation completed: {0}. Sent={1}, Completed={2}",
                    _simulationId, _requestsSent, _requestsCompleted);
            }
            catch (OperationCanceledException)
            {
                Logger.Info("Failed request spawner cancelled for {0}", _simulationId);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in failed request spawner for {0}", _simulationId);
            }
            finally
            {
                _isRunning = false;
                _simulationTracker.UnregisterSimulation(_simulationId);

                // Track simulation completion in Application Insights (if configured)
                _telemetry?.TrackSimulationEnded(_simulationId, SimulationType.FailedRequest, "Completed");

                Logger.Info(
                    "Failed request simulation {0} completed. Generated {1} HTTP 500 errors.",
                    _simulationId, _requestsCompleted);
            }
        }

        /// <summary>
        /// Sends a single request to the load test endpoint with error-guaranteed parameters.
        /// </summary>
        private async Task SendFailedRequest(CancellationToken cancellationToken)
        {
            var requestId = Guid.NewGuid();
            var stopwatch = Stopwatch.StartNew();

            try
            {
                // Build query string for GET request (LoadTestController uses [HttpGet] with [FromQuery] params)
                var queryParams = "?baselineDelayMs=1500" +
                                 "&workIterations=500" +
                                 "&bufferSizeKb=100" +
                                 "&softLimit=10000" +
                                 "&degradationFactor=0" +
                                 "&errorAfter=1" +
                                 "&errorPercent=100";

                var requestUrl = string.Format("{0}/api/loadtest{1}", _baseUrl, queryParams);

                Logger.Debug("Sending failed request {0} to {1}", requestId, requestUrl);

                var response = await HttpClientInstance.GetAsync(requestUrl, cancellationToken);

                stopwatch.Stop();

                // Read the response body to extract error details
                string errorType = null;
                if ((int)response.StatusCode >= 500)
                {
                    try
                    {
                        var responseBody = await response.Content.ReadAsStringAsync();
                        errorType = ExtractExceptionType(responseBody);
                    }
                    catch
                    {
                        // Ignore parsing errors
                    }

                    Logger.Info(
                        "Failed request {0} completed with expected HTTP {1} ({2}) in {3}ms",
                        requestId, (int)response.StatusCode, errorType ?? "Unknown", stopwatch.ElapsedMilliseconds);
                }
                else
                {
                    // If we get a non-5xx response, log it as unexpected
                    Logger.Warn(
                        "Failed request {0} got unexpected HTTP {1} (expected 5xx) in {2}ms",
                        requestId, (int)response.StatusCode, stopwatch.ElapsedMilliseconds);
                }

                // Broadcast latency to dashboard for visibility
                BroadcastFailedRequestEvent(new LatencyMeasurement
                {
                    Timestamp = DateTimeOffset.UtcNow,
                    LatencyMs = stopwatch.ElapsedMilliseconds,
                    IsTimeout = false,
                    IsError = true,
                    ErrorMessage = errorType ?? "Unknown Error",
                    Source = "FailedRequest"
                });
            }
            catch (HttpRequestException ex)
            {
                stopwatch.Stop();
                // HTTP errors (like 500) that throw exceptions - this is expected!
                Logger.Info(
                    "Failed request {0} threw expected exception in {1}ms: {2}",
                    requestId, stopwatch.ElapsedMilliseconds, ex.Message);
            }
            catch (TaskCanceledException)
            {
                Logger.Debug("Failed request {0} was cancelled", requestId);
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Logger.Warn(ex,
                    "Failed request {0} encountered unexpected error in {1}ms",
                    requestId, stopwatch.ElapsedMilliseconds);
            }
            finally
            {
                Interlocked.Increment(ref _requestsCompleted);
                Interlocked.Decrement(ref _requestsInProgress);
            }
        }

        /// <summary>
        /// Broadcasts a failed request event to all connected dashboard clients.
        /// Uses untyped hub context with camelCase method names to match JavaScript client.
        /// </summary>
        private void BroadcastFailedRequestEvent(LatencyMeasurement measurement)
        {
            try
            {
                var hubContext = GlobalHost.ConnectionManager.GetHubContext<MetricsHub>();
                hubContext.Clients.All.receiveLatency(measurement);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error broadcasting failed request event");
            }
        }

        public void Dispose()
        {
            if (_cts != null)
            {
                _cts.Cancel();
                _cts.Dispose();
            }
        }

        /// <summary>
        /// Extracts the exception type from the error response body.
        /// </summary>
        private static string ExtractExceptionType(string responseBody)
        {
            if (string.IsNullOrWhiteSpace(responseBody))
                return null;

            // Check for plain text exception format (e.g., "System.InvalidOperationException: message")
            var colonIndex = responseBody.IndexOf(':');
            if (colonIndex > 0)
            {
                var potentialType = responseBody.Substring(0, colonIndex).Trim();
                // Check if it looks like an exception type (contains Exception or Error)
                if (potentialType.Contains("Exception") || potentialType.Contains("Error"))
                {
                    // Extract just the class name
                    var lastDot = potentialType.LastIndexOf('.');
                    return lastDot >= 0 ? potentialType.Substring(lastDot + 1) : potentialType;
                }
            }

            // Try to parse as JSON and look for type or title field
            try
            {
                var obj = JObject.Parse(responseBody);

                // Problem Details format
                var typeStr = obj["type"]?.ToString();
                if (!string.IsNullOrEmpty(typeStr) && typeStr.Contains("Exception"))
                {
                    var lastDot = typeStr.LastIndexOf('.');
                    return lastDot >= 0 ? typeStr.Substring(lastDot + 1) : typeStr;
                }

                // Try title from Problem Details
                var title = obj["title"]?.ToString();
                if (!string.IsNullOrEmpty(title))
                {
                    return title;
                }

                // Custom ErrorResponse format
                var error = obj["error"]?.ToString();
                if (!string.IsNullOrEmpty(error))
                {
                    return error;
                }
            }
            catch
            {
                // Not valid JSON, ignore
            }

            // Fallback: return first 50 chars of response as error type
            return responseBody.Length > 50 ? responseBody.Substring(0, 50) + "..." : responseBody;
        }
    }
}
