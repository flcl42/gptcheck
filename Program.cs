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
    private const uint TrayIconId = 1;
    private const uint TrayCallbackMessage = NativeMethods.WM_APP + 1;
    private const nuint RefreshTimerId = 1;
    private const uint RefreshIntervalMs = 300_000;
    private const uint CommandRefresh = 1001;
    private const uint CommandOpenSessions = 1002;
    private const uint CommandExit = 1003;

    private static readonly NativeMethods.WndProcDelegate WindowProcedure = HandleWindowMessage;
    private static TrayApplication? Current;

    private readonly CodexUsageReader _usageReader = new();
    private readonly string _windowClassName = $"gptcheck.{Environment.ProcessId}";

    private IntPtr _windowHandle;
    private IntPtr _iconHandle;
    private bool _trayIconAdded;
    private bool _windowClassRegistered;
    private string _tooltip = "gptcheck";
    private string _statusText = "Loading Codex usage...";
    private string _detailText = "Reading local Codex sessions.";
    private string _updatedText = string.Empty;
    private string _sourceText = string.Empty;

    public int Run()
    {
        Current = this;
        RegisterWindowClass();
        CreateMessageWindow();
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
            IntPtr.Zero,
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
        UsageReadResult result = _usageReader.ReadLatestSnapshot();

        if (result.Snapshot is null)
        {
            _tooltip = "gptcheck unavailable";
            _statusText = "No Codex usage data found";
            _detailText = result.ErrorMessage ?? "No token_count events were found.";
            _updatedText = $"Checked {DateTimeOffset.Now:HH:mm:ss}";
            _sourceText = _usageReader.SessionsPath;
            UpdateTrayIcon(TrayIconRenderer.CreateUnavailableIcon());
            return;
        }

        CodexUsageSnapshot snapshot = result.Snapshot;
        _tooltip = BuildTooltip(snapshot);
        _statusText = BuildHeadline(snapshot);
        _detailText = BuildDetail(snapshot);
        _updatedText = $"Seen {snapshot.Timestamp.ToLocalTime():yyyy-MM-dd HH:mm:ss}";
        _sourceText = snapshot.SourceFile;
        UpdateTrayIcon(TrayIconRenderer.CreateUsageIcon(snapshot));
    }

    private void UpdateTrayIcon(IntPtr newIconHandle)
    {
        if (newIconHandle == IntPtr.Zero)
        {
            return;
        }

        IntPtr previousIconHandle = _iconHandle;
        _iconHandle = newIconHandle;

        NativeMethods.NOTIFYICONDATA data = CreateNotifyIconData();
        if (_trayIconAdded)
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_MODIFY, ref data);
        }
        else
        {
            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_ADD, ref data);
            _trayIconAdded = true;
        }

        if (previousIconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(previousIconHandle);
        }
    }

    private NativeMethods.NOTIFYICONDATA CreateNotifyIconData()
    {
        return new NativeMethods.NOTIFYICONDATA
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
            hWnd = _windowHandle,
            uID = TrayIconId,
            uFlags = NativeMethods.NIF_MESSAGE | NativeMethods.NIF_ICON | NativeMethods.NIF_TIP,
            uCallbackMessage = TrayCallbackMessage,
            hIcon = _iconHandle,
            szTip = TruncateTooltip(_tooltip),
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

            case TrayCallbackMessage:
                switch ((uint)lParam.ToInt64())
                {
                    case NativeMethods.WM_LBUTTONDBLCLK:
                        RefreshUsage();
                        return IntPtr.Zero;

                    case NativeMethods.WM_RBUTTONUP:
                    case NativeMethods.WM_CONTEXTMENU:
                        ShowContextMenu();
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

    private void ShowContextMenu()
    {
        IntPtr menuHandle = NativeMethods.CreatePopupMenu();
        if (menuHandle == IntPtr.Zero)
        {
            return;
        }

        try
        {
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(_statusText));
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(_detailText));
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(_updatedText));
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING | NativeMethods.MF_GRAYED, 0, LimitMenuText(_sourceText));
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_SEPARATOR, 0, null);
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, CommandRefresh, "Refresh now");
            NativeMethods.AppendMenu(menuHandle, NativeMethods.MF_STRING, CommandOpenSessions, "Open Codex sessions");
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

            case CommandOpenSessions:
                OpenSessionsFolder();
                break;

            case CommandExit:
                NativeMethods.DestroyWindow(_windowHandle);
                break;
        }
    }

    private void OpenSessionsFolder()
    {
        string target = Directory.Exists(_usageReader.SessionsPath)
            ? _usageReader.SessionsPath
            : _usageReader.CodexHomePath;

        Process.Start(new ProcessStartInfo
        {
            FileName = target,
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
        if (_trayIconAdded && _windowHandle != IntPtr.Zero)
        {
            NativeMethods.NOTIFYICONDATA data = new()
            {
                cbSize = (uint)Marshal.SizeOf<NativeMethods.NOTIFYICONDATA>(),
                hWnd = _windowHandle,
                uID = TrayIconId,
                szTip = string.Empty,
                szInfo = string.Empty,
                szInfoTitle = string.Empty
            };

            NativeMethods.Shell_NotifyIcon(NativeMethods.NIM_DELETE, ref data);
            _trayIconAdded = false;
        }

        if (_windowHandle != IntPtr.Zero)
        {
            NativeMethods.KillTimer(_windowHandle, RefreshTimerId);
        }

        if (_iconHandle != IntPtr.Zero)
        {
            NativeMethods.DestroyIcon(_iconHandle);
            _iconHandle = IntPtr.Zero;
        }

        if (_windowClassRegistered)
        {
            NativeMethods.UnregisterClass(_windowClassName, NativeMethods.GetModuleHandle(null));
            _windowClassRegistered = false;
        }
    }

    private static string BuildTooltip(CodexUsageSnapshot snapshot)
    {
        int primaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.PrimaryUsedPercent);
        int secondaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.SecondaryUsedPercent);

        return TruncateTooltip(
            $"gptcheck {FormatWindow(snapshot.PrimaryWindowMinutes)} left {primaryRemaining}% " +
            $"{FormatWindow(snapshot.SecondaryWindowMinutes)} left {secondaryRemaining}%");
    }

    private static string BuildHeadline(CodexUsageSnapshot snapshot)
    {
        string planType = string.IsNullOrWhiteSpace(snapshot.PlanType) ? "plan ?" : snapshot.PlanType;
        int primaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.PrimaryUsedPercent);
        int secondaryRemaining = CodexUsageMath.GetRemainingPercent(snapshot.SecondaryUsedPercent);

        return $"{planType}: {FormatWindow(snapshot.PrimaryWindowMinutes)} left {primaryRemaining}% | " +
               $"{FormatWindow(snapshot.SecondaryWindowMinutes)} left {secondaryRemaining}%";
    }

    private static string BuildDetail(CodexUsageSnapshot snapshot)
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

