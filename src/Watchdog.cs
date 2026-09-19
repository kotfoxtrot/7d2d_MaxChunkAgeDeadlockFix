using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace MaxChunkAgeDeadlockFix
{
    internal static class MainThreadWatchdog
    {
        private const int PollSeconds = 5;

        private const int WarnSeconds = 45;

        private const int DumpSeconds = 90;

        private const int SIGQUIT = 3;

        private static long lastTickTicks;

        private static long mainTicks;

        private static volatile bool armed;

        private static bool warned;

        private static bool dumped;

        private static volatile bool running;

        private static int generation;

        private static readonly object installLock = new object();

        public static bool Running
        {
            get { return running; }
        }

        public static void Apply()
        {
            lock (installLock)
            {
                if (Settings.Watchdog)
                {
                    Start();
                }
                else
                {
                    Stop();
                }
            }
        }

        private static void Start()
        {
            if (running)
            {
                return;
            }

            armed = false;
            warned = false;
            dumped = false;
            Interlocked.Exchange(ref lastTickTicks, DateTime.UtcNow.Ticks);

            int gen = Interlocked.Increment(ref generation);
            running = true;
            ThreadManager.UpdateEv += OnMainThreadUpdate;

            Thread thread = new Thread(() => Loop(gen));
            thread.Name = "MaxChunkAgeDeadlockFix_Watchdog";
            thread.IsBackground = true;
            thread.Start();
        }

        private static void Stop()
        {
            if (!running)
            {
                return;
            }

            running = false;
            Interlocked.Increment(ref generation);
            try
            {
                ThreadManager.UpdateEv -= OnMainThreadUpdate;
            }
            catch (Exception e)
            {
                Log.Warning("[MaxChunkAgeDeadlockFix][Watchdog] failed to detach from UpdateEv: " + e.Message);
            }

            armed = false;
        }

        private static void OnMainThreadUpdate()
        {
            Interlocked.Increment(ref mainTicks);
            Interlocked.Exchange(ref lastTickTicks, DateTime.UtcNow.Ticks);
            armed = true;
        }

        private static void Loop(int gen)
        {
            while (true)
            {
                Thread.Sleep(PollSeconds * 1000);

                if (!running || Interlocked.CompareExchange(ref generation, gen, gen) != gen)
                {
                    return;
                }

                if (!armed)
                {
                    continue;
                }

                double stalled = (double)(DateTime.UtcNow.Ticks - Interlocked.Read(ref lastTickTicks)) / TimeSpan.TicksPerSecond;
                if (stalled < WarnSeconds)
                {
                    warned = false;
                    dumped = false;
                    continue;
                }

                if (!warned)
                {
                    warned = true;
                    Log.Warning(Diagnostics.Describe(stalled, Interlocked.Read(ref mainTicks)));
                }

                if (stalled >= DumpSeconds && !dumped)
                {
                    dumped = true;
                    Log.Error(Diagnostics.Describe(stalled, Interlocked.Read(ref mainTicks)));
                    Log.Error("[MaxChunkAgeDeadlockFix][Watchdog] main thread considered deadlocked, requesting managed stack dump of all threads.");
                    Thread.Sleep(500);
                    RequestManagedStackDump();
                }
            }
        }

        private static void RequestManagedStackDump()
        {
            PlatformID platform = Environment.OSVersion.Platform;
            if (platform != PlatformID.Unix && platform != PlatformID.MacOSX)
            {
                Log.Error("[MaxChunkAgeDeadlockFix][Watchdog] stack dump via SIGQUIT is only available on Unix, skipping.");
                return;
            }

            try
            {
                int result = kill(getpid(), SIGQUIT);
                if (result != 0)
                {
                    Log.Error("[MaxChunkAgeDeadlockFix][Watchdog] kill(SIGQUIT) failed with code " + result + ".");
                }
            }
            catch (Exception e)
            {
                Log.Error("[MaxChunkAgeDeadlockFix][Watchdog] failed to request stack dump: " + e);
            }
        }

        [DllImport("libc", SetLastError = true)]
        private static extern int kill(int pid, int sig);

        [DllImport("libc")]
        private static extern int getpid();
    }
}
