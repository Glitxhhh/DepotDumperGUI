using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            try
            {
                await PauseControl.WaitIfPausedAsync().ConfigureAwait(false);   // paused: nothing new starts until Resume (or Stop)
                await work().ConfigureAwait(false);
            }
            finally { Release(); }
        }
    }

    /// <summary>
    /// Pause/Resume for a running dump. While paused nothing NEW starts (apps, depots, manifests); work that is already running
    /// finishes normally. Resume (or Stop) lets everything continue. Not saved across restarts: use Stop and Resume for that.
    /// </summary>
    public static class PauseControl
    {
        private static readonly object gate = new object();
        private static TaskCompletionSource<bool> open;   // non-null while paused; completes on resume

        public static bool IsPaused { get { lock (gate) return open != null; } }

        public static void Pause()
        {
            lock (gate)
            {
                if (open != null) return;
                open = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            Logger.Info("Paused: nothing new will start; work already running finishes.");
        }

        public static void Resume()
        {
            TaskCompletionSource<bool> t;
            lock (gate) { t = open; open = null; }
            if (t == null) return;
            t.TrySetResult(true);
            Logger.Info("Resumed.");
        }

        public static Task WaitIfPausedAsync()
        {
            lock (gate) return open?.Task ?? Task.CompletedTask;
        }
    }

    public readonly record struct SpeedSnapshot(bool Dynamic, int Workers, int MaxWorkers, int InFlight, int Waiting,
        int CpuPercent, int MemoryPercent, int FreeMemoryMb, int ProcessMemoryMb, int MemoryLimitMb, int RateLimitHits, string LastAction,
        int CallLimit = 0, int CallsInFlight = 0, int CallsWaiting = 0);

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

        /// <summary>
        /// Every request that goes to Steam itself (depot keys, manifest codes, app info, CDN tokens) passes through here, so no matter how
        /// many depots, manifests and apps are running there are only a few Steam requests outstanding at once. Too many at once make Steam
        /// answer with job timeouts; on a timeout the limit is halved and new requests are held back briefly so the queue can drain.
        /// </summary>
        public static readonly DynamicLimiter SteamCalls = new DynamicLimiter(6);
        private static int callLimit = 6, callMax = 16, callTimeouts, callOkStreak, callCeiling = int.MaxValue;
        private static DateTime callCoolUntil = DateTime.MinValue, callCeilingUntil = DateTime.MinValue, lastCallIncident = DateTime.MinValue;

        public static async Task<T> SteamCallAsync<T>(Func<Task<T>> call)
        {
            await SteamCalls.WaitAsync().ConfigureAwait(false);
            try
            {
                var hold = callCoolUntil - DateTime.UtcNow;   // Steam was just timing out: let what is in flight finish first
                if (hold > TimeSpan.Zero) await Task.Delay(hold).ConfigureAwait(false);
                return await call().ConfigureAwait(false);
            }
            catch (TaskCanceledException) { Interlocked.Increment(ref callTimeouts); throw; }   // a Steam job that did not answer in time
            finally { SteamCalls.Release(); }
        }

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
        private static DateTime cooldownUntil, softMaxUntil, lastLimitAt = DateTime.MinValue;
        private static int softMax = int.MaxValue, healthyStreak;
        private static DateTime lastIncidentAt = DateTime.MinValue;
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
                lowFreeMb = Math.Clamp(totalMemoryMb * 25 / 1000, 250, 700);      // slow down below ~2.5% of RAM free
                roomFreeMb = Math.Clamp(totalMemoryMb * 4 / 100, 400, 1000);      // speed up only above ~4% (a PC with a browser open is often near 7%)
                int start = Math.Clamp(Environment.ProcessorCount / 2, 1, MaxStartWorkers);
                if (freeMemoryMb < roomFreeMb) start = 1;
                workers = dynamic ? Math.Min(start, maxWorkers) : 1;
                okCount = failCount = rateLimitedCount = latencyCount = totalRateLimits = 0;
                latencyTicks = 0; cooldownUntil = DateTime.MinValue; softMaxUntil = DateTime.MinValue; softMax = int.MaxValue; lastLimitAt = DateTime.MinValue; lastIncidentAt = DateTime.MinValue; healthyStreak = 0;
                lastAction = dynamic ? $"starting at {workers} parallel depots (max {maxWorkers})" : "off - one depot at a time";
                lastCpu = CurrentCpu(); lastSample = DateTime.UtcNow;
                Depots.SetLimit(workers);
                callMax = Math.Clamp(maxWorkers * 2, 4, 32);
                callLimit = dynamic ? Math.Min(6, callMax) : 4;
                callTimeouts = 0; callOkStreak = 0; callCeiling = int.MaxValue; callCoolUntil = DateTime.MinValue; callCeilingUntil = DateTime.MinValue; lastCallIncident = DateTime.MinValue;
                SteamCalls.SetLimit(callLimit);
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

        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> cdnHostsThisWindow = new();
        private static int cdnHitsThisWindow;

        /// <summary>A CDN server answered 5xx/429. One misbehaving server is handled by switching to another; only several different servers (or many answers) mean real load.</summary>
        public static void ReportCdnOverload(string host, string why)
        {
            cdnHostsThisWindow[host ?? "?"] = 0;
            Interlocked.Increment(ref cdnHitsThisWindow);
            Logger.Info($"CDN server problem: {why}");
        }

        public static void ReportRateLimited(string why)
        {
            Interlocked.Increment(ref rateLimitedCount);
            Interlocked.Increment(ref totalRateLimits);
            Logger.Warning($"Rate-limit / overload signal: {why}");
        }

        // ---- controller ----------------------------------------------------------------------

        private static void EvaluateSteamCalls(int timeouts, DateTime now)
        {
            if (!dynamic) return;
            if (timeouts > 0)
            {
                if (now - lastCallIncident > TimeSpan.FromSeconds(30))   // one incident, one lesson: stay under the level that timed out for a minute
                {
                    callCeiling = Math.Max(2, (int)(callLimit * 0.8));
                    callCeilingUntil = now.AddMinutes(1);
                    lastCallIncident = now;
                }
                callLimit = Math.Max(2, callLimit / 2);
                callCoolUntil = now.AddSeconds(4);
                callOkStreak = 0;
                SteamCalls.SetLimit(callLimit);
                Logger.Info($"Steam requests: {timeouts} timed out - allowing {callLimit} at once and holding new requests for 4 s.");
            }
            else if (now >= callCoolUntil && ++callOkStreak >= 2)
            {
                callOkStreak = 0;
                var cap = now < callCeilingUntil ? Math.Min(callMax, callCeiling) : callMax;
                if (callLimit < cap && SteamCalls.Waiting > 0)
                {
                    callLimit++;
                    SteamCalls.SetLimit(callLimit);
                }
            }
        }

        private static void Evaluate()
        {
            try
            {
                int ok = Interlocked.Exchange(ref okCount, 0);
                int fail = Interlocked.Exchange(ref failCount, 0);
                int rl = Interlocked.Exchange(ref rateLimitedCount, 0);
                var cdnHits = Interlocked.Exchange(ref cdnHitsThisWindow, 0);
                var cdnHosts = cdnHostsThisWindow.Count; cdnHostsThisWindow.Clear();
                if (cdnHosts >= 2 || cdnHits >= 6) { rl++; Interlocked.Increment(ref totalRateLimits); }
                int lat = Interlocked.Exchange(ref latencyCount, 0);
                long ticks = Interlocked.Exchange(ref latencyTicks, 0);
                double avgMs = lat > 0 ? ticks / (double)lat / TimeSpan.TicksPerMillisecond : 0;

                SampleResources();
                var now = DateTime.UtcNow;
                var callTo = Interlocked.Exchange(ref callTimeouts, 0);

                lock (gate)
                {
                    EvaluateSteamCalls(callTo, now);
                    int next = workers;
                    string action = null;

                    // A real overload signal teaches ONE short lesson: ease off, and stay a little under the level that caused it for a
                    // minute. Nothing escalates and nothing locks the speed down for long: the aim is a steady pace just under Steam's limit.
                    void EaseOff(int holdSeconds)
                    {
                        healthyStreak = 0;
                        if (now - lastIncidentAt < TimeSpan.FromSeconds(30)) return;   // a burst of signals is one incident
                        lastIncidentAt = now;
                        softMax = Math.Max(2, (int)(workers * 0.85));
                        softMaxUntil = now.AddSeconds(holdSeconds);
                    }

                    if (rl > 0)
                    {
                        EaseOff(60);
                        next = Math.Min(workers, Math.Max(2, (int)(workers * 0.75)));
                        action = $"{rl} rate-limit signal(s): easing to {next} (staying under {softMax} for a minute)";
                        cooldownUntil = now.AddSeconds(15);
                    }
                    else if (fail >= 4 && fail > (ok + fail) * 0.25)
                    {
                        EaseOff(45);
                        next = Math.Min(workers, Math.Max(2, (int)(workers * 0.8)));
                        action = $"{fail} errors in {CheckSeconds}s: easing to {next}";
                        cooldownUntil = now.AddSeconds(10);
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
                    else
                    {
                        // a few stray errors (a depot this account has no key for, a slow CDN) must not freeze the pace: only a real share of failures does
                        bool healthy = (fail == 0 || fail * 10 <= ok) && cpuPercent < 70 && freeMemoryMb >= roomFreeMb
                                      && (memoryLimitMb == 0 || processMemoryMb < memoryLimitMb * 0.85);
                        bool backlog = Depots.Waiting > 0;
                        int cap = now < softMaxUntil ? Math.Min(maxWorkers, softMax) : maxWorkers;   // short-lived ceiling after an overload signal
                        if (healthy && backlog && now >= cooldownUntil && workers < cap)
                        {
                            // quick to reach a modest level, then one step per ~8 s so problems show up before the next step
                            healthyStreak++;
                            if (workers < 6 || healthyStreak >= 2)
                            {
                                next = Math.Min(cap, workers + (workers < 6 ? 2 : 1));
                                action = $"healthy with work queued: speeding up to {next}";
                                healthyStreak = 0;
                            }
                        }
                        else if (!healthy) healthyStreak = 0;

                        if (backlog && workers < cap && !healthy)
                        {
                            // say why the pace is not rising (dashboard "last adjustment"); not logged every 4 s
                            lastAction = freeMemoryMb < roomFreeMb ? $"holding at {workers}: only {freeMemoryMb} MB RAM free (needs {roomFreeMb} MB to speed up)"
                                       : cpuPercent >= 70 ? $"holding at {workers}: CPU at {cpuPercent}%"
                                       : $"holding at {workers}: {fail} error(s) in the last {CheckSeconds}s";
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
                return new SpeedSnapshot(dynamic, workers, maxWorkers, Depots.InFlight, Depots.Waiting, cpuPercent, memoryPercent, freeMemoryMb == int.MaxValue ? 0 : freeMemoryMb, processMemoryMb, memoryLimitMb, totalRateLimits, lastAction, callLimit, SteamCalls.InFlight, SteamCalls.Waiting);
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

            if (OperatingSystem.IsWindows())
            {
                var mem = new MEMORYSTATUSEX { dwLength = (uint)Marshal.SizeOf<MEMORYSTATUSEX>() };
                if (GlobalMemoryStatusEx(ref mem))
                {
                    memoryPercent = (int)mem.dwMemoryLoad;
                    freeMemoryMb = (int)Math.Min(int.MaxValue, mem.ullAvailPhys / (1024 * 1024));
                    totalMemoryMb = (int)Math.Min(int.MaxValue, mem.ullTotalPhys / (1024 * 1024));
                }
            }
            else if (TryReadProcMeminfo(out var totalKb, out var availKb) && totalKb > 0)
            {
                // Linux: MemAvailable already counts reclaimable cache as available, like Windows' "available"
                totalMemoryMb = (int)Math.Min(int.MaxValue, totalKb / 1024);
                freeMemoryMb = (int)Math.Min(int.MaxValue, availKb / 1024);
                memoryPercent = (int)Math.Clamp(100 - availKb * 100 / totalKb, 0, 100);
            }
            try { using var self = Process.GetCurrentProcess(); processMemoryMb = (int)(self.PrivateMemorySize64 / (1024 * 1024)); } catch { }
        }

        private static bool TryReadProcMeminfo(out long totalKb, out long availKb)
        {
            totalKb = availKb = 0;
            try
            {
                foreach (var line in System.IO.File.ReadLines("/proc/meminfo"))
                {
                    if (line.StartsWith("MemTotal:")) totalKb = ParseKb(line);
                    else if (line.StartsWith("MemAvailable:")) { availKb = ParseKb(line); break; }
                }
                return totalKb > 0 && availKb > 0;
            }
            catch { return false; }

            static long ParseKb(string line)
            {
                var digits = new string(line.Where(char.IsDigit).ToArray());
                return long.TryParse(digits, out var v) ? v : 0;
            }
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
