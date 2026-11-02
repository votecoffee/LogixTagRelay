using libplctag;
using NativeTag = libplctag.Tag;
using RelayType = LogixTagRelay.PlcType;

namespace LogixTagRelay.Logix;

/// <summary>Explicit symbolic reads/writes. Each leaf is a separately addressed Logix tag/member.</summary>
public sealed class LogixTransport(string gateway, string route = "1,0", int timeoutMs = 5000) : IExplicitTransport
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, NativeTag> tags = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed;
    internal NativeTag Create(string path) => new()
    {
        Name = path, Gateway = gateway, Path = route, PlcType = libplctag.PlcType.ControlLogix,
        Protocol = Protocol.ab_eip, ElementCount = 1, Timeout = TimeSpan.FromMilliseconds(timeoutMs)
    };
    private async ValueTask<NativeTag> GetAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        if (!tags.TryGetValue(path, out var tag))
        {
            tag = Create(path);
            try { await tag.InitializeAsync(cancellationToken).ConfigureAwait(false); tags.Add(path, tag); }
            catch { tag.Dispose(); throw; }
        }
        return tag;
    }
    public async ValueTask<object> ReadAsync(string path, RelayType type, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tag = await GetAsync(path, cancellationToken).ConfigureAwait(false);
            await tag.ReadAsync(cancellationToken).ConfigureAwait(false);
            return type switch
            {
                RelayType.BOOL => tag.GetBit(0), RelayType.SINT => tag.GetInt8(0), RelayType.INT => tag.GetInt16(0),
                RelayType.DINT => tag.GetInt32(0), RelayType.LINT => tag.GetInt64(0), RelayType.REAL => tag.GetFloat32(0),
                _ => throw new ArgumentOutOfRangeException(nameof(type))
            };
        }
        finally { gate.Release(); }
    }
    public async ValueTask WriteAsync(string path, RelayType type, object value, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var tag = await GetAsync(path, cancellationToken).ConfigureAwait(false);
            switch (type)
            {
                case RelayType.BOOL: tag.SetBit(0, (bool)value); break;
                case RelayType.SINT: tag.SetInt8(0, (sbyte)value); break;
                case RelayType.INT: tag.SetInt16(0, (short)value); break;
                case RelayType.DINT: tag.SetInt32(0, (int)value); break;
                case RelayType.LINT: tag.SetInt64(0, (long)value); break;
                case RelayType.REAL: tag.SetFloat32(0, (float)value); break;
                default: throw new ArgumentOutOfRangeException(nameof(type));
            }
            await tag.WriteAsync(cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; foreach (var tag in tags.Values) tag.Dispose(); tags.Clear(); }
        finally { gate.Release(); }
    }
}
