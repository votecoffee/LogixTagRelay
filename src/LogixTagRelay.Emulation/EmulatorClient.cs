using System.Net.Http.Json;
using System.Text.Json;

namespace LogixTagRelay.Emulation;

/// <summary>Emulator instance and contract fingerprint used to reject stale or mismatched clients.</summary>
/// <param name="InstanceId">Loaded emulator instance identity; stale instances are rejected.</param>
/// <param name="Fingerprint">Contract identity used to reject incompatible or stale clients.</param>
/// <param name="ConfigurationJson">Matching serialized contract configuration.</param>
public sealed record EmulationContract(Guid InstanceId, string Fingerprint, string ConfigurationJson);
/// <summary>Contract-bound HTTP request to read or write one typed scalar.</summary>
/// <param name="InstanceId">Loaded emulator instance identity; stale instances are rejected.</param>
/// <param name="Fingerprint">Contract identity used to reject incompatible or stale clients.</param>
/// <param name="Path">Fully qualified Logix path including optional program scope and array coordinates.</param>
/// <param name="Type">Supported PLC scalar type.</param>
/// <param name="Value">Lossless scalar text or typed value associated with this request.</param>
public sealed record ClientTagRequest(Guid InstanceId, string Fingerprint, string Path, PlcType Type, string? Value = null);
/// <summary>Contract-bound HTTP full-image request.</summary>
/// <param name="InstanceId">Loaded emulator instance identity; stale instances are rejected.</param>
/// <param name="Fingerprint">Contract identity used to reject incompatible or stale clients.</param>
/// <param name="Frame">Full symmetric contract image including its relay header.</param>
public sealed record ClientImageRequest(Guid InstanceId, string Fingerprint, byte[] Frame);
/// <summary>Contract-bound HTTP batch read in field order with optional UDT groups.</summary>
/// <param name="InstanceId">Loaded emulator instance identity; stale instances are rejected.</param>
/// <param name="Fingerprint">Contract identity used to reject incompatible or stale clients.</param>
/// <param name="Fields">Ordered scalar field mappings or paths included in this group.</param>
/// <param name="Groups">Whole-UDT read group metadata.</param>
public sealed record ClientReadRequest(Guid InstanceId, string Fingerprint, List<FieldMap> Fields, List<ExplicitReadGroup> Groups);
/// <summary>Ordered lossless text representations of a batch read.</summary>
/// <param name="Values">Ordered lossless scalar text values.</param>
public sealed record ReadReply(List<string> Values);
/// <summary>Lossless scalar text returned by the emulator.</summary>
/// <param name="Value">Lossless scalar text or typed value associated with this request.</param>
public sealed record TagReply(string Value);
/// <summary>Full symmetric SINT image returned by the emulator.</summary>
/// <param name="Frame">Full symmetric contract image including its relay header.</param>
public sealed record ImageReply(byte[] Frame);

