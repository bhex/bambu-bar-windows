using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using MQTTnet;
using MQTTnet.Client;
using MQTTnet.Packets;
using MQTTnet.Protocol;

namespace BambuBar.Core.Protocol;

/// <summary>
/// A small MQTT 3.1.1 client for the Bambu printer's broker: CONNECT with username/password,
/// SUBSCRIBE, QoS 0 PUBLISH both ways, keepalive pings.
///
/// Security: TLS is pinned to 1.2 (the P2S firmware never answers a TLS 1.3 ClientHello) and the
/// server certificate is evaluated by <see cref="BambuTrust"/> — bundled Bambu roots only, leaf named
/// after the expected serial. The Windows certificate store is never consulted.
/// </summary>
public sealed class BambuMqttClient : IAsyncDisposable
{
    public sealed record Failure(FailureKind Kind, string Reason, int? ConnAckCode)
    {
        /// <summary>CONNACK 4 (bad user/password) or 5 (not authorised).</summary>
        public bool IsAuthentication => ConnAckCode is 4 or 5;
    }

    public enum FailureKind
    {
        /// <summary>CONNACK refused (4 bad username/password, 5 not authorised).</summary>
        Refused,
        Unreachable,
        TimedOut,
        Closed,
        /// <summary>The certificate failed <see cref="BambuTrust"/> evaluation.</summary>
        Certificate,
    }

    /// <summary>Raised once the CONNACK arrived.</summary>
    public event Action? Connected;

    /// <summary>Raised for every PUBLISH from the printer: topic, payload.</summary>
    public event Action<string, byte[]>? MessageReceived;

    /// <summary>Raised when the session ends (refused, dropped, certificate, closed).</summary>
    public event Action<Failure>? Disconnected;

    private readonly IMqttClient _client;
    private readonly object _gate = new();
    private bool _finished;

    public BambuMqttClient()
    {
        _client = new MqttFactory().CreateMqttClient();
        _client.ApplicationMessageReceivedAsync += args =>
        {
            OnApplicationMessageReceived(args);
            return Task.CompletedTask;
        };
        _client.DisconnectedAsync += args =>
        {
            OnDisconnected(args);
            return Task.CompletedTask;
        };
    }

    /// <summary>
    /// Connects to <c>host:port</c> as user <c>bblp</c> with the access code.
    /// </summary>
    public async Task<Failure?> ConnectAsync(
        string host, int port, string serial, string accessCode, bool insecure, CancellationToken cancellationToken)
    {
        var clientId = "bambubar-" + Guid.NewGuid().ToString("N")[..12];
        var builder = new MqttClientOptionsBuilder()
            .WithClientId(clientId)
            .WithCredentials("bblp", accessCode)
            .WithCleanSession()
            .WithKeepAlivePeriod(TimeSpan.FromSeconds(30))
            .WithTimeout(TimeSpan.FromSeconds(12))
            .WithTcpServer(host, port)
            .WithTlsOptions(new MqttClientTlsOptions
            {
                UseTls = true,
                // The printer's TLS stack only speaks 1.2; pin it so we never silently downgrade.
                SslProtocol = SslProtocols.Tls12,
                // We validate ourselves against the bundled Bambu roots — never the OS store.
                CertificateValidationHandler = args =>
                {
                    if (insecure)
                        return true; // Emulator mode: self-signed test broker.

                    var leaf = args.Certificate is X509Certificate2 existing ? existing : new X509Certificate2(args.Certificate);
                    var presented = args.Chain?.ChainElements
                        .Select(e => e.Certificate)
                        .Where(c => c.Thumbprint != leaf.Thumbprint)
                        .ToArray() ?? Array.Empty<X509Certificate2>();

                    var verdict = BambuTrust.Evaluate(leaf, presented, serial);
                    return verdict.Verdict == BambuTrust.Verdict.Trusted;
                },
            });

        var options = builder.Build();

        try
        {
            var result = await _client.ConnectAsync(options, cancellationToken);
            if (result.ResultCode == MqttClientConnectResultCode.Success)
            {
                Connected?.Invoke();
                return null;
            }
            return new Failure(FailureKind.Refused, result.ReasonString ?? "The printer refused the connection.", (int)result.ResultCode);
        }
        catch (OperationCanceledException)
        {
            return new Failure(FailureKind.TimedOut, "The connection timed out.", null);
        }
        catch (Exception ex)
        {
            return new Failure(FailureKind.Unreachable, Describe(ex), null);
        }
    }

