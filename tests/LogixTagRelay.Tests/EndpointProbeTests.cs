using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using LogixTagRelay;
using LogixTagRelay.Logix;

static class EndpointProbeTests
{
    public static async Task Run(Action<bool, string> check)
    {
        async Task<PlcConnectionException?> Probe(int mode)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
            using var listener = new TcpListener(IPAddress.Loopback, 0); listener.Start();
            string endpoint = "127.0.0.1:" + ((IPEndPoint)listener.LocalEndpoint).Port;
            bool unregistered = false;
            var peer = Task.Run(async () =>
            {
                using var client = await listener.AcceptTcpClientAsync(timeout.Token); using var stream = client.GetStream();
                byte[] request = new byte[28]; await stream.ReadExactlyAsync(request, timeout.Token);
                if (mode == 2) { await Task.Delay(400, timeout.Token); return; }
                byte[] reply = request.ToArray(); BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(4), 123);
                if (mode == 1) BinaryPrimitives.WriteUInt32LittleEndian(reply.AsSpan(8), 0x69);
                await stream.WriteAsync(reply, timeout.Token);
                if (mode == 0)
                {
                    var close = new byte[24]; await stream.ReadExactlyAsync(close, timeout.Token); unregistered = BinaryPrimitives.ReadUInt16LittleEndian(close) == 0x66;
                }
            });
            PlcConnectionException? fault = null;
            try { await using var transport = new LogixTransport(endpoint, timeoutMs: mode == 2 ? 100 : 1000); await transport.ConnectAsync(timeout.Token); }
            catch (PlcConnectionException error) { fault = error; }
            await peer;
            if (mode == 0) check(fault is null && unregistered, "initial connection validates EtherNet/IP registration and releases its probe session without tag access");
            return fault;
        }
        await Probe(0);
        var rejected = await Probe(1);
        check(rejected is { Operation: PlcOperation.RegisterSession, FailureKind: PlcFailureKind.ProtocolError, IsTransient: false, NativeStatus: 0x69 },
            "TCP reachability is differentiated from EtherNet/IP registration rejection");
        var timeoutFault = await Probe(2);
        check(timeoutFault is { Operation: PlcOperation.RegisterSession, FailureKind: PlcFailureKind.Timeout } && timeoutFault.Message.Contains("does not establish"),
            "registration timeout is not mislabeled as an unreachable PLC");
        using var unused = new TcpListener(IPAddress.Loopback, 0); unused.Start(); string closed = "127.0.0.1:" + ((IPEndPoint)unused.LocalEndpoint).Port; unused.Stop();
        PlcConnectionException? closedFault = null;
        try { await LogixEndpointProbe.CheckAsync(closed, timeoutMs: 1000); }
        catch (PlcConnectionException error) { closedFault = error; }
        check(closedFault?.Operation == PlcOperation.Connect &&
            ((closedFault.FailureKind == PlcFailureKind.ConnectionRefused && closedFault.SocketError == SocketError.ConnectionRefused) ||
             (closedFault.FailureKind == PlcFailureKind.Timeout && closedFault.InnerException is OperationCanceledException)),
            "closed endpoint preserves OS refusal or an earlier configured timeout, never inventing tag or reachability failures");
    }
}
