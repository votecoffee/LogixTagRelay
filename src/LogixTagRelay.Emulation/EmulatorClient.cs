using System.Net.Http.Json;
using System.Text.Json;

namespace LogixTagRelay.Emulation;

public sealed record EmulationContract(Guid InstanceId, string Fingerprint, string ConfigurationJson);
public sealed record ClientTagRequest(Guid InstanceId, string Fingerprint, string Path, PlcType Type, string? Value = null);
public sealed record ClientImageRequest(Guid InstanceId, string Fingerprint, byte[] Frame);
public sealed record ClientReadRequest(Guid InstanceId, string Fingerprint, List<FieldMap> Fields, List<ExplicitReadGroup> Groups);
public sealed record ReadReply(List<string> Values);
public sealed record TagReply(string Value);
public sealed record ImageReply(byte[] Frame);

/// <summary>Loopback emulator client. Explicit sessions use IExplicitTransport; implicit sessions use the v3 image codec.</summary>
public sealed class EmulatorClient : IExplicitTransport
{
    private readonly HttpClient http;
    private readonly SyncConfiguration config;
    private readonly EmulationContract contract;
    private bool disposed;
    private int implicitRunning;
    private EmulatorClient(HttpClient http, SyncConfiguration config, EmulationContract contract)
    { this.http = http; this.config = config; this.contract = contract; }
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
        if (field is null || field.Type != type) throw new ArgumentException("Tag/type differs from the configured contract.");
        return field;
    }
    public async ValueTask<object> ReadAsync(string path, PlcType type, CancellationToken cancellationToken)
    {
        var field = Field(path, type);
        var reply = await PostAsync<ClientTagRequest, TagReply>("api/client/read", new(contract.InstanceId, contract.Fingerprint, path, type), cancellationToken).ConfigureAwait(false);
        return TagText.Parse(field, reply.Value);
    }
    public async ValueTask WriteAsync(string path, PlcType type, object value, CancellationToken cancellationToken)
    {
        var field = Field(path, type); PlcTypes.Encode(field, value, new byte[field.WireBytes]);
        _ = await PostAsync<ClientTagRequest, TagReply>("api/client/write", new(contract.InstanceId, contract.Fingerprint, path, type, TagText.Format(value)), cancellationToken).ConfigureAwait(false);
    }
    public ValueTask<object> ReadAsync(FieldMap field, CancellationToken cancellationToken)
    {
        if (Field(field.Path, field.Type) != field) throw new ArgumentException("Field mapping differs from the contract.");
        return ReadAsync(field.Path, field.Type, cancellationToken);
    }
    public ValueTask WriteAsync(FieldMap field, object value, CancellationToken cancellationToken)
    {
        if (Field(field.Path, field.Type) != field) throw new ArgumentException("Field mapping differs from the contract.");
        return WriteAsync(field.Path, field.Type, value, cancellationToken);
    }
    public async Task<byte[]> ExchangeImplicitAsync(byte[] frame, CancellationToken cancellationToken = default)
    {
        if (config.Implicit is null) throw new InvalidOperationException("Contract has no implicit settings.");
        var reply = await PostAsync<ClientImageRequest, ImageReply>("api/client/exchange", new(contract.InstanceId, contract.Fingerprint, frame), cancellationToken).ConfigureAwait(false);
        return reply.Frame;
    }
    public async ValueTask<IReadOnlyList<object>> ReadBatchAsync(IReadOnlyList<FieldMap> fields, IReadOnlyList<ExplicitReadGroup> groups, CancellationToken cancellationToken)
    {
        ExplicitReads.Validate(fields, groups);
        foreach (var field in fields) if (Field(field.Path, field.Type) != field) throw new ArgumentException("Read batch differs from the configured mapping.");
        var reply = await PostAsync<ClientReadRequest, ReadReply>("api/client/read-batch", new(contract.InstanceId, contract.Fingerprint, fields.ToList(), groups.ToList()), cancellationToken).ConfigureAwait(false);
        if (reply.Values.Count != fields.Count) throw new IOException("Emulator returned an incomplete read batch.");
        return reply.Values.Select((value, i) => TagText.Parse(fields[i], value)).ToArray();
    }
    public async Task RunImplicitAsync(IPlcModel model, CancellationToken cancellationToken)
    {
        if (config.Implicit is null) throw new InvalidOperationException("Contract has no implicit settings.");
        if (Interlocked.Exchange(ref implicitRunning, 1) != 0) throw new InvalidOperationException("An implicit loop is already running on this client.");
        ImplicitImage? codec = null;
        try
        {
            codec = new ImplicitImage(config, model);
            var outgoing = codec.BuildBootstrap();
            using var timer = new PeriodicTimer(TimeSpan.FromMilliseconds(config.Implicit.RpiMs));
            do
            {
                var incoming = await ExchangeImplicitAsync(outgoing, cancellationToken).ConfigureAwait(false);
                if (codec.ReceivePlcToApp(incoming, DateTimeOffset.UtcNow)) outgoing = codec.BuildAppToPlc();
                if (codec.CheckStale(DateTimeOffset.UtcNow, TimeSpan.FromMilliseconds(Math.Max(1000L, (long)config.Implicit.RpiMs * 10))))
                    throw new IOException("Emulated PLC did not complete a fresh exchange before the timeout.");
            } while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false));
        }
        finally { codec?.CheckStale(DateTimeOffset.MaxValue, TimeSpan.FromTicks(1)); Volatile.Write(ref implicitRunning, 0); }
    }
    public ValueTask DisposeAsync() { disposed = true; http.Dispose(); return ValueTask.CompletedTask; }
}
