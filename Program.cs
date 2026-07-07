using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Win32;

namespace GptCheck;

internal static class Program
{
    [STAThread]
    private static int Main()
    {
        using TrayApplication application = new();
        return application.Run();
    }
}

internal sealed class TrayApplication : IDisposable
{
    private const uint CodexTrayIconId = 1;
    private const uint ClaudeTrayIconId = 2;
    private const uint TrayCallbackMessage = NativeMethods.WM_APP + 1;
    private const uint CodexResultMessage = NativeMethods.WM_APP + 2;
    private const uint ClaudeResultMessage = NativeMethods.WM_APP + 3;
    private const nuint RefreshTimerId = 1;
    private const uint RefreshIntervalMs = 300_000;
    private const uint CommandRefresh = 1001;
    private const uint CommandOpenCodexSessions = 1002;
    private const uint CommandExit = 1003;
    private const uint CommandOpenClaudeUsage = 1004;

    private static readonly NativeMethods.WndProcDelegate WindowProcedure = HandleWindowMessage;
    private static TrayApplication? Current;

    private readonly object _shellNotifyQueueLock = new();
    private readonly CodexUsageReader _codexUsageReader = new();
    private readonly ClaudeUsageReader _claudeUsageReader = new();
    private readonly LimitWatchdog _limitWatchdog = new();
    private readonly string _windowClassName = $"gptcheck.{Environment.ProcessId}";
    private Task _shellNotifyQueue = Task.CompletedTask;

    private IntPtr _windowHandle;
    private IntPtr _codexIconHandle;
    private IntPtr _claudeIconHandle;
    private bool _codexTrayIconAdded;
    private bool _claudeTrayIconAdded;
    private bool _windowClassRegistered;
    private string? _codexIconKey;
    private string? _claudeIconKey;
    private string _codexTooltip = "gptcheck";
    private string _codexStatusText = "Loading Codex usage...";
    private string _codexDetailText = "Reading local Codex sessions.";
    private string _codexSparkUsageText = "Spark usage: loading...";
    private string _codexUpdatedText = string.Empty;
    private string _codexSourceText = string.Empty;
    private string _claudeTooltip = "Claude usage";
    private string _claudeStatusText = "Loading Claude usage...";
    private string _claudeDetailText = "Reading Claude OAuth usage.";
    private string _claudeUpdatedText = string.Empty;
    private string _claudeSourceText = string.Empty;
    private volatile bool _codexRefreshInFlight;
    private volatile UsageReadResult? _pendingCodexResult;
    private volatile bool _claudeRefreshInFlight;
    private volatile ClaudeUsageReadResult? _pendingClaudeResult;
    private volatile bool _limitWatchdogInFlight;

    public int Run()
    {
        Current = this;
        RegisterWindowClass();
        CreateMessageWindow();
        UpdateCodexTrayIcon(TrayIconRenderer.CreateUnavailableIcon(), TrayIconRenderer.CodexUnavailableIconKey);
        UpdateClaudeTrayIcon(TrayIconRenderer.CreateClaudeUnavailableIcon(), TrayIconRenderer.ClaudeUnavailableIconKey);
        RefreshUsage();
        NativeMethods.SetTimer(_windowHandle, RefreshTimerId, RefreshIntervalMs, IntPtr.Zero);

        while (NativeMethods.GetMessage(out NativeMethods.MSG message, IntPtr.Zero, 0, 0) > 0)
        {
            NativeMethods.TranslateMessage(ref message);
            NativeMethods.DispatchMessage(ref message);
        }

        return 0;
    }

