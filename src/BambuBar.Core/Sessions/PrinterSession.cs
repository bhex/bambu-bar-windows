using System.Text;
using System.Text.Json;
using BambuBar.Core.Models;
using BambuBar.Core.Protocol;

namespace BambuBar.Core.Sessions;

/// <summary>
/// Keeps one MQTT session to the printer alive: connects, re-requests a full report, merges the deltas
/// into a status, and reconnects with back-off when the printer drops off the network or the machine sleeps.
/// </summary>
public sealed class PrinterSession : IAsyncDisposable
{
    public enum Phase
    {
        Idle,
        Connecting,
        Online,
        Offline,
        Unauthorized,
        Certificate,
    }

    public Phase CurrentPhase { get; private set; } = Phase.Idle;
    public string? PhaseReason { get; private set; }
    public PrinterStatus? LastStatus { get; private set; }
    /// <summary>When the printer last sent anything, to the second ("Updated 3s ago").</summary>
    public DateTime? LastHeard { get; private set; }

    /// <summary>Raised whenever the phase settles (not for the connecting ↔ offline flapping of retries).</summary>
    public event Action<Phase, string?>? PhaseChanged;
    public event Action<PrinterStatus>? StatusChanged;
    /// <summary>Command acknowledgement: command, success, reason.</summary>
    public event Action<string, bool, string?>? Acknowledged;
    /// <summary>Any message from the printer, changed or not.</summary>
    public event Action? Heartbeat;

    private readonly object _gate = new();
    private readonly ReportDocument _document = new();

    private PrinterConfig? _config;

    /// <summary>The printer this session talks to (host, serial, name, model).</summary>
    public PrinterConfig? Config => _config;

    /// <summary>Associates the config before the first <see cref="Start"/> (used by the demo driver).</summary>
    public void AttachConfig(PrinterConfig config)
    {
        lock (_gate) _config = config;
    }

    public void UpdateName(string name)
    {
        lock (_gate)
        {
            if (_config is not null) _config.Name = name;
        }
    }

    /// <summary>Feeds a simulated status (demo mode) through the same surface as a live report.</summary>
    public void RaiseDemoStatus(PrinterStatus status)
    {
        lock (_gate) _lastStatus = status;
        LastStatus = status;
        StatusChanged?.Invoke(status);
    }
    private string _accessCode = "";
    private bool _insecure;
    private int _attempt;
    private DateTime _lastMessageAt;
    private DateTime _lastPushallAt;
    private int _sequence;
    private PrinterStatus? _lastStatus;

    private CancellationTokenSource? _stopCts;
    private CancellationTokenSource? _attemptCts;
    private Task? _runTask;
    private System.Threading.Timer? _watchdog;
    private BambuMqttClient? _client;
    private TaskCompletionSource? _disconnectTcs;
    private BambuMqttClient.Failure? _pendingFailure;

    // MARK: - Lifecycle

    public void Start(PrinterConfig config, string accessCode, bool insecure = false)
    {
        lock (_gate)
        {
            StopLocked();
            _config = config;
            _accessCode = accessCode;
            _insecure = insecure;
            _attempt = 0;
            _lastStatus = null;
            LastStatus = null;
            _document.Reset();
            _stopCts = new CancellationTokenSource();
            _runTask = Task.Run(RunAsync);
            _watchdog = new System.Threading.Timer(_ => CheckHealth(), null, 10_000, 10_000);
        }
    }

    public void Stop()
    {
        lock (_gate)
        {
            StopLocked();
            SetPhase(Phase.Idle, null);
        }
    }

    private void StopLocked()
    {
        _watchdog?.Dispose();
        _watchdog = null;
        _attemptCts?.Cancel();
        _attemptCts?.Dispose();
        _attemptCts = null;
        var client = _client;
        _client = null;
        if (client is not null)
        {
            client.DisconnectAsync().ContinueWith(_ => { });
            client.DisposeAsync().AsTask().ContinueWith(_ => { });
        }
        _stopCts?.Cancel();
        _stopCts?.Dispose();
        _stopCts = null;
    }

    /// <summary>Asks for a full report now (after wake, or when the person clicks refresh).</summary>
    public void Refresh()
    {
        lock (_gate)
        {
            if (CurrentPhase == Phase.Online)
            {
                RequestFullReport(_client);
            }
            else if (_config is not null)
            {
                ForceReconnect();
            }
        }
    }

    public void Suspend()
    {
        lock (_gate)
        {
            _attemptCts?.Cancel();
            var client = _client;
            _client = null;
            client?.DisconnectAsync().ContinueWith(_ => { });
        }
    }

    // MARK: - Commands

    public void Pause() => SendPrint(new Dictionary<string, object?> { ["command"] = "pause" });
    public void Resume() => SendPrint(new Dictionary<string, object?> { ["command"] = "resume" });
    public void StopPrint() => SendPrint(new Dictionary<string, object?> { ["command"] = "stop" });

