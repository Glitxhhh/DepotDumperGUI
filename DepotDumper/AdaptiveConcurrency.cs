using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

namespace DepotDumper
{
    /// <summary>First-come-first-served limiter whose limit can be changed while work is queued.</summary>
    public sealed class DynamicLimiter
    {
        private readonly object gate = new object();
        private readonly LinkedList<TaskCompletionSource<bool>> waiters = new LinkedList<TaskCompletionSource<bool>>();
        private int limit;
        private int inFlight;

        public DynamicLimiter(int initialLimit) { limit = Math.Max(1, initialLimit); }

        public int Limit { get { lock (gate) return limit; } }
        public int InFlight { get { lock (gate) return inFlight; } }
        public int Waiting { get { lock (gate) return waiters.Count; } }

        public void SetLimit(int newLimit)
        {
            List<TaskCompletionSource<bool>> wake;
            lock (gate)
            {
                limit = Math.Max(1, newLimit);
                wake = TakeRunnable();
            }
            foreach (var t in wake) t.TrySetResult(true);
        }

        // caller holds the lock: hand permits to queued waiters while there is room
        private List<TaskCompletionSource<bool>> TakeRunnable()
        {
            var wake = new List<TaskCompletionSource<bool>>();
            while (inFlight < limit && waiters.Count > 0)
            {
                var next = waiters.First.Value;
                waiters.RemoveFirst();
                inFlight++;
                wake.Add(next);
            }
            return wake;
        }

        public Task WaitAsync()
        {
            lock (gate)
            {
                if (inFlight < limit && waiters.Count == 0) { inFlight++; return Task.CompletedTask; }
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                waiters.AddLast(tcs);
                return tcs.Task;
            }
        }

        public void Release()
        {
            List<TaskCompletionSource<bool>> wake;
            lock (gate)
            {
                inFlight--;
                wake = TakeRunnable();
            }
            foreach (var t in wake) t.TrySetResult(true);
        }

        public async Task RunAsync(Func<Task> work)
        {
            await WaitAsync().ConfigureAwait(false);
            try { await work().ConfigureAwait(false); }
            finally { Release(); }
        }
    }

    public readonly record struct SpeedSnapshot(bool Dynamic, int Workers, int MaxWorkers, int InFlight, int Waiting,
        int CpuPercent, int MemoryPercent, int FreeMemoryMb, int ProcessMemoryMb, int MemoryLimitMb, int RateLimitHits, string LastAction);

    /// <summary>
    /// Adjusts how many depots are processed at once while a dump runs (AIMD: add one, halve on trouble).
    ///   speeds up  - when there is a backlog, responses are prompt, errors are rare and CPU/RAM have room
    ///   backs off  - on rate-limit signals from Steam or a CDN, bursts of errors, slow responses, or CPU/RAM pressure
    /// With dynamic speed off it stays at one depot at a time, the way earlier versions worked.
    /// </summary>
    public static class Throttle
    {
        /// <summary>Depots being dumped at the same time (shared by every app).</summary>
        public static readonly DynamicLimiter Depots = new DynamicLimiter(1);

        private const int MaxStartWorkers = 4;
        private const int CheckSeconds = 4;
        // Memory is judged by what is actually free (scaled to this PC's total RAM), not a percentage: many PCs sit near 90% "used"
        // (cache, browsers) yet still have room. lowFreeMb: slow down below it. roomFreeMb: do not speed up below it.
        private static int lowFreeMb = 600, roomFreeMb = 1000, totalMemoryMb;
        private static int memoryLimitMb;        // optional user ceiling for this app's own memory (0 = automatic)
        private static int processMemoryMb;

        private static readonly object gate = new object();
        private static Timer timer;
        private static bool dynamic;
        private static int workers = 1, maxWorkers = 1;

        private static int okCount, failCount, rateLimitedCount, latencyCount, totalRateLimits;
        private static long latencyTicks;
        private static double baselineMs;
        private static DateTime cooldownUntil, softMaxUntil, lastLimitAt = DateTime.MinValue;
        private static int softMax = int.MaxValue, softMinutes = 1;
        private static string lastAction = "off";
        private static int cpuPercent, memoryPercent, freeMemoryMb = int.MaxValue;
        private static TimeSpan lastCpu;
        private static DateTime lastSample;