    public void Dispose()
    {
        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_windowHandle);
        }

        CleanupNativeResources();
        GC.SuppressFinalize(this);
    }

    private void RegisterWindowClass()
    {
        NativeMethods.WNDCLASSEX windowClass = new()
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = Marshal.GetFunctionPointerForDelegate(WindowProcedure),
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = _windowClassName
        };

        ushort atom = NativeMethods.RegisterClassEx(ref windowClass);
        if (atom == 0)
        {
            throw new InvalidOperationException($"RegisterClassEx failed: {Marshal.GetLastWin32Error()}");
        }

        _windowClassRegistered = true;
    }

    private void CreateMessageWindow()
    {
        _windowHandle = NativeMethods.CreateWindowEx(
            0,
            _windowClassName,
            "gptcheck",
            0,
            0,
            0,
            0,
            0,
            NativeMethods.HWND_MESSAGE,
            IntPtr.Zero,
            NativeMethods.GetModuleHandle(null),
            IntPtr.Zero);

        if (_windowHandle == IntPtr.Zero)
        {
            throw new InvalidOperationException($"CreateWindowEx failed: {Marshal.GetLastWin32Error()}");
        }
    }

    private void RefreshUsage()
    {
        RefreshCodexUsage();
        RefreshClaudeUsage();
    }

    private void RefreshCodexUsage()
    {
        if (_codexRefreshInFlight)
        {
            return;
        }

        _codexRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        // Local session folders can contain many JSONL files. Keep that scan off the
        // tray window thread so Explorer is never blocked by notification callbacks.
        Task.Run(() =>
        {
            UsageReadResult result;
            try
            {
                result = _codexUsageReader.ReadLatestSnapshot();
            }
            catch (Exception exception)
            {
                result = new UsageReadResult(null, null, exception.Message);
            }

            _pendingCodexResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, CodexResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _codexRefreshInFlight = false;
            }
        });
    }

    private void ApplyCodexResult()
    {
        UsageReadResult? result = _pendingCodexResult;
        _pendingCodexResult = null;
        _codexRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        if (result.Snapshot is null)
        {
            _codexTooltip = "gptcheck unavailable";
            _codexStatusText = "No Codex usage data found";
            _codexDetailText = result.ErrorMessage ?? "No token_count events were found.";
            _codexSparkUsageText = BuildSparkUsage(result.SparkSnapshot);
            _codexUpdatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _codexSourceText = _codexUsageReader.SessionsPath;
            UpdateCodexTrayIcon(TrayIconRenderer.CreateUnavailableIcon(), TrayIconRenderer.CodexUnavailableIconKey);
            return;
        }

        CodexUsageSnapshot snapshot = result.Snapshot;
        _codexTooltip = BuildCodexTooltip(snapshot);
        _codexStatusText = BuildCodexHeadline(snapshot);
        _codexDetailText = BuildCodexDetail(snapshot);
        _codexSparkUsageText = BuildSparkUsage(result.SparkSnapshot);
        _codexUpdatedText = $"Seen {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _codexSourceText = snapshot.SourceFile;
        UpdateCodexTrayIcon(TrayIconRenderer.CreateUsageIcon(snapshot), TrayIconRenderer.GetCodexIconKey(snapshot));
    }

    private void RefreshClaudeUsage()
    {
        if (_claudeRefreshInFlight)
        {
            return;
        }

        _claudeRefreshInFlight = true;
        IntPtr windowHandle = _windowHandle;

        // Reading Claude usage is a network call that can take seconds. Doing it on the
        // message-loop thread would freeze both tray icons and stall the shell, because
        // Explorer SendMessages to notification-icon owner windows and blocks when the
        // owner stops pumping. Fetch off-thread and post the result back to the UI thread.
        Task.Run(() =>
        {
            ClaudeUsageReadResult result;
            try
            {
                result = _claudeUsageReader.ReadLatestSnapshot();
            }
            catch (Exception exception)
            {
                result = new ClaudeUsageReadResult(null, exception.Message);
            }

            _pendingClaudeResult = result;

            if (windowHandle == IntPtr.Zero ||
                !NativeMethods.PostMessage(windowHandle, ClaudeResultMessage, IntPtr.Zero, IntPtr.Zero))
            {
                _claudeRefreshInFlight = false;
            }
        });
    }

    private void ApplyClaudeResult()
    {
        ClaudeUsageReadResult? result = _pendingClaudeResult;
        _pendingClaudeResult = null;
        _claudeRefreshInFlight = false;

        if (result is null)
        {
            return;
        }

        if (result.Snapshot is null)
        {
            _claudeTooltip = "Claude usage unavailable";
            _claudeStatusText = "No Claude usage data found";
            _claudeDetailText = result.ErrorMessage ?? "Claude usage limits were not found.";
            _claudeUpdatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _claudeSourceText = _claudeUsageReader.CredentialsPath;
            UpdateClaudeTrayIcon(TrayIconRenderer.CreateClaudeUnavailableIcon(), TrayIconRenderer.ClaudeUnavailableIconKey);
            RunLimitWatchdog(null);
            return;
        }

        ClaudeUsageSnapshot snapshot = result.Snapshot;
        _claudeTooltip = BuildClaudeTooltip(snapshot);
        _claudeStatusText = BuildClaudeHeadline(snapshot);
        _claudeDetailText = BuildClaudeDetail(snapshot);
        _claudeUpdatedText = $"Seen {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _claudeSourceText = snapshot.SourceFile;
        UpdateClaudeTrayIcon(TrayIconRenderer.CreateClaudeIcon(snapshot), TrayIconRenderer.GetClaudeIconKey(snapshot));
        RunLimitWatchdog(snapshot);
    }

    private void RunLimitWatchdog(ClaudeUsageSnapshot? snapshot)
    {
        if (_limitWatchdogInFlight)
        {
            return;
        }

        _limitWatchdogInFlight = true;
        Task.Run(() =>
        {
            try
            {
                _limitWatchdog.Check(snapshot);
            }
            finally
            {
                _limitWatchdogInFlight = false;
            }
        });
    }

    private void UpdateCodexTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        UpdateTrayIcon(
            CodexTrayIconId,
            newIconHandle,
            ref _codexIconHandle,
            ref _codexTrayIconAdded,
            ref _codexIconKey,
            iconKey,
            _codexTooltip);
    }

    private void UpdateClaudeTrayIcon(IntPtr newIconHandle, string iconKey)
    {
        UpdateTrayIcon(
            ClaudeTrayIconId,
            newIconHandle,
            ref _claudeIconHandle,
            ref _claudeTrayIconAdded,
            ref _claudeIconKey,
            iconKey,
            _claudeTooltip);
    }

    private void UpdateTrayIcon(
        uint iconId,
        IntPtr newIconHandle,
        ref IntPtr iconHandle,
        ref bool trayIconAdded,
        ref string? currentIconKey,
        string newIconKey,
        string tooltip)
    {
        if (newIconHandle == IntPtr.Zero)
        {
            return;
        }

        if (trayIconAdded && string.Equals(currentIconKey, newIconKey, StringComparison.Ordinal))
        {
            NativeMethods.DestroyIcon(newIconHandle);
            return;
        }

        IntPtr previousIconHandle = iconHandle;
        iconHandle = newIconHandle;
        currentIconKey = newIconKey;

        NativeMethods.NOTIFYICONDATA data = CreateNotifyIconData(iconId, iconHandle, tooltip);
        uint message;
        if (trayIconAdded)
        {
            message = NativeMethods.NIM_MODIFY;
        }
        else
        {
            message = NativeMethods.NIM_ADD;
            trayIconAdded = true;
        }

        QueueShellNotify(message, data, previousIconHandle);
    }

    private void QueueShellNotify(uint message, NativeMethods.NOTIFYICONDATA data, IntPtr iconHandleToDestroy)
    {
        lock (_shellNotifyQueueLock)
        {
            _shellNotifyQueue = _shellNotifyQueue.ContinueWith(
                _ => ExecuteShellNotify(message, data, iconHandleToDestroy),
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    private static void ExecuteShellNotify(uint message, NativeMethods.NOTIFYICONDATA data, IntPtr iconHandleToDestroy)
    {
        NativeMethods.Shell_NotifyIcon(message, ref data);

        if (iconHandleToDestroy != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(iconHandleToDestroy);
        }
    }

    private NativeMethods.NOTIFYICONDATA CreateNotifyIconData(uint iconId, IntPtr iconHandle, string tooltip)
    {
        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _windowHandle,
            uID = iconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = iconHandle,
            szTip = TruncateTooltip(tooltip),
            szInfo = string.Empty,
            szInfoTitle = string.Empty
        };
    }

    private static IntPtr HandleWindowMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        if (Current is not null)
        {
            return Current.WndProc(windowHandle, message, wParam, lParam);
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private IntPtr WndProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam)
    {
        switch (message)
        {
            case NativeMethods.WM_TIMER:
                if ((nuint)wParam == RefreshTimerId)
                {
                    RefreshUsage();
                    return IntPtr.Zero;
                }

                break;

            case CodexResultMessage:
                ApplyCodexResult();
                return IntPtr.Zero;

            case ClaudeResultMessage:
                ApplyClaudeResult();
                return IntPtr.Zero;

            case TrayCallbackMessage:
                uint iconId = (uint)wParam.ToInt64();
                if (iconId is not CodexTrayIconId and not ClaudeTrayIconId)
                {
                    break;
                }

                switch ((uint)lParam.ToInt64())
                {
                    case NativeMethods.WM_LBUTTONDBLCLK:
                        RefreshUsage();
                        return IntPtr.Zero;

                    case NativeMethods.WM_RBUTTONUP:
                    case NativeMethods.WM_CONTEXTMENU:
                        ShowContextMenu(iconId);
                        return IntPtr.Zero;
                }

                break;

            case NativeMethods.WM_DESTROY:
                CleanupNativeResources();
                PostQuit();
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);
    }

    private void ShowContextMenu(uint iconId)
    {
        IntPtr menuHandle = NativeMethods.CreatePopupMenu();
        if (menuHandle == IntPtr.Zero)
        {
            return;
        }

        bool isClaudeMenu = iconId == ClaudeTrayIconId;
        string statusText = isClaudeMenu ? _claudeStatusText : _codexStatusText;
        string detailText = isClaudeMenu ? _claudeDetailText : _codexDetailText;
        string updatedText = isClaudeMenu ? _claudeUpdatedText : _codexUpdatedText;
        string sourceText = isClaudeMenu ? _claudeSourceText : _codexSourceText;
        uint openCommand = isClaudeMenu ? CommandOpenClaudeUsage : CommandOpenCodexSessions;
        string openLabel = isClaudeMenu ? "Open Claude usage settings" : "Open Codex sessions";

        try
        {
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(statusText));
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(detailText));
            if (!isClaudeMenu)
            {
                NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(_codexSparkUsageText));
            }

            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(updatedText));
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(sourceText));
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, CommandRefresh, "Refresh now");
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, openCommand, openLabel);
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, CommandExit, "Exit");

            NativeMethods.GetCursorPos(out NativeMethods.POINT cursor);
            NativeMethods.SetForegroundWindow(_windowHandle);

            uint selectedCommand = NativeMethods.TrackPopupMenu(
                menuHandle,
                NativeMethods.TPM_RIGHTBUTTON | NativeMethods.TPM_RETURNCMD | NativeMethods.TPM_NONOTIFY,
                cursor.X,
                cursor.Y,
                0,
                _windowHandle,
                IntPtr.Zero);

            HandleMenuCommand(selectedCommand);
            NativeMethods.PostMessage(_windowHandle, NativeMethods.WM_NULL, IntPtr.Zero, IntPtr.Zero);
        }
        finally
        {
            NativeMethods.DestroyMenu(menuHandle);
        }
    }

    private void HandleMenuCommand(uint command)
    {
        switch (command)
        {
            case CommandRefresh:
                RefreshUsage();
                break;

            case CommandOpenCodexSessions:
                OpenCodexSessionsFolder();
                break;

            case CommandOpenClaudeUsage:
                OpenClaudeUsagePage();
                break;

            case CommandExit:
                NativeMethods.DestroyWindow(_windowHandle);
                break;
        }
    }

    private void OpenCodexSessionsFolder()
    {
        string target = Directory.Exists(_codexUsageReader.SessionsPath)
            ? _codexUsageReader.SessionsPath
            : _codexUsageReader.CodexHomePath;

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
            UseShellExecute = true
        });
    }

    private static void OpenClaudeUsagePage()
    {
        Process.Start(new ProcessStartInfo
        {
            FileName = "https://claude.ai/settings/usage",
            UseShellExecute = true
        });
    }

    private void PostQuit()
    {
        _windowHandle = IntPtr.Zero;
        Current = null;
        NativeMethods.PostQuitMessage(0);
    }

    private void CleanupNativeResources()
    {
        RemoveTrayIcon(CodexTrayIconId, ref _codexTrayIconAdded);
        RemoveTrayIcon(ClaudeTrayIconId, ref _claudeTrayIconAdded);

        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_windowHandle, RefreshTimerId);
        }

        DestroyIconHandle(ref _codexIconHandle);
        DestroyIconHandle(ref _claudeIconHandle);

        if (_windowClassRegistered)
        {
            NativeMethods.UnregisterClass(_windowClassName, NativeMethods.GetModuleHandle(null));
            _windowClassRegistered = false;
        }
    }

    private void RemoveTrayIcon(uint iconId, ref bool trayIconAdded)
    {
        if (trayIconAdded && _windowHandle != IntPtr.Zero)
        {
            NativeMethods.NOTIFYICONDATA data = new()
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _windowHandle,
                uID = iconId,
                szTip = string.Empty,
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };

            QueueShellNotify(NativeMethods.NIM_DELETE, data, IntPtr.Zero);
            trayIconAdded = false;
        }
    }

    private static void DestroyIconHandle(ref IntPtr iconHandle)
    {
        if (iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(iconHandle);
            iconHandle = IntPtr.Zero;
        }
    }

    private static string BuildCodexTooltip(CodexUsageSnapshot snapshot)
    {
        int primaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.PrimaryUsedPercent);
        int secondaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.SecondaryUsedPercent);

        return TruncateTooltip(
            $"gptcheck {FormatWindow(snapshot.PrimaryWindowMinutes)} left {primaryRemaining}% " +
            $"{FormatWindow(snapshot.SecondaryWindowMinutes)} left {secondaryRemaining}%");
    }

    private static string BuildCodexHeadline(CodexUsageSnapshot snapshot)
    {
        string planType = string.IsNullOrWhiteSpace(snapshot.PlanType) ? "plan ?" : snapshot.PlanType;
        int primaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.PrimaryUsedPercent);
        int secondaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.SecondaryUsedPercent);

        return $"{planType}: {FormatWindow(snapshot.PrimaryWindowMinutes)} left {primaryRemaining}% | " +
               $"{FormatWindow(snapshot.SecondaryWindowMinutes)} left {secondaryRemaining}% " +
               $"({FormatResetCountdown(snapshot.SecondaryResetAt)})";
    }

    private static string BuildCodexDetail(CodexUsageSnapshot snapshot)
    {
        string primaryReset = snapshot.PrimaryResetAt is null
            ? "?"
            : snapshot.PrimaryResetAt.Value.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        string secondaryReset = snapshot.SecondaryResetAt is null
            ? "?"
            : snapshot.SecondaryResetAt.Value.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

        return $"Reset {FormatWindow(snapshot.PrimaryWindowMinutes)} {primaryReset}, " +
               $"{FormatWindow(snapshot.SecondaryWindowMinutes)} {secondaryReset}";
    }

    private static string BuildSparkUsage(CodexUsageSnapshot? sparkSnapshot)
    {
        if (sparkSnapshot is null)
        {
            return "Spark usage: no recent Spark sessions";
        }

        int primaryRemaining = CodexUsageMath.GetRemainingPercent(sparkSnapshot.PrimaryUsedPercent);
        int secondaryRemaining = CodexUsageMath.GetRemainingPercent(sparkSnapshot.SecondaryUsedPercent);
        string sparkSeen = sparkSnapshot.Timestamp.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

        return $"Spark: {FormatWindow(sparkSnapshot.PrimaryWindowMinutes)} left {primaryRemaining}% | " +
               $"{FormatWindow(sparkSnapshot.SecondaryWindowMinutes)} left {secondaryRemaining}% " +
               $"({FormatResetCountdown(sparkSnapshot.SecondaryResetAt)}), Spark seen {sparkSeen}";
    }

    private static string BuildClaudeTooltip(ClaudeUsageSnapshot snapshot)
    {
        return TruncateTooltip(
            $"Claude 5h left {ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent)}% " +
            $"7d left {ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent)}%");
    }

    private static string BuildClaudeHeadline(ClaudeUsageSnapshot snapshot)
    {
        int fiveHourRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent);
        int sevenDayRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent);

        return $"Claude: 5h left {fiveHourRemaining}% | 7d left {sevenDayRemaining}% " +
               $"({FormatResetCountdown(snapshot.SevenDayResetAt)})";
    }

    private static string BuildClaudeDetail(ClaudeUsageSnapshot snapshot)
    {
        string fiveHourReset = snapshot.FiveHourResetAt is null
            ? "?"
            : snapshot.FiveHourResetAt.Value.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);
        string sevenDayReset = snapshot.SevenDayResetAt is null
            ? "?"
            : snapshot.SevenDayResetAt.Value.ToLocalTime().ToString("MMM dd HH:mm", CultureInfo.InvariantCulture);

        return $"Reset 5h {fiveHourReset}, 7d {sevenDayReset}";
    }

    private static string FormatWindow(int minutes)
    {
        if (minutes <= 0)
        {
            return "?";
        }

        if (minutes % (60 * 24) == 0)
        {
            return $"{minutes / (60 * 24)}d";
        }

        if (minutes % 60 == 0)
        {
            return $"{minutes / 60}h";
        }

        return $"{minutes}m";
    }

    private static string FormatResetCountdown(DateTimeOffset? resetAt)
    {
        if (resetAt is null)
        {
            return "reset in ?";
        }

        TimeSpan remaining = resetAt.Value - DateTimeOffset.Now;
        if (remaining <= TimeSpan.Zero)
        {
            return "reset due";
        }

        int days = (int)Math.Floor(remaining.TotalDays);
        int hours = remaining.Hours;
        if (days > 0)
        {
            return hours > 0
                ? $"reset in {days}d {hours}h"
                : $"reset in {days}d";
        }

        if (hours > 0)
        {
            return $"reset in {hours}h";
        }

        int minutes = Math.Max(1, (int)Math.Ceiling(remaining.TotalMinutes));
        return $"reset in {minutes}m";
    }

    private static string TruncateTooltip(string value)
    {
        return value.Length <= 127 ? value : value[..127];
    }

    private static string LimitMenuText(string value)
    {
        const int maxLength = 120;
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        return value.Length <= maxLength ? value : $"{value[..(maxLength - 3)]}...";
    }
}

