using libplctag;
using NativeTag = libplctag.Tag;
using RelayType = LogixTagRelay.PlcType;

namespace LogixTagRelay.Logix;

/// <summary>Grouped UDT snapshots with individually addressed dirty-member writes.</summary>
public sealed class LogixTransport(string gateway, string route = "1,0", int timeoutMs = 5000) : IExplicitTransport
{
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, NativeTag> tags = new(StringComparer.OrdinalIgnoreCase);
    private bool disposed, connected;
    private LogixUdtReader? udtReader;
    private async ValueTask<byte[]> ReadRawAsync(string path, CancellationToken ct)
    {
        var tag = await GetAsync(path, ct).ConfigureAwait(false);
        try { await tag.ReadAsync(ct).ConfigureAwait(false); return tag.GetBuffer(); }
        catch (LibPlcTagException error) { ct.ThrowIfCancellationRequested(); throw LogixFaults.Translate(error, gateway, route, path, PlcOperation.ReadTag); }
    }
    public async ValueTask<IReadOnlyList<object>> ReadBatchAsync(IReadOnlyList<FieldMap> fields, IReadOnlyList<ExplicitReadGroup> groups, CancellationToken cancellationToken)
    {
        ExplicitReads.Validate(fields, groups);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            udtReader ??= new(ReadRawAsync);
            var values = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
            foreach (var group in groups)
                foreach (var pair in await udtReader.ReadAsync(group, fields, cancellationToken).ConfigureAwait(false)) values.Add(pair.Key, pair.Value);
            foreach (var field in fields.Where(f => !values.ContainsKey(f.Path))) values.Add(field.Path, await ReadFieldAsync(field, cancellationToken).ConfigureAwait(false));
            return fields.Select(f => values[f.Path]).ToArray();
        }
        finally { gate.Release(); }
    }
    internal NativeTag Create(string path) => new()
    {
        Name = path, Gateway = gateway, Path = route, PlcType = libplctag.PlcType.ControlLogix,
        Protocol = Protocol.ab_eip, ElementCount = 1, Timeout = TimeSpan.FromMilliseconds(timeoutMs)
    };
    private async ValueTask<NativeTag> GetAsync(string path, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false);
        if (!tags.TryGetValue(path, out var tag))
        {
            tag = Create(path);
            try { await tag.InitializeAsync(cancellationToken).ConfigureAwait(false); tags.Add(path, tag); }
            catch (LibPlcTagException error) { tag.Dispose(); cancellationToken.ThrowIfCancellationRequested(); throw LogixFaults.Translate(error, gateway, route, path, PlcOperation.InitializeTag); }
            catch { tag.Dispose(); throw; }
        }
        return tag;
    }
    public ValueTask<object> ReadAsync(string path, RelayType type, CancellationToken cancellationToken) =>
        ReadAsync(new FieldMap(path, type, Direction.PlcToApp, 24, StringCapacity: type == RelayType.STRING ? 82 : 0), cancellationToken);
    public async ValueTask<object> ReadAsync(FieldMap field, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            return await ReadFieldAsync(field, cancellationToken).ConfigureAwait(false);
        }
        finally { gate.Release(); }
    }
    private async ValueTask<object> ReadFieldAsync(FieldMap field, CancellationToken cancellationToken)
    {
        var tag = await GetAsync(LogixAddress.SymbolicPath(field), cancellationToken).ConfigureAwait(false);
        try
        {
        await tag.ReadAsync(cancellationToken).ConfigureAwait(false);
        if (field.Type == RelayType.STRING) return LogixStringBuffer.Decode(field, tag.GetBuffer());
        return field.Type switch
        {
            RelayType.BOOL => tag.GetInt8(0) != 0, RelayType.SINT => tag.GetInt8(0), RelayType.INT => tag.GetInt16(0),
            RelayType.DINT => tag.GetInt32(0), RelayType.LINT => tag.GetInt64(0), RelayType.REAL => tag.GetFloat32(0),
            _ => PlcTypes.Decode(field, tag.GetBuffer())
        };
        }
        catch (LibPlcTagException error) { cancellationToken.ThrowIfCancellationRequested(); throw LogixFaults.Translate(error, gateway, route, field.Path, PlcOperation.ReadTag); }
    }
    public ValueTask WriteAsync(string path, RelayType type, object value, CancellationToken cancellationToken) =>
        WriteAsync(new FieldMap(path, type, Direction.AppToPlc, 24, StringCapacity: type == RelayType.STRING ? 82 : 0), value, cancellationToken);
    public async ValueTask WriteAsync(FieldMap field, object value, CancellationToken cancellationToken)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (field.Type == RelayType.STRING) PlcTypes.ValidateString((string)value, field.StringCapacity);
            var tag = await GetAsync(LogixAddress.SymbolicPath(field), cancellationToken).ConfigureAwait(false);
            if (field.Type == RelayType.STRING)
            {
                await tag.ReadAsync(cancellationToken).ConfigureAwait(false);
                tag.SetBuffer(LogixStringBuffer.Encode(field, (string)value, tag.GetBuffer()));
            }
            else switch (field.Type)
            {
                case RelayType.BOOL: tag.SetInt8(0, (sbyte)((bool)value ? 1 : 0)); break;
                case RelayType.SINT: tag.SetInt8(0, (sbyte)value); break;
                case RelayType.INT: tag.SetInt16(0, (short)value); break;
                case RelayType.DINT: tag.SetInt32(0, (int)value); break;
                case RelayType.LINT: tag.SetInt64(0, (long)value); break;
                case RelayType.REAL: tag.SetFloat32(0, (float)value); break;
                default:
                    var buffer = tag.GetBuffer();
                    PlcTypes.Encode(field, value, buffer);
                    tag.SetBuffer(buffer); break;
            }
            await tag.WriteAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (LibPlcTagException error) { cancellationToken.ThrowIfCancellationRequested(); throw LogixFaults.Translate(error, gateway, route, field.Path, PlcOperation.WriteTag); }
        finally { gate.Release(); }
    }
    private async ValueTask EnsureConnectedAsync(CancellationToken ct)
    {
        if (!connected) { await LogixEndpointProbe.CheckAsync(gateway, route, timeoutMs, ct).ConfigureAwait(false); connected = true; }
    }
    public async ValueTask ConnectAsync(CancellationToken cancellationToken = default)
    {
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try { ObjectDisposedException.ThrowIf(disposed, this); await EnsureConnectedAsync(cancellationToken).ConfigureAwait(false); }
        finally { gate.Release(); }
    }
    public async ValueTask DisposeAsync()
    {
        await gate.WaitAsync().ConfigureAwait(false);
        try { disposed = true; foreach (var tag in tags.Values) tag.Dispose(); tags.Clear(); }
        finally { gate.Release(); }
    }
}
