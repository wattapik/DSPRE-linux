using System.Text;
using System.Text.Json.Nodes;

namespace DSPRE.LuaLanguageServer;

/// <summary>LSP's base protocol: JSON messages framed by a <c>Content-Length</c> header, over a pair of streams.</summary>
internal sealed class JsonRpc(Stream input, Stream output)
{
    private readonly object writeLock = new();

    public JsonNode? Read()
    {
        int length = -1;
        while (true)
        {
            string? header = ReadHeaderLine();
            if (header is null) return null;
            if (header.Length == 0) break;
            if (header.StartsWith("Content-Length:", StringComparison.OrdinalIgnoreCase)) length = int.Parse(header[15..].Trim());
        }
        if (length < 0) return null;
        byte[] body = new byte[length];
        int read = 0;
        while (read < length)
        {
            int count = input.Read(body, read, length - read);
            if (count == 0) return null;
            read += count;
        }
        return JsonNode.Parse(body);
    }

    private string? ReadHeaderLine()
    {
        var line = new StringBuilder();
        while (true)
        {
            int b = input.ReadByte();
            if (b < 0) return line.Length == 0 ? null : line.ToString();
            if (b == '\n') return line.ToString().TrimEnd('\r');
            line.Append((char)b);
        }
    }

    public void Send(JsonObject message)
    {
        message["jsonrpc"] = "2.0";
        byte[] body = Encoding.UTF8.GetBytes(message.ToJsonString());
        byte[] header = Encoding.ASCII.GetBytes($"Content-Length: {body.Length}\r\n\r\n");
        lock (writeLock)
        {
            output.Write(header);
            output.Write(body);
            output.Flush();
        }
    }

    public void Respond(JsonNode? id, JsonNode? result) => Send(new JsonObject { ["id"] = id?.DeepClone(), ["result"] = result });

    public void Fail(JsonNode? id, int code, string message) =>
        Send(new JsonObject { ["id"] = id?.DeepClone(), ["error"] = new JsonObject { ["code"] = code, ["message"] = message } });

    public void Notify(string method, JsonNode? parameters) => Send(new JsonObject { ["method"] = method, ["params"] = parameters });
}
