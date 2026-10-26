using System.Net;
using System.Net.Sockets;
using System.Text;
using BambuBar.Core.Models;

namespace BambuBar.Core.Discovery;

/// <summary>A printer that announced itself on the local network.</summary>
public class DiscoveredPrinter
{
    public string Serial { get; init; } = "";
    public string Host { get; init; } = "";
    public string Name { get; init; } = "";
    public string ModelCode { get; init; } = "";
    public string? Firmware { get; init; }
    /// <summary>"lan" (LAN Only mode) or "cloud".</summary>
    public string? ConnectMode { get; init; }
    public DateTime LastSeen { get; init; }

    public string ModelName => PrinterModels.Name(ModelCode, Serial);
}

/// <summary>
/// Listens for the SSDP-style NOTIFY packets Bambu printers broadcast on UDP 2021 (and answers to an
/// M-SEARCH), so the printer can be picked from a list and its IP address followed when DHCP changes it.
/// </summary>
public sealed class PrinterDiscovery : IDisposable
{
    public event Action<DiscoveredPrinter>? Found;

    private readonly List<Socket> _sockets = new();
    private volatile bool _running;
    private readonly List<Thread> _listeners = new();

    public void Start()
    {
        if (_running) return;
        _running = true;
        foreach (var port in new[] { 2021u, 1990u })
        {
            var socket = OpenSocket(port);
            if (socket is null) continue;
            _sockets.Add(socket);
            var listener = new Thread(() => Listen(socket)) { IsBackground = true, Name = $"BambuBar.Discovery.{port}" };
            _listeners.Add(listener);
            listener.Start();
        }
        Search();
    }

    public void Stop()
    {
        _running = false;
        foreach (var socket in _sockets)
        {
            try
            {
                socket.Close();
            }
            catch
            {
                // Closing a half-open socket should not matter.
            }
        }
        _sockets.Clear();
    }

    /// <summary>Asks every printer to answer now instead of waiting for its next broadcast (they announce every ~30 s).</summary>
    public void Search()
    {
        // Sent from a listening socket: printers answer unicast to the port the search came from.
        var socket = _sockets.FirstOrDefault();
        if (socket is null) return;
        var message = "M-SEARCH * HTTP/1.1\r\nHOST: 239.255.255.250:1990\r\nMAN: \"ssdp:discover\"\r\nMX: 3\r\nST: urn:bambulab-com:device:3dprinter:1\r\n\r\n";
        var bytes = Encoding.UTF8.GetBytes(message);
        Task.Run(() =>
        {
            foreach (var (address, port) in new[] { ("239.255.255.250", 2021), ("239.255.255.250", 1990), ("255.255.255.255", 2021) })
            {
                try
                {
                    socket.SendTo(bytes, SocketFlags.None, IPEndPoint.Parse($"{address}:{port}"));
                }
                catch
                {
                    // One unreachable multicast target shouldn't sink the search.
                }
            }
        });
    }

    private static Socket? OpenSocket(uint port)
    {
        try
        {
            var socket = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
            socket.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            socket.Bind(new IPEndPoint(IPAddress.Any, (int)port));
            socket.SetSocketOption(SocketOptionLevel.IP, SocketOptionName.AddMembership,
                new MulticastOption(IPAddress.Parse("239.255.255.250")));
            return socket;
        }
        catch
        {
            return null;
        }
    }

    private void Listen(Socket socket)
    {
        var buffer = new byte[4096];
        while (_running)
        {
            string? sender = null;
            int n;
            try
            {
                // The endpoint MUST be pre-allocated: .NET 8 throws ArgumentNullException
                // for a null ref endpoint (unlike .NET Framework, which allocated one for us).
                EndPoint endpoint = new IPEndPoint(IPAddress.Any, 0);
                n = socket.ReceiveFrom(buffer, 0, buffer.Length, SocketFlags.None, ref endpoint);
                sender = (endpoint as IPEndPoint)?.Address.ToString();
            }
            catch (SocketException)
            {
                continue; // Would-block / interrupted.
            }
            catch (ObjectDisposedException)
            {
                return;
            }
            catch (Exception)
            {
                continue; // Discovery is best-effort; never take the app down with it.
            }

            if (n <= 0) continue;
            var text = Encoding.UTF8.GetString(buffer, 0, n);
            if (TryParse(text, sender, out var printer))
                Found?.Invoke(printer);
        }
    }

    public static bool TryParse(string text, string? sender, out DiscoveredPrinter printer)
    {
        printer = null!;
        if (!text.StartsWith("NOTIFY", StringComparison.Ordinal) && !text.StartsWith("HTTP/1.1 200", StringComparison.Ordinal))
            return false;

        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var lines = text.Split("\r\n");
        for (var i = 1; i < lines.Length; i++)
        {
            var line = lines[i];
            var colon = line.IndexOf(':');
            if (colon < 0) continue;
            headers[line[..colon].Trim()] = line[(colon + 1)..].Trim();
        }

        if (!headers.TryGetValue("usn", out var usn) || usn.Length == 0) return false;
        headers.TryGetValue("nt", out var nt);
        if (nt is null) headers.TryGetValue("st", out nt);
        if (nt is null) return false;
        var isBambu = nt.Contains("bambulab", StringComparison.OrdinalIgnoreCase);
        if (!isBambu) return false;

        var location = headers.GetValueOrDefault("location");
        printer = new DiscoveredPrinter
        {
            Serial = usn,
            Host = location is { Length: > 0 } ? location : (sender ?? ""),
            Name = headers.GetValueOrDefault("devname.bambu.com") ?? usn,
            ModelCode = headers.GetValueOrDefault("devmodel.bambu.com") ?? "",
            Firmware = headers.GetValueOrDefault("devversion.bambu.com"),
            ConnectMode = headers.GetValueOrDefault("devconnect.bambu.com"),
            LastSeen = DateTime.Now,
        };
        return true;
    }

    public void Dispose() => Stop();
}
