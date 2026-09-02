using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using RoboCapture.Core;
using RoboCapture.NikonAdapter;
using RoboCapture.Persistence;
using RoboCapture.Vision;

namespace RoboCapture.CameraLab;

public sealed class MainWindow : Window
{
    private sealed record CameraProfile(string DisplayName, string ModuleFolder, string ModuleFile, bool IsLegacy, string[] DetectKeywords);

    // Only cameras whose SDK module is actually assembled under vendor-sdks/nikon/modules/ so
    // far. Other bodies (D700, D600, ...) still work via "Custom (advanced)" below once their
    // module folder is set up the same way — see docs/NIKON_SDK_NOTES.md.
    private static readonly CameraProfile[] KnownProfiles =
    [
        new("Nikon D850", "vendor-sdks/nikon/modules/d850", "Type0022.md3", true, ["D850"]),
        new("Nikon Z6III", "vendor-sdks/nikon/modules/z-unified", "ControlServiceLayer.dll", false, ["Z6_3", "Z6III", "Z 6III", "Z6 III"]),
    ];

    private ICameraDriver _camera = null!;
    private int _activeSelectionIndex = 0; // matches _cameraType's initial Simulator selection
    private readonly CaptureStore _store = new(Path.Combine(Environment.CurrentDirectory, "robocapture.db"));
    private readonly TextBlock _cameraText = new();
    private readonly TextBlock _statusText = new();
    private readonly TextBlock _lastCapture = new();
    private readonly TextBlock _destination = new();
    private readonly TextBlock _counters = new();
    private readonly TextBox _subject = new() { Text = "TEST001", MinWidth = 120 };
    private readonly TextBox _scanInput = new() { MinWidth = 180 };
    private readonly TextBox _count = new() { Text = "10", MinWidth = 50 };
    private readonly TextBox _interval = new() { Text = "0", MinWidth = 50 };
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, Height = 220, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _rosterStatus = new() { Text = "Roster: none loaded (scans use raw value as subject ID)" };
    private readonly StackPanel _recovery = new();
    private readonly ComboBox _cameraType = new() { MinWidth = 190 };
    private readonly TextBox _moduleFolder = new() { MinWidth = 260 };
    private readonly TextBox _moduleFile = new() { MinWidth = 150 };
    private readonly TextBlock _detectStatus = new() { Text = "Camera detection: not run yet", Margin = new Thickness(0, 4, 0, 0) };
    private readonly Image _liveViewImage = new() { Width = 400, Height = 267, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _liveViewStatus = new() { Text = "Live view: off" };
    private readonly Image _lastCaptureImage = new() { Width = 400, Height = 267, Stretch = Stretch.Uniform, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _lastCapturePreviewStatus = new() { Text = "Last capture preview: none yet" };
    private readonly TextBox _saveFolder = new() { Text = Path.Combine(Environment.CurrentDirectory, "captures"), MinWidth = 320 };
    private readonly ComboBox _imageFormat = new() { MinWidth = 140 };
    private readonly TextBlock _formatStatus = new() { Text = "Format: not applied (Nikon Remote SDK v2 only)" };
    private readonly CheckBox _qualityEnabled = new() { Content = "Score captures for quality (blink/smile) - experimental, unvalidated thresholds", IsChecked = true, Margin = new Thickness(0, 4, 0, 0) };
    private readonly TextBlock _qualitySummary = new() { Text = "Quality: n/a" };
    private YuNetShotQualityFilter? _qualityFilter;
    private CancellationTokenSource? _operation;
    private int _attempts, _successes, _failures;
    private int _qualityScored, _qualityKept;
    private IReadOnlyList<SubjectRecord> _roster = Array.Empty<SubjectRecord>();
    private SubjectIdentifier? _identifier;

    public MainWindow()
    {
        Title = "RoboCapture Camera Lab 0.2";
        Width = 900; Height = 650; MinWidth = 700; MinHeight = 500;
        WireCamera(new SimulatedCameraDriver { CaptureLatencyMs = 25, TransferLatencyMs = 10 });
        Content = Layout();
        Loaded += async (_, _) =>
        {
            try
            {
                await _store.InitializeAsync();
                Log("SQLite store initialized.");
                UpdateCamera();
                await RefreshRecoveryAsync();
            }
            catch (Exception exception) { Log($"ERROR: database initialization failed: {exception.Message}"); }
            DetectCamera();
        };
        Closed += async (_, _) => { await _camera.DisposeAsync(); _qualityFilter?.Dispose(); };
    }

    private void WireCamera(ICameraDriver camera)
    {
        _camera = camera;
        _camera.Event += cameraEvent => Dispatcher.InvokeAsync(async () =>
        {
            Log($"{cameraEvent.Operation}: {cameraEvent.Result}{Error(cameraEvent.Error)}");
            try { await _store.RecordCameraEventAsync(cameraEvent); }
            catch (Exception exception) { Log($"ERROR: event persistence failed: {exception.Message}"); }
        });
        _liveViewImage.Source = null;
        _liveViewStatus.Text = "Live view: off";
        _lastCaptureImage.Source = null;
        _lastCapturePreviewStatus.Text = "Last capture preview: none yet";
        if (camera is NikonRemoteSdkV2CameraDriver nikon)
            nikon.LiveViewFrame += frame => Dispatcher.InvokeAsync(() => UpdateLiveViewImage(frame));
    }

    private void UpdateLiveViewImage(byte[] jpegBytes)
    {
        try
        {
            using var stream = new MemoryStream(jpegBytes);
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.StreamSource = stream;
            bitmap.EndInit();
            bitmap.Freeze();
            _liveViewImage.Source = bitmap;
        }
        catch { /* skip a malformed frame rather than crash the UI thread */ }
    }

    private void UpdateLastCapturePreview(CaptureResult result)
    {
        if (!result.Success || string.IsNullOrEmpty(result.LocalPath))
        {
            _lastCapturePreviewStatus.Text = "Last capture preview: none (capture failed)";
            _lastCaptureImage.Source = null;
            return;
        }
        var extension = Path.GetExtension(result.LocalPath);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
        {
            _lastCapturePreviewStatus.Text = $"Last capture preview: no preview for {extension} — {Path.GetFileName(result.LocalPath)}";
            _lastCaptureImage.Source = null;
            return;
        }
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(result.LocalPath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            _lastCaptureImage.Source = bitmap;
            _lastCapturePreviewStatus.Text = $"Last capture preview: {Path.GetFileName(result.LocalPath)}";
        }
        catch (Exception exception)
        {
            _lastCapturePreviewStatus.Text = $"Last capture preview: failed to load ({exception.Message})";
            _lastCaptureImage.Source = null;
        }
    }

    /// <summary>
    /// Runs the (unvalidated — see docs/AUTONOMOUS_CAPTURE_PLAN.md) shot-quality filter against
    /// a successful capture's delivered JPEG, records the verdict against that capture's DB row,
    /// and updates the running kept/flagged summary. Silently skipped for RAW-only captures
    /// (nothing to decode) and for failed captures; scoring failures are logged but never abort
    /// the capture loop — this is a best-effort add-on, not a required part of capturing.
    /// </summary>
    private async Task ScoreCaptureIfEnabledAsync(string sessionId, CaptureRequest request, CaptureResult result, CancellationToken ct)
    {
        if (_qualityEnabled.IsChecked != true || !result.Success || string.IsNullOrEmpty(result.LocalPath)) return;
        var extension = Path.GetExtension(result.LocalPath);
        if (!extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase) && !extension.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            return; // RAW-only capture — nothing this filter can decode.

        try
        {
            var filter = _qualityFilter ??= new YuNetShotQualityFilter();
            var bytes = await File.ReadAllBytesAsync(result.LocalPath, ct);
            var score = await Task.Run(() => filter.Score(bytes), ct);

            await _store.RecordShotQualityAsync(sessionId, request.PoseId, request.ShotNumber, score.Pass, score.Reason, ct);
            _qualityScored++;
            if (score.Pass) _qualityKept++;
            _qualitySummary.Text = $"Quality: {_qualityKept}/{_qualityScored} kept — last shot {(score.Pass ? "KEPT" : "FLAGGED")}: {score.Reason}";
            Log($"Quality check: {(score.Pass ? "KEPT" : "FLAGGED")} — {score.Reason}");
        }
        catch (Exception exception)
        {
            Log($"WARNING: quality scoring failed: {exception.Message}");
        }
    }

    private static TextBlock SectionHeader(string text) =>
        new() { Text = text, FontWeight = FontWeights.Bold, Margin = new Thickness(0, 10, 0, 2) };

    private UIElement Layout()
    {
        var root = new StackPanel { Margin = new Thickness(16) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "CAMERA LAB 0.2", FontSize = 24, FontWeight = FontWeights.Bold });
        header.Children.Add(_cameraText); header.Children.Add(_statusText);
        root.Children.Add(header);

        // Step 1: pick a camera.
        root.Children.Add(SectionHeader("1. CHOOSE CAMERA"));
        var detectRow = new WrapPanel();
        var detectButton = new Button { Content = "AUTO-DETECT CAMERA", Margin = new Thickness(2), Padding = new Thickness(7, 4, 7, 4) };
        detectButton.Click += (_, _) => DetectCamera();
        detectRow.Children.Add(detectButton); detectRow.Children.Add(_detectStatus);
        root.Children.Add(detectRow);

        var driverRow = new WrapPanel { Margin = new Thickness(0, 6, 0, 0) };
        _cameraType.Items.Add("Simulator");
        foreach (var profile in KnownProfiles) _cameraType.Items.Add(profile.DisplayName);
        _cameraType.Items.Add("Custom (advanced)");
        _cameraType.SelectedIndex = 0;
        _cameraType.SelectionChanged += (_, _) => UpdateDriverFields();
        driverRow.Children.Add(new Label { Content = "Camera" }); driverRow.Children.Add(_cameraType);
        root.Children.Add(driverRow);

        var advancedRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        advancedRow.Children.Add(new Label { Content = "Module folder" }); advancedRow.Children.Add(_moduleFolder);
        var browseButton = new Button { Content = "BROWSE...", Margin = new Thickness(2), Padding = new Thickness(7, 4, 7, 4) };
        browseButton.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the Nikon module folder" };
            if (dialog.ShowDialog() == true) _moduleFolder.Text = dialog.FolderName;
        };
        advancedRow.Children.Add(browseButton);
        advancedRow.Children.Add(new Label { Content = "Module file" }); advancedRow.Children.Add(_moduleFile);
        root.Children.Add(advancedRow);

        var switchButton = new Button { Content = "SWITCH CAMERA (apply the selection above)", Margin = new Thickness(2, 6, 2, 2), Padding = new Thickness(7, 4, 7, 4), FontWeight = FontWeights.Bold };
        switchButton.Click += async (_, _) => await SwitchCamera();
        root.Children.Add(switchButton);
        UpdateDriverFields();

        // Step 2: connect.
        root.Children.Add(SectionHeader("2. CONNECT"));
        var connectRow = new WrapPanel();
        Add(connectRow, "CONNECT", Connect); Add(connectRow, "DISCONNECT", Disconnect);
        root.Children.Add(connectRow);

        // Save folder + capture format.
        root.Children.Add(SectionHeader("SAVE SETTINGS"));
        var saveRow = new WrapPanel();
        saveRow.Children.Add(new Label { Content = "Save folder" }); saveRow.Children.Add(_saveFolder);
        var saveBrowseButton = new Button { Content = "BROWSE...", Margin = new Thickness(2), Padding = new Thickness(7, 4, 7, 4) };
        saveBrowseButton.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the folder to save captured photos into" };
            if (dialog.ShowDialog() == true) _saveFolder.Text = dialog.FolderName;
        };
        saveRow.Children.Add(saveBrowseButton);
        root.Children.Add(saveRow);

