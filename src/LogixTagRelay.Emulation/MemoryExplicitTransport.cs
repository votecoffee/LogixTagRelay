namespace LogixTagRelay.Emulation;

/// <summary>In-process explicit transport; the HTTP EmulatorClient shares the same memory semantics.</summary>
/// <param name="memory">Shared emulated PLC memory.</param>
public sealed class MemoryExplicitTransport(PlcMemory memory) : IExplicitTransport
{
    /// <summary>Reads one scalar in its matching CLR type. Use the FieldMap overload for STRING capacity metadata.</summary>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    /// <param name="type">Supported PLC scalar type.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (memory.Map(path).Type != type) throw new ArgumentException("Requested type differs from the contract.");
        return ValueTask.FromResult(memory.Read(path));
    }
    /// <summary>Writes one addressed scalar. Use the FieldMap overload for STRING capacity metadata; completion is not a multi-tag transaction.</summary>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    /// <param name="type">Supported PLC scalar type.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (memory.Map(path).Type != type) throw new ArgumentException("Requested type differs from the contract.");
        memory.Apply([new(path, value)], "API explicit", fromApi: true);
        return ValueTask.CompletedTask;
    }
    /// <summary>Reads one scalar in its matching CLR type. Use the FieldMap overload for STRING capacity metadata.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public ValueTask<object> ReadAsync(FieldMap field, CancellationToken cancellationToken)
    {
        if (memory.Map(field.Path) != field) throw new ArgumentException("Field mapping differs from the loaded contract.");
        return ReadAsync(field.Path, field.Type, cancellationToken);
    }
    /// <summary>Writes one addressed scalar. Use the FieldMap overload for STRING capacity metadata; completion is not a multi-tag transaction.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public ValueTask WriteAsync(FieldMap field, object value, CancellationToken cancellationToken)
    {
        if (memory.Map(field.Path) != field) throw new ArgumentException("Field mapping differs from the loaded contract.");
        return WriteAsync(field.Path, field.Type, value, cancellationToken);
    }
    /// <summary>Releases owned resources. For running adapters, cancel and await RunAsync first.</summary>
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    /// <summary>Returns values in field order. Native UDT groups may share a snapshot; separate groups are not an atomic PLC transaction.</summary>
    /// <param name="fields">Ordered scalar mappings to validate, encode or read.</param>
    /// <param name="groups">Whole-UDT root read groups associated with the scalar fields.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
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
