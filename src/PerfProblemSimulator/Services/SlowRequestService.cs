using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Net.Http;
using System.Threading;
using Microsoft.AspNet.SignalR;
using NLog;
using PerfProblemSimulator.Hubs;
using PerfProblemSimulator.Models;

namespace PerfProblemSimulator.Services
{
    /// <summary>
    /// Service that generates slow HTTP requests to demonstrate thread pool starvation.
    /// </summary>
    public class SlowRequestService : ISlowRequestService, IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly ISimulationTracker _simulationTracker;
        private readonly ISimulationTelemetry _telemetry;
        private readonly Random _random = new Random();

        private CancellationTokenSource _cts;
        private Thread _requestSpawnerThread;
        private volatile bool _isRunning;
        private int _requestsSent;
        private int _requestsCompleted;
        private int _requestsInProgress;
        private int _intervalSeconds;
        private int _requestDurationSeconds;
        private int _maxRequests;
        private DateTimeOffset? _startedAt;
        private Guid _simulationId;
        private readonly Dictionary<string, int> _scenarioCounts = new Dictionary<string, int>();
        private readonly object _lock = new object();
        private string _baseUrl;

        public bool IsRunning => _isRunning;

        public SlowRequestService(ISimulationTracker simulationTracker, ISimulationTelemetry telemetry)
        {
            if (simulationTracker == null) throw new ArgumentNullException("simulationTracker");
            _simulationTracker = simulationTracker;
            _telemetry = telemetry;
        }

        public SimulationResult Start(SlowRequestRequest request)
        {
            if (_isRunning)
            {
                return new SimulationResult
                {
                    SimulationId = _simulationId,
                    Type = SimulationType.SlowRequest,
                    Status = "AlreadyRunning",
                    Message = "Slow request simulation is already running. Stop it first."
                };
            }

            _simulationId = Guid.NewGuid();
            
            // Set Activity tag for Application Insights correlation (if enabled via Azure portal)
            Activity.Current?.SetTag("SimulationId", _simulationId.ToString());
            
            _cts = new CancellationTokenSource();
            _requestsSent = 0;
            _requestsCompleted = 0;
            _requestsInProgress = 0;
            _intervalSeconds = Math.Max(1, request.IntervalSeconds);
            _requestDurationSeconds = Math.Max(5, request.RequestDurationSeconds);
            _maxRequests = request.MaxRequests;
            _startedAt = DateTimeOffset.UtcNow;
            _scenarioCounts.Clear();
            _isRunning = true;
            
            _baseUrl = GetBaseUrl();

            var parameters = new Dictionary<string, object>
            {
                ["IntervalSeconds"] = _intervalSeconds,
                ["RequestDurationSeconds"] = _requestDurationSeconds,
                ["MaxRequests"] = request.MaxRequests
            };

            _simulationTracker.RegisterSimulation(_simulationId, SimulationType.SlowRequest, parameters, _cts);

            // Track simulation start in Application Insights (if configured)
            _telemetry?.TrackSimulationStarted(_simulationId, SimulationType.SlowRequest, parameters);

            _requestSpawnerThread = new Thread(() => SpawnRequestsLoop(request.MaxRequests, _cts.Token))
            {
                Name = string.Format("SlowRequestSpawner-{0:N}", _simulationId),
                IsBackground = true
            };
            _requestSpawnerThread.Start();

            Logger.Warn(
                "Slow request simulation started: {0}. Duration={1}s, Interval={2}s. Making HTTP calls to {3}/api/slowrequest/execute-slow",
                _simulationId, _requestDurationSeconds, _intervalSeconds, _baseUrl);

            return new SimulationResult
            {
                SimulationId = _simulationId,
                Type = SimulationType.SlowRequest,
                Status = "Started",
                Message = string.Format("Slow request simulation started. Sending requests every {0}s, each taking ~{1}s.", _intervalSeconds, _requestDurationSeconds),
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
                    Type = SimulationType.SlowRequest,
                    Status = "NotRunning",
                    Message = "No slow request simulation is running."
                };
            }

