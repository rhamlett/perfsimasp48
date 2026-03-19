using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNet.SignalR;
using NLog;
using PerfProblemSimulator.Hubs;
using PerfProblemSimulator.Models;

namespace PerfProblemSimulator.Services
{
    public class LoadTestService : ILoadTestService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private int _concurrentRequests;
        private long _totalRequestsProcessed;
        private long _totalExceptionsThrown;
        private long _totalResponseTimeMs;
        private long _periodRequestsCompleted;
        private long _periodResponseTimeSum;
        private long _periodMaxResponseTimeMs;
        private int _periodPeakConcurrent;
        private long _periodExceptions;
        private volatile bool _hadActivityThisPeriod;
        private int _periodNumber;

        // Timer is stored to prevent GC from collecting it during the service lifetime
        [System.Diagnostics.CodeAnalysis.SuppressMessage("CodeQuality", "IDE0052:Remove unread private member", Justification = "Timer must be stored to prevent GC collection")]
        private readonly Timer _broadcastTimer;
        
        private static readonly Func<Exception>[] ExceptionFactories = new Func<Exception>[]
        {
            () => new InvalidOperationException("Operation is not valid due to current state"),
            () => new ArgumentException("Value does not fall within the expected range"),
            () => new ArgumentNullException(null, "Value cannot be null"),
            () => new NullReferenceException("Object reference not set to an instance of an object"),
            () => new IndexOutOfRangeException("Index was outside the bounds of the array"),
            () => new KeyNotFoundException("The given key was not present in the dictionary"),
            () => new TimeoutException("The operation has timed out"),
            () => new IOException("Unable to read data from the transport connection"),
            () => new HttpRequestException("An error occurred while sending the request"),
            () => new DivideByZeroException("Attempted to divide by zero"),
            () => new FormatException("Input string was not in a correct format"),
            () => new OverflowException("Arithmetic operation resulted in an overflow"),
            () => new TaskCanceledException("A task was canceled"),
            () => new OperationCanceledException("The operation was canceled"),
            () => new OutOfMemoryException("Insufficient memory to continue execution"),
            () => new StackOverflowException()
        };
        
        private const int BroadcastIntervalSeconds = 60;
        private const int LatencySampleRate = 10;
        private long _sampleCounter;

        private readonly IIdleStateService _idleStateService;
        private readonly Random _random = new Random();

        public LoadTestService(IIdleStateService idleStateService)
        {
            if (idleStateService == null) throw new ArgumentNullException("idleStateService");
            _idleStateService = idleStateService;
            
            _broadcastTimer = new Timer(
                callback: BroadcastStats,
                state: null,
                dueTime: TimeSpan.FromSeconds(BroadcastIntervalSeconds),
                period: TimeSpan.FromSeconds(BroadcastIntervalSeconds));
        }
        
        private void BroadcastStats(object state)
        {
            try
            {
                var requestsCompleted = Interlocked.Exchange(ref _periodRequestsCompleted, 0);
                var hadActivity = _hadActivityThisPeriod;
                _hadActivityThisPeriod = false;
                var currentConcurrent = Interlocked.CompareExchange(ref _concurrentRequests, 0, 0);
                
                // Only broadcast if there was activity this period OR there are active concurrent requests
                // This ensures we report stats throughout the entire load test duration
                var shouldBroadcast = requestsCompleted > 0 || hadActivity || currentConcurrent > 0;
                
                if (!shouldBroadcast)
                {
                    Logger.Debug("Load test timer fired - no activity, skipping broadcast");
                    return;
                }
                
                _periodNumber++;
                
                var responseTimeSum = Interlocked.Exchange(ref _periodResponseTimeSum, 0);
                var maxResponseTime = Interlocked.Exchange(ref _periodMaxResponseTimeMs, 0);
                var peakConcurrent = Interlocked.Exchange(ref _periodPeakConcurrent, 0);
                var exceptions = Interlocked.Exchange(ref _periodExceptions, 0);
                
                var avgResponseTime = requestsCompleted > 0 ? (double)responseTimeSum / requestsCompleted : 0;
                var requestsPerSecond = (double)requestsCompleted / BroadcastIntervalSeconds;
                
                var statsData = new LoadTestStatsData
                {
                    CurrentConcurrent = currentConcurrent,
                    PeakConcurrent = peakConcurrent,
                    RequestsCompleted = requestsCompleted,
                    AvgResponseTimeMs = Math.Round(avgResponseTime, 2),
                    MaxResponseTimeMs = maxResponseTime,
                    RequestsPerSecond = Math.Round(requestsPerSecond, 2),
                    ExceptionCount = (int)exceptions,
                    Timestamp = DateTimeOffset.UtcNow
                };
                
                var errorRate = requestsCompleted > 0 ? (double)exceptions / requestsCompleted * 100 : 0;
                Logger.Info("Load test period #{0}: {1} requests, {2:F1}ms avg, {3}ms max, {4:F2} RPS, {5:F1}% errors, {6} concurrent",
                    _periodNumber, requestsCompleted, avgResponseTime, maxResponseTime, requestsPerSecond, errorRate, currentConcurrent);
                
                var hubContext = GlobalHost.ConnectionManager.GetHubContext<MetricsHub>();
                hubContext.Clients.All.receiveLoadTestStats(statsData);
            }
            catch (Exception ex)
            {
                Logger.Error(ex, "Error in BroadcastStats timer callback");
            }
        }

