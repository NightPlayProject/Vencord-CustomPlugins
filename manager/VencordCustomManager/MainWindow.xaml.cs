using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using Microsoft.Web.WebView2.Core;

namespace VencordCustomManager;

public partial class MainWindow : Window
{
    private readonly InstallationService _installationService = new();
    private UpdateManifest? _manifest;
    private ManagerState _state = new();
    private DiscordInstallProbe? _localProbe;
    private readonly Dictionary<string, DiscordInstallProbe> _clientProbes = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<ScrollViewer, SmoothScrollState> _smoothScrollStates = new();
    private bool _busy;
    private bool _allowCloseWhileBusy;
    private bool _smoothScrollRenderingSubscribed;
    private string _lastProgressMessage = string.Empty;
    private TaskCompletionSource<bool>? _dialogCompletion;
    private IInputElement? _dialogPreviousFocus;
    private bool _webUiReady;
    private TaskCompletionSource<bool>? _webUiReadyCompletion;
    private Task? _webUiInitializationTask;
    private bool _webDialogActive;
    private string? _webDialogId;
    private DialogTone _currentStatusTone = DialogTone.Accent;

    private static readonly SolidColorBrush AccentBrush = Brush(0xF4, 0xF4, 0xF5);
    private static readonly SolidColorBrush AccentSoftBrush = Brush(0x1A, 0x1A, 0x1A);
    private static readonly SolidColorBrush AccentBorderBrush = Brush(0x3F, 0x3F, 0x46);
    private static readonly SolidColorBrush SuccessBrush = Brush(0x34, 0xD3, 0x99);
    private static readonly SolidColorBrush SuccessSoftBrush = Brush(0x10, 0x25, 0x1D);
    private static readonly SolidColorBrush SuccessBorderBrush = Brush(0x20, 0x4A, 0x3A);
    private static readonly SolidColorBrush WarningBrush = Brush(0xFB, 0xBF, 0x24);
    private static readonly SolidColorBrush WarningSoftBrush = Brush(0x2A, 0x21, 0x10);
    private static readonly SolidColorBrush WarningBorderBrush = Brush(0x5A, 0x45, 0x17);
    private static readonly SolidColorBrush DangerBrush = Brush(0xFB, 0x71, 0x85);
    private static readonly SolidColorBrush DangerSoftBrush = Brush(0x30, 0x16, 0x1D);
    private static readonly SolidColorBrush DangerBorderBrush = Brush(0x5B, 0x27, 0x32);
    private static readonly SolidColorBrush NeutralBrush = Brush(0xA1, 0xA1, 0xAA);
    private static readonly SolidColorBrush NeutralSoftBrush = Brush(0x14, 0x14, 0x14);
    private static readonly SolidColorBrush NeutralBorderBrush = Brush(0x27, 0x27, 0x2A);
    private static readonly SolidColorBrush ClientRowBrush = Brush(0x09, 0x09, 0x09);
    private static readonly SolidColorBrush ClientRowBorderBrush = Brush(0x26, 0x26, 0x26);
    private static readonly SolidColorBrush SelectedClientRowBrush = Brush(0x18, 0x18, 0x18);

    private const double WheelScrollMultiplier = 0.72;
    private static readonly TimeSpan SmoothScrollDuration = TimeSpan.FromMilliseconds(145);

    public MainWindow()
    {
        InitializeComponent();

        // Start WebView2 environment creation immediately so its comparatively expensive
        // runtime startup overlaps the native state/probe work and initial window layout.
        // Window_Loaded still awaits this same task before recovery/update work continues.
        _webUiInitializationTask = InitializeWebUiAsync();

        _state = _installationService.LoadState();
        ApplySavedBranch();
        if (_installationService.GetPendingRecoveryBranches().Count == 0)
            _state = _installationService.RecoverAllStateFromLocalInstallations();
        RefreshUi();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        await (_webUiInitializationTask ??= InitializeWebUiAsync());
        AppendLog($"Custom Vencord Manager v{AppInfo.CurrentVersion} started.");
        AppendLog("Managed installation storage ready.");
        await RecoverInterruptedOperationsAsync();
        await CheckForUpdatesAsync(showSuccessDialog: false);
    }

    private async Task InitializeWebUiAsync()
    {
        var startupTimer = Stopwatch.StartNew();
        _webUiReady = false;
        _webUiReadyCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        StartupSurface.Visibility = Visibility.Visible;
        LegacyDashboard.Visibility = Visibility.Collapsed;
        WebDashboard.Visibility = Visibility.Collapsed;

        try
        {
            ManagerPaths.EnsureCreated();
            var webRoot = ExtractEmbeddedWebUi();
            var userData = Path.Combine(ManagerPaths.Root, "webview2");
            Directory.CreateDirectory(userData);

            var environment = await (App.WebViewEnvironmentTask ?? CoreWebView2Environment.CreateAsync(null, userData));
            WebDashboard.DefaultBackgroundColor = System.Drawing.Color.FromArgb(255, 5, 5, 5);
            await WebDashboard.EnsureCoreWebView2Async(environment);
            var core = WebDashboard.CoreWebView2;

            core.Settings.AreDevToolsEnabled = false;
            core.Settings.AreDefaultContextMenusEnabled = false;
            core.Settings.AreBrowserAcceleratorKeysEnabled = false;
            core.Settings.IsStatusBarEnabled = false;
            core.Settings.IsZoomControlEnabled = false;
            core.Settings.IsBuiltInErrorPageEnabled = false;

            core.WebMessageReceived += WebDashboard_WebMessageReceived;
            var bootstrapJson = JsonSerializer.Serialize(BuildWebStateSnapshot());
            await core.AddScriptToExecuteOnDocumentCreatedAsync(
                $"window.__MANAGER_BOOTSTRAP__={bootstrapJson};" +
                "window.addEventListener('error',e=>window.chrome?.webview?.postMessage({type:'webError',message:e.message||'Unknown script error'}));" +
                "window.addEventListener('unhandledrejection',e=>window.chrome?.webview?.postMessage({type:'webError',message:String(e.reason||'Unhandled promise rejection')}));");
            core.ProcessFailed += (_, args) =>
                AppendLog($"Web UI process failure: {args.ProcessFailedKind}.");
            core.NewWindowRequested += (_, args) =>
            {
                args.Handled = true;
                if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) &&
                    (uri.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || uri.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
                    OpenUrl(uri.AbsoluteUri);
            };
            core.NavigationStarting += (_, args) =>
            {
                if (!Uri.TryCreate(args.Uri, UriKind.Absolute, out var uri) ||
                    !uri.Host.Equals("manager.local", StringComparison.OrdinalIgnoreCase))
                {
                    args.Cancel = true;
                    if (Uri.TryCreate(args.Uri, UriKind.Absolute, out var external) &&
                        (external.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase) || external.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase)))
                        OpenUrl(external.AbsoluteUri);
                }
            };
            core.PermissionRequested += (_, args) => args.State = CoreWebView2PermissionState.Deny;