internal static class CodexUsageMath
{
    public static int GetRemainingPercent(double usedPercent)
    {
        double remaining = 100d - usedPercent;
        return Math.Clamp((int)Math.Floor(remaining), 0, 100);
    }
}

internal static class ClaudeUsageMath
{
    public static int GetRemainingPercent(double usedPercent)
    {
        double remaining = 100d - usedPercent;
        return Math.Clamp((int)Math.Floor(remaining), 0, 100);
    }
}

internal sealed record CodexUsageSnapshot(
    DateTimeOffset Timestamp,
    double PrimaryUsedPercent,
    double SecondaryUsedPercent,
    int PrimaryWindowMinutes,
    int SecondaryWindowMinutes,
    DateTimeOffset? PrimaryResetAt,
    DateTimeOffset? SecondaryResetAt,
    string? PlanType,
    string? Model,
    string SourceFile);

internal sealed record UsageReadResult(CodexUsageSnapshot? Snapshot, CodexUsageSnapshot? SparkSnapshot, string? ErrorMessage);

internal sealed class CodexUsageReader
{
    private const int MaxFilesToScan = 32;

    public string CodexHomePath { get; } = ResolveCodexHome();
    public string SessionsPath => Path.Combine(CodexHomePath, "sessions");

    public UsageReadResult ReadLatestSnapshot()
    {
        if (!Directory.Exists(SessionsPath))
        {
            return new UsageReadResult(null, null, $"Missing sessions folder: {SessionsPath}");
        }

        try
        {
            IEnumerable<string> recentFiles = Directory
                .EnumerateFiles(SessionsPath, "*.jsonl", SearchOption.AllDirectories)
                .Select(path => new FileInfo(path))
                .OrderByDescending(file => file.LastWriteTimeUtc)
                .Take(MaxFilesToScan)
                .Select(file => file.FullName);

            CodexUsageSnapshot? latestSnapshot = null;
            CodexUsageSnapshot? latestSparkSnapshot = null;

            foreach (string file in recentFiles)
            {
                UsageFileSnapshot fileSnapshot = TryReadFile(file);
                if (fileSnapshot.Latest is { } candidate &&
                    (latestSnapshot is null || candidate.Timestamp > latestSnapshot.Timestamp))
                {
                    latestSnapshot = candidate;
                }

                if (fileSnapshot.LatestSpark is { } sparkCandidate &&
                    (latestSparkSnapshot is null || sparkCandidate.Timestamp > latestSparkSnapshot.Timestamp))
                {
                    latestSparkSnapshot = sparkCandidate;
                }
            }

            return latestSnapshot is null
                ? new UsageReadResult(null, latestSparkSnapshot, "No token_count events were found in recent sessions.")
                : new UsageReadResult(latestSnapshot, latestSparkSnapshot, null);
        }
        catch (Exception exception)
        {
            return new UsageReadResult(null, null, exception.Message);
        }
    }

