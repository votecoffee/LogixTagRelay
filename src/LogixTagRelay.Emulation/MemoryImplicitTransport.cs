namespace LogixTagRelay.Emulation;

/// <summary>In-process implicit transport over the emulated PLC image protocol.</summary>
/// <param name="plc">Emulated PLC-side full-image protocol instance.</param>
public sealed class MemoryImplicitTransport(ImplicitPlc plc) : IImplicitTransport
{
    /// <summary>Sends one symmetric image and returns an available PLC image, or null when no fresh input is available.</summary>
    /// <param name="image">Complete symmetric relay image including its header.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public ValueTask<byte[]?> ExchangeAsync(byte[] image, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<byte[]?>(plc.Exchange(image)); }
    /// <summary>Releases owned resources. For running adapters, cancel and await RunAsync first.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