            WebDashboard.NavigationCompleted += (_, args) =>
            {
                if (!args.IsSuccess)
                {
                    AppendLog($"React dashboard load failed: {args.WebErrorStatus}.");
                    _webUiReadyCompletion?.TrySetException(
                        new InvalidOperationException($"React dashboard navigation failed: {args.WebErrorStatus}."));
                    return;
                }

                // Keep the native skeleton on-screen until React itself confirms it has
                // mounted. This avoids exposing an empty WebView if JavaScript fails after
                // navigation and makes the native -> React skeleton handoff invisible.
            };

            core.SetVirtualHostNameToFolderMapping(
                "manager.local",
                webRoot,
                CoreWebView2HostResourceAccessKind.Allow);
            WebDashboard.Source = new Uri("https://manager.local/index.html");

            await _webUiReadyCompletion.Task.WaitAsync(TimeSpan.FromSeconds(5));
            AppendLog($"React + Tailwind dashboard loaded in {startupTimer.ElapsedMilliseconds} ms.");
        }
        catch (Exception ex)
        {
            _webUiReady = false;
            StartupSurface.Visibility = Visibility.Collapsed;
            LegacyDashboard.Visibility = Visibility.Visible;
            WebDashboard.Visibility = Visibility.Collapsed;
            Debug.WriteLine("React dashboard unavailable; using native fallback. " + ex);
            AppendLog("React dashboard fallback: " + ex.Message);
        }
    }

    private static string ExtractEmbeddedWebUi()
    {
        const string resourcePrefix = "WebUi/";
        var assembly = Assembly.GetExecutingAssembly();
        var fingerprint = assembly.ManifestModule.ModuleVersionId.ToString("N")[..20].ToUpperInvariant();
        var cacheRoot = Path.Combine(ManagerPaths.Root, "web-ui-cache");
        var target = Path.Combine(cacheRoot, fingerprint);
        var indexPath = Path.Combine(target, "index.html");
        if (File.Exists(indexPath)) return target;

        var resourceNames = assembly.GetManifestResourceNames()
            .Where(name => name.StartsWith(resourcePrefix, StringComparison.Ordinal))
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        if (resourceNames.Length == 0)
            throw new InvalidDataException("The embedded React dashboard is missing from this manager build.");

        var resources = new List<(string RelativePath, byte[] Data)>(resourceNames.Length);
        foreach (var resourceName in resourceNames)
        {
            using var stream = assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidDataException($"Embedded UI resource could not be opened: {resourceName}");
            using var memory = new MemoryStream();
            stream.CopyTo(memory);
            var data = memory.ToArray();
            var relative = resourceName[resourcePrefix.Length..].Replace('\\', '/');
            if (string.IsNullOrWhiteSpace(relative)) continue;
            resources.Add((relative, data));
        }

        Directory.CreateDirectory(cacheRoot);
        var staging = Path.Combine(cacheRoot, $".{fingerprint}.staging-{Guid.NewGuid():N}");
        Directory.CreateDirectory(staging);
        try
        {
            var stagingRoot = Path.GetFullPath(staging).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
            foreach (var resource in resources)
            {
                var relative = resource.RelativePath.Replace('/', Path.DirectorySeparatorChar);
                var destination = Path.GetFullPath(Path.Combine(staging, relative));
                if (!destination.StartsWith(stagingRoot, StringComparison.OrdinalIgnoreCase))
                    throw new InvalidDataException("Embedded UI resource path escaped the extraction directory.");

                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.WriteAllBytes(destination, resource.Data);
            }

            if (!File.Exists(Path.Combine(staging, "index.html")))
                throw new InvalidDataException("The embedded React dashboard does not contain index.html.");

            if (Directory.Exists(target))
                Directory.Delete(staging, recursive: true);
            else
                Directory.Move(staging, target);
        }
        catch
        {
            try { if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true); }
            catch { }
            throw;
        }

        return target;
    }

    private void WebDashboard_WebMessageReceived(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        try
        {
            using var document = JsonDocument.Parse(e.WebMessageAsJson);
            var root = document.RootElement;
            if (!root.TryGetProperty("type", out var typeNode)) return;
            var type = typeNode.GetString();

            switch (type)
            {
                case "ready":
                    _webUiReady = true;
                    WebDashboard.Visibility = Visibility.Visible;
                    StartupSurface.Visibility = Visibility.Collapsed;
                    LegacyDashboard.Visibility = Visibility.Collapsed;
                    SendWebState();
                    break;
                case "rendered":
                    if (!_webUiReady) return;
                    _webUiReadyCompletion?.TrySetResult(true);
                    break;
                case "selectBranch":
                    if (root.TryGetProperty("branch", out var branchNode))
                        SelectBranchFromWeb(branchNode.GetString());
                    break;
                case "action":
                    if (root.TryGetProperty("action", out var actionNode))
                        HandleWebAction(actionNode.GetString());
                    break;
                case "dialogResult":
                    if (root.TryGetProperty("id", out var idNode) &&
                        root.TryGetProperty("result", out var resultNode) &&
                        string.Equals(idNode.GetString(), _webDialogId, StringComparison.Ordinal))
                        CompleteDialog(resultNode.ValueKind == JsonValueKind.True);
                    break;
                case "webError":
                    if (root.TryGetProperty("message", out var errorNode))
                        AppendLog("Web UI error: " + errorNode.GetString());
                    break;
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Ignored invalid React dashboard message: " + ex.Message);
        }
    }

    private void SelectBranchFromWeb(string? branch)
    {
        if (_busy || string.IsNullOrWhiteSpace(branch)) return;
        switch (branch.ToLowerInvariant())
        {
            case "stable":
                BranchStable.IsChecked = true;
                break;
            case "ptb":
                BranchPtb.IsChecked = true;
                break;
            case "canary":
                BranchCanary.IsChecked = true;
                break;
        }
    }

    private void HandleWebAction(string? action)
    {
        switch (action)
        {
            case "primary": PrimaryButton_Click(this, new RoutedEventArgs()); break;
            case "check": CheckButton_Click(this, new RoutedEventArgs()); break;
            case "repair": RepairButton_Click(this, new RoutedEventArgs()); break;
            case "uninstall": UninstallButton_Click(this, new RoutedEventArgs()); break;
            case "plugins": OpenPluginsButton_Click(this, new RoutedEventArgs()); break;
            case "installFolder": OpenInstallButton_Click(this, new RoutedEventArgs()); break;
            case "viewRelease": ViewRelease_Click(this, new RoutedEventArgs()); break;
            case "managerUpdate": ManagerUpdateButton_Click(this, new RoutedEventArgs()); break;
            case "copyLog": CopyActivityButton_Click(this, new RoutedEventArgs()); break;
            case "clearLog": ClearActivityButton_Click(this, new RoutedEventArgs()); break;
        }
    }

    private void SendWebState()
    {
        if (!_webUiReady || WebDashboard.CoreWebView2 is null) return;
        try
        {
            PostWebMessage(new { type = "state", data = BuildWebStateSnapshot() });
        }
        catch (Exception ex)
        {
            Debug.WriteLine("Could not synchronize React dashboard state: " + ex.Message);
        }
    }

    private object BuildWebStateSnapshot()
    {
        var selected = SelectedBranch();
        var effectiveSelected = selected.Equals("auto", StringComparison.OrdinalIgnoreCase)
            ? _localProbe?.Branch ?? "auto"
            : selected;

        return new
        {
            managerVersion = AppInfo.CurrentVersion,
            statusText = StatusText.Text,
            statusTone = ToneName(_currentStatusTone),
            selectedBranch = effectiveSelected,
            selectedChannelLabel = SelectedChannelLabel(),
            installedVersion = InstalledVersionText.Text,
            latestVersion = LatestVersionText.Text,
            components = new
            {
                vencord = VencordVersionText.Text,
                orion = OrionVersionText.Text,
                nitro = NitroVersionText.Text,
                loader = LoaderStatusText.Text
            },
            clients = new
            {
                stable = BuildClientWebState("stable", StableClientStatusText.Text, effectiveSelected),
                ptb = BuildClientWebState("ptb", PtbClientStatusText.Text, effectiveSelected),
                canary = BuildClientWebState("canary", CanaryClientStatusText.Text, effectiveSelected)
            },
            actions = new
            {
                primary = new { label = PrimaryButton.Content?.ToString() ?? "Continue", enabled = PrimaryButton.IsEnabled },
                check = new { enabled = CheckButton.IsEnabled },
                repair = new { enabled = RepairButton.IsEnabled },
                uninstall = new { enabled = UninstallButton.IsEnabled },
                plugins = new { enabled = OpenPluginsButton.IsEnabled },
                installFolder = new { enabled = OpenInstallButton.IsEnabled },
                managerUpdate = new
                {
                    visible = ManagerUpdateButton.Visibility == Visibility.Visible,
                    label = ManagerUpdateButton.Content?.ToString() ?? "Update manager",
                    enabled = ManagerUpdateButton.IsEnabled
                }
            },
            progress = new
            {
                message = ProgressText.Text,
                value = OperationProgressBar.Value,
                indeterminate = OperationProgressBar.IsIndeterminate
            },
            busy = _busy,
            log = ActivityLogText.Text
        };
    }

    private object BuildClientWebState(string branch, string status, string selectedBranch)
    {
        var found = _clientProbes.TryGetValue(branch, out var probe);
        var tone = !found
            ? "neutral"
            : probe!.IsManagedPatch && !_installationService.HasUsableManagedPayload(probe)
                ? "danger"
                : probe.IsManagedPatch
                    ? "success"
                    : probe.IsPatched
                        ? "warning"
                        : "accent";

        return new
        {
            label = DisplayBranch(branch),
            status,
            tone,
            selected = branch.Equals(selectedBranch, StringComparison.OrdinalIgnoreCase),
            found
        };
    }

    private void PostWebMessage(object payload)
    {
        if (!_webUiReady || WebDashboard.CoreWebView2 is null) return;
        WebDashboard.CoreWebView2.PostWebMessageAsJson(JsonSerializer.Serialize(payload));
    }

    private static string ToneName(DialogTone tone) => tone switch
    {
        DialogTone.Success => "success",
        DialogTone.Warning => "warning",
        DialogTone.Danger => "danger",
        _ => "accent"
    };

    private async Task<bool> RecoverInterruptedOperationsAsync()
    {
        var pending = _installationService.GetPendingRecoveryBranches().ToArray();
        if (pending.Length == 0) return true;

        AppendLog($"Detected {pending.Length} interrupted operation{(pending.Length == 1 ? string.Empty : "s")}. Recovery will run one Discord client at a time.");
        foreach (var branch in pending)
        {
            await RunOperationAsync(
                $"Recovering interrupted {DisplayBranch(branch)} operation…",
                progress => _installationService.RecoverInterruptedOperationAsync(branch, progress),
                showCompletionDialog: false);
        }

        var remaining = _installationService.GetPendingRecoveryBranches();
        if (remaining.Count == 0)
        {
            AppendLog("Interrupted-operation recovery completed successfully.");
            return true;
        }

        AppendLog("Recovery is still required for: " + string.Join(", ", remaining.Select(DisplayBranch)) + ". Mutation actions for those clients remain blocked.");
        return false;
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _dialogCompletion?.TrySetResult(false);
        if (_smoothScrollRenderingSubscribed)
        {
            CompositionTarget.Rendering -= SmoothScroll_Rendering;
            _smoothScrollRenderingSubscribed = false;
        }
        try { WebDashboard.Dispose(); }
        catch { }
        _installationService.Dispose();
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_busy || _allowCloseWhileBusy) return;
        e.Cancel = true;
        ProgressText.Text = "Finish the current operation before closing";
        AppendLog("Close request ignored while a Vencord operation is active. This protects the selected Discord client from a partial mutation.");
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Maximized)
            WindowState = WindowState.Normal;

        WindowBorder.CornerRadius = new CornerRadius(12);
        WindowBorder.BorderThickness = new Thickness(1);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && (DialogOverlay.Visibility == Visibility.Visible || _webDialogActive))
        {
            CompleteDialog(false);
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (e.ClickCount > 1) return;

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private async void CheckButton_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(showSuccessDialog: true);

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_manifest is null && !await CheckForUpdatesAsync(false)) return;
        if (_manifest is null) return;

        VerifyLocalInstallation(logResult: false);
        if (!TryResolveActionBranch(out var targetBranch, out var actionError))
        {
            await ShowDialogAsync("Choose a Discord client", actionError, "Done", showCancel: false, DialogTone.Warning);
            return;
        }

        var targetProbe = _clientProbes.TryGetValue(targetBranch, out var exactProbe) ? exactProbe : null;
        var targetVersion = _installationService.GetInstalledVersion(targetBranch, _state);
        var installed = targetProbe?.IsManagedPatch == true && !string.IsNullOrWhiteSpace(targetVersion);
        var action = installed ? "Update" : "Install";
        var selectedClient = DisplayBranch(targetBranch);
        var replacingOtherVencord = targetProbe?.IsPatched == true && targetProbe.IsManagedPatch == false;
        var migratingLegacy = targetProbe?.IsLegacyManagedPatch == true;
        var prompt = migratingLegacy
            ? $"This will migrate {selectedClient} from the old shared Custom Vencord payload to its own isolated payload. Stable, PTB, and Canary will no longer share this client's build files. Only {selectedClient} will be closed and modified."
            : installed
            ? $"This will update Custom Vencord from v{targetVersion} to v{_manifest.Version} for {selectedClient} only. Other Discord channels will keep their current payloads and versions."
            : replacingOtherVencord
                ? $"{selectedClient} currently has a different Vencord injection. Installing Custom Vencord v{_manifest.Version} will replace that injection for this client while leaving the other Discord channels alone."
                : $"This will install Custom Vencord v{_manifest.Version} for {selectedClient} only. Stable, PTB, and Canary use separate managed payloads.";

        if (!await ShowDialogAsync(
                $"{action} Custom Vencord?",
                prompt,
                action,
                showCancel: true,
                DialogTone.Accent))
            return;

        await RunOperationAsync(
            $"Starting {action.ToLowerInvariant()}…",
            progress => _installationService.InstallOrUpdateAsync(_manifest, targetBranch, repair: false, progress),
            completionTitle: $"{action} verified",
            completionMessage: $"Custom Vencord v{_manifest.Version} was verified on {selectedClient}. No other Discord client was modified.");
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        VerifyLocalInstallation(logResult: false);
        if (_busy || !HasManagedPatchOwnershipDetected()) return;
        if (_manifest is null && !await CheckForUpdatesAsync(false)) return;
        if (_manifest is null) return;
        if (!TryResolveActionBranch(out var targetBranch, out var actionError))
        {
            await ShowDialogAsync("Choose a Discord client", actionError, "Done", showCancel: false, DialogTone.Warning);
            return;
        }
        var selectedClient = DisplayBranch(targetBranch);

        if (!await ShowDialogAsync(
            "Repair Custom Vencord?",
            $"The manager will repair {selectedClient} only. It will redownload the latest verified package, back up this client's payload, replace only this client's managed files, and verify only this client's injection.",
            "Repair",
                showCancel: true,
                DialogTone.Accent))
            return;

        await RunOperationAsync(
            "Starting repair…",
            progress => _installationService.RepairAsync(_manifest, targetBranch, progress),
            completionTitle: "Repair verified",
            completionMessage: $"{selectedClient} was repaired and verified. Other Discord clients were not modified.");
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        VerifyLocalInstallation(logResult: false);
        if (_busy || !HasManagedPatchOwnershipDetected()) return;
        if (!TryResolveActionBranch(out var targetBranch, out var actionError))
        {
            await ShowDialogAsync("Choose a Discord client", actionError, "Done", showCancel: false, DialogTone.Warning);
            return;
        }
        var selectedClient = DisplayBranch(targetBranch);

        if (!await ShowDialogAsync(
            "Uninstall Custom Vencord?",
            $"This removes Custom Vencord from {selectedClient} only. Stable, PTB, and Canary are handled independently. Your shared runtime plugins folder is kept.",
                "Uninstall",
                showCancel: true,
                DialogTone.Danger))
            return;

        await RunOperationAsync(
            "Starting uninstall…",
            progress => _installationService.UninstallAsync(targetBranch, progress),
            completionTitle: "Uninstall verified",
            completionMessage: $"Custom Vencord was removed from {selectedClient} only and the client was verified clean.");
    }

    private void OpenPluginsButton_Click(object sender, RoutedEventArgs e)
    {
        ManagerPaths.EnsureCreated();
        OpenFolder(ManagerPaths.PluginsDirectory);
    }

    private void OpenInstallButton_Click(object sender, RoutedEventArgs e)
    {
        ManagerPaths.EnsureCreated();
        if (TryResolveActionBranch(out var branch, out _))
            OpenFolder(_installationService.GetManagedInstallDirectoryForBranch(branch));
        else
            OpenFolder(ManagerPaths.Root);
    }

    private void ViewRelease_Click(object sender, RoutedEventArgs e)
    {
        var url = _manifest?.ReleasePage;
        if (string.IsNullOrWhiteSpace(url))
            url = "https://github.com/NightPlayProject/Vencord-CustomPlugins/releases/latest";
        OpenUrl(url);
    }

    private async void ManagerUpdateButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy || _manifest?.Manager is null || !SelfUpdateService.IsUpdateAvailable(_manifest)) return;

        if (!await ShowDialogAsync(
                "Update the manager?",
                $"Custom Vencord Manager v{_manifest.Manager.Version} is available. The verified EXE will replace this one and the manager will restart automatically.",
                "Update manager",
                showCancel: true,
                DialogTone.Accent))
            return;

        _allowCloseWhileBusy = true;
        try
        {
            await RunOperationAsync(
                "Starting manager update…",
                progress => _installationService.SelfUpdater.DownloadAndRestartAsync(_manifest, progress),
                showCompletionDialog: false);
        }
        finally
        {
            _allowCloseWhileBusy = false;
        }
    }

    private async Task<bool> CheckForUpdatesAsync(bool showSuccessDialog)
    {
        if (_busy) return false;

        AppendLog("Verifying local installation before contacting GitHub…");
        ProgressText.Text = "Verifying local installation…";
        VerifyLocalInstallation(logResult: true);

        SetBusy(true);
        ProgressText.Text = "Checking GitHub…";
        OperationProgressBar.IsIndeterminate = true;
        AppendLog("Checking update manifest…");

        try
        {
            _manifest = await _installationService.GetManifestAsync();
            _state = _installationService.RecoverAllStateFromLocalInstallations(_manifest);
            RefreshClientProbes();
            _localProbe = ResolveSelectedProbe();
            AppendLog($"Latest distribution: v{_manifest.Version} (OrionQuests v{_manifest.Components.OrionQuests}).");
            var selectedStateBranch = ResolveSelectedStateBranch();
            var selectedVersion = selectedStateBranch is null
                ? string.Empty
                : _installationService.GetInstalledVersion(selectedStateBranch, _state);
            if (HasManagedPatchOwnershipDetected() && !string.IsNullOrWhiteSpace(selectedVersion))
                AppendLog($"{SelectedClientDisplayName()} verified as Custom Vencord v{selectedVersion}.");
            RefreshUi();
            ProgressText.Text = "Update check complete";

            if (showSuccessDialog)
            {
                await ShowDialogAsync(
                    "Update check complete",
                    StatusText.Text,
                    "Done",
                    showCancel: false,
                    DialogTone.Success);
            }

            return true;
        }
        catch (Exception ex)
        {
            _manifest = null;
            AppendLog("ERROR: " + ex.Message);
            StatusText.Text = "Update check failed";
            SetStatusVisual(DialogTone.Danger);
            ProgressText.Text = "Could not reach update server";

            if (showSuccessDialog)
            {
                await ShowDialogAsync(
                    "Update check failed",
                    ex.Message,
                    "Close",
                    showCancel: false,
                    DialogTone.Danger);
            }

            return false;
        }
        finally
        {
            OperationProgressBar.IsIndeterminate = false;
            SetBusy(false);
            RefreshUi();
        }
    }

    private async Task RunOperationAsync(
        string initialMessage,
        Func<IProgress<OperationProgress>, Task> action,
        bool showCompletionDialog = true,
        string completionTitle = "Operation verified",
        string completionMessage = "The operation completed and its result was verified successfully.")
    {
        SetBusy(true);
        OperationProgressBar.IsIndeterminate = false;
        OperationProgressBar.Value = 0;
        _lastProgressMessage = string.Empty;
        AppendLog(initialMessage);

        var progress = new Progress<OperationProgress>(value =>
        {
            ProgressText.Text = value.Message;
            if (value.Percent is { } percent)
            {
                OperationProgressBar.IsIndeterminate = false;
                OperationProgressBar.Value = Math.Clamp(percent, 0, 100);
            }
            else
            {
                OperationProgressBar.IsIndeterminate = true;
            }

            SendWebState();

            if (!string.Equals(_lastProgressMessage, value.Message, StringComparison.Ordinal))
            {
                _lastProgressMessage = value.Message;
                AppendLog(value.Message);
            }
        });

        try
        {
            await action(progress);
            _state = _installationService.RecoverAllStateFromLocalInstallations(_manifest);
            RefreshClientProbes();
            _localProbe = ResolveSelectedProbe();
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = 100;
            RefreshUi();

            if (showCompletionDialog)
            {
                await ShowDialogAsync(
                    completionTitle,
                    completionMessage,
                    "Done",
                    showCancel: false,
                    DialogTone.Success);
            }
        }
        catch (Exception ex)
        {
            OperationProgressBar.IsIndeterminate = false;
            OperationProgressBar.Value = 0;
            ProgressText.Text = "Operation failed";
            AppendLog("ERROR: " + ex);
            await ShowDialogAsync(
                "Operation failed",
                ex.Message,
                "Close",
                showCancel: false,
                DialogTone.Danger);
        }
        finally
        {
            SetBusy(false);
            RefreshUi();
        }
    }

    private void RefreshUi()
    {
        _state = _installationService.LoadState();
        RefreshClientProbes();
        _localProbe = ResolveSelectedProbe();
        var selectedDiscordFound = _localProbe is not null;
        var managedOwnership = HasManagedPatchOwnershipDetected();
        var managedPayloadHealthy = HasHealthyManagedInstallationDetected();
        var selectedStateBranch = ResolveSelectedStateBranch();
        var pendingRecovery = selectedStateBranch is not null && _installationService.HasPendingRecovery(selectedStateBranch);
        var selectedVersion = selectedStateBranch is null
            ? string.Empty
            : _installationService.GetInstalledVersion(selectedStateBranch, _state);
        var installed = managedOwnership && !string.IsNullOrWhiteSpace(selectedVersion);
        var otherVencordDetected = _localProbe?.IsPatched == true && _localProbe.IsManagedPatch == false;
        var legacyManaged = _localProbe?.IsLegacyManagedPatch == true;
        var autoAmbiguous = SelectedBranch().Equals("auto", StringComparison.OrdinalIgnoreCase) && _clientProbes.Count > 1;

        SelectedChannelText.Text = SelectedChannelLabel();
        RefreshClientStatusIndicators();
        RefreshSelectedClientRow();

        InstalledVersionText.Text = installed
            ? $"v{selectedVersion}"
            : managedOwnership
                ? "Detected"
                : otherVencordDetected
                    ? "Vencord"
                    : "None";
        LatestVersionText.Text = _manifest is null ? "—" : $"v{_manifest.Version}";

        DialogTone statusTone;
        if (_manifest is null)
        {
            StatusText.Text = pendingRecovery
                ? "Interrupted operation · recovery required"
                : !selectedDiscordFound
                ? $"{SelectedClientDisplayName()} not found"
                : autoAmbiguous
                    ? "Select a Discord client"
                : managedOwnership && !managedPayloadHealthy
                    ? "Custom Vencord injection is broken"
                : installed
                    ? "Installed · locally verified"
                    : managedOwnership
                        ? "Custom Vencord detected · checking version"
                        : otherVencordDetected
                            ? "Existing Vencord detected"
                            : "Custom Vencord not installed";
            VencordVersionText.Text = "—";
            OrionVersionText.Text = "—";
            NitroVersionText.Text = "—";
            LoaderStatusText.Text = "—";
            statusTone = selectedDiscordFound ? DialogTone.Accent : DialogTone.Warning;
        }
        else
        {
            VencordVersionText.Text = _manifest.Components.Vencord.Version;
            OrionVersionText.Text = "v" + _manifest.Components.OrionQuests.TrimStart('v', 'V');
            NitroVersionText.Text = _manifest.Components.NitroSniper;
            LoaderStatusText.Text = _manifest.Components.RuntimePluginLoader ? "Included" : "Not included";

            if (pendingRecovery)
            {
                StatusText.Text = "Interrupted operation · recovery required before changes";
                statusTone = DialogTone.Danger;
            }
            else if (autoAmbiguous)
            {
                StatusText.Text = "Select Stable, PTB, or Canary to continue";
                statusTone = DialogTone.Warning;
            }
            else if (!selectedDiscordFound)
            {
                StatusText.Text = $"{SelectedClientDisplayName()} not found";
                statusTone = DialogTone.Warning;
            }
            else if (managedOwnership && !managedPayloadHealthy)
            {
                StatusText.Text = "Custom Vencord injection found · payload missing or damaged";
                statusTone = DialogTone.Danger;
            }
            else if (!installed)
            {
                if (managedOwnership)
                {
                    StatusText.Text = $"Local Custom Vencord detected · latest is v{_manifest.Version}";
                    statusTone = DialogTone.Warning;
                }
                else if (otherVencordDetected)
                {
                    StatusText.Text = $"Existing Vencord detected · Custom v{_manifest.Version} available";
                    statusTone = DialogTone.Warning;
                }
                else
                {
                    StatusText.Text = $"Ready to install v{_manifest.Version}";
                    statusTone = DialogTone.Accent;
                }
            }
            else
            {
                var comparison = UpdateClient.CompareVersions(selectedVersion, _manifest.Version);
                if (legacyManaged)
                {
                    StatusText.Text = $"Migration required · isolate {SelectedClientDisplayName()}";
                    statusTone = DialogTone.Warning;
                }
                else if (comparison < 0)
                {
                    StatusText.Text = $"Update available · v{_manifest.Version}";
                    statusTone = DialogTone.Warning;
                }
                else if (comparison == 0)
                {
                    StatusText.Text = "Up to date";
                    statusTone = DialogTone.Success;
                }
                else
                {
                    StatusText.Text = "Installed build is newer";
                    statusTone = DialogTone.Success;
                }
            }
        }

        SetStatusVisual(statusTone);

        var updateAvailable = _manifest is not null
            && selectedDiscordFound
            && !autoAmbiguous
            && !pendingRecovery
            && (!installed || legacyManaged || UpdateClient.CompareVersions(selectedVersion, _manifest.Version) < 0);

        PrimaryButton.Content = pendingRecovery
            ? "Recovery required"
            : autoAmbiguous
            ? "Select a client to continue"
            : !selectedDiscordFound
            ? "Discord client not found"
            : managedOwnership && !managedPayloadHealthy
                ? "Use Repair below"
            : legacyManaged
                ? "Isolate this client  →"
            : !installed
                ? otherVencordDetected
                    ? "Install Custom Vencord  →"
                    : "Install latest  →"
                : updateAvailable
                    ? $"Update to v{_manifest!.Version}  →"
                    : "You're up to date";

        PrimaryButton.IsEnabled = !_busy
            && _manifest is not null
            && updateAvailable
            && !pendingRecovery
            && !(managedOwnership && !managedPayloadHealthy);
        ManagerVersionText.Text = $"v{AppInfo.CurrentVersion}";
        var managerUpdateAvailable = SelfUpdateService.IsUpdateAvailable(_manifest);
        ManagerUpdateButton.Visibility = managerUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        ManagerUpdateButton.Content = managerUpdateAvailable && _manifest?.Manager is not null
            ? $"Update manager · v{_manifest.Manager.Version}"
            : "Update manager";
        ManagerUpdateButton.IsEnabled = !_busy && managerUpdateAvailable;
        CheckButton.IsEnabled = !_busy;
        RepairButton.IsEnabled = !_busy && !autoAmbiguous && !pendingRecovery && managedOwnership && _manifest is not null;
        // Uninstall remains available even if the payload files are missing, as long as the
        // Discord injection is manager-owned. This lets users recover from broken installs.
        UninstallButton.IsEnabled = !_busy && !autoAmbiguous && !pendingRecovery && managedOwnership;
        OpenPluginsButton.IsEnabled = !_busy;
        OpenInstallButton.IsEnabled = !_busy && _installationService.HasAnyManagedFiles();
        SetBranchControlsEnabled(!_busy);
        SendWebState();
    }

    private void VerifyLocalInstallation(bool logResult)
    {
        _state = _installationService.RecoverAllStateFromLocalInstallations(_manifest);
        RefreshClientProbes();
        _localProbe = ResolveSelectedProbe();

        if (!logResult) return;
        if (_localProbe is null)
        {
            AppendLog(SelectedBranch().Equals("auto", StringComparison.OrdinalIgnoreCase) && _clientProbes.Count > 1
                ? "Local verification: multiple Discord clients were found; select Stable, PTB, or Canary to inspect or modify one client."
                : "Local verification: no Discord installation was found for the selected channel.");
            return;
        }

        if (_localProbe.IsManagedPatch)
        {
            AppendLog($"Local verification: {DisplayBranch(_localProbe.Branch)} is injected with this manager's Custom Vencord build.");
            return;
        }

        if (_localProbe.IsPatched)
        {
            AppendLog($"Local verification: {DisplayBranch(_localProbe.Branch)} already has another Vencord installation.");
            return;
        }

        AppendLog($"Local verification: {DisplayBranch(_localProbe.Branch)} is installed but has no Vencord injection.");
    }

    private bool HasManagedPatchOwnershipDetected() => _localProbe?.IsManagedPatch == true;

    private bool HasHealthyManagedInstallationDetected() =>
        _localProbe?.IsManagedPatch == true && _installationService.HasUsableManagedPayload(_localProbe);

    private void RefreshClientProbes()
    {
        _clientProbes.Clear();
        foreach (var probe in _installationService.ProbeAllLocalInstallations())
            _clientProbes[probe.Branch] = probe;
    }

    private DiscordInstallProbe? ResolveSelectedProbe()
    {
        var selected = SelectedBranch();
        if (!selected.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return _clientProbes.TryGetValue(selected, out var exact) ? exact : null;

        return _clientProbes.Count == 1 ? _clientProbes.Values.Single() : null;
    }

    private string? ResolveSelectedStateBranch()
    {
        var selected = SelectedBranch();
        if (!selected.Equals("auto", StringComparison.OrdinalIgnoreCase)) return selected;
        return _localProbe?.Branch;
    }

    private bool TryResolveActionBranch(out string branch, out string error)
    {
        var selected = SelectedBranch();
        if (!selected.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            if (_clientProbes.ContainsKey(selected))
            {
                branch = selected;
                error = string.Empty;
                return true;
            }

            branch = string.Empty;
            error = $"{DisplayBranch(selected)} is not installed on this PC.";
            return false;
        }

        if (_clientProbes.Count == 1)
        {
            branch = _clientProbes.Keys.Single();
            error = string.Empty;
            return true;
        }

        branch = string.Empty;
        error = _clientProbes.Count == 0
            ? "No Discord desktop client was found."
            : "More than one Discord client is installed. Select Stable, PTB, or Canary explicitly before installing, repairing, or uninstalling.";
        return false;
    }

    private void RefreshClientStatusIndicators()
    {
        SetClientStatus("stable", StableClientDot, StableClientStatusBadge, StableClientStatusText);
        SetClientStatus("ptb", PtbClientDot, PtbClientStatusBadge, PtbClientStatusText);
        SetClientStatus("canary", CanaryClientDot, CanaryClientStatusBadge, CanaryClientStatusText);
    }

    private void RefreshSelectedClientRow()
    {
        foreach (var row in new[] { StableClientRow, PtbClientRow, CanaryClientRow })
        {
            row.Background = ClientRowBrush;
            row.BorderBrush = ClientRowBorderBrush;
        }

        var selected = SelectedBranch();
        if (selected.Equals("auto", StringComparison.OrdinalIgnoreCase))
            selected = _localProbe?.Branch ?? string.Empty;

        var selectedRow = selected switch
        {
            "stable" => StableClientRow,
            "ptb" => PtbClientRow,
            "canary" => CanaryClientRow,
            _ => null
        };

        if (selectedRow is null) return;
        selectedRow.Background = SelectedClientRowBrush;
        selectedRow.BorderBrush = AccentBorderBrush;
    }

    private void SetClientStatus(string branch, System.Windows.Shapes.Ellipse dot, Border badge, TextBlock text)
    {
        if (!_clientProbes.TryGetValue(branch, out var probe))
        {
            text.Text = "Discord not found";
            text.Foreground = NeutralBrush;
            dot.Fill = NeutralBrush;
            badge.Background = NeutralSoftBrush;
            badge.BorderBrush = NeutralBorderBrush;
            return;
        }

        if (probe.IsManagedPatch && !_installationService.HasUsableManagedPayload(probe))
        {
            text.Text = "Broken Custom Vencord";
            text.Foreground = DangerBrush;
            dot.Fill = DangerBrush;
            badge.Background = DangerSoftBrush;
            badge.BorderBrush = DangerBorderBrush;
            return;
        }

        if (probe.IsManagedPatch)
        {
            var version = _installationService.GetInstalledVersion(branch, _state);
            text.Text = probe.IsLegacyManagedPatch
                ? string.IsNullOrWhiteSpace(version) ? "Legacy Custom Vencord" : $"Legacy v{version}"
                : string.IsNullOrWhiteSpace(version) ? "Custom Vencord" : $"Custom v{version}";
            text.Foreground = SuccessBrush;
            dot.Fill = SuccessBrush;
            badge.Background = SuccessSoftBrush;
            badge.BorderBrush = SuccessBorderBrush;
            return;
        }

        if (probe.IsPatched)
        {
            text.Text = "Other Vencord";
            text.Foreground = WarningBrush;
            dot.Fill = WarningBrush;
            badge.Background = WarningSoftBrush;
            badge.BorderBrush = WarningBorderBrush;
            return;
        }

        text.Text = "No Custom Vencord";
        text.Foreground = AccentBrush;
        dot.Fill = AccentBrush;
        badge.Background = AccentSoftBrush;
        badge.BorderBrush = AccentBorderBrush;
    }

    private string SelectedChannelLabel()
    {
        var selected = SelectedBranch();
        if (!selected.Equals("auto", StringComparison.OrdinalIgnoreCase))
            return DisplayBranch(selected).Replace("Discord ", string.Empty, StringComparison.Ordinal) + " channel";

        if (_clientProbes.Count > 1) return "Auto · multiple clients";
        return _localProbe is null
            ? "Auto · no client found"
            : $"Auto → {DisplayBranch(_localProbe.Branch).Replace("Discord ", string.Empty, StringComparison.Ordinal)}";
    }

    private string SelectedClientDisplayName()
    {
        var selected = SelectedBranch();
        if (!selected.Equals("auto", StringComparison.OrdinalIgnoreCase)) return DisplayBranch(selected);
        if (_clientProbes.Count > 1) return "Discord clients";
        return _localProbe is null ? "Discord" : DisplayBranch(_localProbe.Branch);
    }

    private static string DisplayBranch(string branch) => branch.ToLowerInvariant() switch
    {
        "stable" => "Discord Stable",
        "ptb" => "Discord PTB",
        "canary" => "Discord Canary",
        _ => "Discord"
    };

    private void BranchRadio_Checked(object sender, RoutedEventArgs e)
    {
        // XAML sets Auto during InitializeComponent. Wait until the window is live before
        // probing or touching the rest of the visual tree.
        if (!IsLoaded || _busy) return;

        var selected = SelectedBranch();
        _installationService.SavePreferredBranch(selected);
        _state = _installationService.RecoverStateFromLocalInstallation(selected, _manifest);
        RefreshClientProbes();
        _localProbe = ResolveSelectedProbe();
        RefreshUi();

        var status = _localProbe is null
            ? "Discord client not found"
            : _localProbe.IsManagedPatch
                ? "Custom Vencord installed"
                : _localProbe.IsPatched
                    ? "another Vencord installation detected"
                    : "Custom Vencord not installed";
        ProgressText.Text = $"Viewing {SelectedClientDisplayName()}";
        AppendLog($"Client selection: {SelectedChannelLabel()} · {status}.");
    }

    private void SetStatusVisual(DialogTone tone)
    {
        _currentStatusTone = tone;
        var (dot, background, border) = tone switch
        {
            DialogTone.Success => (SuccessBrush, SuccessSoftBrush, SuccessBorderBrush),
            DialogTone.Warning => (WarningBrush, WarningSoftBrush, WarningBorderBrush),
            DialogTone.Danger => (DangerBrush, DangerSoftBrush, DangerBorderBrush),
            _ => (AccentBrush, AccentSoftBrush, AccentBorderBrush)
        };

        StatusDot.Fill = dot;
        StatusBadge.Background = background;
        StatusBadge.BorderBrush = border;
    }

    private void SetBusy(bool busy)
    {
        _busy = busy;
        CheckButton.IsEnabled = !busy;
        PrimaryButton.IsEnabled = !busy;
        RepairButton.IsEnabled = !busy;
        UninstallButton.IsEnabled = !busy;
        ManagerUpdateButton.IsEnabled = !busy && SelfUpdateService.IsUpdateAvailable(_manifest);
        SetBranchControlsEnabled(!busy);
    }

    private string SelectedBranch()
    {
        if (BranchStable.IsChecked == true) return "stable";
        if (BranchPtb.IsChecked == true) return "ptb";
        if (BranchCanary.IsChecked == true) return "canary";
        return "auto";
    }

    private void ApplySavedBranch()
    {
        var branch = string.IsNullOrWhiteSpace(_state.DiscordBranch) ? "auto" : _state.DiscordBranch.ToLowerInvariant();
        BranchAuto.IsChecked = branch == "auto";
        BranchStable.IsChecked = branch == "stable";
        BranchPtb.IsChecked = branch == "ptb";
        BranchCanary.IsChecked = branch == "canary";

        if (BranchAuto.IsChecked != true && BranchStable.IsChecked != true && BranchPtb.IsChecked != true && BranchCanary.IsChecked != true)
            BranchAuto.IsChecked = true;
    }

    private void SetBranchControlsEnabled(bool enabled)
    {
        BranchAuto.IsEnabled = enabled;
        BranchStable.IsEnabled = enabled;
        BranchPtb.IsEnabled = enabled;
        BranchCanary.IsEnabled = enabled;
    }

    private void SmoothScrollViewer_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (sender is not ScrollViewer scrollViewer || scrollViewer.ScrollableHeight <= 0) return;

        if (!SystemParameters.ClientAreaAnimation)
        {
            var immediateTarget = Math.Clamp(
                scrollViewer.VerticalOffset - (e.Delta * WheelScrollMultiplier),
                0,
                scrollViewer.ScrollableHeight);
            scrollViewer.ScrollToVerticalOffset(immediateTarget);
            e.Handled = true;
            return;
        }

        if (!_smoothScrollStates.TryGetValue(scrollViewer, out var state))
        {
            state = new SmoothScrollState();
            _smoothScrollStates[scrollViewer] = state;
        }

        var currentTarget = state.Active ? state.TargetOffset : scrollViewer.VerticalOffset;
        var target = Math.Clamp(
            currentTarget - (e.Delta * WheelScrollMultiplier),
            0,
            scrollViewer.ScrollableHeight);

        if (Math.Abs(target - scrollViewer.VerticalOffset) < 0.5) return;

        state.StartOffset = scrollViewer.VerticalOffset;
        state.TargetOffset = target;
        state.StartedAtUtc = DateTime.UtcNow;
        state.Active = true;

        if (!_smoothScrollRenderingSubscribed)
        {
            CompositionTarget.Rendering += SmoothScroll_Rendering;
            _smoothScrollRenderingSubscribed = true;
        }

        e.Handled = true;
    }

    private void SmoothScroll_Rendering(object? sender, EventArgs e)
    {
        var now = DateTime.UtcNow;
        var anyActive = false;

        foreach (var pair in _smoothScrollStates)
        {
            var scrollViewer = pair.Key;
            var state = pair.Value;
            if (!state.Active) continue;

            var elapsed = now - state.StartedAtUtc;
            var t = Math.Clamp(elapsed.TotalMilliseconds / SmoothScrollDuration.TotalMilliseconds, 0d, 1d);
            var eased = 1d - Math.Pow(1d - t, 3d);
            var destination = state.StartOffset + ((state.TargetOffset - state.StartOffset) * eased);
            scrollViewer.ScrollToVerticalOffset(destination);

            if (t >= 1d)
            {
                state.Active = false;
                scrollViewer.ScrollToVerticalOffset(state.TargetOffset);
            }
            else
            {
                anyActive = true;
            }
        }

        if (!anyActive && _smoothScrollRenderingSubscribed)
        {
            CompositionTarget.Rendering -= SmoothScroll_Rendering;
            _smoothScrollRenderingSubscribed = false;
        }
    }

    private void AppendLog(string message)
    {
        Dispatcher.Invoke(() =>
        {
            var line = $"[{DateTime.Now:HH:mm:ss}] {message}";
            ActivityLogText.Text += (ActivityLogText.Text.Length == 0 ? string.Empty : Environment.NewLine) + line;
            ActivityScrollViewer.ScrollToEnd();
            SendWebState();
        });
    }

    private void CopyActivityButton_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrWhiteSpace(ActivityLogText.Text))
        {
            ProgressText.Text = "Nothing to copy";
            return;
        }

        try
        {
            Clipboard.SetText(ActivityLogText.Text);
            ProgressText.Text = "Activity copied";
            SendWebState();
        }
        catch
        {
            ProgressText.Text = "Could not copy activity";
            SendWebState();
        }
    }

    private void ClearActivityButton_Click(object sender, RoutedEventArgs e)
    {
        ActivityLogText.Text = string.Empty;
        ProgressText.Text = _busy ? "Operation in progress" : "Activity cleared";
        SendWebState();
    }

    private sealed class SmoothScrollState
    {
        public double StartOffset { get; set; }
        public double TargetOffset { get; set; }
        public DateTime StartedAtUtc { get; set; }
        public bool Active { get; set; }
    }

    private Task<bool> ShowDialogAsync(
        string title,
        string message,
        string confirmText,
        bool showCancel,
        DialogTone tone)
    {
        if (_dialogCompletion is not null)
            CompleteDialog(false);

        _dialogPreviousFocus = Keyboard.FocusedElement;
        _dialogCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        if (_webUiReady && WebDashboard.CoreWebView2 is not null)
        {
            _webDialogActive = true;
            _webDialogId = Guid.NewGuid().ToString("N");
            PostWebMessage(new
            {
                type = "dialog",
                id = _webDialogId,
                title,
                message,
                confirmText,
                showCancel,
                tone = ToneName(tone)
            });
            return _dialogCompletion.Task;
        }

        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogConfirmButton.Content = confirmText;
        DialogCancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        DialogConfirmButton.Style = (Style)FindResource(tone == DialogTone.Danger ? "DangerButton" : "PrimaryButton");
        DialogConfirmButton.IsDefault = tone != DialogTone.Danger;
        DialogCancelButton.IsDefault = tone == DialogTone.Danger && showCancel;

        switch (tone)
        {
            case DialogTone.Success:
                DialogIcon.Text = "✓";
                DialogIcon.Foreground = SuccessBrush;
                DialogIconSurface.Background = SuccessSoftBrush;
                break;
            case DialogTone.Warning:
                DialogIcon.Text = "!";
                DialogIcon.Foreground = WarningBrush;
                DialogIconSurface.Background = WarningSoftBrush;
                break;
            case DialogTone.Danger:
                DialogIcon.Text = "!";
                DialogIcon.Foreground = DangerBrush;
                DialogIconSurface.Background = DangerSoftBrush;
                break;
            default:
                DialogIcon.Text = "i";
                DialogIcon.Foreground = AccentBrush;
                DialogIconSurface.Background = AccentSoftBrush;
                break;
        }

        DialogOverlay.Visibility = Visibility.Visible;
        PlayDialogEntrance();
        if (tone == DialogTone.Danger && showCancel)
            DialogCancelButton.Focus();
        else
            DialogConfirmButton.Focus();
        return _dialogCompletion.Task;
    }

    private void PlayDialogEntrance()
    {
        DialogPanel.BeginAnimation(OpacityProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);

        if (!SystemParameters.ClientAreaAnimation)
        {
            DialogPanel.Opacity = 1;
            DialogScale.ScaleX = 1;
            DialogScale.ScaleY = 1;
            return;
        }

        DialogPanel.RenderTransformOrigin = new Point(0.5, 0.5);
        DialogPanel.Opacity = 0;
        DialogScale.ScaleX = 0.985;
        DialogScale.ScaleY = 0.985;

        var duration = new Duration(TimeSpan.FromMilliseconds(125));
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DialogPanel.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, duration) { EasingFunction = ease });
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.985, 1, duration) { EasingFunction = ease });
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.985, 1, duration) { EasingFunction = ease });
    }

    private void DialogConfirmButton_Click(object sender, RoutedEventArgs e) => CompleteDialog(true);
    private void DialogCancelButton_Click(object sender, RoutedEventArgs e) => CompleteDialog(false);

    private void CompleteDialog(bool result)
    {
        var completion = _dialogCompletion;
        if (completion is null) return;
        _dialogCompletion = null;

        if (_webDialogActive)
        {
            var closedDialogId = _webDialogId;
            _webDialogActive = false;
            _webDialogId = null;
            PostWebMessage(new { type = "dialogClosed", id = closedDialogId });
            var previousWebFocus = _dialogPreviousFocus;
            _dialogPreviousFocus = null;
            if (previousWebFocus is not null)
                Keyboard.Focus(previousWebFocus);
            completion.TrySetResult(result);
            return;
        }

        DialogOverlay.Visibility = Visibility.Collapsed;
        DialogPanel.BeginAnimation(OpacityProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
        DialogScale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        var previousFocus = _dialogPreviousFocus;
        _dialogPreviousFocus = null;
        if (previousFocus is not null)
            Keyboard.Focus(previousFocus);
        completion.TrySetResult(result);
    }

    private static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            Arguments = $"\"{path}\"",
            UseShellExecute = true
        });
    }

    private static void OpenUrl(string url) => Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });

    private static SolidColorBrush Brush(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private enum DialogTone
    {
        Accent,
        Success,
        Warning,
        Danger
    }
}
