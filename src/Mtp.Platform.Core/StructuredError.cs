using System;

namespace Mtp.Platform.Core;

/// <summary>
/// A stable machine-readable error code with user-facing context.
/// </summary>
public sealed record StructuredError
{
    public StructuredError(string code, string message, string? path = null)
    {
        if (string.IsNullOrWhiteSpace(code))
        {
            throw new ArgumentException("An error code cannot be empty.", nameof(code));
        }

        if (string.IsNullOrWhiteSpace(message))
        {
            throw new ArgumentException("An error message cannot be empty.", nameof(message));
        }

        Code = code;
        Message = message;
        Path = path;
    }

    public string Code { get; }

    public string Message { get; }

    public string? Path { get; }
}