    private static string ResolveCodexHome()
    {
        string? configuredHome = Environment.GetEnvironmentVariable("CODEX_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return Environment.ExpandEnvironmentVariables(configuredHome);
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".codex");
    }

    private static UsageFileSnapshot TryReadFile(string path)
    {
        CodexUsageSnapshot? latest = null;
        CodexUsageSnapshot? latestSpark = null;
        string? currentModel = null;

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);

        while (reader.ReadLine() is { } line)
        {
            bool isTurnContext = line.Contains("\"type\":\"turn_context\"", StringComparison.Ordinal);
            bool isTokenCount = line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal);
            if (!isTurnContext && !isTokenCount)
            {
                continue;
            }

            if (isTurnContext && TryReadTurnContextModel(line, out string? model))
            {
                currentModel = model;
                continue;
            }

            if (!isTokenCount)
            {
                continue;
            }

            CodexUsageSnapshot? parsed = TryParseLine(line, path, currentModel);
            if (parsed is null)
            {
                continue;
            }

            if (latest is null || parsed.Timestamp > latest.Timestamp)
            {
                latest = parsed;
            }

            if (IsSparkSnapshot(parsed) && (latestSpark is null || parsed.Timestamp > latestSpark.Timestamp))
            {
                latestSpark = parsed;
            }
        }

        return new UsageFileSnapshot(latest, latestSpark);
    }

    private static bool TryReadTurnContextModel(string line, out string? model)
    {
        model = null;

        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("type", out JsonElement typeElement) ||
                !string.Equals(typeElement.GetString(), "turn_context", StringComparison.Ordinal))
            {
                return false;
            }

            if (root.TryGetProperty("payload", out JsonElement payload) &&
                payload.TryGetProperty("model", out JsonElement modelElement))
            {
                model = modelElement.GetString();
            }

            return true;
        }
        catch (JsonException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static CodexUsageSnapshot? TryParseLine(string line, string sourceFile, string? model)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(line);
            JsonElement root = document.RootElement;

            if (!root.TryGetProperty("payload", out JsonElement payload) ||
                !payload.TryGetProperty("type", out JsonElement payloadType) ||
                !string.Equals(payloadType.GetString(), "token_count", StringComparison.Ordinal))
            {
                return null;
            }

            if (!root.TryGetProperty("timestamp", out JsonElement timestampElement) ||
                !DateTimeOffset.TryParse(timestampElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset timestamp))
            {
                return null;
            }

            if (!payload.TryGetProperty("rate_limits", out JsonElement rateLimits))
            {
                return null;
            }

            RateLimitInfo primary = ReadLimit(rateLimits, "primary");
            RateLimitInfo secondary = ReadLimit(rateLimits, "secondary");
            string? planType = rateLimits.TryGetProperty("plan_type", out JsonElement planTypeElement)
                ? planTypeElement.GetString()
                : null;

            return new CodexUsageSnapshot(
                timestamp,
                primary.UsedPercent,
                secondary.UsedPercent,
                primary.WindowMinutes,
                secondary.WindowMinutes,
                primary.ResetsAt,
                secondary.ResetsAt,
                planType,
                model,
                sourceFile);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (InvalidOperationException)
        {
            return null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static RateLimitInfo ReadLimit(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out JsonElement limit))
        {
            return new RateLimitInfo(0, 0, null);
        }

        double usedPercent = limit.TryGetProperty("used_percent", out JsonElement usedPercentElement)
            ? usedPercentElement.GetDouble()
            : 0;

        int windowMinutes = limit.TryGetProperty("window_minutes", out JsonElement windowMinutesElement)
            ? windowMinutesElement.GetInt32()
            : 0;

        DateTimeOffset? resetsAt = null;
        if (limit.TryGetProperty("resets_at", out JsonElement resetsAtElement) &&
            resetsAtElement.ValueKind is JsonValueKind.Number &&
            resetsAtElement.TryGetInt64(out long resetsAtSeconds))
        {
            resetsAt = DateTimeOffset.FromUnixTimeSeconds(resetsAtSeconds);
        }

        return new RateLimitInfo(usedPercent, windowMinutes, resetsAt);
    }

    private static bool IsSparkSnapshot(CodexUsageSnapshot snapshot)
    {
        return snapshot.Model?.Contains("spark", StringComparison.OrdinalIgnoreCase) == true;
    }

    private sealed record UsageFileSnapshot(CodexUsageSnapshot? Latest, CodexUsageSnapshot? LatestSpark);

    private sealed record RateLimitInfo(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt);
}

