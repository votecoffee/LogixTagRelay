using System.Net.Sockets;

namespace LogixTagRelay;

/// <summary>Communication stage retained in typed exceptions and diagnostic fault records.</summary>
public enum PlcOperation
{
    /// <summary>Establishing TCP connectivity or a transport connection.</summary>
    Connect,
    /// <summary>Registering an EtherNet/IP encapsulation session after TCP connection.</summary>
    RegisterSession,
    /// <summary>Initializing a native tag handle and its address or controller session.</summary>
    InitializeTag,
    /// <summary>Reading contract data.</summary>
    ReadTag,
    /// <summary>Writing an addressed contract member.</summary>
    WriteTag,
    /// <summary>Reading the explicit DINT request control.</summary>
    ReadTrigger,
    /// <summary>Writing a handled mirror or signed DINT ACK/NACK response.</summary>
    WriteResponse,
    /// <summary>Reading the explicit heartbeat BOOL phase.</summary>
    ReadHeartbeat,
    /// <summary>Writing the explicit heartbeat BOOL phase.</summary>
    WriteHeartbeat,
    /// <summary>Starting or communicating with the privately owned native worker.</summary>
    StartWorker,
    /// <summary>Establishing a fresh implicit baseline.</summary>
    ImplicitHandshake,
    /// <summary>Exchanging an implicit full image.</summary>
    ExchangeImage,
}
/// <summary>Failure classification based on available OS or driver evidence; a timeout alone does not prove unreachability.</summary>
public enum PlcFailureKind
{
    /// <summary>The operating system reported the host or network unreachable.</summary>
    Unreachable,
    /// <summary>Name resolution failed.</summary>
    HostNotFound,
    /// <summary>The endpoint refused the connection.</summary>
    ConnectionRefused,
    /// <summary>The configured operation deadline expired.</summary>
    Timeout,
    /// <summary>Connection setup failed without a more specific supported classification.</summary>
    ConnectionFailed,
    /// <summary>Protocol negotiation or framing was rejected.</summary>
    ProtocolError,
    /// <summary>The driver reported a missing tag or member.</summary>
    TagNotFound,
    /// <summary>The driver reported insufficient access.</summary>
    AccessDenied,
    /// <summary>The driver rejected an address or parameter; this does not prove a tag was deleted.</summary>
    InvalidAddress,
    /// <summary>The driver reported incompatible data types.</summary>
    TypeMismatch,
    /// <summary>A read failed without a more specific classification.</summary>
    ReadFailed,
    /// <summary>A write failed without a more specific classification.</summary>
    WriteFailed,
    /// <summary>The private native worker failed.</summary>
    WorkerFailed,
}

/// <summary>Machine-readable communication fault. InnerException retains the original socket/driver error.</summary>
public abstract class PlcCommunicationException : IOException
{
    /// <summary>Configured PLC hostname or IP address, optionally including a TCP port.</summary>
    public string? Gateway { get; internal set; }
    /// <summary>Configured CIP backplane route, such as 1,0.</summary>
    public string? Route { get; internal set; }
    /// <summary>Affected scalar path when known; null for faults not tied to one tag.</summary>
    public string? TagPath { get; }
    /// <summary>Communication stage at which the failure was detected.</summary>
    public PlcOperation Operation { get; }
    /// <summary>Machine-readable classification of the communication failure.</summary>
    public PlcFailureKind FailureKind { get; }
    /// <summary>Whether automatic recovery may retry this failure under the configured policy.</summary>
    public bool IsTransient { get; }
    /// <summary>Original numeric driver status when available; unknown status remains null.</summary>
    public int? NativeStatus { get; }
    /// <summary>Original named driver status when recognized.</summary>
    public string? NativeStatusName { get; }
    /// <summary>Original operating-system socket error when an inner SocketException exists.</summary>
    public SocketError? SocketError { get; }
    /// <summary>Initializes a typed communication fault, retaining endpoint, operation, driver status and original cause.</summary>
    /// <param name="message">Human-readable description retaining the original failure context.</param>
    /// <param name="operation">Communication stage where the failure was detected.</param>
    /// <param name="kind">Evidence-based failure classification.</param>
    /// <param name="transient">Whether the configured recovery policy may retry this fault.</param>
    /// <param name="inner">Original driver, socket or transport exception, when available.</param>
    /// <param name="gateway">PLC hostname or IP address, optionally with a port.</param>
    /// <param name="route">CIP route to the controller, such as 1,0.</param>
    /// <param name="tag">Affected scalar path, or null if the failure is not tag-specific.</param>
    /// <param name="nativeStatus">Original numeric driver status, when recognized.</param>
    /// <param name="nativeName">Original named driver status, when recognized.</param>
    protected PlcCommunicationException(string message, PlcOperation operation, PlcFailureKind kind, bool transient,
        Exception? inner, string? gateway, string? route, string? tag, int? nativeStatus = null, string? nativeName = null) : base(message, inner)
    {
        Operation = operation; FailureKind = kind; IsTransient = transient; Gateway = gateway; Route = route; TagPath = tag;
        NativeStatus = nativeStatus; NativeStatusName = nativeName;
        for (Exception? error = inner; error is not null; error = error.InnerException)
            if (error is SocketException socket) { SocketError = socket.SocketErrorCode; break; }
    }
}

