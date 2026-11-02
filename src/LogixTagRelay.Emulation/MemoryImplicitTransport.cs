namespace LogixTagRelay.Emulation;

public sealed class MemoryImplicitTransport(ImplicitPlc plc) : IImplicitTransport
{
    public ValueTask<byte[]?> ExchangeAsync(byte[] image, CancellationToken cancellationToken)
    { cancellationToken.ThrowIfCancellationRequested(); return ValueTask.FromResult<byte[]?>(plc.Exchange(image)); }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