internal sealed record CodexUsageSnapshot(
    DateTimeOffset Timestamp,
    double PrimaryUsedPercent,
    double SecondaryUsedPercent,
    int PrimaryWindowMinutes,
    int SecondaryWindowMinutes,
    DateTimeOffset? PrimaryResetAt,
    DateTimeOffset? SecondaryResetAt,
    string? PlanType,
    string SourceFile);

internal sealed record UsageReadResult(CodexUsageSnapshot? Snapshot, string? ErrorMessage);

internal sealed class CodexUsageReader
{
    private const int MaxFilesToScan = 32;

    public string CodexHomePath { get; } = ResolveCodexHome();
    public string SessionsPath => Path.Combine(CodexHomePath, "sessions");

    public UsageReadResult ReadLatestSnapshot()
    {
        if (!Directory.Exists(SessionsPath))
        {
            return new UsageReadResult(null, $"Missing sessions folder: {SessionsPath}");
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

            foreach (string file in recentFiles)
            {
                CodexUsageSnapshot? candidate = TryReadFile(file);
                if (candidate is null)
                {
                    continue;
                }

                if (latestSnapshot is null || candidate.Timestamp > latestSnapshot.Timestamp)
                {
                    latestSnapshot = candidate;
                }
            }

            return latestSnapshot is null
                ? new UsageReadResult(null, "No token_count events were found in recent sessions.")
                : new UsageReadResult(latestSnapshot, null);
        }
        catch (Exception exception)
        {
            return new UsageReadResult(null, exception.Message);
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

    private static CodexUsageSnapshot? TryReadFile(string path)
    {
        CodexUsageSnapshot? latest = null;

        using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using StreamReader reader = new(stream);

        while (reader.ReadLine() is { } line)
        {
            if (!line.Contains("\"type\":\"token_count\"", StringComparison.Ordinal))
            {
                continue;
            }

            CodexUsageSnapshot? parsed = TryParseLine(line, path);
            if (parsed is null)
            {
                continue;
            }

            if (latest is null || parsed.Timestamp > latest.Timestamp)
            {
                latest = parsed;
            }
        }

        return latest;
    }

    private static CodexUsageSnapshot? TryParseLine(string line, string sourceFile)
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

    private sealed record RateLimitInfo(double UsedPercent, int WindowMinutes, DateTimeOffset? ResetsAt);
}

internal static class TrayIconRenderer
{
    private const int IconSize = 16;
    private const int GlyphWidth = 4;
    private const int GlyphHeight = 7;
    private const int GlyphSpacing = 1;
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
            ColorForRemaining(secondaryRemaining, palette));
    }

    public static IntPtr CreateUnavailableIcon()
    {
        IconPalette palette = GetPalette();
        return CreateIcon("?", palette.UnknownColor, "?", palette.UnknownColor);
    }

    private static IntPtr CreateIcon(string topText, uint topColor, string bottomText, uint bottomColor)
    {
        uint[] pixels = new uint[IconSize * IconSize];
        DrawText(pixels, topText, 0, topColor);
        DrawText(pixels, bottomText, 8, bottomColor);
        return CreateNativeIcon(pixels);
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