    public void SetSpeed(SpeedLevel level) =>
        SendPrint(new Dictionary<string, object?> { ["command"] = "print_speed", ["param"] = ((int)level).ToString() });

    public void SetChamberLight(bool on) => Send(new Dictionary<string, object?>
    {
        ["system"] = new Dictionary<string, object?>
        {
            ["command"] = "ledctrl",
            ["led_node"] = "chamber_light",
            ["led_mode"] = on ? "on" : "off",
            ["led_on_time"] = 500,
            ["led_off_time"] = 500,
            ["loop_times"] = 0,
            ["interval_time"] = 0,
        },
    }, "system");

    private void SendPrint(Dictionary<string, object?> body) =>
        Send(new Dictionary<string, object?> { ["print"] = body }, "print");

    private void Send(Dictionary<string, object?> message, string key)
    {
        lock (_gate)
        {
            if (_config is not { } config || _client is not { } client || CurrentPhase != Phase.Online) return;
            Interlocked.Increment(ref _sequence);
            if (message.TryGetValue(key, out var inner) && inner is Dictionary<string, object?> body)
                body["sequence_id"] = _sequence.ToString();
            var data = JsonSerializer.SerializeToUtf8Bytes(message);
            client.PublishAsync($"device/{config.Serial}/request", data, CancellationToken.None)
                .ContinueWith(_ => { });
        }
    }

    private void RequestFullReport(BambuMqttClient? client)
    {
        _lastPushallAt = DateTime.Now;
        SendRaw(client, new Dictionary<string, object?> { ["pushing"] = new Dictionary<string, object?> { ["sequence_id"] = "0", ["command"] = "pushall" } });
    }

    private void SendRaw(BambuMqttClient? client, Dictionary<string, object?> message)
    {
        if (client is null) return;
        var config = _config;
        if (config is null) return;
        var data = JsonSerializer.SerializeToUtf8Bytes(message);
        client.PublishAsync($"device/{config.Serial}/request", data, CancellationToken.None)
            .ContinueWith(_ => { });
    }

    // MARK: - Connection loop

