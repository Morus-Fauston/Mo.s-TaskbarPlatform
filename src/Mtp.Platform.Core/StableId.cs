using System;

namespace Mtp.Platform.Core;

/// <summary>
/// A stable identifier whose value is compared case-sensitively.
/// </summary>
public readonly record struct StableId
{
    public StableId(string value)
    {
        ArgumentNullException.ThrowIfNull(value);

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("A stable ID cannot be empty or whitespace.", nameof(value));
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("A stable ID cannot have leading or trailing whitespace.", nameof(value));
        }

        Value = value;
    }

    public string Value { get; }

    public override string ToString() => Value;
}