        public static void Start(bool dynamicEnabled, int maxParallel, int memoryLimitMegabytes = 0)
        {
            Stop();
            lock (gate)
            {
                dynamic = dynamicEnabled;
                maxWorkers = Math.Clamp(maxParallel, 1, 64);
                memoryLimitMb = Math.Max(0, memoryLimitMegabytes);
                lastCpu = CurrentCpu(); lastSample = DateTime.UtcNow;
                SampleResources();
                // scale to this machine: fewer cores or little free RAM = start (and stay) more careful
                lowFreeMb = Math.Clamp(totalMemoryMb * 4 / 100, 300, 1000);
                roomFreeMb = Math.Clamp(totalMemoryMb * 7 / 100, 500, 1500);
                int start = Math.Clamp(Environment.ProcessorCount / 2, 1, MaxStartWorkers);
                if (freeMemoryMb < roomFreeMb) start = 1;
                workers = dynamic ? Math.Min(start, maxWorkers) : 1;
                okCount = failCount = rateLimitedCount = latencyCount = totalRateLimits = 0;
                latencyTicks = 0; baselineMs = 0; cooldownUntil = DateTime.MinValue; softMaxUntil = DateTime.MinValue; softMax = int.MaxValue; softMinutes = 1; lastLimitAt = DateTime.MinValue;
                lastAction = dynamic ? $"starting at {workers} parallel depots (max {maxWorkers})" : "off - one depot at a time";
                lastCpu = CurrentCpu(); lastSample = DateTime.UtcNow;
                Depots.SetLimit(workers);
                Logger.Info($"Dynamic speed: {lastAction}.");
                if (dynamic) timer = new Timer(_ => Evaluate(), null, TimeSpan.FromSeconds(CheckSeconds), TimeSpan.FromSeconds(CheckSeconds));
            }
        }

        public static void Stop()
        {
            lock (gate) { timer?.Dispose(); timer = null; }
        }

        // ---- signals ------------------------------------------------------------------------

        public static void ReportSuccess() => Interlocked.Increment(ref okCount);
        public static void ReportFailure() => Interlocked.Increment(ref failCount);

        public static void ReportLatency(TimeSpan t)
        {
            Interlocked.Add(ref latencyTicks, t.Ticks);
            Interlocked.Increment(ref latencyCount);
        }

        public static void ReportRateLimited(string why)
        {
            Interlocked.Increment(ref rateLimitedCount);
            Interlocked.Increment(ref totalRateLimits);
            Logger.Warning($"Rate-limit / overload signal: {why}");
        }

        // ---- controller ----------------------------------------------------------------------

