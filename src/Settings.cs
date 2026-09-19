using System;
using System.IO;
using System.Threading;
using System.Xml;

namespace MaxChunkAgeDeadlockFix
{
    public static class Settings
    {
        public static volatile bool Logging = true;

        public static volatile bool Watchdog = true;

        public static long Reloads;

        public static string LastError = "";

        private static string path = "";

        private static FileSystemWatcher watcher;

        private static Thread poller;

        private static readonly AutoResetEvent poke = new AutoResetEvent(false);

        private static volatile bool stopping;

        private static long lastStampTicks;

        private static long lastLength = -1L;

        private static readonly object ioLock = new object();

        public static string Path
        {
            get { return path; }
        }

        public static bool Watching
        {
            get { return poller != null && poller.IsAlive; }
        }

        public static void Load(string modPath)
        {
            path = System.IO.Path.Combine(modPath ?? ".", "Config.xml");
            if (!File.Exists(path))
            {
                Save();
                Stamp();
                return;
            }

            Apply(false);
            Stamp();
        }

        public static bool Reload()
        {
            bool ok = Apply(true);
            Stamp();
            return ok;
        }

        private static bool Apply(bool countReload)
        {
            lock (ioLock)
            {
                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.Load(path);
                    XmlElement root = doc.DocumentElement;
                    if (root == null)
                    {
                        LastError = "no root element";
                        return false;
                    }

                    bool logging = Logging;
                    bool watchdog = Watchdog;

                    foreach (XmlNode node in root.ChildNodes)
                    {
                        XmlElement e = node as XmlElement;
                        if (e == null || e.Name != "property")
                        {
                            continue;
                        }

                        string name = e.GetAttribute("name");
                        string value = e.GetAttribute("value");
                        switch (name)
                        {
                            case "Logging":
                                logging = ParseBool(value, logging);
                                break;
                            case "Watchdog":
                                watchdog = ParseBool(value, watchdog);
                                break;
                        }
                    }

                    Logging = logging;
                    Watchdog = watchdog;
                    LastError = "";
                    if (countReload)
                    {
                        Reloads++;
                    }

                    return true;
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    return false;
                }
            }
        }

        public static void Save()
        {
            if (string.IsNullOrEmpty(path))
            {
                return;
            }

            lock (ioLock)
            {
                try
                {
                    XmlDocument doc = new XmlDocument();
                    doc.AppendChild(doc.CreateXmlDeclaration("1.0", "UTF-8", null));
                    XmlElement root = doc.CreateElement("MaxChunkAgeDeadlockFix");
                    doc.AppendChild(root);
                    Put(doc, root, "Logging", Logging ? "true" : "false");
                    Put(doc, root, "Watchdog", Watchdog ? "true" : "false");
                    doc.Save(path);
                    LastError = "";
                }
                catch (Exception ex)
                {
                    LastError = ex.Message;
                    Log.Warning("[MaxChunkAgeDeadlockFix] cannot write Config.xml: " + ex.Message);
                }
            }

            Stamp();
        }

        private static void Put(XmlDocument doc, XmlElement root, string name, string value)
        {
            XmlElement e = doc.CreateElement("property");
            e.SetAttribute("name", name);
            e.SetAttribute("value", value);
            root.AppendChild(e);
        }

        public static void StartWatch()
        {
            if (string.IsNullOrEmpty(path) || poller != null)
            {
                return;
            }

            stopping = false;
            poller = new Thread(Loop);
            poller.IsBackground = true;
            poller.Name = "MaxChunkAgeDeadlockFix_Config";
            poller.Priority = ThreadPriority.BelowNormal;
            poller.Start();

            try
            {
                string dir = System.IO.Path.GetDirectoryName(path);
                string file = System.IO.Path.GetFileName(path);
                watcher = new FileSystemWatcher(dir, file);
                watcher.NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName | NotifyFilters.CreationTime;
                watcher.Changed += OnFileEvent;
                watcher.Created += OnFileEvent;
                watcher.Renamed += OnFileEvent;
                watcher.EnableRaisingEvents = true;
            }
            catch (Exception ex)
            {
                watcher = null;
                Log.Warning("[MaxChunkAgeDeadlockFix] FileSystemWatcher unavailable, polling every second: " + ex.Message);
            }
        }

        public static void StopWatch()
        {
            stopping = true;
            try
            {
                if (watcher != null)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                    watcher = null;
                }
            }
            catch
            {
            }

            try
            {
                poke.Set();
            }
            catch
            {
            }

            poller = null;
        }

        private static void OnFileEvent(object sender, FileSystemEventArgs e)
        {
            try
            {
                poke.Set();
            }
            catch
            {
            }
        }

        private static void Loop()
        {
            while (!stopping)
            {
                poke.WaitOne(1000);
                if (stopping)
                {
                    return;
                }

                try
                {
                    if (!Changed())
                    {
                        continue;
                    }

                    Thread.Sleep(120);
                    if (!File.Exists(path))
                    {
                        continue;
                    }

                    if (Reload())
                    {
                        MainThreadWatchdog.Apply();
                        Log.Out("[MaxChunkAgeDeadlockFix] Config.xml reloaded #" + Reloads + ": " + Describe());
                    }
                    else
                    {
                        Log.Warning("[MaxChunkAgeDeadlockFix] Config.xml reload failed, keeping current values: " + LastError);
                    }
                }
                catch (Exception ex)
                {
                    Log.Warning("[MaxChunkAgeDeadlockFix] config watcher error: " + ex.Message);
                }
            }
        }

        private static bool Changed()
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    return false;
                }

                long ticks = fi.LastWriteTimeUtc.Ticks;
                long len = fi.Length;
                if (ticks == Interlocked.Read(ref lastStampTicks) && len == Interlocked.Read(ref lastLength))
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static void Stamp()
        {
            try
            {
                FileInfo fi = new FileInfo(path);
                if (!fi.Exists)
                {
                    return;
                }

                Interlocked.Exchange(ref lastStampTicks, fi.LastWriteTimeUtc.Ticks);
                Interlocked.Exchange(ref lastLength, fi.Length);
            }
            catch
            {
            }
        }

        private static bool ParseBool(string s, bool fallback)
        {
            bool v;
            if (bool.TryParse(s, out v))
            {
                return v;
            }

            if (s == "1" || s == "on" || s == "yes")
            {
                return true;
            }

            if (s == "0" || s == "off" || s == "no")
            {
                return false;
            }

            return fallback;
        }

        public static string Describe()
        {
            return "logging=" + (Logging ? "on" : "off")
                + " watchdog=" + (Watchdog ? "on" : "off");
        }
    }
}