/// <summary>Endpoint, registration or native controller-session setup failure with its original cause and operation phase.</summary>
public sealed class PlcConnectionException : PlcCommunicationException
{
    /// <summary>Endpoint, registration or native controller-session setup failure with its original cause and operation phase.</summary>
    /// <param name="message">Human-readable description retaining the original failure context.</param>
    /// <param name="kind">Evidence-based failure classification.</param>
    /// <param name="gateway">PLC hostname or IP address, optionally with a port.</param>
    /// <param name="route">CIP route to the controller, such as 1,0.</param>
    /// <param name="inner">Original driver, socket or transport exception, when available.</param>
    /// <param name="operation">Communication stage where the failure was detected.</param>
    /// <param name="transient">Whether the configured recovery policy may retry this fault.</param>
    /// <param name="nativeStatus">Original numeric driver status, when recognized.</param>
    /// <param name="nativeName">Original named driver status, when recognized.</param>
    /// <param name="tagPath">Affected scalar path when known.</param>
    public PlcConnectionException(string message, PlcFailureKind kind, string? gateway, string? route = null,
        Exception? inner = null, PlcOperation operation = PlcOperation.Connect, bool transient = true, int? nativeStatus = null, string? nativeName = null, string? tagPath = null)
        : base(message, operation, kind, transient, inner, gateway, route, tagPath, nativeStatus, nativeName) { }
}
/// <summary>Tag read failure retaining the path, endpoint, operation and original cause.</summary>
public sealed class PlcTagReadException : PlcCommunicationException
{
    /// <summary>Tag read failure retaining the path, endpoint, operation and original cause.</summary>
    /// <param name="tag">Affected scalar path, or null if the failure is not tag-specific.</param>
    /// <param name="gateway">PLC hostname or IP address, optionally with a port.</param>
    /// <param name="route">CIP route to the controller, such as 1,0.</param>
    /// <param name="inner">Original driver, socket or transport exception, when available.</param>
    /// <param name="kind">Evidence-based failure classification.</param>
    /// <param name="transient">Whether the configured recovery policy may retry this fault.</param>
    /// <param name="operation">Communication stage where the failure was detected.</param>
    /// <param name="nativeStatus">Original numeric driver status, when recognized.</param>
    /// <param name="nativeName">Original named driver status, when recognized.</param>
    public PlcTagReadException(string tag, string? gateway, string? route, Exception inner, PlcFailureKind kind = PlcFailureKind.ReadFailed,
        bool transient = true, PlcOperation operation = PlcOperation.ReadTag, int? nativeStatus = null, string? nativeName = null)
        : base($"{operation} failed for tag '{tag}' on '{gateway ?? "configured PLC"}' (route '{route ?? "default"}'): {inner.Message}",
            operation, kind, transient, inner, gateway, route, tag, nativeStatus, nativeName) { }
}
/// <summary>Tag write failure retaining the path, endpoint, operation and original cause. Delivery may be uncertain.</summary>
public sealed class PlcTagWriteException : PlcCommunicationException
{
    /// <summary>Tag write failure retaining the path, endpoint, operation and original cause. Delivery may be uncertain.</summary>
    /// <param name="tag">Affected scalar path, or null if the failure is not tag-specific.</param>
    /// <param name="gateway">PLC hostname or IP address, optionally with a port.</param>
    /// <param name="route">CIP route to the controller, such as 1,0.</param>
    /// <param name="inner">Original driver, socket or transport exception, when available.</param>
    /// <param name="kind">Evidence-based failure classification.</param>
    /// <param name="transient">Whether the configured recovery policy may retry this fault.</param>
    /// <param name="operation">Communication stage where the failure was detected.</param>
    /// <param name="nativeStatus">Original numeric driver status, when recognized.</param>
    /// <param name="nativeName">Original named driver status, when recognized.</param>
    public PlcTagWriteException(string tag, string? gateway, string? route, Exception inner, PlcFailureKind kind = PlcFailureKind.WriteFailed,
        bool transient = true, PlcOperation operation = PlcOperation.WriteTag, int? nativeStatus = null, string? nativeName = null)
        : base($"{operation} failed for tag '{tag}' on '{gateway ?? "configured PLC"}' (route '{route ?? "default"}'): {inner.Message}",
            operation, kind, transient, inner, gateway, route, tag, nativeStatus, nativeName) { }
}

