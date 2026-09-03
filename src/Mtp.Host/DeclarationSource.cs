using System;
using System.IO;
using Mtp.Platform.Core;

namespace Mtp.Host;

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
            return CoreResult<string>.Success(File.ReadAllText(Path));
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
