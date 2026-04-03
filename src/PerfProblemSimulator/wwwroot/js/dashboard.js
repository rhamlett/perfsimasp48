/**
 * Performance Problem Simulator - Dashboard JavaScript
 * 
 * Educational Note:
 * This file handles real-time metrics visualization using SignalR.
 * SignalR provides WebSocket communication with automatic fallback
 * to Server-Sent Events or Long Polling if WebSockets aren't available.
 */

// ==========================================================================
// Configuration & State
// ==========================================================================

const CONFIG = {
    maxDataPoints: 240,  // 60 seconds of data at 250ms intervals
    maxLatencyDataPoints: 600, // 60 seconds of data at 100ms intervals
    // Chart update intervals (independent of server data rates)
    metricsChartIntervalMs: 250,  // Upper charts update rate
    latencyChartIntervalMs: 100,  // Latency chart update rate
    // Server probe interval (informational only, actual rate from server)
    latencyProbeIntervalMs: 100,
    idleTimeoutMinutes: 20,
    latencyTimeoutMs: 30000,
    reconnectDelayMs: 2000,
    apiBaseUrl: '/api'
};

// ==========================================================================
// Latency Color Thresholds & Gradients
// ==========================================================================

// RGB values for smooth color interpolation on latency chart
const LATENCY_RGB = {
    good:     { r: 16,  g: 124, b: 16  }, // Green - good (<150ms)
    degraded: { r: 255, g: 185, b: 0   }, // Yellow - degraded (150ms-1s)
    severe:   { r: 255, g: 140, b: 0   }, // Orange - severe (1s+)
    critical: { r: 209, g: 52,  b: 56  }  // Red - critical (30s+)
};

/**
 * Interpolates between two RGB colors.
 * @param {Object} color1 - Start color {r, g, b}
 * @param {Object} color2 - End color {r, g, b}
 * @param {number} t - Interpolation factor (0-1)
 * @returns {string} - RGB color string
 */
function lerpColor(color1, color2, t) {
    t = Math.max(0, Math.min(1, t)); // Clamp to 0-1
    const r = Math.round(color1.r + (color2.r - color1.r) * t);
    const g = Math.round(color1.g + (color2.g - color1.g) * t);
    const b = Math.round(color1.b + (color2.b - color1.b) * t);
    return `rgb(${r}, ${g}, ${b})`;
}

/**
 * Gets a smoothly interpolated color for a latency value.
 * Blends between threshold colors based on where the value falls.
 * @param {number} latencyMs - Latency value in milliseconds
 * @returns {string} - RGB color string
 */
function getInterpolatedLatencyColor(latencyMs) {
    if (latencyMs <= 0) return lerpColor(LATENCY_RGB.good, LATENCY_RGB.good, 0);
    
    // 0-150ms: green → yellow
    if (latencyMs <= 150) {
        const t = latencyMs / 150;
        return lerpColor(LATENCY_RGB.good, LATENCY_RGB.degraded, t);
    }
    
    // 150-1000ms: yellow → orange
    if (latencyMs <= 1000) {
        const t = (latencyMs - 150) / (1000 - 150);
        return lerpColor(LATENCY_RGB.degraded, LATENCY_RGB.severe, t);
    }
    
    // 1000-30000ms: orange → red
    if (latencyMs <= 30000) {
        const t = (latencyMs - 1000) / (30000 - 1000);
        return lerpColor(LATENCY_RGB.severe, LATENCY_RGB.critical, t);
    }
    
    // >30000ms: solid red
    return lerpColor(LATENCY_RGB.critical, LATENCY_RGB.critical, 1);
}

/**
 * Gets a smoothly interpolated RGBA color for a latency value (for gradient fills).
 * @param {number} latencyMs - Latency value in milliseconds
 * @param {number} alpha - Alpha value (0-1)
 * @returns {string} - RGBA color string
 */
function getInterpolatedLatencyColorRGBA(latencyMs, alpha) {
    let r, g, b;
    
    if (latencyMs <= 0) {
        r = LATENCY_RGB.good.r; g = LATENCY_RGB.good.g; b = LATENCY_RGB.good.b;
    } else if (latencyMs <= 150) {
        const t = latencyMs / 150;
        r = Math.round(LATENCY_RGB.good.r + (LATENCY_RGB.degraded.r - LATENCY_RGB.good.r) * t);
        g = Math.round(LATENCY_RGB.good.g + (LATENCY_RGB.degraded.g - LATENCY_RGB.good.g) * t);
        b = Math.round(LATENCY_RGB.good.b + (LATENCY_RGB.degraded.b - LATENCY_RGB.good.b) * t);
    } else if (latencyMs <= 1000) {
        const t = (latencyMs - 150) / (1000 - 150);
        r = Math.round(LATENCY_RGB.degraded.r + (LATENCY_RGB.severe.r - LATENCY_RGB.degraded.r) * t);
        g = Math.round(LATENCY_RGB.degraded.g + (LATENCY_RGB.severe.g - LATENCY_RGB.degraded.g) * t);
        b = Math.round(LATENCY_RGB.degraded.b + (LATENCY_RGB.severe.b - LATENCY_RGB.degraded.b) * t);
    } else if (latencyMs <= 30000) {
        const t = (latencyMs - 1000) / (30000 - 1000);
        r = Math.round(LATENCY_RGB.severe.r + (LATENCY_RGB.critical.r - LATENCY_RGB.severe.r) * t);
        g = Math.round(LATENCY_RGB.severe.g + (LATENCY_RGB.critical.g - LATENCY_RGB.severe.g) * t);
        b = Math.round(LATENCY_RGB.severe.b + (LATENCY_RGB.critical.b - LATENCY_RGB.severe.b) * t);
    } else {
        r = LATENCY_RGB.critical.r; g = LATENCY_RGB.critical.g; b = LATENCY_RGB.critical.b;
    }
    
    return `rgba(${r}, ${g}, ${b}, ${alpha})`;
}

/**
 * Creates a vertical gradient for the latency chart with smooth color blending.
 * Adds many intermediate color stops for seamless transitions between thresholds.
 * @param {CanvasRenderingContext2D} ctx - Canvas context
 * @param {Object} chartArea - Chart area dimensions
 * @param {Object} scales - Chart scales
 * @returns {CanvasGradient} - The gradient fill
 */
function createLatencyGradient(ctx, chartArea, scales) {
    if (!chartArea || !scales.y) return 'rgba(16, 124, 16, 0.2)';
    
    const gradient = ctx.createLinearGradient(0, chartArea.bottom, 0, chartArea.top);
    const yMax = scales.y.max || 200;
    
    // Add many color stops for smooth blending (20 stops from bottom to top)
    const numStops = 20;
    for (let i = 0; i <= numStops; i++) {
        const position = i / numStops; // 0 = bottom, 1 = top
        const latencyAtPosition = position * yMax;
        
        // Alpha increases slightly with latency for better visual distinction
        const alpha = 0.25 + (position * 0.25); // 0.25 at bottom to 0.50 at top
        
        const color = getInterpolatedLatencyColorRGBA(latencyAtPosition, alpha);
        gradient.addColorStop(position, color);
    }
    
    return gradient;
}

// Probe visualization history (24-dot indicator)
const probeHistory = [];
const MAX_PROBE_DOTS = 24;

const state = {
    connection: null,
    charts: {},
    // Latest values from SignalR (sampled by chart update timers)
    latestMetrics: null,       // Most recent metrics snapshot
    latestLatency: null,       // Most recent latency measurement
    // Chart update timer IDs
    metricsChartTimerId: null,
    latencyChartTimerId: null,
    metricsHistory: {
        timestamps: [],
        cpu: [],
        memory: [],
        threads: [],
        queue: []
    },
    latencyHistory: {
        timestamps: [],
        values: [],
        isTimeout: [],
        isError: []
    },
    slowRequestHistory: {
        timestamps: [],
        values: [],
        scenarios: []
    },
    latencyStats: {
        timeoutCount: 0
    },
    activeSimulations: new Map(),
    lastProcessId: null,
    isIdle: false,  // Tracks whether the server is in idle state
    intentionalDisconnect: false,  // True when WS is closed on purpose (idle), suppresses reconnect
    lastFailedRequestCompletedAt: null  // Suppress load test stats after failed request sim
};

// ==========================================================================
// UTC Time Formatting
// ==========================================================================

/**
 * Formats a Date object as UTC time string (HH:MM:SS)
 * All times in the dashboard use UTC to match Azure diagnostics data.
 * @param {Date} date - The date to format
 * @returns {string} UTC time string in HH:MM:SS format
 */
function formatUtcTime(date) {
    if (!date || !(date instanceof Date)) return '';
    const hours = date.getUTCHours().toString().padStart(2, '0');
    const minutes = date.getUTCMinutes().toString().padStart(2, '0');
    const seconds = date.getUTCSeconds().toString().padStart(2, '0');
    return `${hours}:${minutes}:${seconds}`;
}

/**
 * Gets the current UTC time as a formatted string
 * @returns {string} Current UTC time in HH:MM:SS format
 */
function getCurrentUtcTime() {
    return formatUtcTime(new Date());
}