internal sealed record ClaudeUsageSnapshot(
    DateTimeOffset Timestamp,
    double FiveHourUsedPercent,
    double SevenDayUsedPercent,
    DateTimeOffset? FiveHourResetAt,
    DateTimeOffset? SevenDayResetAt,
    string SourceFile);

internal sealed record ClaudeUsageReadResult(ClaudeUsageSnapshot? Snapshot, string? ErrorMessage);

internal sealed class ClaudeUsageReader
{
    private const string UsageEndpoint = "https://api.anthropic.com/api/oauth/usage";
    private static readonly HttpClient HttpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(20)
    };

    public string ClaudeHomePath { get; } = ResolveClaudeHome();
    public string CredentialsPath => Path.Combine(ClaudeHomePath, ".credentials.json");

    public ClaudeUsageReadResult ReadLatestSnapshot()
    {
        if (!File.Exists(CredentialsPath))
        {
            return new ClaudeUsageReadResult(null, $"Missing Claude credentials file: {CredentialsPath}");
        }

        try
        {
            string? accessToken = ReadAccessToken();
            if (string.IsNullOrWhiteSpace(accessToken))
            {
                return new ClaudeUsageReadResult(null, "Claude OAuth access token was not found.");
            }

            using HttpRequestMessage request = new(HttpMethod.Get, UsageEndpoint);
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {accessToken}");
            request.Headers.TryAddWithoutValidation("Accept", "application/json");
            request.Headers.TryAddWithoutValidation("User-Agent", "gpttrack/1.0");

            using HttpResponseMessage response = HttpClient.Send(request);
            string responseBody = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
            if (!response.IsSuccessStatusCode)
            {
                string reason = response.StatusCode == System.Net.HttpStatusCode.Unauthorized
                    ? "Claude token expired - re-login in Claude Code"
                    : $"Claude usage request failed: HTTP {(int)response.StatusCode}";
                return new ClaudeUsageReadResult(null, reason);
            }

            ClaudeUsageSnapshot? snapshot = ParseUsageResponse(responseBody);
            return snapshot is null
                ? new ClaudeUsageReadResult(null, "Claude usage response did not include five_hour and seven_day limits.")
                : new ClaudeUsageReadResult(snapshot, null);
        }
        catch (Exception exception)
        {
            return new ClaudeUsageReadResult(null, exception.Message);
        }
    }

    private static string ResolveClaudeHome()
    {
        string? configuredHome = Environment.GetEnvironmentVariable("CLAUDE_HOME");
        if (!string.IsNullOrWhiteSpace(configuredHome))
        {
            return Environment.ExpandEnvironmentVariables(configuredHome);
        }

        string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return Path.Combine(userProfile, ".claude");
    }

    private string? ReadAccessToken()
    {
        string? environmentToken = Environment.GetEnvironmentVariable("CLAUDE_CODE_OAUTH_TOKEN");
        if (!string.IsNullOrWhiteSpace(environmentToken))
        {
            return environmentToken;
        }

        using FileStream stream = new(CredentialsPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;

        if (root.TryGetProperty("claudeAiOauth", out JsonElement oauth) &&
            oauth.TryGetProperty("accessToken", out JsonElement accessTokenElement))
        {
            return accessTokenElement.GetString();
        }

        return null;
    }

    private static ClaudeUsageSnapshot? ParseUsageResponse(string responseBody)
    {
        using JsonDocument document = JsonDocument.Parse(responseBody);
        JsonElement root = document.RootElement;

        if (!TryReadLimit(root, "five_hour", out UsageLimit fiveHour) ||
            !TryReadLimit(root, "seven_day", out UsageLimit sevenDay))
        {
            return null;
        }

        return new ClaudeUsageSnapshot(
            DateTimeOffset.Now,
            fiveHour.UsedPercent,
            sevenDay.UsedPercent,
            fiveHour.ResetsAt,
            sevenDay.ResetsAt,
            UsageEndpoint);
    }

    private static bool TryReadLimit(JsonElement root, string name, out UsageLimit limit)
    {
        limit = new UsageLimit(0, null);
        if (!root.TryGetProperty(name, out JsonElement limitElement) ||
            limitElement.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        double usedPercent = limitElement.TryGetProperty("utilization", out JsonElement utilizationElement)
            ? utilizationElement.GetDouble()
            : 0;

        DateTimeOffset? resetsAt = null;
        if (limitElement.TryGetProperty("resets_at", out JsonElement resetsAtElement) &&
            resetsAtElement.ValueKind == JsonValueKind.String &&
            DateTimeOffset.TryParse(resetsAtElement.GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out DateTimeOffset parsedReset))
        {
            resetsAt = parsedReset;
        }

        limit = new UsageLimit(usedPercent, resetsAt);
        return true;
    }

    private sealed record UsageLimit(double UsedPercent, DateTimeOffset? ResetsAt);
}

internal sealed class LimitWatchdog
{
    private const double ThresholdRemainingPercent = 5.0d;
    private const long MinimumFreeDiskBytes = 5L * 1024L * 1024L * 1024L;

    private static readonly string[] DrivesToMonitor = ["C", "D"];

    private static readonly string PauseBatchPath =
        @"C:\Users\flcl\Desktop\Pause Strategy Hunt.bat";

    private static readonly string RunBatchPath =
        @"C:\Users\flcl\Desktop\Run Strategy Hunt.bat";

    private static readonly string AppDataPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "limits");

    private static readonly string LogPath = Path.Combine(AppDataPath, "limits.log");
    private string _state = "unknown";

    public void Check(ClaudeUsageSnapshot? snapshot)
    {
        Directory.CreateDirectory(AppDataPath);

        string state = _state;
        LimitUsage? usage = snapshot is null ? null : LimitUsage.FromSnapshot(snapshot);
        DiskSnapshot disk = ReadDiskSnapshot();

        if (usage is null && !disk.HasLowDisk)
        {
            Log($"No action: state={state}, Claude usage unavailable, disk {disk.Summary}.");
            return;
        }

        bool usageBelowLimit = usage is not null &&
            (usage.FiveHourRemainingPercent < ThresholdRemainingPercent ||
             usage.WeeklyRemainingPercent < ThresholdRemainingPercent);
        bool belowLimit = usageBelowLimit || disk.HasLowDisk;

        bool usageAvailableAgain = usage is not null &&
            usage.FiveHourRemainingPercent > ThresholdRemainingPercent &&
            usage.WeeklyRemainingPercent > ThresholdRemainingPercent;
        bool availableAgain = usageAvailableAgain && !disk.HasLowDisk;

        if (belowLimit && !string.Equals(state, "paused", StringComparison.OrdinalIgnoreCase))
        {
            Log(
                $"Below threshold: {FormatUsage(usage)}, disk {disk.Summary}. " +
                "Starting pause batch.");
            StartBatch(PauseBatchPath);
            _state = "paused";
            return;
        }

        if (availableAgain && string.Equals(state, "paused", StringComparison.OrdinalIgnoreCase))
        {
            Log(
                $"Usage and disk available again: {FormatUsage(usage)}, disk {disk.Summary}. " +
                "Starting run batch.");
            StartBatch(RunBatchPath);
            _state = "running";
            return;
        }

        if (string.Equals(state, "unknown", StringComparison.OrdinalIgnoreCase) && availableAgain)
        {
            _state = "running";
        }

        Log($"No action: state={state}, {FormatUsage(usage)}, disk {disk.Summary}.");
    }

    private static DiskSnapshot ReadDiskSnapshot()
    {
        List<DriveFreeSpace> drives = [];
        foreach (string driveName in DrivesToMonitor)
        {
            try
            {
                DriveInfo drive = new($@"{driveName}:\");
                if (!drive.IsReady)
                {
                    drives.Add(new DriveFreeSpace(driveName, null, "not ready"));
                    continue;
                }

                drives.Add(new DriveFreeSpace(driveName, drive.AvailableFreeSpace, null));
            }
            catch (Exception exception)
            {
                drives.Add(new DriveFreeSpace(driveName, null, exception.Message));
            }
        }

        return new DiskSnapshot(drives);
    }

    private static void StartBatch(string batchPath)
    {
        if (!File.Exists(batchPath))
        {
            Log($"Missing batch file: {batchPath}");
            return;
        }

        ProcessStartInfo startInfo = new()
        {
            FileName = "cmd.exe",
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = Path.GetDirectoryName(batchPath) ?? AppContext.BaseDirectory
        };
        startInfo.ArgumentList.Add("/d");
        startInfo.ArgumentList.Add("/c");
        startInfo.ArgumentList.Add("call");
        startInfo.ArgumentList.Add(batchPath);

        Process? process = Process.Start(startInfo);
        Log($"Started '{batchPath}' pid={process?.Id.ToString(CultureInfo.InvariantCulture) ?? "unknown"}.");
    }

    private static void Log(string message)
    {
        try
        {
            Directory.CreateDirectory(AppDataPath);
            File.AppendAllText(LogPath, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} {message}{Environment.NewLine}");
        }
        catch
        {
            // Logging must not bring down the tray app.
        }
    }

    private static string FormatUsage(LimitUsage? usage)
    {
        return usage is null
            ? "Claude usage unavailable"
            : $"5h left {usage.FiveHourRemainingPercent:0.##}%, weekly left {usage.WeeklyRemainingPercent:0.##}%";
    }

    private static string FormatBytes(long bytes)
    {
        double gib = bytes / 1024d / 1024d / 1024d;
        return $"{gib:0.##} GB";
    }

    private sealed record LimitUsage(
        double FiveHourRemainingPercent,
        double WeeklyRemainingPercent,
        DateTimeOffset? FiveHourResetAt,
        DateTimeOffset? WeeklyResetAt)
    {
        public static LimitUsage FromSnapshot(ClaudeUsageSnapshot snapshot)
        {
            return new LimitUsage(
                ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent),
                ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent),
                snapshot.FiveHourResetAt,
                snapshot.SevenDayResetAt);
        }
    }

    private sealed record DiskSnapshot(IReadOnlyList<DriveFreeSpace> Drives)
    {
        public bool HasLowDisk => Drives.Any(drive =>
            drive.FreeBytes is null || drive.FreeBytes.Value < MinimumFreeDiskBytes);

        public string Summary => string.Join(", ", Drives.Select(drive =>
            drive.FreeBytes is null
                ? $"{drive.Name}: unavailable ({drive.Error ?? "unknown"})"
                : $"{drive.Name}: {FormatBytes(drive.FreeBytes.Value)} free"));
    }

    private sealed record DriveFreeSpace(string Name, long? FreeBytes, string? Error);
}