        private static void Evaluate()
        {
            try
            {
                int ok = Interlocked.Exchange(ref okCount, 0);
                int fail = Interlocked.Exchange(ref failCount, 0);
                int rl = Interlocked.Exchange(ref rateLimitedCount, 0);
                int lat = Interlocked.Exchange(ref latencyCount, 0);
                long ticks = Interlocked.Exchange(ref latencyTicks, 0);
                double avgMs = lat > 0 ? ticks / (double)lat / TimeSpan.TicksPerMillisecond : 0;

                SampleResources();
                var now = DateTime.UtcNow;

                lock (gate)
                {
                    int next = workers;
                    string action = null;

                    if (rl > 0)
                    {
                        // remember the level just below the one that got us limited, and don't exceed it for a while
                        // hit again soon after the last time = the limit is real: remember it for twice as long (2, 4, 8 ... 30 min)
                        softMinutes = (now - lastLimitAt) < TimeSpan.FromMinutes(10) ? Math.Min(30, softMinutes * 2) : 2;
                        lastLimitAt = now;
                        softMax = Math.Max(1, workers - 1);
                        softMaxUntil = now.AddMinutes(softMinutes);
                        next = Math.Max(1, workers / 2);
                        action = $"{rl} rate-limit signal(s): halving to {next} (won't go above {softMax} for {softMinutes} min)";
                        cooldownUntil = now.AddSeconds(30);
                    }
                    else if (fail >= 3 && fail > (ok + fail) * 0.15)
                    {
                        next = Math.Max(1, (int)(workers * 0.7));
                        action = $"{fail} errors in {CheckSeconds}s: slowing to {next}";
                        cooldownUntil = now.AddSeconds(15);
                    }
                    else if (cpuPercent >= 90 || freeMemoryMb < lowFreeMb || memoryPercent >= 98 || (memoryLimitMb > 0 && processMemoryMb > memoryLimitMb))
                    {
                        next = Math.Max(1, (int)(workers * 0.75));
                        action = (memoryLimitMb > 0 && processMemoryMb > memoryLimitMb)
                            ? $"over the {memoryLimitMb / 1024.0:0.#} GB memory limit ({processMemoryMb} MB in use): slowing to {next}"
                            : $"resource pressure (CPU {cpuPercent}%, {freeMemoryMb} MB RAM free): slowing to {next}";
                        ReleaseMemory();   // hand freed manifest buffers back to the OS
                        cooldownUntil = now.AddSeconds(15);
                    }
                    else if (baselineMs > 0 && lat >= 3 && avgMs > baselineMs * 3)
                    {
                        next = Math.Max(1, (int)(workers * 0.75));
                        action = $"responses {avgMs / baselineMs:0.0}x slower than normal: slowing to {next}";
                        cooldownUntil = now.AddSeconds(10);
                    }
                    else
                    {
                        bool healthy = fail == 0 && cpuPercent < 70 && freeMemoryMb >= roomFreeMb
                                      && (memoryLimitMb == 0 || processMemoryMb < memoryLimitMb * 0.85);
                        if (healthy && lat > 0) baselineMs = baselineMs == 0 ? avgMs : baselineMs * 0.8 + avgMs * 0.2;

                        bool backlog = Depots.Waiting > 0 && Depots.InFlight >= workers;
                        int cap = now < softMaxUntil ? Math.Min(maxWorkers, softMax) : maxWorkers;   // learned ceiling after a rate limit
                        if (healthy && backlog && now >= cooldownUntil && workers < cap)
                        {
                            next = Math.Min(cap, workers + (workers < 8 ? 2 : 1));
                            action = $"healthy with work queued: speeding up to {next}";
                        }
                    }

                    if (next != workers)
                    {
                        workers = next;
                        lastAction = action;
                        Depots.SetLimit(workers);
                        Logger.Info($"Dynamic speed: {action}.");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Dynamic speed check failed: {ex.Message}");
            }
        }

        public static SpeedSnapshot Snapshot()
        {
            lock (gate)
                return new SpeedSnapshot(dynamic, workers, maxWorkers, Depots.InFlight, Depots.Waiting, cpuPercent, memoryPercent, freeMemoryMb == int.MaxValue ? 0 : freeMemoryMb, processMemoryMb, memoryLimitMb, totalRateLimits, lastAction);
        }

        private static void ReleaseMemory()
        {
            try
            {
                System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
                GC.Collect(2, GCCollectionMode.Forced, blocking: false, compacting: true);
            }
            catch { }
        }

        // ---- resource sampling ---------------------------------------------------------------

        private static TimeSpan CurrentCpu()
        {
            try { using var p = Process.GetCurrentProcess(); return p.TotalProcessorTime; }
            catch { return TimeSpan.Zero; }
        }

        private static void SampleResources()
        {
            var now = DateTime.UtcNow;
            var cpu = CurrentCpu();
            var wall = (now - lastSample).TotalSeconds;
            if (wall > 0.5)
            {
                double used = (cpu - lastCpu).TotalSeconds / (wall * Environment.ProcessorCount);
                cpuPercent = (int)Math.Clamp(used * 100, 0, 100);
                lastCpu = cpu; lastSample = now;
            }

            var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
            if (GlobalMemoryStatusEx(ref mem))
            {
                memoryPercent = (int)mem.dwMemoryLoad;
                freeMemoryMb = (int)Math.Min(int.MaxValue, mem.ullAvailPhys / (1024 * 1024));
                totalMemoryMb = (int)Math.Min(int.MaxValue, mem.ullTotalPhys / (1024 * 1024));
            }
            try { using var self = Process.GetCurrentProcess(); processMemoryMb = (int)(self.PrivateMemorySize64 / (1024 * 1024)); } catch { }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MEMORYSTATUSEX
        {
            public uint dwLength, dwMemoryLoad;
            public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual, ullAvailExtendedVirtual;
        }

        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX lpBuffer);
    }

    /// <summary>One-at-a-time async locks keyed by file path (depots running in parallel share some files).</summary>
    internal static class FileLocks
    {
        private static readonly ConcurrentDictionary<string, SemaphoreSlim> locks = new ConcurrentDictionary<string, SemaphoreSlim>(StringComparer.OrdinalIgnoreCase);

        public static async Task<IDisposable> LockAsync(string path)
        {
            var sem = locks.GetOrAdd(Path.GetFullPath(path), _ => new SemaphoreSlim(1, 1));
            await sem.WaitAsync().ConfigureAwait(false);
            return new Releaser(sem);
        }

        private sealed class Releaser : IDisposable
        {
            private SemaphoreSlim sem;
            public Releaser(SemaphoreSlim s) { sem = s; }
            public void Dispose() { Interlocked.Exchange(ref sem, null)?.Release(); }
        }
    }
}
