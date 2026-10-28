using System.Text;
using System.Text.Json.Nodes;
using AgentClientProtocol.FakeAgent;

// A scripted ACP agent. The first word of a prompt picks what it does; see Scenarios.cs.
//   --protocol-version N   answer initialize with version N (default 1)
//   --no-usage             never report usage (the default is to follow the prompt's command)
var versionArg = Array.IndexOf(args, "--protocol-version");
var protocolVersion = versionArg >= 0 ? int.Parse(args[versionArg + 1], System.Globalization.CultureInfo.InvariantCulture) : 1;

var wire = new Wire(Console.OpenStandardOutput());
var agent = new Agent(wire, protocolVersion);
using var input = new StreamReader(Console.OpenStandardInput(), new UTF8Encoding(false));
var inFlight = new List<Task>();
while (await input.ReadLineAsync() is { } line)
{
    if (line.Length == 0)
        continue;
    var message = JsonNode.Parse(line)!.AsObject();
    if (message["method"] is { } method)
        inFlight.Add(Task.Run(() => agent.HandleAsync(method.GetValue<string>(), message["id"], message["params"]?.AsObject() ?? [])));
    else if (message["id"] is { } id)
        wire.Complete(id.GetValue<long>(), message);
}

await Task.WhenAll(inFlight);