internal static class TrayIconRenderer
{
    private const int IconSize = 16;
    private const int GlyphWidth = 4;
    private const int GlyphHeight = 7;
    private const int GlyphSpacing = 1;

    public const string CodexUnavailableIconKey = "codex:?:?";
    public const string ClaudeUnavailableIconKey = "claude:?:?";

    // Brand marker colors are fixed, not theme-dependent.
    private const uint OpenAiBrandColor = 0xFF10A37F;
    private const uint ClaudeBrandColor = 0xFFD97757;

    private static readonly IconPalette LightThemePalette = new(
        UnknownColor: 0xFF444444,
        DangerColor: 0xFF9D2B22,
        WarningColor: 0xFF8C5D00,
        SafeColor: 0xFF1E6F36);
    private static readonly IconPalette DarkThemePalette = new(
        UnknownColor: 0xFFD8D8D8,
        DangerColor: 0xFFFF6C62,
        WarningColor: 0xFFF1C84A,
        SafeColor: 0xFF72E089);

    private static readonly IReadOnlyDictionary<char, string[]> Glyphs = new Dictionary<char, string[]>
    {
        ['0'] = ["0110", "1001", "1001", "1001", "1001", "1001", "0110"],
        ['1'] = ["0010", "0110", "0010", "0010", "0010", "0010", "0111"],
        ['2'] = ["0110", "1001", "0001", "0010", "0100", "1000", "1111"],
        ['3'] = ["1110", "0001", "0001", "0110", "0001", "0001", "1110"],
        ['4'] = ["1001", "1001", "1001", "1111", "0001", "0001", "0001"],
        ['5'] = ["1111", "1000", "1000", "1110", "0001", "0001", "1110"],
        ['6'] = ["0111", "1000", "1000", "1110", "1001", "1001", "0110"],
        ['7'] = ["1111", "0001", "0001", "0010", "0010", "0100", "0100"],
        ['8'] = ["0110", "1001", "1001", "0110", "1001", "1001", "0110"],
        ['9'] = ["0110", "1001", "1001", "0111", "0001", "0001", "1110"],
        ['?'] = ["1110", "0001", "0010", "0010", "0000", "0010", "0000"]
    };