// ==========================================================================
// SignalR Connection
// ==========================================================================

/**
 * Initialize SignalR connection to the metrics hub.
 * 
 * Educational Note:
 * SignalR 2.x automatically negotiates the best transport (WebSocket, SSE, Long Polling).
 * The jQuery client uses proxies generated at /signalr/hubs.
 * 
 * Note for .NET Framework 4.8 conversion:
 * Changed from @microsoft/signalr (ASP.NET Core SignalR) to jQuery SignalR 2.x client.
 * Key differences:
 * - Core: new signalR.HubConnectionBuilder().withUrl('/hubs/metrics').build()
 * - 2.x:  $.connection.metricsHub (using auto-generated proxy)
 * - Core: connection.on('receiveMetrics', handler)
 * - 2.x:  $.connection.metricsHub.client.receiveMetrics = handler
 * - Core: await connection.start()
 * - 2.x:  $.connection.hub.start().done(callback)
 * - Core: await connection.invoke('WakeUp')
 * - 2.x:  $.connection.metricsHub.server.wakeUp()
 */
function initializeSignalR() {
    // Get the hub proxy
    var hub = $.connection.metricsHub;
    
    if (!hub) {
        console.error('MetricsHub proxy not found. Make sure /signalr/hubs is loaded.');
        setTimeout(initializeSignalR, CONFIG.reconnectDelayMs);
        return;
    }

    // Register message handlers on the client proxy
    // SignalR 2.x uses client object for server-to-client methods
    hub.client.receiveMetrics = handleMetricsUpdate;
    hub.client.simulationStarted = handleSimulationStarted;
    hub.client.simulationCompleted = handleSimulationCompleted;
    hub.client.receiveLatency = handleLatencyUpdate;
    hub.client.receiveSlowRequestLatency = handleSlowRequestLatency;
    hub.client.receiveLoadTestStats = handleLoadTestStats;
    hub.client.receiveIdleState = handleIdleState;
    hub.client.receiveEventLogMessage = handleEventLogMessage;

    // Configure connection
    $.connection.hub.logging = true;
    
    // Handle connection state changes
    $.connection.hub.stateChanged(function (change) {
        var connectionStates = {
            0: 'connecting',
            1: 'connected',
            2: 'reconnecting',
            4: 'disconnected'
        };
        
        if (change.newState === $.signalR.connectionState.reconnecting) {
            updateConnectionStatus('connecting', 'Reconnecting...');
            logEvent('warning', 'Connection lost. Attempting to reconnect...');
        } else if (change.newState === $.signalR.connectionState.connected) {
            state.intentionalDisconnect = false;
            // Don't set to 'connected' here - let the server's idle state message determine the status
            // The server sends idle state in OnConnected, which will set the correct status
            if (change.oldState === $.signalR.connectionState.reconnecting) {
                logEvent('success', 'Reconnected to server');
                // NOTE: Do NOT call wakeUp() here. Auto-reconnects are framework-level and
                // should not wake the app from idle. The server sends current idle state
                // from OnReconnected, which will update the UI via handleIdleState.
            }
        }
    });

    $.connection.hub.disconnected(function () {
        // If we intentionally closed the connection (idle), don't update status or reconnect
        if (state.intentionalDisconnect) {
            return;
        }
        updateConnectionStatus('disconnected', 'Disconnected');
        logEvent('warning', 'Connection closed. Attempting to reconnect...');
        // Auto-reconnect after close
        setTimeout(initializeSignalR, CONFIG.reconnectDelayMs);
    });

    $.connection.hub.error(function (error) {
        // Suppress SignalR errors from event log - they are mostly idle timeout related and redundant
        console.error('SignalR error:', error);
    });

    // Note: Hub URL is set by the auto-generated proxy script at /hubs/metrics/signalr/hubs
    // It sets: $.hubConnection("/hubs/metrics/signalr", { useDefaultPath: false })

    // Start connection
    updateConnectionStatus('connecting', 'Connecting...');
    $.connection.hub.start()
        .done(function () {
            // Don't set status here - the server sends idle state in OnConnected
            // which will trigger handleIdleState to set the correct status
            logEvent('success', 'Connected to metrics hub');
            state.connection = hub;
            state.intentionalDisconnect = false;
            
            // Wake up the server on initial page load
            // This also triggers a response with the current idle state
            hub.server.wakeUp()
                .fail(function(err) {
                    console.error('WakeUp call failed:', err);
                    logEvent('warning', 'Failed to wake server: ' + (err.message || err));
                });
        })
        .fail(function (err) {
            updateConnectionStatus('disconnected', 'Failed to connect');
            logEvent('warning', 'Connection failed: ' + (err.message || err));
            // Try again after delay
            setTimeout(initializeSignalR, CONFIG.reconnectDelayMs);
        });
}

/**
 * Ensures the SignalR connection is active.
 * If the connection is null, stopped, or stopping, re-initializes it.
 * Called at the top of every simulation trigger so clicking any button
 * automatically reconnects after an intentional idle disconnect.
 */
function ensureSignalR() {
    var hubState = $.connection.hub && $.connection.hub.state;
    if (!state.connection ||
        hubState === $.signalR.connectionState.disconnected ||
        hubState === undefined) {
        state.intentionalDisconnect = false;
        state.isIdle = false;
        startChartUpdateTimers();
        initializeSignalR();
    }
}

function updateConnectionStatus(status, text) {
    const indicator = document.getElementById('connectionIndicator');
    const textEl = document.getElementById('connectionText');
    
    if (indicator) {
        indicator.className = `indicator ${status}`;
    }
    if (textEl) {
        textEl.textContent = text;
    }
}

// ==========================================================================
// Metrics Handling
// ==========================================================================

/**
 * Handle incoming metrics snapshot from SignalR.
 * Stores the latest data for chart sampling and updates UI cards immediately.
 * 
 * ARCHITECTURE NOTE: Chart updates are decoupled from SignalR data arrival.
 * - SignalR delivers data at server's MetricsCollectionIntervalMs rate
 * - Chart updates at fixed CONFIG.metricsChartIntervalMs (250ms)
 * - This ensures consistent 60-second display regardless of server rate
 */
function handleMetricsUpdate(snapshot) {
    // Check for application restart (process ID change)
    if (snapshot.processId) {
        if (state.lastProcessId !== null && state.lastProcessId !== snapshot.processId) {
            logEvent('restart', `APPLICATION RESTARTED! Process ID changed from ${state.lastProcessId} to ${snapshot.processId}. This may indicate an unexpected crash (OOM, StackOverflow, etc.)`);
            // Clear all active simulations since the app restarted
            clearAllActiveSimulations();
        }
        state.lastProcessId = snapshot.processId;
    }

    // Store latest snapshot for chart sampling timer
    state.latestMetrics = snapshot;

    // Update metric cards immediately for responsive UI
    updateMetricCard('cpu', snapshot.cpuPercent, '%', 100);
    const memoryMax = snapshot.totalAvailableMemoryMb || 1000;
    updateMetricCard('memory', snapshot.workingSetMb, 'MB', memoryMax);
    updateMetricCard('threads', snapshot.threadPoolThreads, 'threads', 100);
    updateMetricCard('queue', snapshot.threadPoolSaturationPercent, '%', 100);

    // Update total memory display
    const totalMemoryEl = document.getElementById('memoryTotal');
    if (totalMemoryEl && snapshot.totalAvailableMemoryMb) {
        const totalFormatted = snapshot.totalAvailableMemoryMb >= 1024 
            ? (snapshot.totalAvailableMemoryMb / 1024).toFixed(1) + ' GB'
            : Math.round(snapshot.totalAvailableMemoryMb) + ' MB';
        totalMemoryEl.textContent = `of ${totalFormatted}`;
    }

    // Update last update time (element may not exist in all layouts)
    const lastUpdateEl = document.getElementById('lastUpdate');
    if (lastUpdateEl) {
        lastUpdateEl.textContent = formatUtcTime(new Date(snapshot.timestamp)) + ' UTC';
    }
    
    // NOTE: Chart history is updated by sampledMetricsChartUpdate() timer, not here
}

function updateMetricCard(type, value, unit, maxForBar) {
    const valueEl = document.getElementById(`${type}Value`);
    const barEl = document.getElementById(`${type}Bar`);
    
    // Elements may not exist in all layouts
    if (!valueEl || !barEl) {
        return;
    }
    
    const card = valueEl.closest('.metric-card');
    
    // Format value - saturation % gets 2 decimal places for precision
    let displayValue = '--';
    if (typeof value === 'number') {
        if (type === 'queue') {
            displayValue = value.toFixed(2);
        } else {
            displayValue = value < 10 ? value.toFixed(1) : Math.round(value);
        }
    }
    valueEl.textContent = displayValue;
    
    // Update bar
    const barPercent = Math.min(100, (value / maxForBar) * 100);
    barEl.style.width = `${barPercent}%`;
    
    // Warning states based on percentage of max
    if (card) {
        card.classList.remove('warning', 'danger');
        if (type === 'cpu' || type === 'memory' || type === 'queue') {
            // Use barPercent for threshold comparison (value as % of max)
            if (barPercent > 80) card.classList.add('danger');
            else if (barPercent > 60) card.classList.add('warning');
        }
    }
}