        var formatRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        _imageFormat.Items.Add("JPEG"); _imageFormat.Items.Add("RAW"); _imageFormat.Items.Add("RAW + JPEG");
        _imageFormat.SelectedIndex = 0;
        formatRow.Children.Add(new Label { Content = "Capture format" }); formatRow.Children.Add(_imageFormat);
        var applyFormatButton = new Button { Content = "APPLY FORMAT", Margin = new Thickness(2), Padding = new Thickness(7, 4, 7, 4) };
        applyFormatButton.Click += async (_, _) => await ApplyImageFormat();
        formatRow.Children.Add(applyFormatButton);
        root.Children.Add(formatRow);
        root.Children.Add(_formatStatus);

        // Step 3: capture.
        root.Children.Add(SectionHeader("3. CAPTURE"));
        root.Children.Add(_qualityEnabled);
        var inputs = new WrapPanel();
        inputs.Children.Add(new Label { Content = "Subject" }); inputs.Children.Add(_subject);
        inputs.Children.Add(new Label { Content = "QR/barcode scan" }); inputs.Children.Add(_scanInput);
        inputs.Children.Add(new Label { Content = "Count" }); inputs.Children.Add(_count);
        inputs.Children.Add(new Label { Content = "Interval ms" }); inputs.Children.Add(_interval);
        _scanInput.KeyDown += (_, args) =>
        {
            if (args.Key != System.Windows.Input.Key.Enter) return;
            var value = _scanInput.Text.Trim();
            if (value.Length == 0) { Log("ERROR: scan value is empty."); return; }
            if (_identifier is not null)
            {
                var result = _identifier.Resolve(value);
                if (result.Found)
                {
                    _subject.Text = result.Subject!.StudentId;
                    Log($"Scanner resolved subject: {result.Subject.StudentId} ({result.Subject.FirstName} {result.Subject.LastName})");
                }
                else Log($"ERROR: {result.Error}");
            }
            else
            {
                _subject.Text = value;
                Log($"Scanner subject selected: {value}");
            }
            _scanInput.Clear();
        };
        root.Children.Add(inputs);
        var captureRow = new WrapPanel { Margin = new Thickness(0, 4, 0, 0) };
        Add(captureRow, "CAPTURE", Capture); Add(captureRow, "CAPTURE X N", CaptureMany); Add(captureRow, "STOP", Stop);
        Add(captureRow, "LIVE VIEW ON", StartLiveView); Add(captureRow, "LIVE VIEW OFF", StopLiveView);
        root.Children.Add(captureRow);
        root.Children.Add(new StackPanel { Children = { _destination, _lastCapture, _counters, _qualitySummary, _recovery } });
        var previewRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        var liveViewPanel = new StackPanel { Margin = new Thickness(0, 0, 16, 0) };
        liveViewPanel.Children.Add(SectionHeader("LIVE VIEW"));
        liveViewPanel.Children.Add(_liveViewStatus);
        liveViewPanel.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _liveViewImage });
        previewRow.Children.Add(liveViewPanel);
        var lastCapturePanel = new StackPanel();
        lastCapturePanel.Children.Add(SectionHeader("LAST CAPTURE"));
        lastCapturePanel.Children.Add(_lastCapturePreviewStatus);
        lastCapturePanel.Children.Add(new Border { BorderBrush = Brushes.Gray, BorderThickness = new Thickness(1), Child = _lastCaptureImage });
        previewRow.Children.Add(lastCapturePanel);
        root.Children.Add(previewRow);

        // Roster (optional).
        root.Children.Add(SectionHeader("ROSTER (optional)"));
        var rosterRow = new WrapPanel();
        Add(rosterRow, "LOAD ROSTER", LoadRoster);
        root.Children.Add(rosterRow);
        root.Children.Add(_rosterStatus);

        // Simulator-only testing tools — no effect on real hardware.
        root.Children.Add(SectionHeader("SIMULATOR TESTING (Simulator driver only)"));
        var testRow = new WrapPanel();
        Add(testRow, "RUN STRESS TEST", Stress);
        Add(testRow, "INJECT DISCONNECT", InjectDisconnect); Add(testRow, "INJECT CAPTURE FAILURE", InjectCaptureFailure);
        Add(testRow, "INJECT TRANSFER FAILURE", InjectTransferFailure); Add(testRow, "CLEAR FAULTS", ClearFaults);
        root.Children.Add(testRow);

        root.Children.Add(SectionHeader("LOG"));
        root.Children.Add(_log);
        return new ScrollViewer { Content = root, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    }

    private static void Add(Panel panel, string text, Func<Task> action)
    {
        var button = new Button { Content = text, Margin = new Thickness(2), Padding = new Thickness(7, 4, 7, 4) };
        button.Click += async (_, _) => await action(); panel.Children.Add(button);
    }

    private Task LoadRoster()
    {
        var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "Roster CSV (*.csv)|*.csv|All files (*.*)|*.*" };
        if (dialog.ShowDialog() != true) return Task.CompletedTask;
        try
        {
            _roster = CsvRosterImporter.Parse(File.ReadAllText(dialog.FileName));
            _identifier = new SubjectIdentifier(_roster);
            _rosterStatus.Text = $"Roster: {_roster.Count} subjects loaded from {Path.GetFileName(dialog.FileName)}";
            Log($"Roster loaded: {_roster.Count} subjects from {dialog.FileName}");
        }
        catch (Exception exception) { Log($"ERROR: roster load failed: {exception.Message}"); }
        return Task.CompletedTask;
    }

    private async Task RefreshRecoveryAsync()
    {
        var incomplete = await _store.GetIncompleteSessionsAsync();
        _recovery.Children.Clear();
        if (incomplete.Count == 0) return;
        _recovery.Children.Add(new TextBlock { Text = $"INCOMPLETE SESSIONS ({incomplete.Count}) — review before continuing:", FontWeight = FontWeights.Bold, Margin = new Thickness(0, 8, 0, 2) });
        foreach (var session in incomplete)
        {
            var row = new WrapPanel { Margin = new Thickness(0, 0, 0, 2) };
            row.Children.Add(new TextBlock { Text = $"{session.StartedUtc:yyyy-MM-dd HH:mm} | subject {session.SubjectId} | {session.CaptureCount} shot(s) recorded | session {session.SessionId}", Margin = new Thickness(0, 0, 8, 0) });
            var button = new Button { Content = "MARK REVIEWED", Padding = new Thickness(5, 1, 5, 1) };
            button.Click += async (_, _) =>
            {
                try { await _store.CompleteSessionAsync(session.SessionId); Log($"Session {session.SessionId} marked reviewed."); await RefreshRecoveryAsync(); }
                catch (Exception exception) { Log($"ERROR: could not mark session reviewed: {exception.Message}"); }
            };
            row.Children.Add(button);
            _recovery.Children.Add(row);
        }
    }

    private CameraProfile? SelectedProfile =>
        _cameraType.SelectedIndex >= 1 && _cameraType.SelectedIndex <= KnownProfiles.Length
            ? KnownProfiles[_cameraType.SelectedIndex - 1] : null;
    private bool IsCustomSelected => _cameraType.SelectedIndex == KnownProfiles.Length + 1;

    private void UpdateDriverFields()
    {
        var profile = SelectedProfile;
        if (profile is not null)
        {
            _moduleFolder.Text = profile.ModuleFolder;
            _moduleFile.Text = profile.ModuleFile;
            _moduleFolder.IsEnabled = false;
            _moduleFile.IsEnabled = false;
        }
        else if (IsCustomSelected)
        {
            _moduleFolder.IsEnabled = true;
            _moduleFile.IsEnabled = true;
        }
        else // Simulator
        {
            _moduleFolder.Text = string.Empty;
            _moduleFile.Text = string.Empty;
            _moduleFolder.IsEnabled = false;
            _moduleFile.IsEnabled = false;
        }
    }

    private static IReadOnlyList<string> GetConnectedNikonDeviceNames()
    {
        var names = new List<string>();
        try
        {
            // Built from every known profile's own DetectKeywords (plus "Nikon" itself) rather
            // than a separately hand-maintained keyword list — some Nikon bodies enumerate in
            // Windows with a bare model number and no "Nikon" prefix at all (confirmed: a D850
            // shows up as literally "D850", PNPClass WPD, not "Nikon D850" or anything
            // Nikon-branded), so a query that only matched "%Nikon%"/Z-series names silently
            // never found it even when it was fully connected and working. Keeping the WHERE
            // clause derived from KnownProfiles means adding a new camera profile automatically
            // makes it detectable too, with no separate query to remember to update.
            var keywords = new[] { "Nikon" }.Concat(KnownProfiles.SelectMany(p => p.DetectKeywords)).Distinct();
            var clause = string.Join(" OR ", keywords.Select(k => $"Name LIKE '%{k.Replace("'", "''")}%'"));
            using var searcher = new System.Management.ManagementObjectSearcher(
                $"SELECT Name FROM Win32_PnPEntity WHERE {clause}");
            foreach (var obj in searcher.Get())
            {
                var name = obj["Name"]?.ToString();
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
        }
        catch (Exception exception)
        {
            names.Add($"__error__{exception.Message}");
        }
        return names;
    }

    private void DetectCamera()
    {
        var names = GetConnectedNikonDeviceNames();
        if (names.Count == 1 && names[0].StartsWith("__error__"))
        {
            _detectStatus.Text = $"Camera detection: unavailable ({names[0][9..]})";
            Log($"Auto-detect failed: {names[0][9..]}");
            return;
        }
        if (names.Count == 0)
        {
            _detectStatus.Text = "Camera detection: no Nikon camera found (check USB connection and that it's powered on)";
            Log("Auto-detect: no Nikon camera found.");
            return;
        }

        var matched = KnownProfiles.FirstOrDefault(profile =>
            names.Any(name => profile.DetectKeywords.Any(keyword => name.Contains(keyword, StringComparison.OrdinalIgnoreCase))));
        if (matched is not null)
        {
            _detectStatus.Text = $"Camera detection: found {matched.DisplayName} — selected below";
            Log($"Auto-detect: found {matched.DisplayName} ({string.Join(", ", names)}).");
            _cameraType.SelectedIndex = Array.IndexOf(KnownProfiles, matched) + 1;
        }
        else
        {
            _detectStatus.Text = $"Camera detection: Nikon device present ({string.Join(", ", names)}) but no matching profile — use Custom (advanced)";
            Log($"Auto-detect: unrecognized Nikon device(s): {string.Join(", ", names)}.");
        }
    }

    private async Task SwitchCamera()
    {
        if (_operation is not null) { Log("ERROR: stop the current operation before switching cameras."); return; }
        try
        {
            if (_camera.State != CameraConnectionState.Disconnected) await _camera.DisconnectAsync();
        }
        catch (Exception exception) { Log($"WARNING: error disconnecting previous camera: {exception.Message}"); }
        await _camera.DisposeAsync();

        var profile = SelectedProfile;
        var isLegacy = profile?.IsLegacy ?? _moduleFile.Text.EndsWith(".md3", StringComparison.OrdinalIgnoreCase);
        ICameraDriver next;
        try
        {
            next = _cameraType.SelectedIndex == 0
                ? new SimulatedCameraDriver { CaptureLatencyMs = 25, TransferLatencyMs = 10 }
                : isLegacy
                    ? new NikonCameraDriver(_moduleFolder.Text, _moduleFile.Text)
                    : new NikonRemoteSdkV2CameraDriver(_moduleFolder.Text, _moduleFile.Text);
        }
        catch (Exception exception)
        {
            Log($"ERROR: could not create driver: {exception.Message}");
            WireCamera(new SimulatedCameraDriver { CaptureLatencyMs = 25, TransferLatencyMs = 10 });
            _cameraType.SelectedIndex = 0;
            _activeSelectionIndex = 0;
            UpdateCamera();
            return;
        }
        WireCamera(next);
        _activeSelectionIndex = _cameraType.SelectedIndex;
        UpdateCamera();
        Log($"Camera driver switched to: {_cameraType.SelectedItem}");
    }

    private async Task Connect()
    {
        if (_camera.State == CameraConnectionState.Connected)
        {
            Log("Already connected.");
            return;
        }
        if (_activeSelectionIndex != _cameraType.SelectedIndex)
        {
            Log($"Applying camera selection ({_cameraType.SelectedItem}) before connecting...");
            await SwitchCamera();
            if (_activeSelectionIndex != _cameraType.SelectedIndex) return; // SwitchCamera failed and logged why
        }
        try
        {
            await _camera.ConnectAsync(); UpdateCamera(); Log("Connected.");
            await ApplyImageFormat();
        }
        catch (Exception e) { Log($"ERROR: {e.Message}"); }
    }
    private async Task Disconnect() { await _camera.DisconnectAsync(); UpdateCamera(); Log("Disconnected."); }

    private async Task ApplyImageFormat()
    {
        if (_camera is not NikonRemoteSdkV2CameraDriver nikon)
        {
            _formatStatus.Text = "Format: not applied (Nikon Remote SDK v2 only)";
            return;
        }
        if (_camera.State != CameraConnectionState.Connected)
        {
            _formatStatus.Text = "Format: not applied (connect the camera first)";
            return;
        }
        var format = _imageFormat.SelectedIndex switch
        {
            1 => ImageFormat.Raw,
            2 => ImageFormat.RawAndJpeg,
            _ => ImageFormat.Jpeg
        };
        try
        {
            await nikon.SetImageFormatAsync(format);
            _formatStatus.Text = $"Format: {_imageFormat.SelectedItem} applied.";
            Log($"Capture format set to {_imageFormat.SelectedItem}.");
        }
        catch (Exception exception)
        {
            _formatStatus.Text = $"Format: failed to apply ({exception.Message})";
            Log($"ERROR: could not set capture format: {exception.Message}");
        }
    }
    private Task Capture() => CaptureManyInternal(1);
    private Task CaptureMany() => int.TryParse(_count.Text, out var count) && count > 0 ? CaptureManyInternal(count) : Task.CompletedTask;
    private async Task CaptureManyInternal(int count)
    {
        if (!Connected()) return;
        _operation = new CancellationTokenSource(); var subject = string.IsNullOrWhiteSpace(_subject.Text) ? "TEST001" : _subject.Text.Trim();
        var sessionId = Guid.NewGuid().ToString("N");
        try
        {
            await _store.StartSessionAsync(sessionId, subject, _operation.Token);
            for (var shot = 1; shot <= count; shot++)
            {
                _operation.Token.ThrowIfCancellationRequested();
                var baseFolder = string.IsNullOrWhiteSpace(_saveFolder.Text) ? Path.Combine(Environment.CurrentDirectory, "captures") : _saveFolder.Text.Trim();
                var request = new CaptureRequest(sessionId, subject, "MANUAL", shot, Path.Combine(baseFolder, subject));
                var result = await _camera.CaptureAsync(request, _operation.Token); _attempts++;
                await _store.RecordCaptureAsync(sessionId, request, result, _operation.Token);
                if (result.Success) _successes++; else _failures++;
                _destination.Text = $"Destination: {request.DestinationFolder}";
                _lastCapture.Text = $"Last capture: {result.State} | {result.LocalPath ?? result.Error} | capture={result.CaptureDuration?.TotalMilliseconds:0}ms transfer={result.TransferDuration?.TotalMilliseconds:0}ms"; UpdateCounters();
                UpdateLastCapturePreview(result);
                await ScoreCaptureIfEnabledAsync(sessionId, request, result, _operation.Token);
                if (int.TryParse(_interval.Text, out var interval) && interval > 0 && shot < count) await Task.Delay(interval, _operation.Token);
            }
            await _store.CompleteSessionAsync(sessionId, _operation.Token);
        }
        catch (OperationCanceledException) { Log("Operation stopped."); }
        finally { _operation.Dispose(); _operation = null; await RefreshRecoveryAsync(); }
    }

    private async Task Stress()
    {
        if (!Connected() || !int.TryParse(_count.Text, out var count) || count < 1) return;
        _operation = new CancellationTokenSource();
        try
        {
            var folder = Path.Combine(Environment.CurrentDirectory, "captures", "stress");
            var report = await new StressTestEngine(_camera).RunAsync(new StressTestOptions(count), folder, _operation.Token);
            await File.WriteAllTextAsync(Path.Combine(folder, "stress-report.json"), report.ToJson(), _operation.Token);
            _attempts += report.Attempts; _successes += report.Successes; _failures += report.Failures; UpdateCounters(); Log($"STRESS: attempts={report.Attempts}, successes={report.Successes}, failures={report.Failures}, unaccounted={report.UnaccountedAttempts}; report saved.");
        }
        catch (OperationCanceledException) { Log("Stress test stopped."); }
        finally { _operation.Dispose(); _operation = null; }
    }

    private Task Stop() { _operation?.Cancel(); return Task.CompletedTask; }
    private async Task StartLiveView()
    {
        if (_camera is not NikonRemoteSdkV2CameraDriver nikon) { Log("ERROR: live view is only available with the Nikon Remote SDK v2 driver."); return; }
        try { await nikon.StartLiveViewAsync(); _liveViewStatus.Text = "Live view: on"; Log("Live view started."); }
        catch (Exception exception) { Log($"ERROR: live view failed to start: {exception.Message}"); }
    }
    private async Task StopLiveView()
    {
        if (_camera is not NikonRemoteSdkV2CameraDriver nikon) { Log("ERROR: live view is only available with the Nikon Remote SDK v2 driver."); return; }
        try { await nikon.StopLiveViewAsync(); _liveViewStatus.Text = "Live view: off"; _liveViewImage.Source = null; Log("Live view stopped."); }
        catch (Exception exception) { Log($"ERROR: live view failed to stop: {exception.Message}"); }
    }
    private Task InjectDisconnect()
    {
        if (_camera is SimulatedCameraDriver sim) { sim.InjectDisconnect(); Log("Next capture will disconnect."); }
        else Log("ERROR: fault injection is only available with the Simulator driver.");
        return Task.CompletedTask;
    }
    private Task InjectCaptureFailure()
    {
        if (_camera is SimulatedCameraDriver sim) { sim.FailureRate = 1; sim.TransferFailureRate = 0; Log("Capture failure injection enabled."); }
        else Log("ERROR: fault injection is only available with the Simulator driver.");
        return Task.CompletedTask;
    }
    private Task InjectTransferFailure()
    {
        if (_camera is SimulatedCameraDriver sim) { sim.TransferFailureRate = 1; sim.FailureRate = 0; Log("Transfer failure injection enabled."); }
        else Log("ERROR: fault injection is only available with the Simulator driver.");
        return Task.CompletedTask;
    }
    private Task ClearFaults()
    {
        if (_camera is SimulatedCameraDriver sim) { sim.FailureRate = 0; sim.TransferFailureRate = 0; Log("Failure injections cleared."); }
        else Log("ERROR: fault injection is only available with the Simulator driver.");
        return Task.CompletedTask;
    }
    private bool Connected() { if (_camera.State == CameraConnectionState.Connected) return true; Log("ERROR: connect the camera first."); return false; }
    private void UpdateCamera() { _statusText.Text = $"Connection: {_camera.State}"; _cameraText.Text = _camera.Info is null ? "Camera: unavailable" : $"Camera: {_camera.Info.Manufacturer} {_camera.Info.Model} | ID: {_camera.Info.SerialNumber} | Tier: {_camera.Info.CompatibilityTier} | Capabilities: {_camera.Info.Capabilities}"; }
    private void UpdateCounters() => _counters.Text = $"Attempts: {_attempts} | Successes: {_successes} | Failures: {_failures}";
    private void Log(string message) { _log.AppendText($"[{DateTime.Now:HH:mm:ss.fff}] {message}{Environment.NewLine}"); _log.ScrollToEnd(); }
    private static string Error(string? error) => string.IsNullOrWhiteSpace(error) ? string.Empty : $" ({error})";
}