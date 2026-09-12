using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Threading;

namespace Ferry;

internal sealed record MainWindowLaunchOptions
{
    public static readonly MainWindowLaunchOptions Default = new();

    public DeviceIdentity? Device { get; init; }
    public string? DraftPath { get; init; }
    public string? LastReadPath { get; init; }
    public string? BaseAddress { get; init; }
    public bool EnableTrayAndHotkeys { get; init; } = true;
    public bool EnableAutoBoot { get; init; } = true;
    public bool EnableDraftPersistence { get; init; } = true;
    public bool EnableChromeEffects { get; init; } = true;
}

public partial class MainWindow : Window
{
    internal const bool UseMicaBackdrop = false;
    private readonly DeviceIdentity _device;
    private readonly ObservableCollection<object> _thread = new();
    private readonly CancellationTokenSource _cts = new();
    private CancellationTokenSource? _transferCts;
    private FerryClient _client = null!;

    private readonly System.Windows.Threading.DispatcherTimer _draftDebounce = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private readonly string _draftPath;
    private int _lastReadId;
    private readonly string _lastReadPath;
    private readonly bool _enableTrayAndHotkeys;
    private readonly bool _enableAutoBoot;
    private readonly bool _enableDraftPersistence;
    private readonly bool _enableChromeEffects;

    private readonly ObservableCollection<FerryMessage> _pinnedMessages = new();
    private bool _pinsExpanded;
    private SettingsWindow? _settingsWindow;
    private MediaWindow? _mediaWindow;
    private Point _dragStartPoint;
    private FerryMessage? _dragCandidate;
    private IReadOnlyList<FerryMessage> _allMessages = Array.Empty<FerryMessage>();
    private string _filterTerm = string.Empty;

    public MainWindow() : this(null)
    {
    }