function addToHistory(timestamp, snapshot) {
    const history = state.metricsHistory;
    
    history.timestamps.push(timestamp);
    history.cpu.push(snapshot.cpuPercent);
    history.memory.push(snapshot.workingSetMb);
    history.threads.push(snapshot.threadPoolThreads);
    history.queue.push(snapshot.threadPoolSaturationPercent);
    
    // Trim to max data points
    while (history.timestamps.length > CONFIG.maxDataPoints) {
        history.timestamps.shift();
        history.cpu.shift();
        history.memory.shift();
        history.threads.shift();
        history.queue.shift();
    }
}

// ==========================================================================
// Charts
// ==========================================================================

function initializeCharts() {
    // Resource chart (CPU + Memory)
    const resourceCtx = document.getElementById('resourceChart').getContext('2d');
    state.charts.resource = new Chart(resourceCtx, {
        type: 'line',
        data: {
            labels: [],
            datasets: [
                {
                    label: 'CPU %',
                    data: [],
                    borderColor: '#0078d4',
                    backgroundColor: 'rgba(0, 120, 212, 0.1)',
                    tension: 0.3,
                    fill: 'origin',
                    yAxisID: 'y',
                    pointRadius: 0,
                    pointHoverRadius: 0,
                    borderWidth: 1
                },
                {
                    label: 'Memory MB',
                    data: [],
                    borderColor: '#107c10',
                    backgroundColor: 'rgba(16, 124, 16, 0.1)',
                    tension: 0.3,
                    fill: 'origin',
                    yAxisID: 'y1',
                    pointRadius: 0,
                    pointHoverRadius: 0,
                    borderWidth: 1
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: {
                mode: 'index',
                intersect: false
            },
            scales: {
                x: {
                    display: true,
                    ticks: {
                        maxTicksLimit: 10,
                        callback: (value, index) => {
                            const date = state.metricsHistory.timestamps[index];
                            return date ? formatUtcTime(date) : '';
                        }
                    }
                },
                y: {
                    type: 'linear',
                    display: true,
                    position: 'left',
                    min: 0,
                    max: 100,
                    title: { display: true, text: 'CPU %' }
                },
                y1: {
                    type: 'linear',
                    display: true,
                    position: 'right',
                    min: 0,
                    title: { display: true, text: 'Memory MB' },
                    grid: { drawOnChartArea: false }
                }
            },
            plugins: {
                legend: { position: 'top' }
            }
        }
    });

    // Thread chart (Threads + Queue)
    const threadCtx = document.getElementById('threadChart').getContext('2d');
    state.charts.threads = new Chart(threadCtx, {
        type: 'line',
        data: {
            labels: [],
            datasets: [
                {
                    label: 'Active Threads',
                    data: [],
                    borderColor: '#8764b8',
                    backgroundColor: 'rgba(135, 100, 184, 0.3)',
                    tension: 0.3,
                    fill: 'origin',
                    yAxisID: 'y',
                    pointRadius: 0,
                    pointHoverRadius: 0,
                    borderWidth: 1
                },
                {
                    label: 'Saturation %',
                    data: [],
                    borderColor: '#ffb900',
                    backgroundColor: 'rgba(255, 185, 0, 0.3)',
                    tension: 0.3,
                    fill: 'origin',
                    yAxisID: 'y1',
                    pointRadius: 0,
                    pointHoverRadius: 0,
                    borderWidth: 1
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            interaction: {
                mode: 'index',
                intersect: false
            },
            scales: {
                x: {
                    display: true,
                    ticks: {
                        maxTicksLimit: 10,
                        callback: (value, index) => {
                            const date = state.metricsHistory.timestamps[index];
                            return date ? formatUtcTime(date) : '';
                        }
                    }
                },
                y: {
                    type: 'linear',
                    display: true,
                    position: 'left',
                    min: 0,
                    title: { display: true, text: 'Threads' }
                },
                y1: {
                    type: 'linear',
                    display: true,
                    position: 'right',
                    min: 0,
                    max: 100,
                    title: { display: true, text: 'Saturation %' },
                    grid: { drawOnChartArea: false }
                }
            },
            plugins: {
                legend: { position: 'top' }
            }
        }
    });

    // Latency chart
    const latencyCtx = document.getElementById('latencyChart').getContext('2d');
    state.charts.latency = new Chart(latencyCtx, {
        type: 'line',
        data: {
            labels: [],
            datasets: [
                {
                    label: 'Latency (ms)',
                    data: [],
                    // Segment-based border color - smooth gradient based on data value
                    segment: {
                        borderColor: (ctx) => {
                            const p0 = ctx.p0.parsed?.y;
                            const p1 = ctx.p1.parsed?.y;
                            if (p0 == null || p1 == null) return 'rgba(0,0,0,0)';
                            const value = Math.max(p0, p1);
                            return getInterpolatedLatencyColor(value);
                        },
                    },
                    borderColor: '#107c10', // Default/fallback (green)
                    // Dynamic gradient fill based on latency thresholds
                    backgroundColor: (context) => {
                        const chart = context.chart;
                        const { ctx, chartArea, scales } = chart;
                        if (!chartArea) return 'rgba(16, 124, 16, 0.2)';
                        return createLatencyGradient(ctx, chartArea, scales);
                    },
                    tension: 0.3,
                    fill: true,
                    pointRadius: 0, // Hide points for performance with many data points
                    pointHoverRadius: 0, // Disable hover circles to prevent visual artifacts
                    borderWidth: 1.5
                }
            ]
        },
        options: {
            responsive: true,
            maintainAspectRatio: false,
            animation: false, // Disable animation for better performance
            interaction: {
                mode: 'index',
                intersect: false
            },
            scales: {
                x: {
                    display: true,
                    ticks: {
                        maxTicksLimit: 6,
                        maxRotation: 0,
                        minRotation: 0,
                        font: { size: 10 },
                        callback: (value, index) => {
                            const date = state.latencyHistory.timestamps[index];
                            return date ? formatUtcTime(date) : '';
                        }
                    }
                },
                y: {
                    display: true,
                    position: 'left',
                    beginAtZero: true,
                    grace: '5%',
                    title: { display: true, text: 'Latency (ms)', font: { size: 10 } },
                    ticks: {
                        font: { size: 10 },
                        callback: (value) => {
                            if (value >= 1000) {
                                return (value / 1000).toFixed(1) + 's';
                            }
                            return value + 'ms';
                        }
                    }
                }
            },
            plugins: {
                legend: { display: false },
                tooltip: {
                    callbacks: {
                        label: (context) => {
                            const index = context.dataIndex;
                            const latency = context.raw;
                            const isTimeout = state.latencyHistory.isTimeout[index];
                            const isError = state.latencyHistory.isError[index];
                            
                            if (isTimeout) return `Critical (>30s): ${latency}ms`;
                            if (isError) return `Error: ${latency}ms`;
                            return `Latency: ${latency}ms`;
                        }
                    }
                }
            }
        }
    });
}

function updateCharts() {
    const history = state.metricsHistory;
    const labels = history.timestamps.map(t => formatUtcTime(t));
    
    // Update resource chart
    state.charts.resource.data.labels = labels;
    state.charts.resource.data.datasets[0].data = history.cpu;
    state.charts.resource.data.datasets[1].data = history.memory;
    state.charts.resource.update('none'); // 'none' prevents animation on updates
    
    // Update thread chart
    state.charts.threads.data.labels = labels;
    state.charts.threads.data.datasets[0].data = history.threads;
    state.charts.threads.data.datasets[1].data = history.queue;
    state.charts.threads.update('none');
}

// ==========================================================================
// Chart Sampling Timers (Decoupled from Server Data Rate)
// ==========================================================================

/**
 * Starts the fixed-interval chart update timers.
 * This decouples chart display rate from server data collection rate.
 * 
 * - Metrics chart: samples at 250ms intervals (240 points = 60 seconds)
 * - Latency chart: samples at 100ms intervals (600 points = 60 seconds)
 */
function startChartUpdateTimers() {
    // Metrics chart timer (250ms)
    if (state.metricsChartTimerId) {
        clearInterval(state.metricsChartTimerId);
    }
    state.metricsChartTimerId = setInterval(sampledMetricsChartUpdate, CONFIG.metricsChartIntervalMs);
    
    // Latency chart timer (100ms)
    if (state.latencyChartTimerId) {
        clearInterval(state.latencyChartTimerId);
    }
    state.latencyChartTimerId = setInterval(sampledLatencyChartUpdate, CONFIG.latencyChartIntervalMs);
    
    console.log(`Chart timers started: metrics=${CONFIG.metricsChartIntervalMs}ms, latency=${CONFIG.latencyChartIntervalMs}ms`);
}

/**
 * Stops the chart update timers.
 */
function stopChartUpdateTimers() {
    if (state.metricsChartTimerId) {
        clearInterval(state.metricsChartTimerId);
        state.metricsChartTimerId = null;
    }
    if (state.latencyChartTimerId) {
        clearInterval(state.latencyChartTimerId);
        state.latencyChartTimerId = null;
    }
}

/**
 * Samples the latest metrics and adds to chart history.
 * Called every CONFIG.metricsChartIntervalMs (250ms).
 */
function sampledMetricsChartUpdate() {
    if (!state.latestMetrics) {
        return; // No data received yet
    }
    
    // Use current time for consistent chart time axis
    const timestamp = new Date();
    addToHistory(timestamp, state.latestMetrics);
    updateCharts();
}

/**
 * Samples the latest latency and adds to chart history.
 * Called every CONFIG.latencyChartIntervalMs (100ms).
 */
function sampledLatencyChartUpdate() {
    if (!state.latestLatency) {
        return; // No data received yet
    }
    
    // Use current time for consistent chart time axis
    const timestamp = new Date();
    addLatencyToHistory(
        timestamp,
        state.latestLatency.latencyMs,
        state.latestLatency.isTimeout,
        state.latestLatency.isError
    );
    updateLatencyChart();
}

// ==========================================================================
// Latency Monitoring
// ==========================================================================

/**
 * Handle incoming latency measurement from server-side probe.
 * Stores the latest data for chart sampling and updates UI immediately.
 * 
 * ARCHITECTURE NOTE: Chart updates are decoupled from probe arrival.
 * - Server probes at LatencyProbeIntervalMs (configurable via HEALTH_PROBE_RATE)
 * - Chart updates at fixed CONFIG.latencyChartIntervalMs (100ms)
 * - This ensures consistent 60-second display regardless of probe rate
 */
function handleLatencyUpdate(measurement) {
    // Log significant latency events to the dashboard log (immediate)
    if (measurement.isError) {
        if (measurement.source === 'FailedRequest') {
            const errorType = measurement.errorMessage || 'HTTP 5xx';
            logEvent('failedrequests', `Failed Request: ${errorType} (${formatLatency(measurement.latencyMs)})`);
        } else {
            logEvent('system', `Health Probe Error: ${measurement.errorMessage || 'Unknown error'} (${formatLatency(measurement.latencyMs)})`);
        }
    } else if (measurement.isTimeout) {
        logEvent('system', `Health Probe Critical (>30s): ${formatLatency(measurement.latencyMs)}`);
    } else if (measurement.latencyMs > 10000) {
        logEvent('warning', `High Latency Probe: ${formatLatency(measurement.latencyMs)}`);
    }
    
    // Store latest measurement for chart sampling timer
    state.latestLatency = {
        timestamp: new Date(measurement.timestamp),
        latencyMs: measurement.latencyMs,
        isTimeout: measurement.isTimeout,
        isError: measurement.isError
    };

    // Update latency display immediately for responsive UI
    updateLatencyDisplay(measurement.latencyMs, measurement.isTimeout, measurement.isError);
    
    // Update probe visualization dots immediately
    updateProbeVisualization(measurement.latencyMs);
    
    // NOTE: Chart history is updated by sampledLatencyChartUpdate() timer, not here
}

/**
 * Handle incoming slow request latency from server.
 * This shows the actual duration of slow requests (20+ seconds).
 */
function handleSlowRequestLatency(data) {
    const timestamp = new Date(data.timestamp);
    const latencyMs = data.latencyMs;
    const scenario = data.scenario;
    const expectedMs = data.expectedDurationMs || 0;
    const isError = data.isError;
    const errorMessage = data.errorMessage;
    
    // Calculate Queue Time (Total - Expected)
    // If negative (processing was faster than expected?), clamp to 0
    const queueTimeMs = Math.max(0, latencyMs - expectedMs);
    
    // Flag as timeout if total time exceeds threshold (30s)
    const isTimeout = latencyMs >= CONFIG.latencyTimeoutMs;
    
    // Add to slow request history
    const history = state.slowRequestHistory;
    history.timestamps.push(timestamp);
    history.values.push(latencyMs);
    history.scenarios.push(scenario);
    
    // Trim to max data points
    while (history.timestamps.length > 100) {
        history.timestamps.shift();
        history.values.shift();
        history.scenarios.shift();
    }
    
    // Add as a special latency point on the chart (it will show as a large spike)
    addLatencyToHistory(timestamp, latencyMs, isTimeout, isError);
    updateLatencyDisplay(latencyMs, isTimeout, isError);
    updateLatencyChart();
    
    // Log the slow request completion with Queue Time breakdown
    const durationSec = (latencyMs / 1000).toFixed(1);
    const queueSec = (queueTimeMs / 1000).toFixed(1);
    
    if (isError) {
        let msg = `Slow request #${data.requestNumber} FAILED: ${durationSec}s (${scenario}) - ${errorMessage}`;
        if (queueTimeMs > 100) {
            msg += ` [Queue Time: ${queueSec}s]`;
        }
        logEvent('slowrequest', msg);
    } else if (isTimeout) {
        // Request completed but exceeded the 30s critical threshold
        let msg = `Slow request #${data.requestNumber} completed: ${durationSec}s (${scenario}) [Queue Time: ${queueSec}s] ⚠️ CRITICAL (>30s)`;
        logEvent('slowrequest', msg);
    } else {
        let msg = `Slow request #${data.requestNumber} completed: ${durationSec}s (${scenario}) [Queue Time: ${queueSec}s]`;
        logEvent('slowrequest', msg);
    }
}

/**
 * Handle incoming load test statistics from server.
 * Broadcast every 60 seconds while /api/loadtest endpoint is receiving traffic.
 */
function handleLoadTestStats(data) {
    // Suppress load test stats if a failed request simulation recently completed
    // (since failed requests use the load test endpoint internally)
    if (state.lastFailedRequestCompletedAt && 
        (Date.now() - state.lastFailedRequestCompletedAt) < 90000) {
        return; // Suppress for 90 seconds after failed request sim
    }
    
    const completed = data.requestsCompleted;
    const avgMs = data.avgResponseTimeMs;
    const maxMs = data.maxResponseTimeMs;
    const rps = data.requestsPerSecond;
    const exceptions = data.exceptionCount;
    
    // Calculate error rate percentage
    const errorRate = completed > 0 ? (exceptions / completed) * 100 : 0;
    
    // Build detailed stats message matching screenshot format
    let msg = `Load test period stats (60s): ${completed.toLocaleString()} requests, ` +
              `${avgMs.toFixed(1)} avg ms, ${maxMs.toFixed(0)} max ms, ` +
              `${rps.toFixed(2)} RPS, ${errorRate.toFixed(1)}% errors`;
    
    logEvent('loadtest', msg);
}

/**
 * Handle idle state change notification from server.
 * When the server goes idle, health probes stop. When it wakes up, they resume.
 * This also handles initial connection - server always sends current idle state on connect.
 */
function handleIdleState(data) {
    const wasIdle = state.isIdle;
    state.isIdle = data.isIdle;
    
    // Always update the connection status indicator based on current idle state
    if (data.isIdle) {
        // Only log and disconnect if transitioning to idle (not on every message)
        if (!wasIdle) {
            logEvent('idle', data.message || 'Application going idle, no health probes being sent. There will be gaps in diagnostics and logs.');
            updateConnectionStatus('idle', 'Idle');

            // Stop chart update timers (no data while idle)
            stopChartUpdateTimers();

            // Intentionally close SignalR to prevent reconnect-induced status flicker
            state.intentionalDisconnect = true;
            if ($.connection.hub && $.connection.hub.state !== $.signalR.connectionState.disconnected) {
                $.connection.hub.stop();
            }
        }
    } else {
        updateConnectionStatus('connected', 'Connected');
        // Log wake-up message if transitioning from idle or message indicates waking
        if (wasIdle || (data.message && data.message.toLowerCase().includes('waking up'))) {
            logEvent('system', data.message || 'App waking up from idle state. There may be gaps in diagnostics and logs.');
        }
    }
}

/**
 * Handle event log message from server.
 * Displays a server-pushed message in the UI event log panel.
 */
function handleEventLogMessage(data) {
    const level = data.level || 'info';
    const message = data.message || '';
    const options = {};
    
    if (data.icon) {
        options.icon = data.icon;
    }
    
    logEvent(level, message, options);
}

/**
 * Add latency measurement to history.
 */
function addLatencyToHistory(timestamp, latencyMs, isTimeout, isError) {
    const history = state.latencyHistory;
    
    history.timestamps.push(timestamp);
    history.values.push(latencyMs);
    history.isTimeout.push(isTimeout);
    history.isError.push(isError);
    
    // Track timeout count
    if (isTimeout) {
        state.latencyStats.timeoutCount++;
    }
    
    // Trim to max data points (60 seconds at 100ms = 600 points)
    while (history.timestamps.length > CONFIG.maxLatencyDataPoints) {
        history.timestamps.shift();
        const wasTimeout = history.isTimeout.shift();
        history.values.shift();
        history.isError.shift();
        
        // Adjust timeout count when old timeouts scroll out
        if (wasTimeout) {
            state.latencyStats.timeoutCount = Math.max(0, state.latencyStats.timeoutCount - 1);
        }
    }
}

/**
 * Update the latency stat displays (if present).
 */
function updateLatencyDisplay(currentLatency, isTimeout, isError) {
    const history = state.latencyHistory;
    
    // Current latency with color coding (check if element exists)
    const currentEl = document.getElementById('latencyCurrent');
    if (currentEl) {
        currentEl.textContent = formatLatency(currentLatency);
        currentEl.className = `latency-value ${getLatencyClass(currentLatency, isTimeout)}`;
    }
    
    // Calculate average
    const avgEl = document.getElementById('latencyAverage');
    if (avgEl && history.values.length > 0) {
        const avg = history.values.reduce((a, b) => a + b, 0) / history.values.length;
        avgEl.textContent = formatLatency(avg);
        avgEl.className = `latency-value ${getLatencyClass(avg, false)}`;
    }
    
    // Calculate max
    const maxEl = document.getElementById('latencyMax');
    if (maxEl && history.values.length > 0) {
        const max = Math.max(...history.values);
        maxEl.textContent = formatLatency(max);
        
        // precise timeout check for max value could be complex, 
        // but high latency > 1s will be red anyway which is sufficient
        maxEl.className = `latency-value ${getLatencyClass(max, false)}`;
    }
    
    // Update timeout count
    const timeoutsEl = document.getElementById('latencyTimeouts');
    if (timeoutsEl) {
        timeoutsEl.textContent = state.latencyStats.timeoutCount;
        if (state.latencyStats.timeoutCount > 0) {
            timeoutsEl.className = 'latency-value timeout';
        } else {
            timeoutsEl.className = 'latency-value';
        }
    }
}

/**
 * Format latency value for display with dynamic units.
 */
function formatLatency(ms) {
    if (ms >= 10000) {
        return (ms / 1000).toFixed(1) + 's';
    } else if (ms >= 1000) {
        return (ms / 1000).toFixed(2) + 's';
    } else {
        return ms.toFixed(1) + 'ms';
    }
}

/**
 * Get CSS class based on latency value.
 */
function getLatencyClass(ms, isTimeout) {
    if (isTimeout) return 'timeout';
    if (ms > 1000) return 'danger';
    if (ms > 150) return 'warning';
    return 'good';
}

/**
 * Updates probe visualization dots (24-dot history indicator)
 * Shows visual history of recent latency measurements next to the title
 */
function updateProbeVisualization(latency) {
    let status = 'good';
    if (latency >= 30000) status = 'failed';
    else if (latency >= 1000) status = 'slow';
    else if (latency >= 150) status = 'degraded';

    probeHistory.push(status);
    if (probeHistory.length > MAX_PROBE_DOTS) {
        probeHistory.shift();
    }

    const vizEl = document.getElementById('probe-visualization');
    if (vizEl) {
        vizEl.innerHTML = probeHistory.map(s =>
            `<span class="probe-dot-inline ${s === 'good' ? '' : s}"></span>`
        ).join('');
    }
}

/**
 * Update the latency chart.
 */
function updateLatencyChart() {
    if (!state.charts.latency) return;
    
    const history = state.latencyHistory;
    
    // Create gradient based on chart's actual dimensions
    const ctx = state.charts.latency.ctx;
    const chartArea = state.charts.latency.chartArea;
    const gradientHeight = chartArea ? (chartArea.bottom - chartArea.top) : 200;
    const gradient = ctx.createLinearGradient(0, 0, 0, gradientHeight);
    gradient.addColorStop(0, 'rgba(209, 52, 56, 0.3)');   // Red at top (high latency)
    gradient.addColorStop(0.5, 'rgba(255, 185, 0, 0.2)'); // Yellow in middle
    gradient.addColorStop(1, 'rgba(16, 124, 16, 0.1)');   // Green at bottom (low latency)
    
    // Map data points to colors based on latency
    const pointColors = history.values.map((v, i) => {
        if (history.isTimeout[i]) return '#d13438';
        if (v > 1000) return '#d13438';
        if (v > 150) return '#ffb900';
        return '#107c10';
    });
    
    state.charts.latency.data.labels = history.timestamps.map(t => formatUtcTime(t));
    state.charts.latency.data.datasets[0].data = history.values;
    state.charts.latency.data.datasets[0].backgroundColor = gradient;
    state.charts.latency.data.datasets[0].borderColor = pointColors;
    state.charts.latency.data.datasets[0].segment = {
        borderColor: ctx => {
            const index = ctx.p0DataIndex;
            const latency = history.values[index];
            const isTimeout = history.isTimeout[index];
            if (isTimeout) return '#d13438';
            if (latency > 1000) return '#d13438';
            if (latency > 150) return '#ffb900';
            return '#107c10';
        }
    };
    state.charts.latency.update('none');
}

// ==========================================================================
// Simulation Controls
// ==========================================================================

async function triggerCpuStress() {
    ensureSignalR();
    const duration = parseInt(document.getElementById('cpuDuration').value) || 30;
    const level = document.getElementById('cpuLevel').value || 'high';
    
    try {
        logEvent('cpu', `Triggering CPU stress for ${duration} seconds (${level})...`);
        const response = await fetch(`${CONFIG.apiBaseUrl}/cpu/trigger-high-cpu`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ 
                durationSeconds: duration,
                level: level
            })
        });
        
        if (response.ok) {
            const result = await response.json();
            const displayLevel = level.charAt(0).toUpperCase() + level.slice(1);
            addActiveSimulation(result.simulationId, 'cpu', `CPU Stress (${displayLevel})`);
            logEvent('cpu', withSimulationId(`CPU stress started (${displayLevel})`, result.simulationId));
        } else {
            const error = await response.json();
            logEvent('cpu', `Failed: ${error.detail || 'Unknown error'}`);
        }
    } catch (err) {
        logEvent('cpu', `Request failed: ${err.message}`);
    }
}

/**
 * Stops all active CPU stress simulations.
 */
async function stopCpuStress() {
    ensureSignalR();
    try {
        logEvent('cpu', 'Stopping CPU stress simulations...');
        const response = await fetch(`${CONFIG.apiBaseUrl}/cpu/stop`, {
            method: 'POST'
        });
        
        if (response.ok) {
            const result = await response.json();
            logEvent('cpu', result.message || 'CPU stress stopped');
            // Remove CPU simulations from active list
            removeSimulationsByType('cpu');
        } else {
            const error = await response.json();
            logEvent('cpu', `Stop request: ${error.detail || 'May have already stopped'}`);
        }
    } catch (err) {
        logEvent('cpu', `Stop request: ${err.message || 'May have already stopped'}`);
    }
}

async function allocateMemory() {
    ensureSignalR();
    const sizeMb = parseInt(document.getElementById('memorySize').value) || 100;
    
    try {
        logEvent('memory', `Allocating ${sizeMb} MB of memory...`);
        const response = await fetch(`${CONFIG.apiBaseUrl}/memory/allocate-memory`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ sizeMegabytes: sizeMb })
        });
        
        if (response.ok) {
            const result = await response.json();
            const actualSizeMb = result.actualParameters?.sizeMegabytes ?? sizeMb;
            addActiveSimulation(result.simulationId, 'memory', `Memory ${actualSizeMb}MB`);
            logEvent('memory', withSimulationId(`Memory allocated (${actualSizeMb} MB)`, result.simulationId));
        } else {
            const error = await response.json();
            logEvent('memory', `Failed: ${error.detail || 'Unknown error'}`);
        }
    } catch (err) {
        logEvent('memory', `Request failed: ${err.message}`);
    }
}

