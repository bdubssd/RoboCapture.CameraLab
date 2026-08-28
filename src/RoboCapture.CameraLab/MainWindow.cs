using System.IO;
using System.Windows;
using System.Windows.Controls;
using RoboCapture.Core;
using RoboCapture.NikonAdapter;
using RoboCapture.Persistence;

namespace RoboCapture.CameraLab;

public sealed class MainWindow : Window
{
    private ICameraDriver _camera = null!;
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
    private readonly TextBox _log = new() { IsReadOnly = true, AcceptsReturn = true, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
    private readonly TextBlock _rosterStatus = new() { Text = "Roster: none loaded (scans use raw value as subject ID)" };
    private readonly StackPanel _recovery = new();
    private readonly ComboBox _cameraType = new() { MinWidth = 190 };
    private readonly TextBox _moduleFolder = new() { MinWidth = 260 };
    private readonly TextBox _moduleFile = new() { MinWidth = 150 };
    private CancellationTokenSource? _operation;
    private int _attempts, _successes, _failures;
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
        };
        Closed += async (_, _) => await _camera.DisposeAsync();
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
    }

    private UIElement Layout()
    {
        var root = new DockPanel { Margin = new Thickness(16) };
        var header = new StackPanel();
        header.Children.Add(new TextBlock { Text = "CAMERA LAB 0.2", FontSize = 24, FontWeight = FontWeights.Bold });
        header.Children.Add(_cameraText); header.Children.Add(_statusText);
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);

        var driverRow = new WrapPanel { Margin = new Thickness(0, 8, 0, 0) };
        _cameraType.Items.Add("Simulator");
        _cameraType.Items.Add("Nikon (Legacy MAID3)");
        _cameraType.Items.Add("Nikon (Remote SDK v2)");
        _cameraType.SelectedIndex = 0;
        _cameraType.SelectionChanged += (_, _) => UpdateDriverFields();
        driverRow.Children.Add(new Label { Content = "Driver" }); driverRow.Children.Add(_cameraType);
        driverRow.Children.Add(new Label { Content = "Module folder" }); driverRow.Children.Add(_moduleFolder);
        var browseButton = new Button { Content = "BROWSE...", Margin = new Thickness(2), Padding = new Thickness(7, 4, 7, 4) };
        browseButton.Click += (_, _) =>
        {
            var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Select the Nikon module folder" };
            if (dialog.ShowDialog() == true) _moduleFolder.Text = dialog.FolderName;
        };
        driverRow.Children.Add(browseButton);
        driverRow.Children.Add(new Label { Content = "Module file" }); driverRow.Children.Add(_moduleFile);
        var switchButton = new Button { Content = "SWITCH CAMERA", Margin = new Thickness(2), Padding = new Thickness(7, 4, 7, 4) };
        switchButton.Click += async (_, _) => await SwitchCamera();
        driverRow.Children.Add(switchButton);
        DockPanel.SetDock(driverRow, Dock.Top); root.Children.Add(driverRow);
        UpdateDriverFields();

        var buttons = new WrapPanel { Margin = new Thickness(0, 12, 0, 8) };
        Add(buttons, "CONNECT", Connect); Add(buttons, "DISCONNECT", Disconnect); Add(buttons, "CAPTURE", Capture);
        Add(buttons, "CAPTURE X N", CaptureMany); Add(buttons, "RUN STRESS TEST", Stress); Add(buttons, "STOP", Stop);
        Add(buttons, "INJECT DISCONNECT", InjectDisconnect); Add(buttons, "INJECT CAPTURE FAILURE", InjectCaptureFailure);
        Add(buttons, "INJECT TRANSFER FAILURE", InjectTransferFailure); Add(buttons, "CLEAR FAULTS", ClearFaults); Add(buttons, "RECONNECT", Connect);
        Add(buttons, "LOAD ROSTER", LoadRoster);
        root.Children.Add(buttons);
        var inputs = new WrapPanel { Margin = new Thickness(0, 0, 0, 10) };
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
        root.Children.Add(new StackPanel { Children = { _rosterStatus, _destination, _lastCapture, _counters, _recovery } });
        root.Children.Add(_log); return root;
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

    private void UpdateDriverFields()
    {
        var isNikon = _cameraType.SelectedIndex is 1 or 2;
        _moduleFolder.IsEnabled = isNikon;
        if (!isNikon) return;
        _moduleFile.Text = _cameraType.SelectedIndex == 1 ? "Type0022.md3" : "ControlServiceLayer.dll";
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

        ICameraDriver next;
        try
        {
            next = _cameraType.SelectedIndex switch
            {
                1 => new NikonCameraDriver(_moduleFolder.Text, _moduleFile.Text),
                2 => new NikonRemoteSdkV2CameraDriver(_moduleFolder.Text, _moduleFile.Text),
                _ => new SimulatedCameraDriver { CaptureLatencyMs = 25, TransferLatencyMs = 10 }
            };
        }
        catch (Exception exception)
        {
            Log($"ERROR: could not create driver: {exception.Message}");
            WireCamera(new SimulatedCameraDriver { CaptureLatencyMs = 25, TransferLatencyMs = 10 });
            _cameraType.SelectedIndex = 0;
            UpdateCamera();
            return;
        }
        WireCamera(next);
        UpdateCamera();
        Log($"Camera driver switched to: {_cameraType.SelectedItem}");
    }

    private async Task Connect() { try { await _camera.ConnectAsync(); UpdateCamera(); Log("Connected."); } catch (Exception e) { Log($"ERROR: {e.Message}"); } }
    private async Task Disconnect() { await _camera.DisconnectAsync(); UpdateCamera(); Log("Disconnected."); }
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
                var request = new CaptureRequest(sessionId, subject, "MANUAL", shot, Path.Combine(Environment.CurrentDirectory, "captures", subject));
                var result = await _camera.CaptureAsync(request, _operation.Token); _attempts++;
                await _store.RecordCaptureAsync(sessionId, request, result, _operation.Token);
                if (result.Success) _successes++; else _failures++;
                _destination.Text = $"Destination: {request.DestinationFolder}";
                _lastCapture.Text = $"Last capture: {result.State} | {result.LocalPath ?? result.Error} | capture={result.CaptureDuration?.TotalMilliseconds:0}ms transfer={result.TransferDuration?.TotalMilliseconds:0}ms"; UpdateCounters();
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