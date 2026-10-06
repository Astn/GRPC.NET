namespace GrpcNet
{
    /// <summary>The gRPC status codes, with the numeric values the wire carries in <c>grpc-status</c>.</summary>
    public enum StatusCode
    {
        /// <summary>Not an error.</summary>
        OK = 0,
        /// <summary>The operation was cancelled, typically by the caller.</summary>
        Cancelled = 1,
        /// <summary>An unknown error.</summary>
        Unknown = 2,
        /// <summary>The client specified an invalid argument.</summary>
        InvalidArgument = 3,
        /// <summary>The deadline expired before the operation could complete.</summary>
        DeadlineExceeded = 4,
        /// <summary>A requested entity was not found.</summary>
        NotFound = 5,
        /// <summary>The entity the client attempted to create already exists.</summary>
        AlreadyExists = 6,
        /// <summary>The caller does not have permission for the operation.</summary>
        PermissionDenied = 7,
        /// <summary>A resource has been exhausted, such as a per-user quota or a message size limit.</summary>
        ResourceExhausted = 8,
        /// <summary>The system is not in a state required for the operation.</summary>
        FailedPrecondition = 9,
        /// <summary>The operation was aborted, typically because of a concurrency issue.</summary>
        Aborted = 10,
        /// <summary>The operation was attempted past the valid range.</summary>
        OutOfRange = 11,
        /// <summary>The operation is not implemented or not supported.</summary>
        Unimplemented = 12,
        /// <summary>An internal invariant was broken.</summary>
        Internal = 13,
        /// <summary>The service is currently unavailable.</summary>
        Unavailable = 14,
        /// <summary>Unrecoverable data loss or corruption.</summary>
        DataLoss = 15,
        /// <summary>The request does not have valid authentication credentials.</summary>
        Unauthenticated = 16,
    }
}