async function releaseMemory() {
    ensureSignalR();
    try {
        logEvent('memory', 'Releasing all allocated memory...');
        const response = await fetch(`${CONFIG.apiBaseUrl}/memory/release-memory`, {
            method: 'POST'
        });
        
        if (response.ok) {
            const result = await response.json();
            // Remove all memory simulations from active list
            state.activeSimulations.forEach((value, key) => {
                if (value.type === 'memory') {
                    state.activeSimulations.delete(key);
                }
            });
            updateActiveSimulationsUI();
            const releasedMb = result.releasedMegabytes ?? (result.releasedBytes / 1024 / 1024);
            logEvent('memory', `Released ${result.releasedBlockCount ?? 0} blocks (${releasedMb.toFixed(1)} MB)`);
        } else {
            const error = await response.json();
            logEvent('memory', `Failed: ${error.detail || 'Unknown error'}`);
        }
    } catch (err) {
        logEvent('memory', `Request failed: ${err.message}`);
    }
}

async function triggerThreadBlock() {
    ensureSignalR();
    const delaySeconds = parseFloat(document.getElementById('threadDelay').value) || 10;
    const delayMs = Math.round(delaySeconds * 1000);
    const concurrent = parseInt(document.getElementById('threadConcurrent').value) || 100;
    
    try {
        logEvent('threads', `Triggering thread blocking: ${concurrent} requests, ${delaySeconds}s delay...`);
        const response = await fetch(`${CONFIG.apiBaseUrl}/threadblock/trigger-sync-over-async`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ 
                delayMilliseconds: delayMs,
                concurrentRequests: concurrent
            })
        });
        
        if (response.ok) {
            const result = await response.json();
            addActiveSimulation(result.simulationId, 'threadblock', 'Thread Block');
            logEvent('threads', withSimulationId(`Thread blocking started`, result.simulationId));
        } else {
            const error = await response.json();
            logEvent('threads', `Failed: ${error.detail || 'Unknown error'}`);
        }
    } catch (err) {
        logEvent('threads', `Request failed: ${err.message}`);
    }
}

