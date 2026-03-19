using System;
using System.Threading;
using System.Timers;
using NLog;
using Timer = System.Timers.Timer;

namespace PerfProblemSimulator.Services
{
    /// <summary>
    /// Implementation of idle state management to reduce unnecessary network traffic.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <strong>PURPOSE:</strong>
    /// Manages application idle state to prevent unnecessary health probe traffic when
    /// the dashboard is not actively being used. This reduces noise in AppLens and
    /// Application Insights diagnostics.
    /// </para>
    /// <para>
    /// <strong>ALGORITHM:</strong>
    /// <list type="number">
    /// <item>Timer checks for inactivity every minute</item>
    /// <item>If no activity for the configured timeout (default 20 min), go idle</item>
    /// <item>Fire GoingIdle event to notify services to stop probing</item>
    /// <item>When dashboard reconnects (page reload) or WakeUp() is called, wake up</item>
    /// <item>Fire WakingUp event to notify services to resume probing</item>
    /// </list>
    /// </para>
    /// <para>
    /// <strong>THREAD SAFETY:</strong>
    /// All state changes are protected by a lock. The probe loop polls IsIdle directly.
    /// </para>
    /// </remarks>
    public class IdleStateService : IIdleStateService, IDisposable
    {
        private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

        /// <summary>
        /// Default idle timeout in minutes.
        /// </summary>
        private const int DefaultIdleTimeoutMinutes = 20;

        /// <summary>
        /// Environment variable name for overriding the idle timeout.
        /// </summary>
        private const string IdleTimeoutEnvVar = "IDLE_TIMEOUT_MINUTES";

        private readonly Timer _idleCheckTimer;
        private readonly object _stateLock = new object();

        private DateTime _lastActivityUtc;
        private volatile bool _isIdle;
        private bool _disposed;

        /// <inheritdoc />
        public event EventHandler GoingIdle;

        /// <inheritdoc />
        public event EventHandler WakingUp;

        /// <inheritdoc />
        public bool IsIdle => _isIdle;

        /// <inheritdoc />
        public int IdleTimeoutMinutes { get; }

        /// <inheritdoc />
        public ManualResetEventSlim WakeSignal { get; } = new ManualResetEventSlim(false);

        /// <summary>
        /// Initializes a new instance of the <see cref="IdleStateService"/> class.
        /// </summary>
        public IdleStateService()
        {
            // Read idle timeout from environment variable or use default
            IdleTimeoutMinutes = GetIdleTimeoutFromEnvironment();

            _lastActivityUtc = DateTime.UtcNow;
            _isIdle = false;

            // Check for idle state every minute
            _idleCheckTimer = new Timer(TimeSpan.FromMinutes(1).TotalMilliseconds)
            {
                AutoReset = true
            };
            _idleCheckTimer.Elapsed += OnIdleCheckTimerElapsed;
            _idleCheckTimer.Start();

            Logger.Info("Idle state service initialized. Timeout: {0} minutes", IdleTimeoutMinutes);
        }

        /// <inheritdoc />
        public void RecordActivity()
        {
            bool wasIdle;
            lock (_stateLock)
            {
                wasIdle = _isIdle;
                _lastActivityUtc = DateTime.UtcNow;

                if (_isIdle)
                {
                    _isIdle = false;
                    WakeSignal.Set();
                }
            }

            if (wasIdle)
            {
                Logger.Info("App waking up from idle state due to recorded activity. There may be gaps in diagnostics and logs.");
                OnWakingUp();
            }
        }

        /// <inheritdoc />
        public bool WakeUp()
        {
            bool wasIdle;
            lock (_stateLock)
            {
                wasIdle = _isIdle;
                _lastActivityUtc = DateTime.UtcNow;

                if (_isIdle)
                {
                    _isIdle = false;
                    WakeSignal.Set();
                }
            }

            if (wasIdle)
            {
                Logger.Info("App waking up from idle state. There may be gaps in diagnostics and logs.");
                OnWakingUp();
                return true;
            }

            return false;
        }

        /// <summary>
        /// Fires the WakingUp event.
        /// </summary>
        private void OnWakingUp()
        {
            var handler = WakingUp;
            handler?.Invoke(this, EventArgs.Empty);
        }

        /// <summary>
        /// Timer callback to check if the application should go idle.
        /// </summary>
        private void OnIdleCheckTimerElapsed(object sender, ElapsedEventArgs e)
        {
            bool shouldGoIdle;
            lock (_stateLock)
            {
                if (_isIdle)
                {
                    return;
                }

                var timeSinceActivity = DateTime.UtcNow - _lastActivityUtc;
                shouldGoIdle = timeSinceActivity >= TimeSpan.FromMinutes(IdleTimeoutMinutes);

                if (shouldGoIdle)
                {
                    _isIdle = true;
                    WakeSignal.Reset();
                }
            }

            if (shouldGoIdle)
            {
                Logger.Warn("Application going idle, no health probes being sent. There will be gaps in diagnostics and logs.");
                var handler = GoingIdle;
                handler?.Invoke(this, EventArgs.Empty);
            }
        }

        /// <summary>
        /// Reads the idle timeout from the environment variable.
        /// </summary>
        private int GetIdleTimeoutFromEnvironment()
        {
            var envValue = Environment.GetEnvironmentVariable(IdleTimeoutEnvVar);

            if (string.IsNullOrEmpty(envValue))
            {
                return DefaultIdleTimeoutMinutes;
            }

            if (int.TryParse(envValue, out var timeoutMinutes) && timeoutMinutes > 0)
            {
                Logger.Info("Using custom idle timeout from {0}: {1} minutes", IdleTimeoutEnvVar, timeoutMinutes);
                return timeoutMinutes;
            }

            Logger.Warn("Invalid value '{0}' for {1}, using default {2} minutes", envValue, IdleTimeoutEnvVar, DefaultIdleTimeoutMinutes);
            return DefaultIdleTimeoutMinutes;
        }

        /// <inheritdoc />
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _idleCheckTimer.Stop();
            _idleCheckTimer.Dispose();
            WakeSignal.Dispose();
        }
    }
}
