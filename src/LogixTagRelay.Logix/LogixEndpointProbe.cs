using System.Buffers.Binary;
using System.Net.Sockets;
using System.Security.Cryptography;

namespace LogixTagRelay.Logix;

/// <summary>Checks TCP and EtherNet/IP registration without reading/writing a PLC tag or opening Class 1 I/O.</summary>
public static class LogixEndpointProbe
{
    public static async Task CheckAsync(string gateway, string route = "1,0", int timeoutMs = 5000, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gateway) || timeoutMs <= 0 || !Uri.TryCreate("tcp://" + gateway, UriKind.Absolute, out var address)) throw new ArgumentException("A PLC host[:port] and positive timeout are required.");
        int port = address.Port > 0 ? address.Port : 44818;
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(timeoutMs);
        using var tcp = new TcpClient(); PlcOperation operation = PlcOperation.Connect;
        try
        {
            await tcp.ConnectAsync(address.Host, port, timeout.Token).ConfigureAwait(false);
            operation = PlcOperation.RegisterSession;
            using var stream = tcp.GetStream();
            var request = new byte[28]; BinaryPrimitives.WriteUInt16LittleEndian(request, 0x65); BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(2), 4);
            RandomNumberGenerator.Fill(request.AsSpan(12, 8)); BinaryPrimitives.WriteUInt16LittleEndian(request.AsSpan(24), 1);
            await stream.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            var header = new byte[24]; await stream.ReadExactlyAsync(header, timeout.Token).ConfigureAwait(false);
            uint status = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(8));
            if (status != 0) throw new PlcConnectionException($"EtherNet/IP registration was rejected by '{gateway}' with encapsulation status 0x{status:X8}.", PlcFailureKind.ProtocolError, gateway, route,
                operation: operation, transient: false, nativeStatus: unchecked((int)status), nativeName: "EtherNet/IP encapsulation status");
            uint handle = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4));
            if (BinaryPrimitives.ReadUInt16LittleEndian(header) != 0x65 || BinaryPrimitives.ReadUInt16LittleEndian(header.AsSpan(2)) != 4 || handle == 0 ||
                !header.AsSpan(12, 8).SequenceEqual(request.AsSpan(12, 8)) || BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(20)) != 0)
                throw new PlcConnectionException($"'{gateway}' accepted TCP but returned an invalid EtherNet/IP registration reply.", PlcFailureKind.ProtocolError, gateway, route, operation: operation, transient: false);
            var payload = new byte[4]; await stream.ReadExactlyAsync(payload, timeout.Token).ConfigureAwait(false);
            var unregister = new byte[24]; BinaryPrimitives.WriteUInt16LittleEndian(unregister, 0x66); BinaryPrimitives.WriteUInt32LittleEndian(unregister.AsSpan(4), handle);
            await stream.WriteAsync(unregister, timeout.Token).ConfigureAwait(false);
            if (BinaryPrimitives.ReadUInt16LittleEndian(payload) != 1 || BinaryPrimitives.ReadUInt16LittleEndian(payload.AsSpan(2)) != 0)
                throw new PlcConnectionException($"'{gateway}' returned unsupported EtherNet/IP registration parameters.", PlcFailureKind.ProtocolError, gateway, route, operation: operation, transient: false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (OperationCanceledException error)
        { throw new PlcConnectionException($"{operation} to '{gateway}' timed out after {timeoutMs} ms. A timeout does not establish whether the host is unreachable.", PlcFailureKind.Timeout, gateway, route, error, operation); }
        catch (SocketException error)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var kind = error.SocketErrorCode switch
            {
                SocketError.NetworkUnreachable or SocketError.HostUnreachable or SocketError.NetworkDown => PlcFailureKind.Unreachable,
                SocketError.HostNotFound or SocketError.NoData => PlcFailureKind.HostNotFound,
                SocketError.ConnectionRefused => PlcFailureKind.ConnectionRefused,
                SocketError.TimedOut => PlcFailureKind.Timeout, _ => PlcFailureKind.ConnectionFailed
            };
            throw new PlcConnectionException($"{operation} to '{gateway}' failed ({error.SocketErrorCode}): {error.Message}", kind, gateway, route, error, operation);
        }
        catch (IOException error) when (error is not PlcCommunicationException)
        { cancellationToken.ThrowIfCancellationRequested(); throw new PlcConnectionException($"{operation} to '{gateway}' failed: {error.Message}", PlcFailureKind.ConnectionFailed, gateway, route, error, operation); }
    }
}