/**
 * Stops all active thread pool starvation simulations.
 */
async function stopThreadBlock() {
    ensureSignalR();
    try {
        logEvent('threads', 'Stopping thread blocking simulations...');
        const response = await fetch(`${CONFIG.apiBaseUrl}/threadblock/stop`, {
            method: 'POST'
        });
        
        if (response.ok) {
            const result = await response.json();
            logEvent('threads', result.message || 'Thread blocking stopped');
            // Remove thread block simulations from active list
            removeSimulationsByType('threadblock');
        } else {
            const error = await response.json();
            logEvent('threads', `Stop request: ${error.detail || 'May have already stopped'}`);
        }
    } catch (err) {
        logEvent('threads', `Stop request: ${err.message || 'May have already stopped'}`);
    }
}

/**
 * Triggers an application crash.
 * WARNING: This will terminate the application!
 */
async function triggerCrash() {
    ensureSignalR();
    const crashType = document.getElementById('crashType').value;
    // Delay option removed from UI - default to 0 (immediate crash)
    const delayElement = document.getElementById('crashDelay');
    const delaySeconds = delayElement ? parseInt(delayElement.value) || 0 : 0;
    
    // Confirmation dialog - always synchronous for Azure Crash Monitoring
    const confirmed = confirm(
        `⚠️ WARNING: This will CRASH the application!\n\n` +
        `Crash Type: ${crashType}\n` +
        `\nThe application will terminate and Azure will auto-restart it.\n` +
        `✓ Azure Crash Monitoring will capture this crash.\n` +
        `\nAre you sure you want to proceed?`
    );
    
    if (!confirmed) {
        logEvent('crash', 'Crash cancelled by user');
        return;
    }
    
    try {
        logEvent('crash', `CRASH: ${crashType}${delaySeconds > 0 ? ` in ${delaySeconds}s` : ''} - Connection will be lost!`);
        
        const response = await fetch(`${CONFIG.apiBaseUrl}/crash/trigger`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({ 
                crashType: crashType,
                delaySeconds: delaySeconds,
                synchronous: true,
                message: `Crash triggered from dashboard at ${new Date().toISOString()}`
            })
        });
        
        // If synchronous, we shouldn't get here (process crashed)
        if (response.ok) {
            const result = await response.json();
            logEvent('crash', `💀 ${result.message}`);
            
            // Show countdown for async crashes
            if (!synchronous && delaySeconds > 0) {
                let countdown = delaySeconds;
                const countdownInterval = setInterval(() => {
                    countdown--;
                    if (countdown > 0) {
                        logEvent('crash', `Crash in ${countdown}...`);
                    } else {
                        logEvent('crash', 'CRASHING NOW!');
                        clearInterval(countdownInterval);
                    }
                }, 1000);
            }
        } else {
            const error = await response.json();
            logEvent('crash', `Failed: ${error.message || 'Unknown error'}`);
        }
    } catch (err) {
        // For synchronous crashes, a network error is expected (connection lost)
        if (synchronous) {
            logEvent('crash', 'Application crashed! Connection lost. Waiting for restart...');
        } else {
            logEvent('crash', `Request failed: ${err.message}`);
        }
    }
}