    internal MainWindow(MainWindowLaunchOptions? options)
    {
        options ??= MainWindowLaunchOptions.Default;
        _device = options.Device ?? DeviceIdentity.LoadOrCreate();
        _draftPath = options.DraftPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ferry", "draft.txt");
        _lastReadPath = options.LastReadPath ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Ferry", "last_read.txt");
        _enableTrayAndHotkeys = options.EnableTrayAndHotkeys;
        _enableAutoBoot = options.EnableAutoBoot;
        _enableDraftPersistence = options.EnableDraftPersistence;
        _enableChromeEffects = options.EnableChromeEffects;

        InitializeComponent();

        if (_enableDraftPersistence)
        {
            LoadLastReadId();
        }

        var baseAddress = options.BaseAddress ?? FerryEndpoint.BaseAddress;
        _client = new FerryClient(App.Log, baseAddress)
        {
            SenderId = _device.Id,
            SenderName = _device.Name,
        };
        ThreadList.ItemsSource = _thread;
        PinnedItemsList.ItemsSource = _pinnedMessages;

        if (_enableDraftPersistence)
        {
            SetUpDraftPersistence();
        }

        // Chrome first: the HWND does not exist until SourceInitialized, and
        // DWM needs one. App.ThemeChanged re-runs it because the caption
        // colour and the border live outside WPF and do not follow a palette
        // swap on their own.
        SourceInitialized += (_, _) =>
        {
            if (_enableChromeEffects)
            {
                ApplyChrome();
            }
            // Both need the HWND: RegisterHotKey hangs the hotkeys off it, and
            // the tray icon is only worth creating if the window can be hidden.
            if (_enableTrayAndHotkeys)
            {
                SetUpTrayAndHotkeys();
            }
        };

        if (_enableChromeEffects)
        {
            App.ThemeChanged += ApplyChrome;
        }

        Closing += MainWindow_Closing;
        StateChanged += (_, _) => UpdateMaxGlyph();
        MinButton.Click += (_, _) => WindowState = WindowState.Minimized;
        MaxButton.Click += (_, _) => WindowState =
            WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
        CloseButton.Click += (_, _) => Close();

        if (_enableAutoBoot)
        {
            Loaded += async (_, _) => await BootAsync();
        }

        Closed += (_, _) =>
        {
            if (_enableDraftPersistence)
            {
                SaveDraftNow();
            }
            if (_enableChromeEffects)
            {
                App.ThemeChanged -= ApplyChrome;
            }
            // Unregister before the HWND goes: hotkeys outlive a leaked window
            // and the tray icon outlives the process that drew it.
            _hotkeys?.Dispose();
            _tray?.Dispose();
            _cts.Cancel();
            _client.Dispose();
        };
        Composer.TextChanged += (_, _) => SendButton.IsEnabled = !string.IsNullOrWhiteSpace(Composer.Text);
        SendButton.Click += async (_, _) => await SendAsync();
        ConnectButton.Click += (_, _) => new ConnectWindow { Owner = this }.ShowDialog();
        SettingsButton.Click += (_, _) => OpenSettings();
        // The full thread, not the filtered view: the gallery is the way back
        // to old pictures, so a filter left in the box must not hide them.
        MediaButton.Click += (_, _) => OpenMedia();
        PinnedToggle.Click += (_, _) => TogglePinned();
        FilterBox.TextChanged += (_, _) =>
        {
            _filterTerm = FilterBox.Text;
            ApplyThread(FilterMessages(_allMessages));
        };
        FilterBox.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Escape)
            {
                e.Handled = true;
                HideFilter();
            }
        };
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.F &&
                System.Windows.Input.Keyboard.Modifiers == System.Windows.Input.ModifierKeys.Control)
            {
                e.Handled = true;
                ShowFilter();
            }
            else if (e.Key == System.Windows.Input.Key.Escape && FilterBox.Visibility == Visibility.Visible)
            {
                e.Handled = true;
                HideFilter();
            }
        };
        ThreadList.KeyDown += (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.C &&
                System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control))
                CopySelectedText();
            else if (e.Key == System.Windows.Input.Key.Delete)
            {
                e.Handled = true;
                _ = DeleteSelectedAsync();
            }
        };
        ThreadList.SelectionChanged += (_, _) => UpdateSelectionBar();
        ThreadList.PreviewMouseRightButtonDown += ThreadList_PreviewRightDown;
        ThreadList.PreviewMouseLeftButtonDown += ThreadList_PreviewMouseLeftButtonDown;
        ThreadList.MouseMove += ThreadList_MouseMove;
        ThreadList.ContextMenuOpening += (_, e) =>
        {
            // Nothing to act on: suppress the menu rather than show three
            // greyed-out rows.
            var selectedMsgs = ThreadList.SelectedItems.OfType<FerryMessage>().ToList();
            if (selectedMsgs.Count == 0) { e.Handled = true; return; }
            CopyMenuItem.IsEnabled = SelectedTexts().Any();
            var first = selectedMsgs[0];
            PinMenuItem.Header = first.PinnedAt.HasValue ? "Unpin" : "Pin";
        };
        PinMenuItem.Click += async (_, _) => await TogglePinSelectedAsync();
        CopyMenuItem.Click += (_, _) => CopySelectedText();
        DeleteMenuItem.Click += async (_, _) => await DeleteSelectedAsync();
        SelectionCopyButton.Click += (_, _) => CopySelectedText();
        SelectionDeleteButton.Click += async (_, _) => await DeleteSelectedAsync();
        SelectionClearButton.Click += (_, _) => ThreadList.SelectedItems.Clear();
        Composer.PreviewKeyDown += async (_, e) =>
        {
            if (e.Key == System.Windows.Input.Key.Enter)
            {
                if (System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Shift))
                    return; // Shift+Enter inserts newline

                e.Handled = true;
                if (!string.IsNullOrWhiteSpace(Composer.Text))
                {
                    await SendAsync();
                }
                return;
            }
            if (e.Key == System.Windows.Input.Key.V &&
                System.Windows.Input.Keyboard.Modifiers.HasFlag(System.Windows.Input.ModifierKeys.Control) &&
                await PasteNonTextAsync())
            {
                e.Handled = true;
            }
        };
        AttachButton.Click += async (_, _) => await PickAndUploadAsync();
        CancelTransferButton.Click += (_, _) => _transferCts?.Cancel();
        RetryButton.Click += async (_, _) =>
        {
            if (_failedUpload is not null) await UploadOneAsync(_failedUpload);
            else if (_failedSave is { } save)
            {
                _failedSave = null;
                RetryButton.Visibility = Visibility.Collapsed;
                SetTransferProgress(0);
                UploadRow.Visibility = Visibility.Visible;
                UploadLabel.Text = "Retrying save…";
                UploadQueueText.Visibility = Visibility.Collapsed;
                using var transferCts = BeginTransfer();
                var progress = new Progress<double>(SetTransferProgress);
                try
                {
                    await _client.DownloadToAsync(save.Id, save.Path, progress, transferCts.Token);
                    UploadRow.Visibility = Visibility.Collapsed;
                }
                catch (TaskCanceledException) when (transferCts.IsCancellationRequested && !_cts.IsCancellationRequested)
                {
                    UploadRow.Visibility = Visibility.Collapsed;
                }
                catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
                {
                    _failedSave = save;
                    UploadLabel.Text = "Couldn't save · Retry available";
                    RetryButton.Visibility = Visibility.Visible;
                }
                finally { EndTransfer(transferCts); }
            }
        };
    }

    private void SetUpDraftPersistence()
    {
        try
        {
            if (File.Exists(_draftPath))
            {
                var text = File.ReadAllText(_draftPath);
                if (!string.IsNullOrEmpty(text))
                {
                    Composer.Text = text;
                    Composer.CaretIndex = Composer.Text.Length;
                }
            }
        }
        catch (IOException) { }

        _draftDebounce.Tick += (_, _) =>
        {
            _draftDebounce.Stop();
            SaveDraftNow();
        };
        Composer.TextChanged += (_, _) =>
        {
            _draftDebounce.Stop();
            _draftDebounce.Start();
        };
    }

    private void SaveDraftNow()
    {
        try
        {
            var text = Composer.Text;
            var dir = Path.GetDirectoryName(_draftPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            if (string.IsNullOrEmpty(text))
            {
                if (File.Exists(_draftPath)) File.Delete(_draftPath);
            }
            else
            {
                File.WriteAllText(_draftPath, text);
            }
        }
        catch (IOException) { }
    }

    private void ClearDraft()
    {
        _draftDebounce.Stop();
        try
        {
            if (File.Exists(_draftPath)) File.Delete(_draftPath);
        }
        catch (IOException) { }
    }

    private void LoadLastReadId()
    {
        try
        {
            if (File.Exists(_lastReadPath) && int.TryParse(File.ReadAllText(_lastReadPath).Trim(), out var id))
            {
                _lastReadId = id;
            }
        }
        catch (IOException) { }
    }

    private void SaveLastReadId(int id)
    {
        if (id <= _lastReadId) return;
        _lastReadId = id;
        try
        {
            var dir = Path.GetDirectoryName(_lastReadPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_lastReadPath, id.ToString());
        }
        catch (IOException) { }
    }

    // A fresh database restarts ids at 1. A stored read position from the previous
    // database is then above every id in the thread, so nothing would ever read as
    // new. Treat "newest id is below what we have read" as a reset.
    private void ResetLastReadIfThreadRewound(int newestId)
    {
        if (_lastReadId <= newestId) return;
        _lastReadId = 0;
        try
        {
            var dir = Path.GetDirectoryName(_lastReadPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            File.WriteAllText(_lastReadPath, "0");
        }
        catch (IOException) { }
    }

    // Everything about the frame that WPF cannot express. Safe to call more
    // than once; it is re-run on every theme change.
    private void ApplyChrome()
    {
        if (new System.Windows.Interop.WindowInteropHelper(this).Handle == IntPtr.Zero) return;
        WindowEffects.Apply(this, App.IsDark, useMica: UseMicaBackdrop);
        if (System.Windows.Shell.WindowChrome.GetWindowChrome(this) is { } chrome)
            chrome.GlassFrameThickness = new Thickness(0);
        // Border.BackgroundProperty, not the Control.BackgroundProperty that
        // this Window would otherwise resolve: they are different properties.
        Backdrop.SetResourceReference(Border.BackgroundProperty, "Bg");
    }

    private HotkeyManager? _hotkeys;
    private TrayIcon? _tray;
    private bool _exiting;
    private bool _saidWhereItWent;
    private readonly List<FerryMessage> _notifyBatch = [];
    private DateTime _notifyBatchStarted;
    private DispatcherTimer? _notifyTimer;
    private bool _notifyTimerHooked;

    private const System.Windows.Input.ModifierKeys SummonMods =
        System.Windows.Input.ModifierKeys.Control | System.Windows.Input.ModifierKeys.Alt;
    private const System.Windows.Input.ModifierKeys SendMods =
        SummonMods | System.Windows.Input.ModifierKeys.Shift;
    private const System.Windows.Input.Key HotkeyKey = System.Windows.Input.Key.F;

    private void SetUpTrayAndHotkeys()
    {
        _tray = new TrayIcon("Ferry", Summon, () => _ = SendClipboardAsync(), ExitApp);
        if (_tray.Ok)
            CloseButton.ToolTip =
                $"Hide · {HotkeyManager.Describe(SummonMods, HotkeyKey)} to restore";

        _hotkeys = new HotkeyManager(this);
        var summon = _hotkeys.TryRegister(SummonMods, HotkeyKey, Summon);
        var send = _hotkeys.TryRegister(SendMods, HotkeyKey, () => _ = SendClipboardAsync());
        // Another app holding a combination is normal and permanent for the
        // session; say which one went missing rather than failing silently.
        if (!summon || !send)
        {
            var taken = !summon
                ? HotkeyManager.Describe(SummonMods, HotkeyKey)
                : HotkeyManager.Describe(SendMods, HotkeyKey);
            FlashStatus($"Hotkey {taken} is held by another app.");
        }
    }

    // Ctrl+Alt+F: bring Ferry to the front, or put it away if it is already
    // there. One key for both directions beats two to remember.
    private void Summon()
    {
        if (IsVisible && IsActive)
        {
            Close();
            return;
        }
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        Activate();
        // Windows only grants foreground rights to the thread that owns the
        // hotkey for an instant; the Topmost flip makes the window surface even
        // when Activate alone would just flash the taskbar button.
        Topmost = true;
        Topmost = false;
        Composer.Focus();
    }

    // Ctrl+Alt+Shift+F: whatever is on the clipboard goes to the phone without
    // the window ever appearing. Files and images take the upload path that
    // Ctrl+V in the composer already uses.
    private async Task SendClipboardAsync()
    {
        try
        {
            if (await PasteNonTextAsync())
            {
                _tray?.Notify("Sent to Ferry", "The clipboard's files are on their way.");
                return;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _tray?.Notify("Send failed", "Ferry could not reach the server on this laptop.");
            return;
        }

        var text = TryGetClipboardText()?.Trim();
        if (string.IsNullOrEmpty(text))
        {
            _tray?.Notify("Nothing to send", "There is no text or file on the clipboard.");
            return;
        }
        try
        {
            await _client.SendMessageAsync(text, _device.Id, _device.Name, _cts.Token);
            await RefreshAsync();
            _tray?.Notify("Sent to Ferry", Preview(text));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _tray?.Notify("Send failed", "Ferry could not reach the server on this laptop.");
        }
    }

    // The clipboard is a shared, single-owner resource: another app holding it
    // open makes this throw, and the answer is to wait a moment and ask again.
    private static bool TrySetClipboardText(string text)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                Clipboard.SetText(text);
                return true;
            }
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(60);
            }
        }
        App.Log("clipboard unwritable");
        return false;
    }

    private static string? TryGetClipboardText()
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try { return Clipboard.ContainsText() ? Clipboard.GetText() : null; }
            // COMException derives from this one, and WPF throws both.
            catch (System.Runtime.InteropServices.ExternalException)
            {
                Thread.Sleep(60);
            }
        }
        App.Log("clipboard unreadable");
        return null;
    }

    private static string Preview(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 70 ? line : line[..69] + "…";
    }

    private void ExitApp()
    {
        _exiting = true;
        Close();
    }

    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        // Closing puts Ferry in the tray instead of ending it, so the hotkey
        // still answers. Without a tray icon there would be no way back and no
        // way out, so then let the close through and end the app properly.
        if (_exiting || _tray is not { Ok: true }) return;
        e.Cancel = true;
        Hide();
        if (_saidWhereItWent) return;
        _saidWhereItWent = true;
        _tray.Notify("Ferry is still running",
            $"{HotkeyManager.Describe(SummonMods, HotkeyKey)} opens it. Exit from the tray.");
    }

    // Restore / maximize glyphs, Segoe Fluent Icons.
    private void UpdateMaxGlyph() =>
        MaxButton.Content = WindowState == WindowState.Maximized ? "\uE923" : "\uE922";

    private void SetUpNotifications() => ToastRegistration.Ensure();

    // A phone sending twenty photos uploads them one at a time, so twenty
    // separate messages arrive over half a minute. Collect them and toast once
    // the arrivals stop, rather than once per file.
    private static readonly TimeSpan NotifyQuietPeriod = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan NotifyMaxHold = TimeSpan.FromSeconds(15);

    private void NotifyInboundMessage(FerryMessage m)
    {
        // Fire only when the message is not ours and the window is not active
        if (m.SenderId == _device?.Id || IsActive) return;

        if (_notifyBatch.Count == 0) _notifyBatchStarted = DateTime.UtcNow;
        _notifyBatch.Add(m);

        // A steady stream would otherwise defer the toast forever, so cap the
        // hold and let a long burst report itself in instalments.
        if (DateTime.UtcNow - _notifyBatchStarted >= NotifyMaxHold)
        {
            FlushNotifyBatch();
            return;
        }

        _notifyTimer ??= new DispatcherTimer { Interval = NotifyQuietPeriod };
        if (!_notifyTimerHooked)
        {
            _notifyTimer.Tick += (_, _) => FlushNotifyBatch();
            _notifyTimerHooked = true;
        }
        _notifyTimer.Stop();
        _notifyTimer.Start();
    }

    private void FlushNotifyBatch()
    {
        _notifyTimer?.Stop();
        if (_notifyBatch.Count == 0) return;
        var batch = _notifyBatch.ToList();
        _notifyBatch.Clear();

        // He may have opened Ferry while the batch was still filling.
        if (IsActive) return;

        var senders = batch.Select(x => x.DisplayName).Distinct().ToList();
        var title = senders.Count == 1 ? $"{senders[0]} on Ferry" : "Ferry";

        if (batch.Count == 1)
        {
            var only = batch[0];
            ShowInboundToast(title, only.IsText ? (only.Text ?? "") : (only.Filename ?? "sent a file"), copyable: true);
            return;
        }
        ShowInboundToast(title, SummarizeBatch(batch), copyable: false);
    }

    internal static string SummarizeBatch(IReadOnlyList<FerryMessage> batch)
    {
        var photos = batch.Count(x => x.IsImage);
        var texts = batch.Count(x => x.IsText);
        var files = batch.Count - photos - texts;

        var parts = new List<string>();
        if (photos > 0) parts.Add(photos == 1 ? "1 photo" : $"{photos} photos");
        if (files > 0) parts.Add(files == 1 ? "1 file" : $"{files} files");
        if (texts > 0) parts.Add(texts == 1 ? "1 message" : $"{texts} messages");

        return parts.Count switch
        {
            0 => $"{batch.Count} items",
            1 => parts[0],
            2 => $"{parts[0]} and {parts[1]}",
            _ => $"{string.Join(", ", parts.Take(parts.Count - 1))} and {parts[^1]}",
        };
    }

    private void ShowInboundToast(string title, string content, bool copyable)
    {
        // Windows drops a toast from an unregistered app id without raising
        // anything, so the choice has to be made before Show(), not in a catch.
        if (!ToastRegistration.Ready)
        {
            _tray?.Notify(title, Preview(content));
            return;
        }

        try
        {
            var copyAction = copyable
                ? $@"<action content=""Copy"" arguments=""action=copy&amp;content={System.Security.SecurityElement.Escape(content)}"" activationType=""background"" />"
                : "";
            var xml = $@"
<toast launch=""action=show"">
    <visual>
        <binding template=""ToastGeneric"">
            <text>{System.Security.SecurityElement.Escape(title)}</text>
            <text>{System.Security.SecurityElement.Escape(Preview(content))}</text>
        </binding>
    </visual>
    <actions>
        {copyAction}
        <action content=""Show Ferry"" arguments=""action=show"" activationType=""foreground"" />
    </actions>
</toast>";
            var doc = new Windows.Data.Xml.Dom.XmlDocument();
            doc.LoadXml(xml);
            // Same tag and group, so a later arrival replaces the toast in
            // Action Center instead of stacking another card on top of it.
            var toast = new Windows.UI.Notifications.ToastNotification(doc)
            {
                Tag = "inbound",
                Group = "ferry",
            };
            toast.Activated += (t, args) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (args is Windows.UI.Notifications.ToastActivatedEventArgs activatedArgs)
                    {
                        var argStr = activatedArgs.Arguments ?? "";
                        if (argStr.Contains("action=copy"))
                        {
                            TrySetClipboardText(content);
                            FlashStatus("Copied.");
                        }
                        else
                        {
                            Summon();
                        }
                    }
                    else
                    {
                        Summon();
                    }
                });
            };

            var notifier = Windows.UI.Notifications.ToastNotificationManager
                .CreateToastNotifier(ToastRegistration.Aumid);
            notifier.Show(toast);
        }
        catch (Exception ex)
        {
            App.Log($"Toast failed, falling back to tray balloon: {ex.Message}");
            _tray?.Notify(title, Preview(content));
        }
    }

    private async Task BootAsync()
    {
        SetUpNotifications();
        if (!string.IsNullOrEmpty(App.StartupWarning))
        {
            FlashStatus(App.StartupWarning);
        }
        await RefreshAsync();
        await RefreshPinsAsync();
        _ = WatchLoopAsync();
    }

    private void ShowFilter()
    {
        TitleAndStatusPanel.Visibility = Visibility.Collapsed;
        FilterBox.Visibility = Visibility.Visible;
        FilterBox.Focus();
        FilterBox.SelectAll();
    }

    private void HideFilter()
    {
        FilterBox.Text = string.Empty;
        FilterBox.Visibility = Visibility.Collapsed;
        TitleAndStatusPanel.Visibility = Visibility.Visible;
        _filterTerm = string.Empty;
        ApplyThread(FilterMessages(_allMessages));
        Composer.Focus();
    }

    private IReadOnlyList<FerryMessage> FilterMessages(IReadOnlyList<FerryMessage> messages)
    {
        if (string.IsNullOrWhiteSpace(_filterTerm)) return messages;
        var term = _filterTerm.Trim();
        return messages.Where(m =>
            (m.Text != null && m.Text.Contains(term, StringComparison.OrdinalIgnoreCase)) ||
            (m.Filename != null && m.Filename.Contains(term, StringComparison.OrdinalIgnoreCase))
        ).ToList();
    }

    private async Task RefreshAsync()
    {
        try
        {
            var messages = await _client.GetMessagesAsync(_cts.Token);
            App.Log($"fetched {messages.Count} messages");
            var newestId = messages.Count > 0 ? messages[^1].Id : 0;
            ResetLastReadIfThreadRewound(newestId);
            _allMessages = messages;
            var isFiltering = !string.IsNullOrWhiteSpace(_filterTerm);
            var stick = !isFiltering && AtBottom();
            ApplyThread(FilterMessages(_allMessages));
            var msgCount = _thread.OfType<FerryMessage>().Count();
            if (DateTime.UtcNow >= _statusHoldUntil) StatusText.Text = "Ready";
            if (stick && ThreadList.Items.Count > 0)
            {
                ThreadList.ScrollIntoView(ThreadList.Items[^1]);
                if (messages.Count > 0)
                {
                    SaveLastReadId(messages[^1].Id);
                }
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            _statusHoldUntil = default;
            StatusText.Text = "Ferry is unavailable.";
        }
    }

    private async Task RefreshPinsAsync()
    {
        try
        {
            var pins = await _client.GetPinsAsync(_cts.Token);
            _pinnedMessages.Clear();
            foreach (var pin in pins)
            {
                _pinnedMessages.Add(pin);
            }
            PinnedStrip.Visibility = pins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PinnedToggleText.Text = $"Pinned · {pins.Count}";
        }
        catch (Exception)
        {
            // Ignore pin load failures on network glitch
        }
    }

    private void OpenSettings()
    {
        if (_settingsWindow is { IsVisible: true })
        {
            _settingsWindow.Activate();
            return;
        }

        _settingsWindow = new SettingsWindow(
            _client,
            _device,
            () => _thread.OfType<FerryMessage>().ToList(),
            newName =>
            {
                _client.SenderName = newName;
                _ = RefreshAsync();
            },
            () =>
            {
                // Pairing token rotated
                FlashStatus("Token rotated. Scan QR.");
            })
        {
            Owner = this
        };
        _settingsWindow.Closed += (_, _) => _settingsWindow = null;
        _settingsWindow.ShowDialog();
    }

    private void OpenMedia()
    {
        if (_mediaWindow is { IsVisible: true })
        {
            _mediaWindow.Activate();
            return;
        }

        // Modeless, so he can keep the grid beside the thread. It reads
        // _allMessages through the closure rather than a snapshot, so reopening
        // is never needed to see something that just arrived.
        _mediaWindow = new MediaWindow(() => _allMessages, this, SaveAsAsync);
        _mediaWindow.Closed += (_, _) => _mediaWindow = null;
        _mediaWindow.Show();
    }

    private void TogglePinned()
    {
        _pinsExpanded = !_pinsExpanded;
        PinnedItemsList.Visibility = _pinsExpanded ? Visibility.Visible : Visibility.Collapsed;
    }

    private void PinnedItem_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FerryMessage m })
        {
            var target = _thread.OfType<FerryMessage>().FirstOrDefault(x => x.Id == m.Id);
            if (target is not null)
            {
                ThreadList.SelectedItem = target;
                ThreadList.ScrollIntoView(target);
            }
        }
    }

    private async void UnpinItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FerryMessage m })
        {
            try
            {
                var updatedPins = await _client.SetPinnedAsync(m.Id, false, _cts.Token);
                _pinnedMessages.Clear();
                foreach (var pin in updatedPins)
                {
                    _pinnedMessages.Add(pin);
                }
                PinnedStrip.Visibility = updatedPins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
                PinnedToggleText.Text = $"Pinned · {updatedPins.Count}";
                await RefreshAsync();
            }
            catch (Exception)
            {
                FlashStatus("Couldn't unpin.");
            }
        }
    }

    private async Task TogglePinSelectedAsync()
    {
        var selected = ThreadList.SelectedItems.OfType<FerryMessage>().ToList();
        if (selected.Count == 0) return;
        var first = selected[0];
        var newPinned = !first.PinnedAt.HasValue;
        try
        {
            var updatedPins = await _client.SetPinnedAsync(first.Id, newPinned, _cts.Token);
            _pinnedMessages.Clear();
            foreach (var pin in updatedPins)
            {
                _pinnedMessages.Add(pin);
            }
            PinnedStrip.Visibility = updatedPins.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
            PinnedToggleText.Text = $"Pinned · {updatedPins.Count}";
            await RefreshAsync();
            FlashStatus(newPinned ? "Pinned." : "Unpinned.");
        }
        catch (Exception)
        {
            FlashStatus("Couldn't update pin.");
        }
    }

    private void ThreadList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        _dragStartPoint = e.GetPosition(this);
        var item = ItemUnder(e.OriginalSource as DependencyObject);
        _dragCandidate = item?.DataContext as FerryMessage;
    }

    private void ThreadList_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (e.LeftButton != System.Windows.Input.MouseButtonState.Pressed || _dragCandidate is null)
            return;

        var currentPoint = e.GetPosition(this);
        var diff = _dragStartPoint - currentPoint;
        if (Math.Abs(diff.X) > SystemParameters.MinimumHorizontalDragDistance ||
            Math.Abs(diff.Y) > SystemParameters.MinimumVerticalDragDistance)
        {
            var candidate = _dragCandidate;
            _dragCandidate = null;
            StartDragOut(candidate);
        }
    }

    private void StartDragOut(FerryMessage m)
    {
        if (m.Deleted) return;

        try
        {
            string exportPath;
            if (m.IsText)
            {
                var filename = $"Ferry message {DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedAt).ToLocalTime():yyyy-MM-dd-HH-mm}.txt";
                exportPath = Path.Combine(Path.GetTempPath(), filename);
                File.WriteAllText(exportPath, m.Text ?? "", Encoding.UTF8);
            }
            else
            {
                var filename = m.Filename ?? "file";
                exportPath = Path.Combine(Path.GetTempPath(), filename);
                // Download directly to temp file synchronously for drag out
                _client.ExportTempFile(m.Id, exportPath);
            }

            if (File.Exists(exportPath))
            {
                var data = new DataObject(DataFormats.FileDrop, new[] { exportPath });
                DragDrop.DoDragDrop(ThreadList, data, DragDropEffects.Copy);
            }
        }
        catch (Exception ex)
        {
            App.Log($"Drag out failed: {ex.Message}");
        }
    }

    private DateTime _statusHoldUntil;

    // Hold a one-off message on screen for a few seconds. Deleting broadcasts a
    // cleanup, which bounces straight back as a refresh, so without this the
    // outcome would be replaced by the message count before it could be read.
    private void FlashStatus(string text)
    {
        StatusText.Text = text;
        _statusHoldUntil = DateTime.UtcNow.AddSeconds(6);
    }

    private static string GetDateKey(DateTimeOffset dto) => dto.ToLocalTime().ToString("yyyy-MM-dd");

    private static string GetDayLabel(DateTimeOffset dto)
    {
        var local = dto.ToLocalTime().Date;
        var today = DateTime.Today;
        if (local == today) return "Today";
        if (local == today.AddDays(-1)) return "Yesterday";
        return dto.ToLocalTime().ToString("ddd, MMM d");
    }

    // Synthesize day separators and "New since you were last here" unread divider,
    // and reconcile against _thread in-place so existing ListBoxItem containers and
    // decoded thumbnails are preserved.
    private void ApplyThread(IReadOnlyList<FerryMessage> incoming)
    {
        var displayItems = new List<object>();
        string? currentDayKey = null;
        var unreadDividerPlaced = false;
        var isFiltering = !string.IsNullOrWhiteSpace(_filterTerm);

        for (var idx = 0; idx < incoming.Count; idx++)
        {
            var m = incoming[idx] with { Mine = incoming[idx].SenderId == _device.Id };
            var dto = DateTimeOffset.FromUnixTimeMilliseconds(m.CreatedAt);
            var dayKey = GetDateKey(dto);

            if (dayKey != currentDayKey)
            {
                displayItems.Add(new ThreadSeparator(GetDayLabel(dto)));
                currentDayKey = dayKey;
            }

            if (!isFiltering && !unreadDividerPlaced && _lastReadId > 0 && m.Id > _lastReadId)
            {
                displayItems.Add(new ThreadSeparator("New since you were last here", IsUnreadDivider: true));
                unreadDividerPlaced = true;
            }

            displayItems.Add(m);
        }

        ReconcileThread(displayItems);
    }

    private void ReconcileThread(List<object> desired)
    {
        var i = 0;
        while (i < desired.Count)
        {
            var next = desired[i];
            if (i >= _thread.Count)
            {
                _thread.Add(next);
                i++;
            }
            else if (AreItemsEquivalent(_thread[i], next))
            {
                if (!Equals(_thread[i], next))
                {
                    _thread[i] = next;
                }
                i++;
            }
            else
            {
                // Find if 'next' exists further down in _thread
                var foundIndex = -1;
                for (var j = i + 1; j < Math.Min(i + 10, _thread.Count); j++)
                {
                    if (AreItemsEquivalent(_thread[j], next))
                    {
                        foundIndex = j;
                        break;
                    }
                }

                if (foundIndex != -1)
                {
                    // Items between i and foundIndex were removed upstream
                    while (i < foundIndex)
                    {
                        if (_thread[i] is FerryMessage removedMsg && !_allMessages.Any(m => m.Id == removedMsg.Id))
                        {
                            ThumbnailCache.Forget(removedMsg.Id);
                        }
                        _thread.RemoveAt(i);
                        foundIndex--;
                    }
                    if (!Equals(_thread[i], next))
                    {
                        _thread[i] = next;
                    }
                    i++;
                }
                else
                {
                    // 'next' is newly inserted at position i
                    _thread.Insert(i, next);
                    i++;
                }
            }
        }

        while (_thread.Count > desired.Count)
        {
            if (_thread[^1] is FerryMessage removedMsg && !_allMessages.Any(m => m.Id == removedMsg.Id))
            {
                ThumbnailCache.Forget(removedMsg.Id);
            }
            _thread.RemoveAt(_thread.Count - 1);
        }
    }

    private static bool AreItemsEquivalent(object a, object b)
    {
        if (a is FerryMessage ma && b is FerryMessage mb) return ma.Id == mb.Id;
        if (a is ThreadSeparator sa && b is ThreadSeparator sb)
            return sa.Text == sb.Text && sa.IsUnreadDivider == sb.IsUnreadDivider;
        return false;
    }

    private ScrollViewer? _threadScroller;

    // Only auto-scroll when the user is already parked at the bottom, so a
    // refresh cannot yank them away from something they scrolled up to read.
    private bool AtBottom()
    {
        _threadScroller ??= FindScrollViewer(ThreadList);
        if (_threadScroller is not { } sv) return true;
        return sv.ScrollableHeight <= 0 || sv.VerticalOffset >= sv.ScrollableHeight - 24;
    }

    private static ScrollViewer? FindScrollViewer(DependencyObject root)
    {
        if (root is ScrollViewer found) return found;
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var hit = FindScrollViewer(System.Windows.Media.VisualTreeHelper.GetChild(root, i));
            if (hit is not null) return hit;
        }
        return null;
    }

    private async Task SendAsync()
    {
        var text = Composer.Text.Trim();
        if (text.Length == 0) return;
        SendButton.IsEnabled = false;
        try
        {
            await _client.SendMessageAsync(text, _device.Id, _device.Name, _cts.Token);
            Composer.Clear();
            ClearDraft();
            await RefreshAsync();
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            StatusText.Text = "Couldn't send.";
            SendButton.IsEnabled = true;
        }
    }

    private string? _failedUpload;
    private (int Id, string Path)? _failedSave;

    private void SetTransferProgress(double value)
    {
        UploadBar.Value = value;
        UploadPercentText.Text = $"{Math.Round(value):0}%";
    }

    private void FileAction_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string action, DataContext: FerryMessage m } && !m.Deleted)
            _ = HandleFileActionAsync(action, m);
    }

    private async Task HandleFileActionAsync(string action, FerryMessage m)
    {
        try
        {
            switch (action)
            {
                case "open":
                    await _client.OpenOnLaptopAsync(m.Id, _cts.Token);
                    break;
                case "reveal":
                    await _client.RevealOnLaptopAsync(m.Id, _cts.Token);
                    break;
                case "save":
                    await SaveAsAsync(m);
                    break;
            }
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            StatusText.Text = "File action failed.";
        }
    }

    private async Task SaveAsAsync(FerryMessage m)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = m.Filename ?? "file" };
        if (dialog.ShowDialog() != true) return;
        _failedUpload = null;
        _failedSave = null;
        RetryButton.Visibility = Visibility.Collapsed;
        SetTransferProgress(0);
        UploadRow.Visibility = Visibility.Visible;
        UploadLabel.Text = $"Saving {m.Filename}…";
        UploadQueueText.Visibility = Visibility.Collapsed;
        using var transferCts = BeginTransfer();
        var progress = new Progress<double>(SetTransferProgress);
        try
        {
            await _client.DownloadToAsync(m.Id, dialog.FileName, progress, transferCts.Token);
            UploadRow.Visibility = Visibility.Collapsed;
        }
        catch (TaskCanceledException) when (transferCts.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            UploadRow.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _failedSave = (m.Id, dialog.FileName);
            UploadLabel.Text = $"Couldn't save {m.Filename} · Retry available";
            RetryButton.Visibility = Visibility.Visible;
        }
        finally { EndTransfer(transferCts); }
    }

    private async Task PickAndUploadAsync()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Multiselect = true };
        if (dialog.ShowDialog() != true) return;
        await UploadManyAsync(dialog.FileNames);
    }

    private async Task UploadOneAsync(string path, int position = 1, int total = 1)
    {
        _failedUpload = null;
        RetryButton.Visibility = Visibility.Collapsed;
        SetTransferProgress(0);
        UploadRow.Visibility = Visibility.Visible;
        UploadLabel.Text = $"Uploading {System.IO.Path.GetFileName(path)}…";
        UploadQueueText.Text = total == 1
            ? "1 transfer"
            : $"{position} of {total} · {total - position} queued";
        UploadQueueText.Visibility = Visibility.Visible;
        using var transferCts = BeginTransfer();
        var progress = new Progress<double>(SetTransferProgress);
        try
        {
            await _client.UploadFileAsync(path, _device.Id, _device.Name, progress, transferCts.Token);
            UploadRow.Visibility = Visibility.Collapsed;
            await RefreshAsync();
        }
        catch (TaskCanceledException) when (transferCts.IsCancellationRequested && !_cts.IsCancellationRequested)
        {
            UploadRow.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or IOException)
        {
            _failedUpload = path;
            UploadLabel.Text = $"Couldn't send {System.IO.Path.GetFileName(path)} · Retry available";
            RetryButton.Visibility = Visibility.Visible;
        }
        finally { EndTransfer(transferCts); }
    }

    private CancellationTokenSource BeginTransfer()
    {
        var transferCts = CancellationTokenSource.CreateLinkedTokenSource(_cts.Token);
        _transferCts = transferCts;
        CancelTransferButton.Visibility = Visibility.Visible;
        return transferCts;
    }

    private void EndTransfer(CancellationTokenSource transferCts)
    {
        if (!ReferenceEquals(_transferCts, transferCts)) return;
        _transferCts = null;
        CancelTransferButton.Visibility = Visibility.Collapsed;
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop)) return;
        // Read the payload synchronously: the DataObject is only valid for the
        // duration of the event, and an async void handler let it die mid-await.
        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths) return;
        e.Handled = true;
        _ = UploadManyAsync(paths);
    }

    private async Task UploadManyAsync(IReadOnlyList<string> paths)
    {
        var existing = paths.Where(File.Exists).ToList();
        for (var index = 0; index < existing.Count; index++)
        {
            await UploadOneAsync(existing[index], index + 1, existing.Count);
            if (_cts.IsCancellationRequested) break;
        }
    }

    private void Thumb_Click(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FerryMessage m } && m.ShowThumb && !m.Deleted)
        {
            var imageMessages = ThreadList.Items.OfType<FerryMessage>()
                .Where(x => x.ShowThumb && !x.Deleted)
                .ToList();
            int idx = imageMessages.FindIndex(x => x.Id == m.Id);
            if (idx < 0) idx = 0;
            new ImageWindow(imageMessages, idx, this, SaveAsAsync).Show();
        }
    }

    private IEnumerable<string> SelectedTexts() =>
        ThreadList.SelectedItems.OfType<FerryMessage>()
            .Where(m => m.IsText && !string.IsNullOrEmpty(m.Text))
            .Select(m => m.Text!);

    private void CopySelectedText()
    {
        var joined = string.Join(Environment.NewLine + Environment.NewLine, SelectedTexts());
        if (joined.Length > 0) TrySetClipboardText(joined);
    }

    private void RowCopy_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FerryMessage m })
        {
            var textToCopy = m.IsText ? m.Text : m.Filename;
            if (!string.IsNullOrEmpty(textToCopy))
            {
                if (TrySetClipboardText(textToCopy))
                {
                    FlashStatus("Copied.");
                }
            }
        }
    }

    // Right-clicking a row that is not part of the selection should act on that
    // row, not on whatever happened to be selected before — the same rule every
    // file manager follows. Right-clicking inside an existing selection leaves
    // it alone, so a multi-row delete survives the trip to the menu.
    private void ThreadList_PreviewRightDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        var item = ItemUnder(e.OriginalSource as DependencyObject);
        if (item is null) return;
        if (!item.IsSelected)
        {
            ThreadList.SelectedItems.Clear();
            item.IsSelected = true;
        }
        item.Focus();
    }

    private static ListBoxItem? ItemUnder(DependencyObject? node)
    {
        // Linkification puts Run and Hyperlink inlines inside the bubbles, and
        // those are ContentElements rather than Visuals: VisualTreeHelper.GetParent
        // throws on one. A press that lands on the text itself has to step up
        // through the logical tree until it reaches a visual again.
        while (node is not null and not ListBoxItem)
        {
            node = node switch
            {
                System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D =>
                    System.Windows.Media.VisualTreeHelper.GetParent(node),
                FrameworkContentElement framework => framework.Parent,
                ContentElement content => ContentOperations.GetParent(content),
                _ => null,
            };
        }
        return node as ListBoxItem;
    }

    private void UpdateSelectionBar()
    {
        var count = ThreadList.SelectedItems.Count;
        SelectionBar.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
        if (count == 0) return;
        var files = SelectedFileCount();
        SelectionText.Text = (count, files) switch
        {
            (1, 0) => "1 selected",
            (1, _) => "1 selected · 1 file",
            (_, 0) => $"{count} selected",
            (_, 1) => $"{count} selected · 1 file",
            _ => $"{count} selected · {files} files",
        };
        SelectionCopyButton.IsEnabled = SelectedTexts().Any();
    }

    // Rows whose blob is already gone (cleanup tombstones) are not counted:
    // there is no file left to ask about.
    private int SelectedFileCount() =>
        ThreadList.SelectedItems.OfType<FerryMessage>().Count(m => !m.IsText && !m.Deleted);

    private bool _deleting;

    private async Task DeleteSelectedAsync()
    {
        // Re-entrancy is real here: Del on the keyboard, the context menu and
        // the selection bar all land on this, and the dialog is modal but the
        // request that follows it is not.
        if (_deleting) return;
        var selected = ThreadList.SelectedItems.OfType<FerryMessage>().ToList();
        if (selected.Count == 0) return;

        var dialog = new DeleteWindow(selected.Count, SelectedFileCount()) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        _deleting = true;
        SelectionDeleteButton.IsEnabled = false;
        var ids = selected.Select(m => m.Id).ToList();
        try
        {
            var result = await _client.DeleteMessagesAsync(ids, dialog.DeleteFiles, _cts.Token);
            foreach (var id in ids) ThumbnailCache.Forget(id);
            ThreadList.SelectedItems.Clear();
            await RefreshAsync();
            FlashStatus(DeleteSummary(result));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            FlashStatus("Couldn't delete.");
        }
        finally
        {
            _deleting = false;
            SelectionDeleteButton.IsEnabled = true;
        }
    }

    private static string DeleteSummary(DeleteResult r)
    {
        var head = r.Deleted == 1 ? "Deleted 1 message" : $"Deleted {r.Deleted} messages";
        if (r.FilesDeleted > 0)
            head += r.FilesDeleted == 1 ? " · 1 file" : $" · {r.FilesDeleted} files";
        if (r.FilesKept > 0)
            head += r.FilesKept == 1
                ? " · 1 file kept"
                : $" · {r.FilesKept} files kept";
        return head + ".";
    }

    // Returns true when the clipboard held files or an image that we consumed
    // as uploads; plain text falls through to the TextBox default paste.
    private async Task<bool> PasteNonTextAsync()
    {
        if (Clipboard.ContainsFileDropList())
        {
            await UploadManyAsync(Clipboard.GetFileDropList().Cast<string>().ToList());
            return true;
        }
        if (Clipboard.ContainsImage())
        {
            var image = Clipboard.GetImage();
            if (image is not null)
            {
                var path = Path.Combine(Path.GetTempPath(), $"Ferry paste {DateTime.Now:yyyy-MM-dd-HH-mm-ss}.png");
                using (var stream = File.Create(path))
                {
                    var encoder = new System.Windows.Media.Imaging.PngBitmapEncoder();
                    encoder.Frames.Add(System.Windows.Media.Imaging.BitmapFrame.Create(image));
                    encoder.Save(stream);
                }
                await UploadOneAsync(path);
            }
            return true;
        }
        return false;
    }

    private void RenderPresence(IReadOnlyList<PresenceDevice> devices)
    {
        var others = devices.Where(d => d.Id != _device.Id).Select(d => d.Name).ToList();
        PresenceText.Text = others.Count switch
        {
            0 => "",
            1 => $"● {others[0]} attached",
            _ => $"● {others.Count} devices attached",
        };
    }

    private async Task WatchLoopAsync()
    {
        var attempts = 0;
        while (!_cts.IsCancellationRequested)
        {
            try
            {
                await _client.WatchAsync(
                    async () =>
                    {
                        await Dispatcher.InvokeAsync(RefreshAsync);
                        await Dispatcher.InvokeAsync(RefreshPinsAsync);
                    },
                    _cts.Token,
                    async () =>
                    {
                        await Dispatcher.InvokeAsync(RefreshAsync);
                        await Dispatcher.InvokeAsync(RefreshPinsAsync);
                    },
                    list => Dispatcher.InvokeAsync(() => RenderPresence(list)).Task,
                    stats => Dispatcher.InvokeAsync(() => _settingsWindow?.UpdateStorageUI(stats)).Task,
                    msg => Dispatcher.InvokeAsync(() => NotifyInboundMessage(msg)).Task);
                attempts = 0;
            }
            catch (Exception) when (!_cts.IsCancellationRequested)
            {
                attempts++;
                var ceilingMs = Math.Min(30_000, 1_000 * (int)Math.Pow(2, Math.Min(attempts, 5)));
                var delayMs = Random.Shared.Next(ceilingMs);
                await Task.Delay(TimeSpan.FromMilliseconds(delayMs), _cts.Token)
                          .ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
            }
        }
    }
}
