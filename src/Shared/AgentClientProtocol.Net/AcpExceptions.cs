using System.Text.Json;

namespace AgentClientProtocol;

/// <summary>Base of every error this library raises on its own.</summary>
public class AcpException : Exception
{
    public AcpException(string message)
        : base(message)
    {
    }

    public AcpException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

/// <summary>The agent answered a request with a JSON-RPC error.</summary>
public sealed class AcpRequestException : AcpException
{
    public AcpRequestException(string method, int code, string message, JsonElement? data = null)
        : base($"{method} failed with code {code}: {message}")
    {
        Method = method;
        Code = code;
        ErrorData = data;
    }

    public string Method { get; }

    /// <summary>The JSON-RPC error code, for example <c>-32601</c> (method not found) or <c>-32602</c> (invalid params).</summary>
    public int Code { get; }

    /// <summary>The error's <c>data</c>, when the agent sent any.</summary>
    public JsonElement? ErrorData { get; }
}

/// <summary>The connection to the agent ended: the process exited or closed its output. Every request in flight, and every later one, fails with this.</summary>
public sealed class AcpConnectionException : AcpException
{
    public AcpConnectionException(string message, int? exitCode = null, string stderrTail = "")
        : base(message)
    {
        ExitCode = exitCode;
        StderrTail = stderrTail;
    }

    /// <summary>The agent process's exit code; null when it is not known or the agent was not started by this library.</summary>
    public int? ExitCode { get; }

    /// <summary>The last lines the agent wrote to its standard error: usually why it died.</summary>
    public string StderrTail { get; }
}

/// <summary>The agent speaks a protocol version this client does not.</summary>
public sealed class AcpProtocolException : AcpException
{
    public AcpProtocolException(string message)
        : base(message)
    {
    }
}