// ==========================================================================
// Slow Request Simulator
// ==========================================================================

/**
 * Starts the slow request simulator.
 * Generates requests with sync-over-async patterns for CLR Profiler analysis.
 */
async function startSlowRequests() {
    ensureSignalR();
    const durationSeconds = parseInt(document.getElementById('slowRequestDuration').value) || 25;
    const intervalSeconds = parseInt(document.getElementById('slowRequestInterval').value) || 2;
    const maxRequests = parseInt(document.getElementById('slowRequestMax').value) || 10;
    
    const statusDiv = document.getElementById('slowRequestStatus');
    const startBtn = document.getElementById('btnStartSlowRequests');
    const stopBtn = document.getElementById('btnStopSlowRequests');
    
    try {
        startBtn.disabled = true;
        stopBtn.disabled = false;
        
        const response = await fetch(`${CONFIG.apiBaseUrl}/slowrequest/start`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                requestDurationSeconds: durationSeconds,
                intervalSeconds: intervalSeconds,
                maxRequests: maxRequests
            })
        });
        
        if (response.ok) {
            const result = await response.json();
            addActiveSimulation(result.simulationId, 'slowrequest', 'Slow Requests');
            logEvent('slowrequest', withSimulationId(`Slow request simulation started`, result.simulationId));
            statusDiv.textContent = `Running: ${durationSeconds}s requests every ${intervalSeconds}s (max ${maxRequests})`;
            statusDiv.classList.add('active');
            
            // Start polling for status
            pollSlowRequestStatus();
        } else {
            const error = await response.json();
            logEvent('slowrequest', `Failed to start: ${error.message || error.title || 'Unknown error'}`);
            startBtn.disabled = false;
            stopBtn.disabled = true;
            statusDiv.classList.remove('active');
        }
    } catch (err) {
        logEvent('slowrequest', `Request failed: ${err.message}`);
        startBtn.disabled = false;
        stopBtn.disabled = true;
        statusDiv.classList.remove('active');
    }
}

/**
 * Stops the slow request simulator.
 */
async function stopSlowRequests() {
    ensureSignalR();
    const statusDiv = document.getElementById('slowRequestStatus');
    const startBtn = document.getElementById('btnStartSlowRequests');
    const stopBtn = document.getElementById('btnStopSlowRequests');
    
    try {
        logEvent('slowrequest', 'Stopping slow request simulator...');
        
        const response = await fetch(`${CONFIG.apiBaseUrl}/slowrequest/stop`, {
            method: 'POST'
        });
        
        if (response.ok) {
            const result = await response.json();
            logEvent('slowrequest', `${result.message}`);
        } else {
            const error = await response.json();
            logEvent('slowrequest', `Stop request: ${error.message || 'May have already stopped'}`);
        }
    } catch (err) {
        logEvent('slowrequest', `Request failed: ${err.message}`);
    } finally {
        startBtn.disabled = false;
        stopBtn.disabled = true;
        statusDiv.textContent = '';
        statusDiv.classList.remove('active');
    }
}

// ==========================================================================
// Failed Request Simulation (HTTP 5xx generation)
// ==========================================================================

/**
 * Starts the failed request simulator.
 * Generates HTTP 500 errors visible in AppLens and Application Insights.
 */
async function startFailedRequests() {
    ensureSignalR();
    const requestCount = parseInt(document.getElementById('failedRequestCount').value) || 10;
    
    const startBtn = document.getElementById('btnStartFailedRequests');
    
    try {
        logEvent('failedrequests', `Generating ${requestCount} HTTP 500 errors...`);
        
        startBtn.disabled = true;
        
        const response = await fetch(`${CONFIG.apiBaseUrl}/failedrequest/start`, {
            method: 'POST',
            headers: { 'Content-Type': 'application/json' },
            body: JSON.stringify({
                requestCount: requestCount
            })
        });
        
        if (response.ok) {
            const result = await response.json();
            addActiveSimulation(result.simulationId, 'failedrequest', 'Failed Requests');
            logEvent('failedrequests', withSimulationId(`Started generating ${requestCount} failures`, result.simulationId));
            
            // Start polling for completion
            pollFailedRequestStatus();
        } else {
            const error = await response.json();
            logEvent('failedrequests', `Failed to start: ${error.message || error.title || 'Unknown error'}`);
            startBtn.disabled = false;
        }
    } catch (err) {
        logEvent('failedrequests', `Request failed: ${err.message}`);
        startBtn.disabled = false;
    }
}

/**
 * Stops the failed request simulator.
 * Note: Stop button removed from UI - requests complete too quickly to intervene.
 * This function retained for API compatibility.
 */
async function stopFailedRequests() {
    const startBtn = document.getElementById('btnStartFailedRequests');
    
    try {
        logEvent('failedrequests', 'Stopping failed request simulator...');
        
        const response = await fetch(`${CONFIG.apiBaseUrl}/failedrequest/stop`, {
            method: 'POST'
        });
        
        if (response.ok) {
            const result = await response.json();
            logEvent('failedrequests', result.message || 'Stopped');
            removeSimulationsByType('failedrequest');
        } else {
            const error = await response.json();
            logEvent('failedrequests', `Stop request: ${error.message || 'May have already completed'}`);
        }
    } catch (err) {
        logEvent('failedrequests', `Request failed: ${err.message}`);
    } finally {
        startBtn.disabled = false;
    }
}

/**
 * Polls the failed request status and updates UI when complete.
 */
