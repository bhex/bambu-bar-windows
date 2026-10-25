using BambuBar.Core.Protocol;

namespace BambuBar.Core.Demo;

/// <summary>
/// <c>--probe &lt;ip&gt; &lt;serial&gt; &lt;access-code&gt; [seconds]</c>: connects to a real printer (or the
/// local emulator) and prints what it reports. For diagnostics; remove the serial number and access
/// code before posting the output anywhere.
/// </summary>
public static class Probe
{
    public static async Task<int> Run(string host, string serial, string code, int seconds)
    {
        Console.WriteLine($"Probing {host} (serial {serial})…");
        var client = new BambuMqttClient();
        var failure = await client.ConnectAsync(host, 8883, serial, code, insecure: true, CancellationToken.None);
        if (failure is not null)
        {
            Console.WriteLine($"  {failure.Kind}: {failure.Reason}");
            Console.WriteLine($"  Certificate verdict: {BambuTrust.LastVerdict.Reason}");
            await client.DisposeAsync();
            return 1;
        }

        Console.WriteLine("  Connected (TLS 1.2, bblp). Certificate verdict: " +
            (BambuTrust.LastVerdict.Verdict == BambuTrust.Verdict.Trusted ? "trusted" : "see above"));

        var document = new ReportDocument();

        // Listen before asking, or the printer's instant reply races us.
        client.MessageReceived += (topic, payload) =>
        {
            try
            {
                var json = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, object?>>(payload);
                if (json is null) return;
                var changed = document.Apply(json);
                if (changed && document.HasFullReport && json.TryGetValue("print", out _))
                {
                    var status = ReportParser.From(document, HmsCatalog.Shared);
                    Console.WriteLine($"  {status.State,-10} {status.Progress,3}%  " +
                        $"nozzle {status.Nozzle.Current:0}/{status.Nozzle.Target:0}°  " +
                        $"bed {status.Bed.Current:0}/{status.Bed.Target:0}°  " +
                        $"layer {status.Layer}/{status.TotalLayers}  {status.RemainingMinutes} min left");
                }
            }
            catch
            {
                // Probe output is best-effort.
            }
        };

        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(seconds));
        try
        {
            await client.SubscribeAsync($"device/{serial}/report", cts.Token);
            // Same bootstrap as a normal session: version first, then a full report.
            await client.PublishAsync($"device/{serial}/request",
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    new Dictionary<string, object?> { ["info"] = new Dictionary<string, object?> { ["sequence_id"] = "0", ["command"] = "get_version" } }),
                cts.Token);
            await client.PublishAsync($"device/{serial}/request",
                System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
                    new Dictionary<string, object?> { ["pushing"] = new Dictionary<string, object?> { ["sequence_id"] = "0", ["command"] = "pushall" } }),
                cts.Token);
        }
        catch (OperationCanceledException)
        {
            // Time's up — fall through and print what we got.
        }

        Console.WriteLine("  (use Ctrl+C or wait for the time limit to stop)");
        try
        {
            await Task.Delay(seconds > 0 ? TimeSpan.FromSeconds(seconds) : TimeSpan.FromSeconds(10), CancellationToken.None);
        }
        catch
        {
            // Interrupted.
        }

        Console.WriteLine("Firmware modules:");
        foreach (var (name, module) in document.Modules)
            Console.WriteLine($"  {name,-16} {module.Product,-24} {module.Version}");

        await client.DisposeAsync();
        return 0;
    }
}