            if (_cts != null) _cts.Cancel();
            _isRunning = false;
            _simulationTracker.UnregisterSimulation(_simulationId);

            // Track simulation end in Application Insights (if configured)
            _telemetry?.TrackSimulationEnded(_simulationId, SimulationType.SlowRequest, "Stopped");

            Logger.Info("Slow request simulation stopped: {0}. Sent={1}, Completed={2}, InProgress={3}",
                _simulationId, _requestsSent, _requestsCompleted, _requestsInProgress);

            return new SimulationResult
            {
                SimulationId = _simulationId,
                Type = SimulationType.SlowRequest,
                Status = "Stopped",
                Message = string.Format("Slow request simulation stopped. Total requests: {0}, Completed: {1}, Still in progress: {2}",
                    _requestsSent, _requestsCompleted, _requestsInProgress),
                ActualParameters = new Dictionary<string, object>
                {
                    ["RequestsSent"] = _requestsSent,
                    ["RequestsCompleted"] = _requestsCompleted,
                    ["ScenarioCounts"] = _scenarioCounts
                }
            };
        }

        public SlowRequestStatus GetStatus()
        {
            lock (_lock)
            {
                return new SlowRequestStatus
                {
                    IsRunning = _isRunning,
                    RequestsSent = _requestsSent,
                    RequestsCompleted = _requestsCompleted,
                    RequestsInProgress = _requestsInProgress,
                    IntervalSeconds = _intervalSeconds,
                    RequestDurationSeconds = _requestDurationSeconds,
                    StartedAt = _startedAt,
                    ScenarioCounts = new Dictionary<string, int>(_scenarioCounts)
                };
            }
        }

        private void SpawnRequestsLoop(int maxRequests, CancellationToken ct)
        {
            Logger.Info("Waiting 3 seconds for existing health probes to drain...");
            Thread.Sleep(3000);

            var requestNumber = 0;

            while (!ct.IsCancellationRequested && (maxRequests == 0 || requestNumber < maxRequests))
            {
                requestNumber++;
                Interlocked.Increment(ref _requestsSent);
                Interlocked.Increment(ref _requestsInProgress);

                var scenario = (SlowRequestScenario)_random.Next(1, 4);
                
                lock (_lock)
                {
                    var scenarioName = scenario.ToString();
                    int count;
                    _scenarioCounts.TryGetValue(scenarioName, out count);
                    _scenarioCounts[scenarioName] = count + 1;
                }

                Logger.Info("Spawning slow HTTP request #{0} to {1}/api/slowrequest/execute-slow?durationSeconds={2}",
                    requestNumber, _baseUrl, _requestDurationSeconds);

                var reqNum = requestNumber;
                var requestThread = new Thread(() => ExecuteSlowHttpRequest(scenario, reqNum, _requestDurationSeconds, ct))
                {
                    Name = string.Format("SlowRequest-{0}-{1}", requestNumber, scenario),
                    IsBackground = true
                };
                requestThread.Start();

                try
                {
                    for (var i = 0; i < _intervalSeconds * 2; i++)
                    {
                        if (ct.IsCancellationRequested) break;
                        Thread.Sleep(500);
                        Logger.Info("Generating trace noise to flush ETW buffers...");
                    }
                }
                catch (ThreadInterruptedException)
                {
                    break;
                }
            }

            Logger.Info("Slow request spawner loop ended");
        }

        private void ExecuteSlowHttpRequest(SlowRequestScenario scenario, int requestNumber, int durationSeconds, CancellationToken ct)
        {
            var sw = Stopwatch.StartNew();
            try
            {
                if (string.IsNullOrEmpty(_baseUrl))
                {
                    Logger.Error("Base URL not configured for slow request HTTP calls");
                    return;
                }

                using (var client = new HttpClient { BaseAddress = new Uri(_baseUrl), Timeout = TimeSpan.FromSeconds(durationSeconds + 30) })
                {
                    var url = string.Format("{0}/api/slowrequest/execute-slow?durationSeconds={1}&scenario={2}", _baseUrl, durationSeconds, scenario);
                    var response = client.GetAsync(url, ct).GetAwaiter().GetResult();
                    
                    sw.Stop();
                    var latencyMs = sw.Elapsed.TotalMilliseconds;

                    if (!response.IsSuccessStatusCode)
                    {
                        Logger.Error("Slow HTTP request #{0} failed with status {1} after {2:F0}ms", requestNumber, response.StatusCode, latencyMs);
                    }
                    else
                    {
                        Logger.Info("Slow HTTP request #{0} ({1}) completed in {2:F0}ms (expected ~{3}s)", requestNumber, scenario, latencyMs, durationSeconds);
                    }

                    BroadcastSlowRequestLatency(requestNumber, scenario, latencyMs, durationSeconds * 1000);
                }
            }
            catch (OperationCanceledException)
            {
                sw.Stop();
                var latencyMs = sw.Elapsed.TotalMilliseconds;
                Logger.Warn("Slow request #{0} timed out after {1:F0}ms", requestNumber, latencyMs);
                BroadcastSlowRequestLatency(requestNumber, scenario, latencyMs, durationSeconds * 1000, isError: true, errorMessage: "Request Timed Out (Client Side)");
            }
            catch (Exception ex)
            {
                sw.Stop();
                var latencyMs = sw.Elapsed.TotalMilliseconds;
                Logger.Error(ex, "Slow HTTP request #{0} failed after {1}ms", requestNumber, latencyMs);
                BroadcastSlowRequestLatency(requestNumber, scenario, latencyMs, durationSeconds * 1000, isError: true, errorMessage: ex.Message);
            }
            finally
            {
                Interlocked.Increment(ref _requestsCompleted);
                Interlocked.Decrement(ref _requestsInProgress);
                CheckAndCompleteSimulation();
            }
        }
        
        private void BroadcastSlowRequestLatency(int requestNumber, SlowRequestScenario scenario, double latencyMs, double expectedDurationMs, bool isError = false, string errorMessage = null)
        {
            try
            {
                var hubContext = GlobalHost.ConnectionManager.GetHubContext<MetricsHub>();
                hubContext.Clients.All.receiveSlowRequestLatency(new SlowRequestLatencyData
                {
                    RequestNumber = requestNumber,
                    Scenario = scenario.ToString(),
                    LatencyMs = latencyMs,
                    ExpectedDurationMs = expectedDurationMs,
                    Timestamp = DateTimeOffset.UtcNow,
                    IsError = isError,
                    ErrorMessage = errorMessage
                });
            }
            catch (Exception ex)
            {
                Logger.Debug(ex, "Failed to broadcast slow request latency");
            }
        }
        
        private string GetBaseUrl()
        {
            var websiteHostname = Environment.GetEnvironmentVariable("WEBSITE_HOSTNAME");
            if (!string.IsNullOrEmpty(websiteHostname))
                return "https://" + websiteHostname;
            return "http://localhost";
        }

        private void CheckAndCompleteSimulation()
        {
            if (_maxRequests > 0 && _requestsSent >= _maxRequests && _requestsInProgress == 0 && _isRunning)
            {
                lock (_lock)
                {
                    if (_isRunning && _requestsInProgress == 0 && _requestsSent >= _maxRequests)
                    {
                        _isRunning = false;
                        _simulationTracker.UnregisterSimulation(_simulationId);
                        
                        // Track simulation completion in Application Insights (if configured)
                        _telemetry?.TrackSimulationEnded(_simulationId, SimulationType.SlowRequest, "Completed");
                        
                        Logger.Info("Slow request simulation completed naturally: {0}. Total requests: {1}, Completed: {2}",
                            _simulationId, _requestsSent, _requestsCompleted);
                    }
                }
            }
        }

        public void Dispose()
        {
            Stop();
            if (_cts != null) _cts.Dispose();
            GC.SuppressFinalize(this);
        }
    }
}