async function pollFailedRequestStatus() {
    const startBtn = document.getElementById('btnStartFailedRequests');
    
    try {
        const response = await fetch(`${CONFIG.apiBaseUrl}/failedrequest/status`);
        if (response.ok) {
            const status = await response.json();
            
            if (status.isRunning) {
                // Continue polling
                setTimeout(pollFailedRequestStatus, 2000);
            } else {
                // Simulation ended
                startBtn.disabled = false;
                removeSimulationsByType('failedrequest');
                
                // Track completion time to suppress load test stats message
                state.lastFailedRequestCompletedAt = Date.now();
                
                if (status.requestsCompleted > 0) {
                    logEvent('failedrequests', `Completed: Generated ${status.requestsCompleted} HTTP 500 errors`);
                }
            }
        }
    } catch (err) {
        // Connection lost - probably a restart
        startBtn.disabled = false;
    }
}

/**
 * Polls the slow request status and updates UI.
 * Poll interval is 5 seconds to minimize noise during CLR profiling.
 */
async function pollSlowRequestStatus() {
    const statusDiv = document.getElementById('slowRequestStatus');
    const startBtn = document.getElementById('btnStartSlowRequests');
    const stopBtn = document.getElementById('btnStopSlowRequests');
    
    try {
        const response = await fetch(`${CONFIG.apiBaseUrl}/slowrequest/status`);
        if (response.ok) {
            const status = await response.json();
            
            if (status.isRunning) {
                statusDiv.textContent = `Running: ${status.requestsCompleted}/${status.requestsSent} completed, ${status.requestsInProgress} active`;
                statusDiv.classList.add('active');

                // Ensure overlay is active if running (in case page was refreshed)
                const overlay = document.getElementById('latencyOverlay');
                const msg = document.getElementById('latencySuspendedMsg');
                if (overlay && !overlay.classList.contains('active')) {
                     overlay.classList.add('active');
                }
                if (msg && msg.style.display === 'none') {
                    msg.style.display = 'block';
                }
                
                // Continue polling at 5-second intervals to reduce profiler noise
                setTimeout(pollSlowRequestStatus, 5000);
            } else {
                // Simulation ended
                statusDiv.textContent = `Completed: ${status.requestsCompleted}/${status.requestsSent} requests`;
                setTimeout(() => {
                    statusDiv.classList.remove('active');
                    statusDiv.textContent = '';
                }, 3000);
                
                startBtn.disabled = false;
                stopBtn.disabled = true;
                
                // Hide overlay when simulation is confirmed done via polling
                const overlay = document.getElementById('latencyOverlay');
                const msg = document.getElementById('latencySuspendedMsg');
                if (overlay) overlay.classList.remove('active');
                if (msg) msg.style.display = 'none';

                if (status.requestsCompleted > 0) {
                    logEvent('slowrequest', `Slow request simulation completed: ${status.requestsCompleted} requests`);
                }
            }
        }
    } catch (err) {
        // Connection lost - probably a restart
        statusDiv.classList.remove('active');
        startBtn.disabled = false;
        stopBtn.disabled = true;
    }
}

// ==========================================================================
// Active Simulations UI
// ==========================================================================

/**
 * Maps simulation types to their log categories
 */
const SIMULATION_CATEGORY_MAP = {
    'cpu': 'cpu',
    'memory': 'memory',
    'threadblock': 'threads',
    'slowrequest': 'slowrequest',
    'failedrequest': 'failedrequests',
    'crash': 'crash'
};

function handleSimulationStarted(simulationType, simulationId) {
    const simTypeLower = simulationType.toLowerCase();
    addActiveSimulation(simulationId, simTypeLower, simulationType);
    // Note: Don't log here - the API response handlers already log the start message

    // Handle SlowRequest specific UI
    if (simulationType === 'SlowRequest') {
        const overlay = document.getElementById('latencyOverlay');
        const statusOverlay = document.getElementById('slowRequestStatus');
        const msg = document.getElementById('latencySuspendedMsg');

        if (overlay) overlay.classList.add('active');
        if (statusOverlay) statusOverlay.classList.add('active');
        if (msg) msg.style.display = 'block';
    }
}

function handleSimulationCompleted(simulationType, simulationId) {
    removeActiveSimulation(simulationId);
    const simTypeLower = simulationType.toLowerCase();
    const category = SIMULATION_CATEGORY_MAP[simTypeLower] || 'system';
    
    // Don't log SlowRequest completion here - the polling handler logs with request count
    if (simulationType !== 'SlowRequest') {
        logEvent(category, withSimulationId(`${simulationType} simulation completed`, simulationId));
    }

    // Handle SlowRequest specific UI
    if (simulationType === 'SlowRequest') {
        const overlay = document.getElementById('latencyOverlay');
        const statusOverlay = document.getElementById('slowRequestStatus');
        const msg = document.getElementById('latencySuspendedMsg');

        if (overlay) overlay.classList.remove('active');
        if (msg) msg.style.display = 'none';
        
        // Let the status overlay hang around for a few seconds via the polling loop instead of hiding immediately
        // The polling loop will handle the 'Running' -> 'Completed' text update.
    }
}

function addActiveSimulation(id, type, label) {
    state.activeSimulations.set(id, { type, label, startTime: new Date() });
    updateActiveSimulationsUI();
}

function removeActiveSimulation(id) {
    state.activeSimulations.delete(id);
    updateActiveSimulationsUI();
}

function removeSimulationsByType(type) {
    state.activeSimulations.forEach((value, key) => {
        if (value.type === type) {
            state.activeSimulations.delete(key);
        }
    });
    updateActiveSimulationsUI();
}

function updateActiveSimulationsUI() {
    const container = document.getElementById('simulationsList');
    
    if (state.activeSimulations.size === 0) {
        container.innerHTML = '<p class="no-simulations">No active simulations</p>';
        return;
    }
    
    container.innerHTML = Array.from(state.activeSimulations.entries())
        .map(([id, sim]) => `
            <div class="simulation-badge ${sim.type}">
                <span class="spinner"></span>
                <span>${sim.label}</span>
            </div>
        `).join('');
}

// ==========================================================================
// Event Log
// ==========================================================================

/**
 * Copies the event log content to the clipboard.
 */
function copyEventLog() {
    const log = document.getElementById('eventLog');
    const entries = log.querySelectorAll('.log-entry');
    
    // Extract text from each log entry
    const logText = Array.from(entries).map(entry => {
        const time = entry.querySelector('.log-time')?.textContent || '';
        const icon = entry.querySelector('.log-icon')?.textContent || '';
        // Get the text content after time and icon
        const clone = entry.cloneNode(true);
        clone.querySelector('.log-time')?.remove();
        clone.querySelector('.log-icon')?.remove();
        const message = clone.textContent.trim();
        return `${time} ${icon} ${message}`.trim();
    }).join('\n');
    
    navigator.clipboard.writeText(logText).then(() => {
        const btn = document.getElementById('btnCopyEventLog');
        const originalText = btn.textContent;
        btn.textContent = '✓ Copied!';
        btn.classList.add('copied');
        setTimeout(() => {
            btn.textContent = originalText;
            btn.classList.remove('copied');
        }, 2000);
    }).catch(err => {
        console.error('Failed to copy event log:', err);
        alert('Failed to copy to clipboard');
    });
}

/**
 * Category icons for event log entries
 */
const LOG_ICONS = {
    cpu: '🔥',
    memory: '📊',
    threads: '🧵',
    slowrequest: '🐌',
    failedrequests: '❌',
    crash: '💥',
    restart: '🔄',
    loadtest: '📈'
};

/**
 * Wraps a message with a simulation ID tooltip for correlation.
 * Click on the message to copy the simulation ID to clipboard.
 * @param {string} message - The message to display
 * @param {string} simulationId - The full GUID simulation ID (shown in tooltip)
 * @returns {string} HTML string with message and tooltip
 */
function withSimulationId(message, simulationId) {
    if (!simulationId) return message;
    return `<span class="sim-msg" data-simulation-id="${simulationId}" title="Click to copy Simulation ID: ${simulationId}">${message}</span>`;
}

/**
 * Copies a simulation ID to the clipboard and shows visual feedback.
 * Uses navigator.clipboard API with fallback to execCommand for HTTP sites.
 * @param {HTMLElement} element - The element that was clicked
 * @param {string} simulationId - The simulation ID to copy
 */
async function copySimulationId(element, simulationId) {
    let success = false;
    
    // Try modern clipboard API first (requires HTTPS or localhost)
    if (navigator.clipboard && navigator.clipboard.writeText) {
        try {
            await navigator.clipboard.writeText(simulationId);
            success = true;
        } catch (err) {
            console.warn('Clipboard API failed, trying fallback:', err);
        }
    }
    
    // Fallback for HTTP sites or older browsers
    if (!success) {
        try {
            const textArea = document.createElement('textarea');
            textArea.value = simulationId;
            textArea.style.position = 'fixed';
            textArea.style.left = '-9999px';
            textArea.style.top = '-9999px';
            document.body.appendChild(textArea);
            textArea.focus();
            textArea.select();
            success = document.execCommand('copy');
            document.body.removeChild(textArea);
        } catch (err) {
            console.error('Fallback copy failed:', err);
        }
    }
    
    // Show visual feedback
    if (success) {
        element.classList.add('copied');
        const originalTitle = element.getAttribute('title');
        element.setAttribute('title', 'Copied!');
        setTimeout(() => {
            element.classList.remove('copied');
            element.setAttribute('title', originalTitle);
        }, 1500);
    } else {
        // Show error feedback
        element.classList.add('copy-failed');
        const originalTitle = element.getAttribute('title');
        element.setAttribute('title', 'Copy failed - try selecting manually');
        setTimeout(() => {
            element.classList.remove('copy-failed');
            element.setAttribute('title', originalTitle);
        }, 2000);
    }
}

