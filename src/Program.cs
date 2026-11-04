using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Threading;
using System.Windows;
using System.Windows.Markup;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

[assembly: TargetFramework(".NETFramework,Version=v4.8", FrameworkDisplayName = ".NET Framework 4.8")]
[assembly: System.Runtime.CompilerServices.InternalsVisibleTo("Snap")]
[assembly: AssemblyTitle("LiteTodo")]
[assembly: AssemblyProduct("LiteTodo")]
[assembly: AssemblyDescription("轻量桌面待办")]
[assembly: AssemblyVersion("1.2.0.0")]
[assembly: AssemblyFileVersion("1.2.0.0")]

namespace LiteTodo
{
    public static class Program
    {
        static readonly string LogPath = Path.Combine(Path.GetDirectoryName(Store.FilePath), "error.log");

        [DllImport("psapi.dll")] static extern int EmptyWorkingSet(IntPtr hProcess);

        /// <summary>回收内存并归还工作集（后台常驻时更轻量）</summary>
        public static void TrimMemory()
        {
            try
            {
                GC.Collect();
                GC.WaitForPendingFinalizers();
                GC.Collect();
                EmptyWorkingSet(System.Diagnostics.Process.GetCurrentProcess().Handle);
            }
            catch { }
        }

        [STAThread]
        public static int Main(string[] args)
        {
            bool silent = Array.IndexOf(args, "--autostart") >= 0;
            // 每个数据文件一个实例（便携版副本之间互不影响）
            var key = InstanceKey(Store.FilePath);

            // --exit：通知正在运行的实例保存并退出（升级 / 卸载时使用）
            if (Array.IndexOf(args, "--exit") >= 0)
            {
                try { using (var ev = EventWaitHandle.OpenExisting("LiteTodo.Exit." + key)) ev.Set(); } catch { }
                return 0;
            }
            using (var mutex = new Mutex(true, "LiteTodo.SingleInstance." + key, out bool first))
            using (var showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "LiteTodo.Show." + key))
            using (var exitEvent = new EventWaitHandle(false, EventResetMode.AutoReset, "LiteTodo.Exit." + key))
            {
                if (!first)
                {
                    showEvent.Set(); // 已在运行：唤醒已有窗口
                    return 0;
                }

                var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                app.DispatcherUnhandledException += (s, e) =>
                {
                    Log("未处理异常: " + e.Exception);
                    e.Handled = true;
                };
                AppDomain.CurrentDomain.UnhandledException += (s, e) => Log("致命异常: " + e.ExceptionObject);

                try
                {
                    app.Resources.MergedDictionaries.Add(LoadXaml<ResourceDictionary>("Styles.xaml"));
                    var ctl = new MainController(app, silent);

                    // 第二个实例启动时，把窗口调出来
                    var waiter = new Thread(() =>
                    {
                        try
                        {
                            while (true)
                            {
                                int i = WaitHandle.WaitAny(new WaitHandle[] { showEvent, exitEvent });
                                if (i == 1) { app.Dispatcher.BeginInvoke(new Action(ctl.RequestExit)); break; }
                                app.Dispatcher.BeginInvoke(new Action(ctl.ShowWindow));
                            }
                        }
                        catch { }
                    }) { IsBackground = true };
                    waiter.Start();

                    app.Run();
                }
                catch (Exception ex)
                {
                    Log("启动失败: " + ex);
                    MessageBox.Show("LiteTodo 启动失败：\n" + ex.Message + "\n\n详情见 " + LogPath, "LiteTodo",
                        MessageBoxButton.OK, MessageBoxImage.Error);
                    return 1;
                }
                GC.KeepAlive(mutex);
            }
            return 0;
        }

        static string InstanceKey(string path)
        {
            using (var sha = System.Security.Cryptography.SHA1.Create())
            {
                var h = sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes(path.ToLowerInvariant()));
                return BitConverter.ToString(h, 0, 8).Replace("-", "");
            }
        }

        static Stream Res(string name) => Assembly.GetExecutingAssembly().GetManifestResourceStream(name);

        public static T LoadXaml<T>(string name)
        {
            using (var s = Res(name))
            {
                if (s == null) throw new FileNotFoundException("缺少内嵌资源 " + name);
                return (T)XamlReader.Load(s);
            }
        }

        public static BitmapFrame LoadIconFrame()
        {
            try
            {
                var s = Res("app.ico");
                return s == null ? null : BitmapFrame.Create(s, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            }
            catch { return null; }
        }

        public static System.Drawing.Icon LoadTrayIcon()
        {
            try
            {
                using (var s = Res("app.ico"))
                    return s == null ? null : new System.Drawing.Icon(s, System.Windows.Forms.SystemInformation.SmallIconSize);
            }
            catch { return null; }
        }

        public static void Log(string msg)
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(LogPath));
                File.AppendAllText(LogPath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + msg + Environment.NewLine);
            }
            catch { }
        }
    }
}