    private async Task RunAsync()
    {
        while (true)
        {
            if (_stopCts is null || _stopCts.IsCancellationRequested) break;

            var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(_stopCts.Token);
            lock (_gate) _attemptCts = attemptCts;

            var failure = await ConnectOnceAsync(attemptCts.Token);

            if (_stopCts is null || _stopCts.IsCancellationRequested) break;

            switch (failure)
            {
                case null:
                    // The session was closed (stop) — fall through and re-check the stop flag.
                    failure = new BambuMqttClient.Failure(BambuMqttClient.FailureKind.Closed, "The connection to the printer closed.", null);
                    break;
                case { Kind: BambuMqttClient.FailureKind.Refused, IsAuthentication: true } f:
                    // A wrong access code won't fix itself; wait for the person to change it.
                    SetPhase(Phase.Unauthorized, null);
                    return;
                case { Kind: BambuMqttClient.FailureKind.Certificate } f:
                    // A certificate that doesn't check out won't fix itself either; don't keep sending it the code.
                    SetPhase(Phase.Certificate, f.Reason);
                    return;
            }

            SetPhase(Phase.Offline, failure?.Reason ?? "The connection to the printer closed.");

            var delays = new[] { 2, 4, 8, 15, 30 };
            var delay = delays[Math.Min(_attempt, delays.Length - 1)];
            _attempt++;
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(delay), _stopCts.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task<BambuMqttClient.Failure?> ConnectOnceAsync(CancellationToken ct)
    {
        var config = _config;
        if (config is null) return null;

        SetPhase(Phase.Connecting, null);
        _lastMessageAt = DateTime.Now;

        var client = new BambuMqttClient();
        var disconnectTcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            _client = client;
            _disconnectTcs = disconnectTcs;
            _pendingFailure = null;
        }

        client.Disconnected += f =>
        {
            lock (_gate) _pendingFailure = f;
            disconnectTcs.TrySetResult();
        };
        client.MessageReceived += OnMessage;

        var failure = await client.ConnectAsync(config.Host, 8883, config.Serial, _accessCode, _insecure, ct);
        if (failure is not null)
        {
            await client.DisposeAsync();
            return failure;
        }

        // Stay "connecting" until the first report arrives, so a silent broker doesn't look online.
        try
        {
            await client.SubscribeAsync($"device/{config.Serial}/report", ct);
            SendRaw(client, new Dictionary<string, object?>
            {
                ["info"] = new Dictionary<string, object?> { ["sequence_id"] = "0", ["command"] = "get_version" },
            });
            RequestFullReport(client);
        }
        catch (OperationCanceledException)
        {
            // Stopped or watchdog-forced; fall through.
        }

        var winner = await Task.WhenAny(disconnectTcs.Task, Task.Delay(Timeout.Infinite, ct));
        if (winner == disconnectTcs.Task)
        {
            lock (_gate)
            {
                _pendingFailure ??= new BambuMqttClient.Failure(BambuMqttClient.FailureKind.Closed, "The connection to the printer closed.", null);
                return _pendingFailure;
            }
        }

        // Cancelled (stop or watchdog): close the client and report that the session was cut.
        try
        {
            await client.DisconnectAsync();
        }
        catch
        {
            // Already gone.
        }
        return new BambuMqttClient.Failure(BambuMqttClient.FailureKind.Closed, "The connection to the printer closed.", null);
    }

    private void OnMessage(string topic, byte[] payload)
    {
        var json = Decode(payload);
        if (json is null) return;

        Heartbeat?.Invoke();

        if (JsonTools.GetDictionary(JsonTools.Get(json, "print")) is { } p &&
            JsonTools.GetString(JsonTools.Get(p, "command")) is { } command && command != "push_status" &&
            JsonTools.GetString(JsonTools.Get(p, "result")) is { } result)
        {
            Acknowledged?.Invoke(command, result.Equals("success", StringComparison.OrdinalIgnoreCase),
                JsonTools.GetString(JsonTools.Get(p, "reason")));
        }

        var changed = false;
        lock (_gate)
        {
            changed = _document.Apply(json);
        }

        if (!changed) return;

        bool hasUsable;
        lock (_gate)
        {
            hasUsable = _document.HasFullReport || _document.Print.ContainsKey("gcode_state");
        }
        if (!hasUsable) return;

        lock (_gate)
        {
            _lastMessageAt = DateTime.Now;
            if (CurrentPhase != Phase.Online)
            {
                // Back-off resets only once the printer is really talking, not on a CONNACK alone.
                _attempt = 0;
            }
        }
        if (CurrentPhase != Phase.Online) SetPhase(Phase.Online, null);

        PublishStatus();
    }

    private void PublishStatus()
    {
        var status = ReportParser.From(_document, HmsCatalog.Shared);
        PrinterStatus? last;
        lock (_gate) last = _lastStatus;

        if (last is not null)
        {
            // Keep the timestamp the remaining time was measured at, so the ETA doesn't drift between reports.
            var same = CloneWithoutTimestamp(status);
            same.UpdatedAt = last.UpdatedAt;
            if (StatusEquals(same, last)) return;
            if (status.RemainingMinutes == last.RemainingMinutes
                && status.Progress == last.Progress
                && status.State == last.State)
            {
                status.UpdatedAt = last.UpdatedAt;
            }
        }

        lock (_gate) _lastStatus = status;
        LastStatus = status;
        StatusChanged?.Invoke(status);
    }

    private static PrinterStatus CloneWithoutTimestamp(PrinterStatus s)
    {
        var clone = JsonSerializer.Deserialize<PrinterStatus>(JsonSerializer.Serialize(s))!;
        clone.UpdatedAt = default;
        return clone;
    }

    private static bool StatusEquals(PrinterStatus a, PrinterStatus b) =>
        JsonSerializer.Serialize(CloneWithoutTimestamp(a)) == JsonSerializer.Serialize(CloneWithoutTimestamp(b));

    // MARK: - Watchdog

    private void CheckHealth()
    {
        lock (_gate)
        {
            if (_config is null) return;
            var silence = DateTime.Now - _lastMessageAt;
            switch (CurrentPhase)
            {
                case Phase.Online:
                    if (silence > TimeSpan.FromSeconds(95))
                    {
                        SetPhase(Phase.Offline, "The printer stopped sending updates.");
                        ForceReconnect();
                    }
                    else if (silence > TimeSpan.FromSeconds(60) || DateTime.Now - _lastPushallAt > TimeSpan.FromSeconds(300))
                    {
                        RequestFullReport(_client);
                    }
                    break;
                case Phase.Connecting:
                    if (_client is not null && silence > TimeSpan.FromSeconds(25))
                        ForceReconnect();
                    break;
            }
        }
    }

    private void ForceReconnect()
    {
        _attemptCts?.Cancel();
    }

    private void SetPhase(Phase phase, string? reason)
    {
        var changed = false;
        lock (_gate)
        {
            changed = CurrentPhase != phase || PhaseReason != reason;
            CurrentPhase = phase;
            PhaseReason = reason;
        }
        if (changed) PhaseChanged?.Invoke(phase, reason);
    }

    /// <summary>
    /// Some firmware sends bytes that aren't valid UTF-8; fall back to Latin-1 rather than drop the report.
    /// </summary>
    private static Dictionary<string, object?>? Decode(byte[] data)
    {
        try
        {
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(data);
        }
        catch (JsonException)
        {
            // fall through
        }
        try
        {
            var text = Encoding.Latin1.GetString(data);
            return System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(text);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public async ValueTask DisposeAsync()
    {
        Stop();
        var client = _client;
        if (client is not null)
        {
            _client = null;
            await client.DisposeAsync();
        }
    }
}