/**
 * Logs an event to the event log panel.
 * @param {string} levelOrCategory - Log level ('info','success','warning','error') or category ('cpu','memory','threads','slowrequest','crash','loadtest','system')
 * @param {string} message - The message to log
 * @param {Object} [options] - Optional settings
 * @param {string} [options.icon] - Override the default icon for this category
 */
function logEvent(levelOrCategory, message, options = {}) {
    const log = document.getElementById('eventLog');
    if (!log) {
        console.log(`[${levelOrCategory}] ${message}`);
        return;
    }
    
    const time = getCurrentUtcTime();
    
    // Determine CSS class and icon based on category
    let cssClass = levelOrCategory;
    let icon = options.icon || LOG_ICONS[levelOrCategory] || '';
    
    const entry = document.createElement('div');
    entry.className = `log-entry ${cssClass}`;
    
    const iconHtml = icon ? `<span class="log-icon">${icon}</span>` : '';
    entry.innerHTML = `<span class="log-time">${time} UTC</span>${iconHtml}${message}`;
    
    log.insertBefore(entry, log.firstChild);
}

// ==========================================================================
// Initialization
// ==========================================================================

/**
 * Fetches and displays the Azure SKU info.
 */
async function fetchAzureSku() {
    try {
        const response = await fetch(`${CONFIG.apiBaseUrl}/admin/stats`);
        if (response.ok) {
            const data = await response.json();
            const skuElement = document.getElementById('skuDisplay');
            if (skuElement && data.processInfo && data.processInfo.azureSku) {
                skuElement.textContent = `SKU: ${data.processInfo.azureSku}`;
                skuElement.style.display = 'block';
            }
            
            // Log SKU/worker info on page load
            if (data.processInfo) {
                const sku = data.processInfo.azureSku;
                const computerName = data.processInfo.computerName;
                
                if (sku === 'Local' || !computerName) {
                    logEvent('system', 'Application is currently running on Local');
                } else {
                    logEvent('system', `Application is currently running on ${sku} SKU on worker ${computerName}`);
                }
            }
        }
    } catch (error) {
        console.error('Failed to fetch Azure SKU', error);
    }
}

/**
 * Fetches and displays the build timestamp.
 */
async function fetchBuildInfo() {
    try {
        const response = await fetch(`${CONFIG.apiBaseUrl}/health/build`);
        if (response.ok) {
            const data = await response.json();
            if (data.buildTimestamp) {
                // Parse ISO 8601 timestamp and format as date + time
                const buildDate = new Date(data.buildTimestamp);
                const formatted = buildDate.toISOString().replace('T', ' ').substring(0, 19) + ' UTC';
                
                // Update footer build time
                const buildTimeElement = document.getElementById('buildTime');
                if (buildTimeElement) buildTimeElement.textContent = formatted;
                
                // Update sidebar build time
                const sidebarBuildTime = document.getElementById('sidebarBuildTime');
                if (sidebarBuildTime) sidebarBuildTime.textContent = formatted;
            }
        }
    } catch (error) {
        console.error('Failed to fetch build info', error);
    }
}

/**
 * Fetches app configuration and updates the UI.
 * The PageFooter can be set via Azure App Service environment variable: PAGE_FOOTER
 * LatencyProbeIntervalMs configures the server probe rate.
 */
async function fetchAppConfig() {
    try {
        const response = await fetch(`${CONFIG.apiBaseUrl}/config`);
        if (response.ok) {
            const config = await response.json();
            
            // Update probe interval (server sends its interval for display)
            if (config.latencyProbeIntervalMs && config.latencyProbeIntervalMs > 0) {
                CONFIG.latencyProbeIntervalMs = config.latencyProbeIntervalMs;
                console.log(`Latency probe interval set to ${CONFIG.latencyProbeIntervalMs}ms`);
            }

            // Update idle timeout (server sends its timeout for display)
            if (config.idleTimeoutMinutes && config.idleTimeoutMinutes > 0) {
                CONFIG.idleTimeoutMinutes = config.idleTimeoutMinutes;
                console.log(`Idle timeout set to ${CONFIG.idleTimeoutMinutes}m`);
            }
            
            // Update page footer if configured
            const footerElement = document.getElementById('pageFooterContent');
            if (footerElement) {
                if (config.pageFooter && config.pageFooter.trim()) {
                    footerElement.innerHTML = config.pageFooter;
                } else {
                    // Hide the empty paragraph if no footer is configured
                    footerElement.style.display = 'none';
                }
            }
        }
    } catch (error) {
        console.error('Failed to fetch app config', error);
    }
}

document.addEventListener('DOMContentLoaded', async () => {
    // Initialize charts first
    initializeCharts();
    
    // Fetch app configuration
    await fetchAppConfig();
    
    // Fetch SKU info (non-blocking)
    fetchAzureSku();
    
    // Fetch build info (non-blocking)
    fetchBuildInfo();
    
    // Start SignalR connection (receives probe results from server)
    initializeSignalR();
    
    // Start fixed-interval chart update timers (decoupled from server data rate)
    startChartUpdateTimers();

    
    // Wire up button handlers
    document.getElementById('btnTriggerCpu').addEventListener('click', triggerCpuStress);
    document.getElementById('btnStopCpu').addEventListener('click', stopCpuStress);
    document.getElementById('btnAllocateMemory').addEventListener('click', allocateMemory);
    document.getElementById('btnReleaseMemory').addEventListener('click', releaseMemory);
    document.getElementById('btnTriggerThreadBlock').addEventListener('click', triggerThreadBlock);
    document.getElementById('btnStopThreadBlock').addEventListener('click', stopThreadBlock);
    document.getElementById('btnTriggerCrash').addEventListener('click', triggerCrash);
    document.getElementById('btnStartSlowRequests').addEventListener('click', startSlowRequests);
    document.getElementById('btnStopSlowRequests').addEventListener('click', stopSlowRequests);
    document.getElementById('btnStartFailedRequests').addEventListener('click', startFailedRequests);
    document.getElementById('btnCopyEventLog').addEventListener('click', copyEventLog);
    
    // Enable click-to-copy for simulation IDs in event log (delegated handler)
    document.getElementById('eventLog').addEventListener('click', function(e) {
        const simMsg = e.target.closest('.sim-msg');
        if (simMsg && simMsg.dataset.simulationId) {
            copySimulationId(simMsg, simMsg.dataset.simulationId);
        }
    });
    
    // Initialize slow request Stop button as disabled
    document.getElementById('btnStopSlowRequests').disabled = true;
    
    // Wire up side panel toggle
    initializeSidePanel();
    
    logEvent('system', `Dashboard initialized (probe rate: ${CONFIG.latencyProbeIntervalMs}ms, idle timeout: ${CONFIG.idleTimeoutMinutes}m)`);

    // Static license disclaimers (Line 2 first so Line 1 appears on top)
    logEvent('warning', '⚖️ Deploy only in isolated, non-production environments. Licensed under MIT License.');
    logEvent('warning', '⚖️ This software is provided "AS IS" without warranty. The author shall not be liable for any damages arising from use or misuse.');
});

// ==========================================================================
// Side Panel Management
// ==========================================================================

function initializeSidePanel() {
    const btnToggle = document.getElementById('btnTogglePanel');
    const btnClose = document.getElementById('btnClosePanel');
    const sidePanel = document.getElementById('sidePanel');
    
    if (btnToggle) {
        btnToggle.addEventListener('click', toggleSidePanel);
    }
    
    if (btnClose) {
        btnClose.addEventListener('click', closeSidePanel);
    }
    
    // Close panel when clicking outside (on main content)
    document.addEventListener('click', (e) => {
        if (sidePanel.classList.contains('open')) {
            // Check if click is outside the panel and not on the toggle button
            if (!sidePanel.contains(e.target) && !btnToggle.contains(e.target)) {
                closeSidePanel();
            }
        }
    });
    
    // Close panel on Escape key
    document.addEventListener('keydown', (e) => {
        if (e.key === 'Escape' && sidePanel.classList.contains('open')) {
            closeSidePanel();
        }
    });
}

function toggleSidePanel() {
    const sidePanel = document.getElementById('sidePanel');
    const btnToggle = document.getElementById('btnTogglePanel');
    
    if (sidePanel.classList.contains('open')) {
        closeSidePanel();
    } else {
        sidePanel.classList.add('open');
        btnToggle.classList.add('active');
    }
}

function closeSidePanel() {
    const sidePanel = document.getElementById('sidePanel');
    const btnToggle = document.getElementById('btnTogglePanel');
    
    sidePanel.classList.remove('open');
    btnToggle.classList.remove('active');
}

/**
 * Clear all active simulations from state and UI.
 */
function clearAllActiveSimulations() {
    state.activeSimulations.clear();
    updateActiveSimulationsUI();
}
