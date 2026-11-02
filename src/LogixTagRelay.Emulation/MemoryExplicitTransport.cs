namespace LogixTagRelay.Emulation;

/// <summary>In-process explicit transport; the HTTP EmulatorClient shares the same memory semantics.</summary>
public sealed class MemoryExplicitTransport(PlcMemory memory) : IExplicitTransport
{
    public ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (memory.Map(path).Type != type) throw new ArgumentException("Requested type differs from the contract.");
        return ValueTask.FromResult(memory.Read(path));
    }
    public ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (memory.Map(path).Type != type) throw new ArgumentException("Requested type differs from the contract.");
        memory.Apply([new(path, value)], "API explicit", fromApi: true);
        return ValueTask.CompletedTask;
    }
    public ValueTask<object> ReadAsync(FieldMap field, CancellationToken cancellationToken)
    {
        if (memory.Map(field.Path) != field) throw new ArgumentException("Field mapping differs from the loaded contract.");
        return ReadAsync(field.Path, field.Type, cancellationToken);
    }
    public ValueTask WriteAsync(FieldMap field, object value, CancellationToken cancellationToken)
    {
        if (memory.Map(field.Path) != field) throw new ArgumentException("Field mapping differs from the loaded contract.");
        return WriteAsync(field.Path, field.Type, value, cancellationToken);
    }
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    public ValueTask<IReadOnlyList<object>> ReadBatchAsync(IReadOnlyList<FieldMap> fields, IReadOnlyList<ExplicitReadGroup> groups, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ExplicitReads.Validate(fields, groups);
        lock (memory.Gate)
        {
            foreach (var field in fields) if (memory.Map(field.Path) != field) throw new ArgumentException("Read batch differs from the loaded mapping.");
            return ValueTask.FromResult<IReadOnlyList<object>>(fields.Select(f => memory.Read(f.Path)).ToArray());
        }
    }
}
