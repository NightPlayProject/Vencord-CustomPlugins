using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows;
using Microsoft.Web.WebView2.Core;

namespace VencordCustomManager;

public partial class App : Application
{
    private const string MutexName = @"Local\NightPlayProject.VencordCustomManager";
    private Mutex? _singleInstanceMutex;
    private bool _ownsMutex;
    internal static Task<CoreWebView2Environment>? WebViewEnvironmentTask { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        _singleInstanceMutex = new Mutex(initiallyOwned: true, MutexName, out var createdNew);
        _ownsMutex = createdNew;
        if (!createdNew)
        {
            TryActivateExistingInstance();
            Shutdown(0);
            return;
        }

        // Begin spinning up the Evergreen runtime before WPF creates MainWindow. The
        // window consumes this same task, so the expensive browser-process startup
        // overlaps application/XAML construction instead of starting afterwards.
        try
        {
            ManagerPaths.EnsureCreated();
            var userData = Path.Combine(ManagerPaths.Root, "webview2");
            Directory.CreateDirectory(userData);
            WebViewEnvironmentTask = CoreWebView2Environment.CreateAsync(null, userData);
        }
        catch
        {
            // MainWindow retains its normal creation/fallback path if prewarm cannot start.
            WebViewEnvironmentTask = null;
        }

        base.OnStartup(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_ownsMutex)
        {
            try { _singleInstanceMutex?.ReleaseMutex(); }
            catch (ApplicationException) { }
        }
        _singleInstanceMutex?.Dispose();
        base.OnExit(e);
    }

    private static void TryActivateExistingInstance()
    {
        try
        {
            using var current = Process.GetCurrentProcess();
            foreach (var process in Process.GetProcessesByName(current.ProcessName))
            {
                using (process)
                {
                    if (process.Id == current.Id || process.MainWindowHandle == IntPtr.Zero) continue;
                    ShowWindow(process.MainWindowHandle, 9); // SW_RESTORE
                    SetForegroundWindow(process.MainWindowHandle);
                    break;
                }
            }
        }
        catch
        {
            // The mutex still prevents concurrent mutation even if Windows refuses focus.
        }
    }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
}