    public static IntPtr CreateUsageIcon(CodexUsageSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        int primaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.PrimaryUsedPercent);
        int secondaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.SecondaryUsedPercent);

        return CreateIcon(
            primaryRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(primaryRemaining, palette),
            secondaryRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(secondaryRemaining, palette),
            OpenAiBrandColor);
    }

    public static string GetCodexIconKey(CodexUsageSnapshot snapshot)
    {
        int primaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.PrimaryUsedPercent);
        int secondaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.SecondaryUsedPercent);
        return $"codex:{primaryRemaining}:{secondaryRemaining}";
    }

    public static IntPtr CreateUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateIcon("?", palette.UnknownColor, "?", palette.UnknownColor, OpenAiBrandColor);
    }

    public static IntPtr CreateClaudeIcon(ClaudeUsageSnapshot snapshot)
    {
        IconPalette palette = GetPalette();
        int fiveHourRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent);
        int sevenDayRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent);

        return CreateIcon(
            fiveHourRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(fiveHourRemaining, palette),
            sevenDayRemaining.ToString(CultureInfo.InvariantCulture),
            ColorForRemaining(sevenDayRemaining, palette),
            ClaudeBrandColor);
    }

    public static string GetClaudeIconKey(ClaudeUsageSnapshot snapshot)
    {
        int fiveHourRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.FiveHourUsedPercent);
        int sevenDayRemaining = ClaudeUsageMath.GetRemainingPercent(snapshot.SevenDayUsedPercent);
        return $"claude:{fiveHourRemaining}:{sevenDayRemaining}";
    }

    public static IntPtr CreateClaudeUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateIcon("?", palette.UnknownColor, "?", palette.UnknownColor, ClaudeBrandColor);
    }

    private static IntPtr CreateIcon(
        string topText,
        uint topColor,
        string bottomText,
        uint bottomColor,
        uint brandMarkerColor)
    {
        uint[] pixels = new uint[IconSize * IconSize];
        DrawBrandTriangle(pixels, brandMarkerColor);
        DrawText(pixels, topText, 0, topColor);
        DrawText(pixels, bottomText, 8, bottomColor);
        return CreateNativeIcon(pixels);
    }

    private static void DrawBrandTriangle(uint[] pixels, uint color)
    {
        const int markerSize = 4;
        int startY = IconSize - markerSize;

        for (int y = startY; y < IconSize; y++)
        {
            int startX = IconSize - 1 - (y - startY);
            for (int x = startX; x < IconSize; x++)
            {
                pixels[(y * IconSize) + x] = color;
            }
        }
    }

    private static void DrawText(uint[] pixels, string text, int y, uint fillColor)
    {
        int width = (text.Length * GlyphWidth) + Math.Max(0, text.Length - 1) * GlyphSpacing;
        int startX = Math.Max(0, (IconSize - width) / 2);

        for (int index = 0; index < text.Length; index++)
        {
            int glyphX = startX + (index * (GlyphWidth + GlyphSpacing));
            DrawGlyph(pixels, text[index], glyphX, y, fillColor);
        }
    }

    private static void DrawGlyph(uint[] pixels, char value, int x, int y, uint color)
    {
        char glyphKey = Glyphs.ContainsKey(value) ? value : '?';
        string[] rows = Glyphs[glyphKey];

        for (int rowIndex = 0; rowIndex < GlyphHeight; rowIndex++)
        {
            string row = rows[rowIndex];
            for (int columnIndex = 0; columnIndex < GlyphWidth; columnIndex++)
            {
                if (row[columnIndex] != '1')
                {
                    continue;
                }

                int pixelX = x + columnIndex;
                int pixelY = y + rowIndex;
                if (pixelX < 0 || pixelX >= IconSize || pixelY < 0 || pixelY >= IconSize)
                {
                    continue;
                }

                pixels[(pixelY * IconSize) + pixelX] = color;
            }
        }
    }

    private static IntPtr CreateNativeIcon(uint[] pixels)
    {
        byte[] rawBytes = new byte[pixels.Length * sizeof(uint)];
        Buffer.BlockCopy(pixels, 0, rawBytes, 0, rawBytes.Length);

        NativeMethods.BITMAPINFO bitmapInfo = new()
        {
            bmiHeader = new NativeMethods.BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<NativeMethods.BITMAPINFOHEADER>(),
                biWidth = IconSize,
                biHeight = -IconSize,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = NativeMethods.BI_RGB
            }
        };

        IntPtr colorBitmap = NativeMethods.CreateDIBSection(
            IntPtr.Zero,
            ref bitmapInfo,
            NativeMethods.DIB_RGB_COLORS,
            out IntPtr pixelBuffer,
            IntPtr.Zero,
            0);

        if (colorBitmap == IntPtr.Zero || pixelBuffer == IntPtr.Zero)
        {
            return IntPtr.Zero;
        }

        Marshal.Copy(rawBytes, 0, pixelBuffer, rawBytes.Length);

        byte[] maskBytes = new byte[(IconSize * IconSize) / 8];
        GCHandle maskHandle = GCHandle.Alloc(maskBytes, GCHandleType.Pinned);
        IntPtr maskBitmap;
        try
        {
            maskBitmap = NativeMethods.CreateBitmap(IconSize, IconSize, 1, 1, maskHandle.AddrOfPinnedObject());
        }
        finally
        {
            maskHandle.Free();
        }

        if (maskBitmap == IntPtr.Zero)
        {
            NativeMethods.DeleteObject(colorBitmap);
            return IntPtr.Zero;
        }

        NativeMethods.ICONINFO iconInfo = new()
        {
            fIcon = true,
            hbmColor = colorBitmap,
            hbmMask = maskBitmap
        };

        IntPtr iconHandle = NativeMethods.CreateIconIndirect(ref iconInfo);
        NativeMethods.DeleteObject(colorBitmap);
        NativeMethods.DeleteObject(maskBitmap);
        return iconHandle;
    }

    private static uint ColorForRemaining(int remainingPercent, IconPalette palette)
    {
        if (remainingPercent <= 15)
        {
            return palette.DangerColor;
        }

        if (remainingPercent <= 40)
        {
            return palette.WarningColor;
        }

        return palette.SafeColor;
    }

    private static IconPalette GetPalette()
    {
        return IsLightTaskbarTheme() ? LightThemePalette : DarkThemePalette;
    }

    private static bool IsLightTaskbarTheme()
    {
        const string personalizeKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize";

        try
        {
            using RegistryKey? personalizeKey = Registry.CurrentUser.OpenSubKey(personalizeKeyPath, writable: false);
            object? value = personalizeKey?.GetValue("SystemUsesLightTheme");
            return value switch
            {
                int intValue => intValue != 0,
                byte byteValue => byteValue != 0,
                _ => false
            };
        }
        catch
        {
            return false;
        }
    }

    private sealed record IconPalette(uint UnknownColor, uint DangerColor, uint WarningColor, uint SafeColor);
}

