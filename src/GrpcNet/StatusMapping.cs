using System;

namespace GrpcNet
{
    /// <summary>Maps exceptions to statuses, per CALL-INTERFACE.md "Status and errors".</summary>
    internal static class StatusMapping
    {
        internal static GrpcStatus FromException(Exception exception, in CallHead head, GrpcProcessorOptions options)
        {
            switch (exception)
            {
                case GrpcException grpc:
                    return grpc.HasStatusMessage ? GrpcStatus.FromString(grpc.Code, grpc.Message) : new GrpcStatus(grpc.Code);
                case OperationCanceledException:
                    return new GrpcStatus(head.IsPastDeadline ? StatusCode.DeadlineExceeded : StatusCode.Cancelled);
                case GrpcCodecException codec:
                    return FromCodecException(codec, options);
                default:
                    return options.DetailedErrors ? GrpcStatus.FromString(StatusCode.Unknown, exception.Message) : new GrpcStatus(StatusCode.Unknown);
            }
        }

        internal static GrpcStatus FromCodecException(Exception exception, GrpcProcessorOptions options)
        {
            return options.DetailedErrors ? GrpcStatus.FromString(StatusCode.Internal, exception.Message) : new GrpcStatus(StatusCode.Internal);
        }

        internal static GrpcStatus InvalidCodecSize(GrpcProcessorOptions options)
        {
            return options.DetailedErrors ? GrpcStatus.FromString(StatusCode.Internal, "The codec reported a negative message size.") : new GrpcStatus(StatusCode.Internal);
        }
    }
}