        public async Task<LoadTestResult> ExecuteWorkAsync(LoadTestRequest request, CancellationToken cancellationToken)
        {
            var currentConcurrent = Interlocked.Increment(ref _concurrentRequests);
            UpdatePeakConcurrent(currentConcurrent);
            _idleStateService.RecordActivity();
            _hadActivityThisPeriod = true;
            
            var stopwatch = Stopwatch.StartNew();
            var totalCpuWorkDone = 0;

            try
            {
                var bufferSize = request.BufferSizeKb * 1024;
                var buffer = new byte[bufferSize];
                TouchMemoryBuffer(buffer);
                
                var overLimit = Math.Max(0, currentConcurrent - request.SoftLimit);
                var degradationDelayMs = overLimit * request.DegradationFactor;
                var totalDurationMs = request.BaselineDelayMs + degradationDelayMs;
                
                Logger.Debug("Load test: Concurrent={0}, Duration={1}ms (base={2}ms + degradation={3}ms)",
                    currentConcurrent, totalDurationMs, request.BaselineDelayMs, degradationDelayMs);
                
                const int sleepPerCycleMs = 50;
                var cpuWorkMsPerCycle = request.WorkIterations / 100;
                
                while (stopwatch.ElapsedMilliseconds < totalDurationMs)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    if (cpuWorkMsPerCycle > 0)
                    {
                        PerformCpuWork(cpuWorkMsPerCycle);
                        totalCpuWorkDone += cpuWorkMsPerCycle;
                    }
                    
                    TouchMemoryBuffer(buffer);
                    
                    var remainingMs = totalDurationMs - (int)stopwatch.ElapsedMilliseconds;
                    var sleepMs = Math.Min(sleepPerCycleMs, Math.Max(0, remainingMs));
                    if (sleepMs > 0) await Task.Delay(sleepMs, cancellationToken);
                }
                
                CheckAndThrowTimeoutException(stopwatch, request.ErrorAfterSeconds, request.ErrorPercent);
                TouchMemoryBuffer(buffer);
                stopwatch.Stop();
                
                return BuildResult(stopwatch.ElapsedMilliseconds, currentConcurrent, (int)stopwatch.ElapsedMilliseconds,
                    totalCpuWorkDone, buffer.Length, true, false, null);
            }
            catch (OperationCanceledException) { stopwatch.Stop(); throw; }
            catch (Exception ex)
            {
                stopwatch.Stop();
                Interlocked.Increment(ref _totalExceptionsThrown);
                Interlocked.Increment(ref _periodExceptions);
                Logger.Warn(ex, "Load test exception after {0}ms: {1}", stopwatch.ElapsedMilliseconds, ex.GetType().Name);
                throw;
            }
            finally
            {
                Interlocked.Decrement(ref _concurrentRequests);
                Interlocked.Increment(ref _totalRequestsProcessed);
                Interlocked.Add(ref _totalResponseTimeMs, stopwatch.ElapsedMilliseconds);
                
                var elapsedMs = stopwatch.ElapsedMilliseconds;
                Interlocked.Increment(ref _periodRequestsCompleted);
                Interlocked.Add(ref _periodResponseTimeSum, elapsedMs);
                UpdateMaxResponseTime(elapsedMs);
                
                var sampleCount = Interlocked.Increment(ref _sampleCounter);
                if (sampleCount % LatencySampleRate == 0)
                {
                    try
                    {
                        var hubContext = GlobalHost.ConnectionManager.GetHubContext<MetricsHub>();
                        hubContext.Clients.All.receiveLatency(new LatencyMeasurement
                        {
                            Timestamp = DateTimeOffset.UtcNow,
                            LatencyMs = elapsedMs,
                            IsTimeout = false,
                            IsError = false
                        });
                    }
                    catch
                    {
                        // SignalR broadcast failure is non-critical - ignore silently
                    }
                }
            }
        }
        
        private void TouchMemoryBuffer(byte[] buffer)
        {
            var checksum = 0;
            for (var i = 0; i < buffer.Length; i += 4096)
            {
                buffer[i] = (byte)(buffer[i] ^ 0xFF);
                checksum += buffer[i];
            }
            if (checksum < -1000000) Logger.Trace("Checksum: {0}", checksum);
        }

        private void CheckAndThrowTimeoutException(Stopwatch stopwatch, int errorAfterSeconds, int errorPercent)
        {
            if (errorAfterSeconds <= 0 || errorPercent <= 0) return;
            var elapsedSeconds = stopwatch.Elapsed.TotalSeconds;
            
            if (elapsedSeconds > errorAfterSeconds)
            {
                var probability = errorPercent / 100.0;
                if (_random.NextDouble() < probability)
                {
                    var exceptionIndex = _random.Next(ExceptionFactories.Length);
                    var exception = ExceptionFactories[exceptionIndex]();
                    Logger.Info("Load test throwing random exception after {0}s: {1}", elapsedSeconds, exception.GetType().Name);
                    throw exception;
                }
            }
        }

        private void PerformCpuWork(int workMs)
        {
            if (workMs <= 0) return;
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < workMs) Thread.SpinWait(1000);
        }

        private LoadTestResult BuildResult(long elapsedMs, int concurrentRequests, int degradationDelayMs,
            int workIterations, int bufferSizeBytes, bool workCompleted, bool exceptionThrown, string exceptionType)
        {
            return new LoadTestResult
            {
                ElapsedMs = elapsedMs,
                ConcurrentRequestsAtStart = concurrentRequests,
                DegradationDelayAppliedMs = degradationDelayMs,
                WorkIterationsCompleted = workCompleted ? workIterations : 0,
                MemoryAllocatedBytes = workCompleted ? bufferSizeBytes : 0,
                WorkCompleted = workCompleted,
                ExceptionThrown = exceptionThrown,
                ExceptionType = exceptionType,
                Timestamp = DateTimeOffset.UtcNow
            };
        }

        public LoadTestStats GetCurrentStats()
        {
            var totalRequests = Interlocked.Read(ref _totalRequestsProcessed);
            var totalTime = Interlocked.Read(ref _totalResponseTimeMs);
            var avgResponseTime = totalRequests > 0 ? (double)totalTime / totalRequests : 0;
            
            return new LoadTestStats(
                currentConcurrentRequests: _concurrentRequests,
                totalRequestsProcessed: totalRequests,
                totalExceptionsThrown: Interlocked.Read(ref _totalExceptionsThrown),
                averageResponseTimeMs: avgResponseTime
            );
        }
        
        private void UpdatePeakConcurrent(int currentConcurrent)
        {
            int currentPeak;
            do
            {
                currentPeak = _periodPeakConcurrent;
                if (currentConcurrent <= currentPeak) return;
            }
            while (Interlocked.CompareExchange(ref _periodPeakConcurrent, currentConcurrent, currentPeak) != currentPeak);
        }
        
        private void UpdateMaxResponseTime(long responseTimeMs)
        {
            long currentMax;
            do
            {
                currentMax = Interlocked.Read(ref _periodMaxResponseTimeMs);
                if (responseTimeMs <= currentMax) return;
            }
            while (Interlocked.CompareExchange(ref _periodMaxResponseTimeMs, responseTimeMs, currentMax) != currentMax);
        }
    }
}
