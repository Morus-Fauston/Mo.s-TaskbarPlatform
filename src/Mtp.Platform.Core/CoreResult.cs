using System;

namespace Mtp.Platform.Core;

/// <summary>
/// A result that carries either a value or one structured error.
/// </summary>
public sealed class CoreResult<T>
{
    private CoreResult(bool isSuccess, T? value, StructuredError? error)
    {
        IsSuccess = isSuccess;
        Value = value;
        Error = error;
    }

    public bool IsSuccess { get; }

    public T? Value { get; }

    public StructuredError? Error { get; }

    public static CoreResult<T> Success(T value) => new(true, value, null);

    public static CoreResult<T> Failure(StructuredError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        return new(false, default, error);
    }
}
