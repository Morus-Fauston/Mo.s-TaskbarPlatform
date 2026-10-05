namespace Mtp.Transport;

public sealed class ProtocolException(string code, Exception? innerException = null)
    : IOException($"Protocol frame rejected: {code}.", innerException)
{
    public string Code { get; } = code;
}