/// <summary>Loopback emulator client. Explicit sessions use IExplicitTransport; implicit sessions use the v3 image codec.</summary>
public sealed class EmulatorClient : IExplicitTransport, IImplicitTransport
{
    private readonly HttpClient http;
    private readonly SyncConfiguration config;
    private readonly EmulationContract contract;
    private bool disposed;
    private int implicitRunning;
    private EmulatorClient(HttpClient http, SyncConfiguration config, EmulationContract contract)
    { this.http = http; this.config = config; this.contract = contract; }
    /// <summary>Connects to a loopback HTTP emulator and validates contract identity; it does not use EtherNet/IP.</summary>
    /// <param name="config">Matching generated contract configuration.</param>
    /// <param name="address">Emulator HTTP base address; defaults to the local emulator endpoint.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public static async Task<EmulatorClient> ConnectAsync(SyncConfiguration config, Uri? address = null, CancellationToken cancellationToken = default)
    {
        var captured = SyncConfiguration.FromJson(config.ToJson());
        address ??= new("http://127.0.0.1:5275/");
        if (!address.IsAbsoluteUri || !address.IsLoopback || address.Scheme != "http" || address.AbsolutePath != "/" || address.Query.Length > 0 || address.Fragment.Length > 0 || address.UserInfo.Length > 0)
            throw new ArgumentException("Use the emulator's loopback HTTP root address.");
        var client = new HttpClient { BaseAddress = address, Timeout = TimeSpan.FromSeconds(5) };
        client.DefaultRequestHeaders.Add("X-LogixTagRelay", "emulator");
        try
        {
            var loaded = await client.GetFromJsonAsync<EmulationContract>("api/contract", SyncConfiguration.Json, cancellationToken).ConfigureAwait(false)
                ?? throw new IOException("Emulator has no loaded contract.");
            if (loaded.Fingerprint != PlcMemory.ContractFingerprint(captured)) throw new ArgumentException("Emulator contract differs from the application's contract. Load the same JSON in both.");
            return new(client, captured, loaded);
        }
        catch { client.Dispose(); throw; }
    }
    private async Task<TReply> PostAsync<TRequest, TReply>(string path, TRequest request, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        using var response = await http.PostAsJsonAsync(path, request, SyncConfiguration.Json, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            string detail = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            throw new IOException($"Emulator rejected {path} ({(int)response.StatusCode}): {detail[..Math.Min(detail.Length, 512)]}");
        }
        return await response.Content.ReadFromJsonAsync<TReply>(SyncConfiguration.Json, cancellationToken).ConfigureAwait(false)
            ?? throw new IOException("Empty emulator response.");
    }
    private FieldMap Field(string path, PlcType type)
    {
        var field = config.Fields.FirstOrDefault(f => f.Path.Equals(path, StringComparison.OrdinalIgnoreCase));
        if (field is null && new[] { config.Explicit?.TriggerTag, config.Explicit?.MirrorTag, config.Explicit?.ResponseTag }.Any(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase)))
            field = new(path, PlcType.DINT, Direction.PlcToApp, 24);
        if (field is null && path.Equals(config.Heartbeat?.Tag, StringComparison.OrdinalIgnoreCase)) field = new(path, PlcType.BOOL, Direction.AppToPlc, 24);
        if (field is null || field.Type != type) throw new ArgumentException("Tag/type differs from the configured contract.");
        return field;
    }
    /// <summary>Reads one scalar in its matching CLR type. Use the FieldMap overload for STRING capacity metadata.</summary>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    /// <param name="type">Supported PLC scalar type.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken cancellationToken)
    {
        var field = Field(path, type);
        var reply = await PostAsync<ClientTagRequest, TagReply>("api/client/read", new(contract.InstanceId, contract.Fingerprint, path, type), cancellationToken).ConfigureAwait(false);
        return TagText.Parse(field, reply.Value);
    }
    /// <summary>Writes one addressed scalar. Use the FieldMap overload for STRING capacity metadata; completion is not a multi-tag transaction.</summary>
    /// <param name="path">Fully qualified Logix scalar path.</param>
    /// <param name="type">Supported PLC scalar type.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken cancellationToken)
    {
        var field = Field(path, type); PlcTypes.Encode(field, value, new byte[field.WireBytes]);
        _ = await PostAsync<ClientTagRequest, TagReply>("api/client/write", new(contract.InstanceId, contract.Fingerprint, path, type, TagText.Format(value)), cancellationToken).ConfigureAwait(false);
    }
    /// <summary>Reads one scalar in its matching CLR type. Use the FieldMap overload for STRING capacity metadata.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public ValueTask<object> ReadAsync(FieldMap field, CancellationToken cancellationToken)
    {
        if (Field(field.Path, field.Type) != field) throw new ArgumentException("Field mapping differs from the contract.");
        return ReadAsync(field.Path, field.Type, cancellationToken);
    }
    /// <summary>Writes one addressed scalar. Use the FieldMap overload for STRING capacity metadata; completion is not a multi-tag transaction.</summary>
    /// <param name="field">Scalar mapping, including STRING capacity when applicable.</param>
    /// <param name="value">Typed scalar or timestamp value required by this operation.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public ValueTask WriteAsync(FieldMap field, object value, CancellationToken cancellationToken)
    {
        if (Field(field.Path, field.Type) != field) throw new ArgumentException("Field mapping differs from the contract.");
        return WriteAsync(field.Path, field.Type, value, cancellationToken);
    }
    /// <summary>Exchanges a full implicit contract image over emulator HTTP.</summary>
    /// <param name="frame">Full symmetric relay image to exchange with the emulator.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async Task<byte[]> ExchangeImplicitAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        if (config.Implicit is null) throw new InvalidOperationException("Contract has no implicit settings.");
        var reply = await PostAsync<ClientImageRequest, ImageReply>("api/client/exchange", new(contract.InstanceId, contract.Fingerprint, frame), cancellationToken).ConfigureAwait(false);
        return reply.Frame;
    }
    /// <summary>Returns values in field order. Native UDT groups may share a snapshot; separate groups are not an atomic PLC transaction.</summary>
    /// <param name="fields">Ordered scalar mappings to validate, encode or read.</param>
    /// <param name="groups">Whole-UDT root read groups associated with the scalar fields.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async ValueTask<IReadOnlyList<object>> ReadBatchAsync(IReadOnlyList<FieldMap> fields, IReadOnlyList<ExplicitReadGroup> groups, CancellationToken cancellationToken)
    {
        ExplicitReads.Validate(fields, groups);
        foreach (var field in fields) if (Field(field.Path, field.Type) != field) throw new ArgumentException("Read batch differs from the configured mapping.");
        var reply = await PostAsync<ClientReadRequest, ReadReply>("api/client/read-batch", new(contract.InstanceId, contract.Fingerprint, fields.ToList(), groups.ToList()), cancellationToken).ConfigureAwait(false);
        if (reply.Values.Count != fields.Count) throw new IOException("Emulator returned an incomplete read batch.");
        return reply.Values.Select((value, i) => TagText.Parse(fields[i], value)).ToArray();
    }
    /// <summary>Sends one symmetric image and returns an available PLC image, or null when no fresh input is available.</summary>
    /// <param name="image">Complete symmetric relay image including its header.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async ValueTask<byte[]?> ExchangeAsync(byte[] image, CancellationToken cancellationToken) => await ExchangeImplicitAsync(image, cancellationToken).ConfigureAwait(false);
    /// <summary>Most recently created managed implicit session, exposing recovery, diagnostics and write suspension.</summary>
    public ImplicitContractSession? LastImplicitSession { get; private set; }
    /// <summary>Creates a managed emulator session with a separate explicit HTTP heartbeat channel when configured.</summary>
    /// <param name="model">Live application model instance used by synchronization.</param>
    /// <param name="recovery">Optional automatic recovery and uncertain-write policy; defaults apply when null.</param>
    public ImplicitContractSession CreateImplicitSession(IPlcModel model, RecoveryOptions? recovery = null) =>
        new(config, model, async ct => await ConnectAsync(config, http.BaseAddress, ct).ConfigureAwait(false), recovery,
            async ct => await ConnectAsync(config, http.BaseAddress, ct).ConfigureAwait(false));
    /// <summary>Creates and runs a managed emulator implicit session until cancellation or terminal failure.</summary>
    /// <param name="model">Live application model instance used by synchronization.</param>
    /// <param name="cancellationToken">Cancels this operation. Caller cancellation remains cancellation rather than a fabricated communication fault.</param>
    public async Task RunImplicitAsync(IPlcModel model, CancellationToken cancellationToken)
    {
        if (Interlocked.Exchange(ref implicitRunning, 1) != 0) throw new InvalidOperationException("An implicit loop is already running on this client.");
        try { LastImplicitSession = CreateImplicitSession(model); await LastImplicitSession.RunAsync(cancellationToken).ConfigureAwait(false); }
        finally { Volatile.Write(ref implicitRunning, 0); }
    }
    /// <summary>Releases owned resources. For running adapters, cancel and await RunAsync first.</summary>
    public ValueTask DisposeAsync() { disposed = true; http.Dispose(); return ValueTask.CompletedTask; }
}
