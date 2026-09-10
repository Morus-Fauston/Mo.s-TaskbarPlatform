using System;
using System.IO;
using System.Text;
using Mtp.Platform.Core;

namespace Mtp.Host;

internal static class BoundedUtf8File
{
    public static bool TryRead(string path, int maximumBytes, out string content)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        var bytes = new byte[maximumBytes + 1];
        var total = 0;
        while (total < bytes.Length)
        {
            var read = stream.Read(bytes, total, bytes.Length - total);
            if (read == 0)
            {
                break;
            }

            total += read;
        }

        if (total > maximumBytes)
        {
            content = string.Empty;
            return false;
        }

        content = Encoding.UTF8.GetString(bytes, 0, total);
        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            content = content[1..];
        }

        return true;
    }
}

/// <summary>
/// A replaceable boundary for obtaining the raw declaration document.
/// </summary>
public interface IDeclarationSource
{
    CoreResult<string> Read();
}

/// <summary>
/// Reads the fixed local JSON declaration file for the initial Host slice.
/// </summary>
public sealed class LocalJsonDeclarationSource : IDeclarationSource
{
    public LocalJsonDeclarationSource(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("A declaration path is required.", nameof(path));
        }

        Path = path;
    }

    public string Path { get; }

    public CoreResult<string> Read()
    {
        if (!File.Exists(Path))
        {
            return CoreResult<string>.Failure(
                new StructuredError("declaration_not_found", "The local declaration file was not found.", Path));
        }

        try
        {
            if (!BoundedUtf8File.TryRead(Path, DeclarationValidator.MaximumJsonSizeInBytes, out var json))
            {
                return CoreResult<string>.Failure(
                    new StructuredError("declaration_too_large", "The local declaration file exceeds the 1 MiB limit.", Path));
            }

            return CoreResult<string>.Success(json);
        }
        catch (UnauthorizedAccessException)
        {
            return CoreResult<string>.Failure(
                new StructuredError("declaration_read_failed", "The local declaration file cannot be read.", Path));
        }
        catch (IOException)
        {
            return CoreResult<string>.Failure(
                new StructuredError("declaration_read_failed", "The local declaration file cannot be read.", Path));
        }
    }
}