internal static class NativeMethods
{
    public static readonly IntPtr HWND_MESSAGE = new(-3);

    public const uint WM_NULL = 0x0000;
    public const uint WM_DESTROY = 0x0002;
    public const uint WM_CONTEXTMENU = 0x007B;
    public const uint WM_TIMER = 0x0113;
    public const uint WM_LBUTTONDBLCLK = 0x0203;
    public const uint WM_RBUTTONUP = 0x0205;
    public const uint WM_APP = 0x8000;

    public const uint NIM_ADD = 0x00000000;
    public const uint NIM_MODIFY = 0x00000001;
    public const uint NIM_DELETE = 0x00000002;

    public const uint NIF_MESSAGE = 0x00000001;
    public const uint NIF_ICON = 0x00000002;
    public const uint NIF_TIP = 0x00000004;

    public const uint MF_STRING = 0x00000000;
    public const uint MF_GRAYED = 0x00000001;
    public const uint MF_SEPARATOR = 0x00000800;

    public const uint TPM_NONOTIFY = 0x0080;
    public const uint TPM_RETURNCMD = 0x0100;
    public const uint TPM_RIGHTBUTTON = 0x0002;

    public const uint BI_RGB = 0;
    public const uint DIB_RGB_COLORS = 0;

    public delegate IntPtr WndProcDelegate(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct WNDCLASSEX
    {
        public uint cbSize;
        public uint style;
        public IntPtr lpfnWndProc;
        public int cbClsExtra;
        public int cbWndExtra;
        public IntPtr hInstance;
        public IntPtr hIcon;
        public IntPtr hCursor;
        public IntPtr hbrBackground;
        public string? lpszMenuName;
        public string? lpszClassName;
        public IntPtr hIconSm;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT
    {
        public int X;
        public int Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
        public uint lPrivate;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NOTIFYICONDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uID;
        public uint uFlags;
        public uint uCallbackMessage;
        public IntPtr hIcon;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string szTip;

        public uint dwState;
        public uint dwStateMask;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        public string szInfo;

        public uint uVersionOrTimeout;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)]
        public string szInfoTitle;

        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct BITMAPINFO
    {
        public BITMAPINFOHEADER bmiHeader;
        public uint bmiColors;
    }

    [StructLayout(LayoutKind.Sequential)]
    internal struct ICONINFO
    {
        [MarshalAs(UnmanagedType.Bool)]
        public bool fIcon;

        public uint xHotspot;
        public uint yHotspot;
        public IntPtr hbmMask;
        public IntPtr hbmColor;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr GetModuleHandle(string? moduleName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern ushort RegisterClassEx(ref WNDCLASSEX windowClass);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern bool UnregisterClass(string className, IntPtr instanceHandle);

    [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    public static extern IntPtr CreateWindowEx(
        uint extendedStyle,
        string className,
        string windowName,
        uint style,
        int x,
        int y,
        int width,
        int height,
        IntPtr parentHandle,
        IntPtr menuHandle,
        IntPtr instanceHandle,
        IntPtr parameter);

    [DllImport("user32.dll")]
    public static extern IntPtr DefWindowProc(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern int GetMessage(out MSG message, IntPtr windowHandle, uint minimumMessage, uint maximumMessage);

    [DllImport("user32.dll")]
    public static extern bool TranslateMessage(ref MSG message);

    [DllImport("user32.dll")]
    public static extern IntPtr DispatchMessage(ref MSG message);

    [DllImport("user32.dll")]
    public static extern void PostQuitMessage(int exitCode);

    [DllImport("user32.dll")]
    public static extern nuint SetTimer(IntPtr windowHandle, nuint timerId, uint intervalMilliseconds, IntPtr timerFunction);

    [DllImport("user32.dll")]
    public static extern bool KillTimer(IntPtr windowHandle, nuint timerId);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool DestroyWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern bool PostMessage(IntPtr windowHandle, uint message, IntPtr wParam, IntPtr lParam);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool Shell_NotifyIcon(uint message, ref NOTIFYICONDATA data);

    [DllImport("user32.dll")]
    public static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool AppendMenu(IntPtr menuHandle, uint flags, nuint itemId, string? text);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyMenu(IntPtr menuHandle);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool GetCursorPos(out POINT point);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetForegroundWindow(IntPtr windowHandle);

    [DllImport("user32.dll")]
    public static extern uint TrackPopupMenu(
        IntPtr menuHandle,
        uint flags,
        int x,
        int y,
        int reserved,
        IntPtr windowHandle,
        IntPtr rectangle);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateDIBSection(
        IntPtr deviceContext,
        ref BITMAPINFO bitmapInfo,
        uint usage,
        out IntPtr bits,
        IntPtr section,
        uint offset);

    [DllImport("gdi32.dll", SetLastError = true)]
    public static extern IntPtr CreateBitmap(int width, int height, uint planes, uint bitsPerPixel, IntPtr bits);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern IntPtr CreateIconIndirect(ref ICONINFO iconInfo);

    [DllImport("gdi32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DeleteObject(IntPtr objectHandle);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool DestroyIcon(IntPtr iconHandle);
}
