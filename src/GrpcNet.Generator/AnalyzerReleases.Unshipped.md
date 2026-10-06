; Unshipped analyzer release
; https://github.com/dotnet/roslyn-analyzers/blob/main/src/Microsoft.CodeAnalysis.Analyzers/ReleaseTrackingAnalyzers.Help.md

### New Rules

Rule ID | Category | Severity | Notes
--------|----------|----------|-------
GN0001 | GrpcNet | Error | The embedded descriptor of a protoc-generated file cannot be read
GN0002 | GrpcNet | Error | A method's message type cannot be resolved to a C# type
GN0003 | GrpcNet | Error | The generated service class collides with an existing type or member
GN0099 | GrpcNet | Error | The generator failed on an input
