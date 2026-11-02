using libplctag;

namespace LogixTagRelay.Logix;

/// <summary>Preserves libplctag status while distinguishing endpoint/session errors from tag operations.</summary>
public static class LogixFaults
{
    /// <summary>Preserves exact recognized driver status, endpoint, path and cause in a typed communication exception.</summary>
    /// <param name="error">Original pinned libplctag exception to classify.</param>
    /// <param name="gateway">PLC hostname or IP address, optionally with a port.</param>
    /// <param name="route">CIP route to the controller, such as 1,0.</param>
    /// <param name="tag">Affected scalar path, or null if the failure is not tag-specific.</param>
    /// <param name="operation">Communication stage where the failure was detected.</param>
    public static PlcCommunicationException Translate(LibPlcTagException error, string gateway, string route, string tag, PlcOperation operation)
    {
        // The pinned 1.5.2 wrapper serializes Status only as its exact enum-name message.
        Status? status = Enum.TryParse<Status>(error.Message, out var parsed) && Enum.IsDefined(parsed) ? parsed : null;
        var kind = status switch
        {
            Status.ErrorTimeout => PlcFailureKind.Timeout, Status.ErrorNotFound => PlcFailureKind.TagNotFound,
            Status.ErrorNotAllowed => PlcFailureKind.AccessDenied, Status.ErrorBadParam or Status.ErrorOutOfBounds => PlcFailureKind.InvalidAddress,
            Status.ErrorBadData => PlcFailureKind.TypeMismatch, Status.ErrorBadReply => PlcFailureKind.ProtocolError,
            Status.ErrorBadConnection or Status.ErrorBadGateway or Status.ErrorOpen or Status.ErrorClose => PlcFailureKind.ConnectionFailed,
            _ => operation == PlcOperation.WriteTag ? PlcFailureKind.WriteFailed : PlcFailureKind.ReadFailed
        };
        bool retryable = kind is PlcFailureKind.Timeout or PlcFailureKind.ConnectionFailed or PlcFailureKind.ReadFailed or PlcFailureKind.WriteFailed;
        if (kind == PlcFailureKind.ConnectionFailed)
            return new PlcConnectionException($"{operation} could not establish/maintain the controller session for tag '{tag}' on '{gateway}' (route '{route}'): {status}.", kind,
                gateway, route, error, operation, retryable, status.HasValue ? (int)status.Value : null, status?.ToString(), tagPath: tag);
        return operation == PlcOperation.WriteTag
            ? new PlcTagWriteException(tag, gateway, route, error, kind, retryable, operation, status.HasValue ? (int)status.Value : null, status?.ToString())
            : new PlcTagReadException(tag, gateway, route, error, kind, retryable, operation, status.HasValue ? (int)status.Value : null, status?.ToString());
    }
}
