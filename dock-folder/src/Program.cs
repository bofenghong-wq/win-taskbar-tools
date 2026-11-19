using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Pipes;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading;

namespace DockFolder
{
    /// <summary>
    /// Entry point. Every click on a pinned taskbar folder starts this exe with --group "name".
    /// If the resident host is already running, the new process only forwards the request over a
    /// named pipe and exits (before WPF is ever loaded), so the popup appears almost instantly.
    /// </summary>
    internal static class Program
    {
        public const char Sep = '\u001f';
        public static string ExePath;
        public static string ExeDir;
        public static string InstanceId;
        public static string PipeName;
        static Mutex _mutex;

        [STAThread]
        static int Main(string[] args)
        {
            ExePath = System.Reflection.Assembly.GetEntryAssembly().Location;
            ExeDir = Path.GetDirectoryName(ExePath);
            InstanceId = "DDtools.DockFolder." + Fnv(ExePath.ToLowerInvariant()).ToString("x8");
            PipeName = InstanceId + ".pipe";

            string cmd = BuildCommand(args);
            bool createdNew;
            _mutex = new Mutex(true, "Local\\" + InstanceId, out createdNew);
            if (!createdNew)
            {
                if (cmd == "background") return 0;
                Native.AllowSetForegroundWindow(-1); // let the host bring its popup to the front
                if (Send(cmd)) return 0;
                if (cmd == "exit") return 0;
                // host is probably shutting down - wait for it, then take over
                try { if (!_mutex.WaitOne(4000)) return 1; }
                catch (AbandonedMutexException) { }
            }
            if (cmd == "exit") return 0;
            return StartHost(cmd);
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        static int StartHost(string cmd)
        {
            return Host.Run(cmd);
        }

        static string BuildCommand(string[] args)
        {
            string group = null, view = null;
            bool bg = false, exit = false, manage = false;
            List<string> paths = new List<string>();
            for (int i = 0; i < args.Length; i++)
            {
                string a = args[i];
                if ((a == "--group" || a == "-g") && i + 1 < args.Length) group = args[++i];
                else if (a == "--background") bg = true;
                else if (a == "--exit") exit = true;
                else if (a == "--manage") manage = true;
                else if (a == "--view" && i + 1 < args.Length) view = args[++i].ToLowerInvariant();
                else paths.Add(a);
            }
            if (exit) return "exit";
            if (group != null && view != null) return "view" + Sep + group + Sep + view;
            if (group != null && manage) return "manage" + Sep + group;
            if (group != null)
            {
                // files dropped onto the group's shortcut arrive as extra arguments
                if (paths.Count > 0) return "add" + Sep + group + Sep + string.Join(Sep.ToString(), paths.ToArray());
                Native.POINT p;
                Native.GetCursorPos(out p);
                return "show" + Sep + group + Sep + p.X + Sep + p.Y;
            }
            if (bg) return "background";
            if (paths.Count > 0) return "manage" + Sep + string.Join(Sep.ToString(), paths.ToArray());
            return "manage";
        }

        static bool Send(string cmd)
        {
            try
            {
                using (NamedPipeClientStream c = new NamedPipeClientStream(".", PipeName, PipeDirection.Out))
                {
                    c.Connect(2500);
                    byte[] b = Encoding.UTF8.GetBytes(cmd);
                    c.Write(b, 0, b.Length);
                    c.Flush();
                }
                return true;
            }
            catch { return false; }
        }

        public static uint Fnv(string s)
        {
            uint h = 2166136261;
            foreach (char c in s) { h ^= c; h *= 16777619; }
            return h;
        }

        static readonly object _logLock = new object();
        public static void Log(string s)
        {
            try
            {
                lock (_logLock)
                {
                    string f = Path.Combine(ExeDir, "DockFolder.log");
                    if (File.Exists(f) && new FileInfo(f).Length > 512 * 1024) File.Delete(f);
                    File.AppendAllText(f, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss  ") + s + Environment.NewLine, Encoding.UTF8);
                }
            }
            catch { }
        }
    }
}