/// <summary>Serializable fault snapshot with phase, endpoint, native status and retryability.</summary>
/// <param name="Timestamp">UTC timestamp when the failure or snapshot was recorded.</param>
/// <param name="Operation">Communication stage where the failure was detected, when known.</param>
/// <param name="Kind">Evidence-based fault classification, when known.</param>
/// <param name="Gateway">PLC endpoint hostname or IP address.</param>
/// <param name="Route">CIP route to the intended controller.</param>
/// <param name="TagPath">Affected scalar path, or null for an endpoint-level fault.</param>
/// <param name="NativeStatus">Original numeric driver status, when recognized.</param>
/// <param name="NativeStatusName">Original named driver status, when recognized.</param>
/// <param name="SocketError">Original operating-system socket status, when present.</param>
/// <param name="Retryable">Whether automatic recovery may retry this fault.</param>
/// <param name="Message">Human-readable fault details.</param>
public sealed record PlcFault(DateTimeOffset Timestamp, PlcOperation? Operation, PlcFailureKind? Kind, string? Gateway,
    string? Route, string? TagPath, int? NativeStatus, string? NativeStatusName, SocketError? SocketError, bool Retryable, string Message);

internal static class PlcErrors
{
    public static bool IsCommunication(Exception error) => error is IOException or TimeoutException or OperationCanceledException;
    public static Exception Read(Exception error, SyncConfiguration config, string path, PlcOperation operation = PlcOperation.ReadTag)
    {
        if (error is PlcCommunicationException typed) { typed.Gateway ??= config.Gateway; typed.Route ??= config.Route; return typed; }
        return new PlcTagReadException(path, config.Gateway, config.Route, error, error is TimeoutException or OperationCanceledException ? PlcFailureKind.Timeout : PlcFailureKind.ReadFailed, operation: operation);
    }
    public static Exception Write(Exception error, SyncConfiguration config, string path, PlcOperation operation = PlcOperation.WriteTag)
    {
        if (error is PlcCommunicationException typed) { typed.Gateway ??= config.Gateway; typed.Route ??= config.Route; return typed; }
        return new PlcTagWriteException(path, config.Gateway, config.Route, error, error is TimeoutException or OperationCanceledException ? PlcFailureKind.Timeout : PlcFailureKind.WriteFailed, operation: operation);
    }
    public static PlcConnectionException Connect(Exception error, SyncConfiguration config, PlcOperation operation = PlcOperation.Connect) =>
        new($"{operation} failed for '{config.Gateway}' (route '{config.Route}'): {error.Message}",
            error is TimeoutException or OperationCanceledException ? PlcFailureKind.Timeout : operation == PlcOperation.StartWorker ? PlcFailureKind.WorkerFailed : PlcFailureKind.ConnectionFailed,
            config.Gateway, config.Route, error, operation);
}