    public async Task SubscribeAsync(string topic, CancellationToken cancellationToken)
    {
        var options = new MqttClientSubscribeOptions
        {
            TopicFilters =
            {
                new MqttTopicFilter
                {
                    Topic = topic,
                    QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce,
                },
            },
        };
        await _client.SubscribeAsync(options, cancellationToken);
    }

    public async Task PublishAsync(string topic, byte[] payload, CancellationToken cancellationToken)
    {
        var message = new MqttApplicationMessage
        {
            Topic = topic,
            PayloadSegment = new ArraySegment<byte>(payload),
            QualityOfServiceLevel = MqttQualityOfServiceLevel.AtMostOnce,
            Retain = false,
        };
        await _client.PublishAsync(message, cancellationToken);
    }

    public async Task DisconnectAsync()
    {
        try
        {
            if (_client.IsConnected)
                await _client.DisconnectAsync(new MqttClientDisconnectOptions
                {
                    Reason = MqttClientDisconnectOptionsReason.NormalDisconnection,
                }, CancellationToken.None);
        }
        catch
        {
            // Closing a dying connection should never throw into the session loop.
        }
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
        _client.Dispose();
    }

    // MARK: - Events

    private void OnApplicationMessageReceived(MqttApplicationMessageReceivedEventArgs args)
    {
        lock (_gate)
        {
            if (_finished) return;
        }
        MessageReceived?.Invoke(args.ApplicationMessage.Topic, args.ApplicationMessage.PayloadSegment.ToArray());
    }

    private void OnDisconnected(MqttClientDisconnectedEventArgs args)
    {
        lock (_gate)
        {
            if (_finished) return;
            _finished = true;
        }

        if (args.Reason == MqttClientDisconnectReason.NormalDisconnection)
        {
            Disconnected?.Invoke(new Failure(FailureKind.Closed, "The connection to the printer closed.", null));
            return;
        }

        if (args.Exception is not null)
        {
            // A rejected server certificate surfaces here on the TLS handshake.
            var text = args.Exception.ToString();
            if (text.Contains("remote certificate", StringComparison.OrdinalIgnoreCase)
                || text.Contains("CERT", StringComparison.OrdinalIgnoreCase)
                || text.Contains("ssl", StringComparison.OrdinalIgnoreCase)
                || text.Contains("tls", StringComparison.OrdinalIgnoreCase)
                || text.Contains("authentication", StringComparison.OrdinalIgnoreCase))
            {
                Disconnected?.Invoke(new Failure(FailureKind.Certificate, BambuTrust.LastVerdict.Reason, null));
                return;
            }
            Disconnected?.Invoke(new Failure(FailureKind.Unreachable, Describe(args.Exception), null));
            return;
        }

        Disconnected?.Invoke(new Failure(FailureKind.Closed, "The connection to the printer closed.", null));
    }

    private static string Describe(Exception ex)
    {
        var inner = ex.InnerException ?? ex;
        var message = inner.Message;
        if (message.Contains("refused", StringComparison.OrdinalIgnoreCase)
            || message.Contains("connection refused", StringComparison.OrdinalIgnoreCase))
            return "The printer refused the connection.";
        if (message.Contains("timed out", StringComparison.OrdinalIgnoreCase)
            || message.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return "The connection timed out.";
        return message.Length > 0 ? message : inner.GetType().Name;
    }
}
