using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

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
    private bool _smoothScrollRenderingSubscribed;
    private string _lastProgressMessage = string.Empty;
    private TaskCompletionSource<bool>? _dialogCompletion;

    private static readonly SolidColorBrush AccentBrush = Brush(0x87, 0x95, 0xFF);
    private static readonly SolidColorBrush AccentSoftBrush = Brush(0x1D, 0x24, 0x4A);
    private static readonly SolidColorBrush AccentBorderBrush = Brush(0x34, 0x41, 0x78);
    private static readonly SolidColorBrush SuccessBrush = Brush(0x58, 0xC9, 0x95);
    private static readonly SolidColorBrush SuccessSoftBrush = Brush(0x13, 0x2A, 0x25);
    private static readonly SolidColorBrush SuccessBorderBrush = Brush(0x24, 0x50, 0x43);
    private static readonly SolidColorBrush WarningBrush = Brush(0xF0, 0xB4, 0x5B);
    private static readonly SolidColorBrush WarningSoftBrush = Brush(0x2C, 0x25, 0x18);
    private static readonly SolidColorBrush WarningBorderBrush = Brush(0x58, 0x46, 0x29);
    private static readonly SolidColorBrush DangerBrush = Brush(0xF0, 0x7B, 0x87);
    private static readonly SolidColorBrush DangerSoftBrush = Brush(0x32, 0x1B, 0x21);
    private static readonly SolidColorBrush DangerBorderBrush = Brush(0x5A, 0x29, 0x33);
    private static readonly SolidColorBrush NeutralBrush = Brush(0x92, 0x9C, 0xAB);
    private static readonly SolidColorBrush NeutralSoftBrush = Brush(0x15, 0x1B, 0x24);
    private static readonly SolidColorBrush NeutralBorderBrush = Brush(0x25, 0x2D, 0x3A);

    private const double WheelScrollMultiplier = 0.72;
    private static readonly TimeSpan SmoothScrollDuration = TimeSpan.FromMilliseconds(175);

    public MainWindow()
    {
        InitializeComponent();
        _state = _installationService.LoadState();
        ApplySavedBranch();
        _state = _installationService.RecoverStateFromLocalInstallation(SelectedBranch());
        RefreshClientProbes();
        _localProbe = ResolveSelectedProbe();
        RefreshUi();
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        AppendLog($"Custom Vencord Manager v{AppInfo.CurrentVersion} started.");
        AppendLog($"Managed install: {ManagerPaths.InstallDirectory}");
        await CheckForUpdatesAsync(showSuccessDialog: false);
    }

    private void Window_Closed(object? sender, EventArgs e)
    {
        _dialogCompletion?.TrySetResult(false);
        if (_smoothScrollRenderingSubscribed)
        {
            CompositionTarget.Rendering -= SmoothScroll_Rendering;
            _smoothScrollRenderingSubscribed = false;
        }
        _installationService.Dispose();
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        var maximized = WindowState == WindowState.Maximized;
        MaximizeWindowButton.Content = maximized ? "\uE923" : "\uE922";
        WindowBorder.CornerRadius = maximized ? new CornerRadius(0) : new CornerRadius(12);
        WindowBorder.BorderThickness = maximized ? new Thickness(0) : new Thickness(1);
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape && DialogOverlay.Visibility == Visibility.Visible)
        {
            CompleteDialog(false);
            e.Handled = true;
        }
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        if (WindowState == WindowState.Maximized)
            return;

        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void MinimizeButton_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void MaximizeButton_Click(object sender, RoutedEventArgs e) => ToggleMaximize();
    private void CloseButton_Click(object sender, RoutedEventArgs e) => Close();

    private void ToggleMaximize() =>
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private async void CheckButton_Click(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(showSuccessDialog: true);

    private async void PrimaryButton_Click(object sender, RoutedEventArgs e)
    {
        if (_busy) return;
        if (_manifest is null && !await CheckForUpdatesAsync(false)) return;
        if (_manifest is null) return;

        VerifyLocalInstallation(logResult: false);
        var installed = HasManagedInstallationDetected()
            && !string.IsNullOrWhiteSpace(_state.InstalledVersion);
        var action = installed ? "Update" : "Install";
        var selectedClient = SelectedClientDisplayName();
        var replacingOtherVencord = _localProbe?.IsPatched == true && _localProbe.IsManagedPatch == false;
        var prompt = installed
            ? $"This will update the shared Custom Vencord build from v{_state.InstalledVersion} to v{_manifest.Version} and verify {selectedClient}. Any other Discord channels already using this managed build will continue using the same updated files."
            : replacingOtherVencord
                ? $"{selectedClient} currently has a different Vencord injection. Installing Custom Vencord v{_manifest.Version} will replace that injection for this client while leaving the other Discord channels alone."
                : $"This will install Custom Vencord v{_manifest.Version} for {selectedClient}. If the latest managed build is already present for another Discord channel, the manager will reuse it instead of downloading it again.";

        if (!await ShowDialogAsync(
                $"{action} Custom Vencord?",
                prompt,
                action,
                showCancel: true,
                DialogTone.Accent))
            return;

        await RunOperationAsync(
            $"Starting {action.ToLowerInvariant()}…",
            progress => _installationService.InstallOrUpdateAsync(_manifest, SelectedBranch(), repair: false, progress),
            completionTitle: $"{action} verified",
            completionMessage: $"Custom Vencord v{_manifest.Version} is installed and the Discord injection was verified successfully.");
    }

    private async void RepairButton_Click(object sender, RoutedEventArgs e)
    {
        VerifyLocalInstallation(logResult: false);
        if (_busy || !HasManagedInstallationDetected()) return;
        if (_manifest is null && !await CheckForUpdatesAsync(false)) return;
        if (_manifest is null) return;

        if (!await ShowDialogAsync(
                "Repair Custom Vencord?",
                "The manager will redownload the latest verified package, preserve a rollback copy, replace the managed files, and run Vencord's repair step. Discord will close briefly.",
                "Repair",
                showCancel: true,
                DialogTone.Accent))
            return;

        await RunOperationAsync(
            "Starting repair…",
            progress => _installationService.RepairAsync(_manifest, SelectedBranch(), progress),
            completionTitle: "Repair verified",
            completionMessage: "The managed files were replaced and the Discord injection was verified successfully.");
    }

    private async void UninstallButton_Click(object sender, RoutedEventArgs e)
    {
        VerifyLocalInstallation(logResult: false);
        if (_busy || !HasManagedInstallationDetected()) return;

        if (!await ShowDialogAsync(
                "Uninstall Custom Vencord?",
                "This removes the managed Custom Vencord installation from Discord. Your runtime plugins folder is kept, so your personal .js plugins will not be deleted.",
                "Uninstall",
                showCancel: true,
                DialogTone.Danger))
            return;

        await RunOperationAsync(
            "Starting uninstall…",
            progress => _installationService.UninstallAsync(SelectedBranch(), progress),
            completionTitle: "Uninstall verified",
            completionMessage: "The managed Custom Vencord injection was removed and the Discord installation was verified clean.");
    }

    private void OpenPluginsButton_Click(object sender, RoutedEventArgs e)
    {
        ManagerPaths.EnsureCreated();
        OpenFolder(ManagerPaths.PluginsDirectory);
    }

    private void OpenInstallButton_Click(object sender, RoutedEventArgs e)
    {
        ManagerPaths.EnsureCreated();
        OpenFolder(Directory.Exists(ManagerPaths.InstallDirectory) ? ManagerPaths.InstallDirectory : ManagerPaths.Root);
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

        await RunOperationAsync(
            "Starting manager update…",
            progress => _installationService.SelfUpdater.DownloadAndRestartAsync(_manifest, progress),
            showCompletionDialog: false);
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
            _state = _installationService.RecoverStateFromLocalInstallation(SelectedBranch(), _manifest);
            RefreshClientProbes();
            _localProbe = ResolveSelectedProbe();
            AppendLog($"Latest distribution: v{_manifest.Version} (OrionQuests v{_manifest.Components.OrionQuests}).");
            if (HasManagedInstallationDetected() && !string.IsNullOrWhiteSpace(_state.InstalledVersion))
                AppendLog($"{SelectedClientDisplayName()} verified as Custom Vencord v{_state.InstalledVersion}.");
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

            if (!string.Equals(_lastProgressMessage, value.Message, StringComparison.Ordinal))
            {
                _lastProgressMessage = value.Message;
                AppendLog(value.Message);
            }
        });

        try
        {
            await action(progress);
            _state = _installationService.RecoverStateFromLocalInstallation(SelectedBranch(), _manifest);
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
        var managedDetected = HasManagedInstallationDetected();
        var installed = managedDetected && !string.IsNullOrWhiteSpace(_state.InstalledVersion);
        var otherVencordDetected = _localProbe?.IsPatched == true && _localProbe.IsManagedPatch == false;

        SelectedChannelText.Text = SelectedChannelLabel();
        RefreshClientStatusIndicators();

        InstalledVersionText.Text = installed
            ? $"v{_state.InstalledVersion}"
            : managedDetected
                ? "Detected"
                : otherVencordDetected
                    ? "Vencord"
                    : "None";
        LatestVersionText.Text = _manifest is null ? "—" : $"v{_manifest.Version}";

        DialogTone statusTone;
        if (_manifest is null)
        {
            StatusText.Text = !selectedDiscordFound
                ? $"{SelectedClientDisplayName()} not found"
                : installed
                    ? "Installed · locally verified"
                    : managedDetected
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

            if (!selectedDiscordFound)
            {
                StatusText.Text = $"{SelectedClientDisplayName()} not found";
                statusTone = DialogTone.Warning;
            }
            else if (!installed)
            {
                if (managedDetected)
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
                var comparison = UpdateClient.CompareVersions(_state.InstalledVersion, _manifest.Version);
                if (comparison < 0)
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
            && (!installed || UpdateClient.CompareVersions(_state.InstalledVersion, _manifest.Version) < 0);

        PrimaryButton.Content = !selectedDiscordFound
            ? "Discord client not found"
            : !installed
                ? otherVencordDetected
                    ? "Install Custom Vencord  →"
                    : "Install latest  →"
                : updateAvailable
                    ? $"Update to v{_manifest!.Version}  →"
                    : "You're up to date";

        PrimaryButton.IsEnabled = !_busy && _manifest is not null && updateAvailable;
        ManagerVersionText.Text = $"v{AppInfo.CurrentVersion}";
        var managerUpdateAvailable = SelfUpdateService.IsUpdateAvailable(_manifest);
        ManagerUpdateButton.Visibility = managerUpdateAvailable ? Visibility.Visible : Visibility.Collapsed;
        ManagerUpdateButton.Content = managerUpdateAvailable && _manifest?.Manager is not null
            ? $"Update manager · v{_manifest.Manager.Version}"
            : "Update manager";
        ManagerUpdateButton.IsEnabled = !_busy && managerUpdateAvailable;
        CheckButton.IsEnabled = !_busy;
        RepairButton.IsEnabled = !_busy && managedDetected && _manifest is not null;
        UninstallButton.IsEnabled = !_busy && managedDetected;
        OpenPluginsButton.IsEnabled = !_busy;
        OpenInstallButton.IsEnabled = !_busy && _installationService.HasManagedFiles();
        SetBranchControlsEnabled(!_busy);
    }

    private void VerifyLocalInstallation(bool logResult)
    {
        _state = _installationService.RecoverStateFromLocalInstallation(SelectedBranch(), _manifest);
        RefreshClientProbes();
        _localProbe = ResolveSelectedProbe();

        if (!logResult) return;
        if (_localProbe is null)
        {
            AppendLog("Local verification: no Discord installation was found for the selected channel.");
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

    private bool HasManagedInstallationDetected() =>
        _localProbe?.IsManagedPatch == true && _installationService.HasManagedFiles();

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

        foreach (var branch in new[] { "stable", "canary", "ptb" })
        {
            if (_clientProbes.TryGetValue(branch, out var probe)) return probe;
        }

        return null;
    }

    private void RefreshClientStatusIndicators()
    {
        SetClientStatus("stable", StableClientDot, StableClientStatusBadge, StableClientStatusText);
        SetClientStatus("ptb", PtbClientDot, PtbClientStatusBadge, PtbClientStatusText);
        SetClientStatus("canary", CanaryClientDot, CanaryClientStatusBadge, CanaryClientStatusText);
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

        if (probe.IsManagedPatch && _installationService.HasManagedFiles())
        {
            text.Text = string.IsNullOrWhiteSpace(_state.InstalledVersion)
                ? "Custom Vencord"
                : $"Custom v{_state.InstalledVersion}";
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

        return _localProbe is null
            ? "Auto · no client found"
            : $"Auto → {DisplayBranch(_localProbe.Branch).Replace("Discord ", string.Empty, StringComparison.Ordinal)}";
    }

    private string SelectedClientDisplayName()
    {
        var selected = SelectedBranch();
        if (!selected.Equals("auto", StringComparison.OrdinalIgnoreCase)) return DisplayBranch(selected);
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
        });
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

        _dialogCompletion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        DialogTitle.Text = title;
        DialogMessage.Text = message;
        DialogConfirmButton.Content = confirmText;
        DialogCancelButton.Visibility = showCancel ? Visibility.Visible : Visibility.Collapsed;
        DialogConfirmButton.Style = (Style)FindResource(tone == DialogTone.Danger ? "DangerButton" : "PrimaryButton");

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
        DialogConfirmButton.Focus();
        return _dialogCompletion.Task;
    }

    private void DialogConfirmButton_Click(object sender, RoutedEventArgs e) => CompleteDialog(true);
    private void DialogCancelButton_Click(object sender, RoutedEventArgs e) => CompleteDialog(false);

    private void CompleteDialog(bool result)
    {
        var completion = _dialogCompletion;
        if (completion is null) return;
        _dialogCompletion = null;
        DialogOverlay.Visibility = Visibility.Collapsed;
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
