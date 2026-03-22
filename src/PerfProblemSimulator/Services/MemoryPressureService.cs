using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using NLog;
using PerfProblemSimulator.Models;

namespace PerfProblemSimulator.Services
{
    public class MemoryPressureService : IMemoryPressureService
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        private readonly ISimulationTracker _simulationTracker;
        private readonly ISimulationTelemetry _telemetry;
        private readonly List<AllocatedMemoryBlock> _allocatedBlocks = new List<AllocatedMemoryBlock>();
        private readonly object _lock = new object();
        private const int DefaultSizeMegabytes = 100;
        private const int MinimumSizeMegabytes = 10;

        public MemoryPressureService(ISimulationTracker simulationTracker, ISimulationTelemetry telemetry)
        {
            if (simulationTracker == null) throw new ArgumentNullException("simulationTracker");
            _simulationTracker = simulationTracker;
            _telemetry = telemetry;
        }

        public SimulationResult AllocateMemory(int sizeMegabytes)
        {
            var actualSize = sizeMegabytes <= 0 ? DefaultSizeMegabytes : Math.Max(MinimumSizeMegabytes, sizeMegabytes);

            long currentAllocatedBytes;
            lock (_lock) { currentAllocatedBytes = _allocatedBlocks.Sum(b => b.SizeBytes); }

            Logger.Info("Allocating {0} MB. Current total: {1} MB", actualSize, currentAllocatedBytes / (1024.0 * 1024.0));

            var simulationId = Guid.NewGuid();
            
            // Set Activity tag for Application Insights correlation (if enabled via Azure portal)
            Activity.Current?.SetTag("SimulationId", simulationId.ToString());
            
            var startedAt = DateTimeOffset.UtcNow;
            var sizeBytes = (long)actualSize * 1024 * 1024;

            byte[] data;
            try
            {
                data = new byte[sizeBytes];
                for (int i = 0; i < data.Length; i += 4096) data[i] = 0xAB;
            }
            catch (OutOfMemoryException ex)
            {
                Logger.Error(ex, "Out of memory allocating {0} MB", actualSize);
                return new SimulationResult
                {
                    SimulationId = Guid.Empty,
                    Type = SimulationType.Memory,
                    Status = "Failed",
                    Message = string.Format("Out of memory attempting to allocate {0} MB. Try a smaller allocation.", actualSize),
                    ActualParameters = new Dictionary<string, object> { ["RequestedSizeMegabytes"] = actualSize },
                    StartedAt = startedAt,
                    EstimatedEndAt = null
                };
            }

            var block = new AllocatedMemoryBlock
            {
                Id = simulationId,
                SizeBytes = sizeBytes,
                AllocatedAt = startedAt,
                Data = data
            };

            lock (_lock) { _allocatedBlocks.Add(block); }

            var parameters = new Dictionary<string, object>
            {
                ["SizeMegabytes"] = actualSize,
                ["SizeBytes"] = sizeBytes,
                ["TotalAllocatedMegabytes"] = GetTotalAllocatedMegabytes()
            };

            var cts = new CancellationTokenSource();
            _simulationTracker.RegisterSimulation(simulationId, SimulationType.Memory, parameters, cts);

            // Track simulation start in Application Insights (if configured)
            _telemetry?.TrackSimulationStarted(simulationId, SimulationType.Memory, parameters);

            Logger.Info("Allocated {0} MB (block {1}). Total allocated: {2} MB", actualSize, simulationId, GetTotalAllocatedMegabytes());

            return new SimulationResult
            {
                SimulationId = simulationId,
                Type = SimulationType.Memory,
                Status = "Started",
                Message = string.Format("Allocated {0} MB of memory. Total allocated: {1:F1} MB.", actualSize, GetTotalAllocatedMegabytes()),
                ActualParameters = parameters,
                StartedAt = startedAt,
                EstimatedEndAt = null
            };
        }

        public MemoryReleaseResult ReleaseAllMemory(bool forceGc)
        {
            int releasedCount;
            long releasedBytes;

            lock (_lock)
            {
                releasedCount = _allocatedBlocks.Count;
                releasedBytes = _allocatedBlocks.Sum(b => b.SizeBytes);
                foreach (var block in _allocatedBlocks)
                {
                    _simulationTracker.UnregisterSimulation(block.Id);
                    // Track simulation end in Application Insights for each released block
                    _telemetry?.TrackSimulationEnded(block.Id, SimulationType.Memory, "Released");
                }
                _allocatedBlocks.Clear();
            }

            Logger.Info("Released {0} memory blocks ({1} MB). ForceGC: {2}", releasedCount, releasedBytes / (1024.0 * 1024.0), forceGc);

            if (forceGc)
            {
                Logger.Info("Forcing garbage collection with LOH compaction...");
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                GC.WaitForPendingFinalizers();
                GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, true);
                TrimWorkingSet();
                Logger.Info("Garbage collection and working set trim completed");
            }

            return new MemoryReleaseResult
            {
                ReleasedBlockCount = releasedCount,
                ReleasedBytes = releasedBytes,
                ForcedGarbageCollection = forceGc,
                Message = releasedCount > 0
                    ? string.Format("Released {0} memory blocks ({1:F1} MB). {2}", releasedCount, releasedBytes / (1024.0 * 1024.0),
                        forceGc ? "Forced GC to reclaim memory." : "Memory is now eligible for garbage collection.")
                    : "No memory blocks were allocated."
            };
        }

        public MemoryStatus GetMemoryStatus()
        {
            lock (_lock)
            {
                DateTimeOffset? oldest = null;
                DateTimeOffset? newest = null;
                if (_allocatedBlocks.Count > 0)
                {
                    oldest = _allocatedBlocks.Min(b => b.AllocatedAt);
                    newest = _allocatedBlocks.Max(b => b.AllocatedAt);
                }
                return new MemoryStatus
                {
                    AllocatedBlocksCount = _allocatedBlocks.Count,
                    TotalAllocatedBytes = _allocatedBlocks.Sum(b => b.SizeBytes),
                    OldestAllocationAt = oldest,
                    NewestAllocationAt = newest
                };
            }
        }

        private double GetTotalAllocatedMegabytes()
        {
            lock (_lock) { return _allocatedBlocks.Sum(b => b.SizeBytes) / (1024.0 * 1024.0); }
        }

        private void TrimWorkingSet()
        {
            try
            {
                var process = System.Diagnostics.Process.GetCurrentProcess();
                bool success = SetProcessWorkingSetSizeEx(process.Handle, (IntPtr)(-1), (IntPtr)(-1), 0);
                if (success)
                    Logger.Info("Working set trimmed. Before: {0} MB", process.WorkingSet64 / (1024.0 * 1024.0));
                else
                    Logger.Warn("Failed to trim working set. Error code: {0}", Marshal.GetLastWin32Error());
            }
            catch (Exception ex) { Logger.Warn(ex, "Failed to trim working set"); }
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool SetProcessWorkingSetSizeEx(IntPtr hProcess, IntPtr dwMinimumWorkingSetSize, IntPtr dwMaximumWorkingSetSize, uint flags);
    }
}
