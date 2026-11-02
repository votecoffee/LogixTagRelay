using System.Net.Sockets;

namespace LogixTagRelay;

public enum PlcOperation { Connect, RegisterSession, InitializeTag, ReadTag, WriteTag, ReadTrigger, WriteResponse, ReadHeartbeat, WriteHeartbeat, StartWorker, ImplicitHandshake, ExchangeImage }
public enum PlcFailureKind { Unreachable, HostNotFound, ConnectionRefused, Timeout, ConnectionFailed, ProtocolError, TagNotFound, AccessDenied, InvalidAddress, TypeMismatch, ReadFailed, WriteFailed, WorkerFailed }

/// <summary>Machine-readable communication fault. InnerException retains the original socket/driver error.</summary>
public abstract class PlcCommunicationException : IOException
{
    public string? Gateway { get; internal set; }
    public string? Route { get; internal set; }
    public string? TagPath { get; }
    public PlcOperation Operation { get; }
    public PlcFailureKind FailureKind { get; }
    public bool IsTransient { get; }
    public int? NativeStatus { get; }
    public string? NativeStatusName { get; }
    public SocketError? SocketError { get; }
    protected PlcCommunicationException(string message, PlcOperation operation, PlcFailureKind kind, bool transient,
        Exception? inner, string? gateway, string? route, string? tag, int? nativeStatus = null, string? nativeName = null) : base(message, inner)
    {
        Operation = operation; FailureKind = kind; IsTransient = transient; Gateway = gateway; Route = route; TagPath = tag;
        NativeStatus = nativeStatus; NativeStatusName = nativeName;
        for (Exception? error = inner; error is not null; error = error.InnerException)
            if (error is SocketException socket) { SocketError = socket.SocketErrorCode; break; }
    }
}

public sealed class PlcConnectionException : PlcCommunicationException
{
    public PlcConnectionException(string message, PlcFailureKind kind, string? gateway, string? route = null,
        Exception? inner = null, PlcOperation operation = PlcOperation.Connect, bool transient = true, int? nativeStatus = null, string? nativeName = null, string? tagPath = null)
        : base(message, operation, kind, transient, inner, gateway, route, tagPath, nativeStatus, nativeName) { }
}
public sealed class PlcTagReadException : PlcCommunicationException
{
    public PlcTagReadException(string tag, string? gateway, string? route, Exception inner, PlcFailureKind kind = PlcFailureKind.ReadFailed,
        bool transient = true, PlcOperation operation = PlcOperation.ReadTag, int? nativeStatus = null, string? nativeName = null)
        : base($"{operation} failed for tag '{tag}' on '{gateway ?? "configured PLC"}' (route '{route ?? "default"}'): {inner.Message}",
            operation, kind, transient, inner, gateway, route, tag, nativeStatus, nativeName) { }
}
public sealed class PlcTagWriteException : PlcCommunicationException
{
    public PlcTagWriteException(string tag, string? gateway, string? route, Exception inner, PlcFailureKind kind = PlcFailureKind.WriteFailed,
        bool transient = true, PlcOperation operation = PlcOperation.WriteTag, int? nativeStatus = null, string? nativeName = null)
        : base($"{operation} failed for tag '{tag}' on '{gateway ?? "configured PLC"}' (route '{route ?? "default"}'): {inner.Message}",
            operation, kind, transient, inner, gateway, route, tag, nativeStatus, nativeName) { }
}

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
