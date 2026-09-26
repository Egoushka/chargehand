// The content engine's call to chargehand (ADR 0014), made with Chargehand.Contracts alone: non-interactive, one call
// per draft, facts as inputs[] (cited with evidence kind "input"), the generator prompt as a caller block, the draft
// returned as an inline artifact, and prompt_chain recorded per draft. Until the content engine exists, this stand-in
// makes exactly that call against `chargehand serve`.
//
// usage: CHARGEHAND_API_KEY=... dotnet run --project samples/ContentEngineCall [base-url]
// Exit codes: 0 the draft passed every check, 1 a check failed, 2 usage error.
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Chargehand.Contracts;

var baseUrl = args.FirstOrDefault() ?? "http://127.0.0.1:4300";
if (Environment.GetEnvironmentVariable("CHARGEHAND_API_KEY") is not { Length: > 0 } key)
{
    Console.Error.WriteLine("CHARGEHAND_API_KEY is not set");
    return 2;
}

// Public facts only: this repository's own changelog.
const string Changelog = "https://github.com/Egoushka/chargehand/blob/main/CHANGELOG.md";
var generator = PromptBlock.Create("generator/github-readme-section", "0.1.0", """
    Write the "What I'm building" section of a GitHub profile README: 3 to 5 sentences, first person, plain words,
    no headings and no emoji. Use only the inputs; say nothing the inputs do not support.
    """);
var request = new RunRequest("request/v1", "Draft the 'What I'm building' section of a GitHub profile README from the inputs.",
    new RequestContext(Interactive: false, Preset: "draft", BudgetUsd: 0.05m),
    [
        new CallerInput("project", "identity", "I build chargehand, an open-source .NET 10 orchestrator that drives OpenCode sessions over HTTP.", Changelog),
        new CallerInput("rel-v1", "signal", "chargehand v1 runs 2 to 4 read-only subtasks as a task graph; later nodes fork the first node's session and read its prompt prefix from cache.", Changelog),
        new CallerInput("evidence", "signal", "Every claim in chargehand's result contract carries evidence that is checked before the result leaves its node.", Changelog),
        new CallerInput("bench-p4", "signal", "In the phase 4 benchmark on a small model, 99 of 99 cited file lines resolved.", Changelog),
    ],
    [generator]);

using var http = new HttpClient { BaseAddress = new Uri(baseUrl), Timeout = TimeSpan.FromMinutes(5) };
http.DefaultRequestHeaders.Authorization = new("Bearer", key);
using var post = new HttpRequestMessage(HttpMethod.Post, "/v1/runs") { Content = JsonContent.Create(request, options: ContractJson.Options) };
post.Headers.Add("Prefer", "wait=30");
var response = await http.SendAsync(post);
var location = response.Headers.Location;
while (response.StatusCode == HttpStatusCode.Accepted)
{
    await Task.Delay(TimeSpan.FromSeconds(2));
    response = await http.GetAsync(location);
}
var body = await response.Content.ReadAsStringAsync();
if (response.StatusCode != HttpStatusCode.OK)
{
    Console.Error.WriteLine($"HTTP {(int)response.StatusCode}: {body}");
    return 1;
}

using var doc = JsonDocument.Parse(body);
var failures = ContractSchemas.Validate(ContractSchemas.Result, doc.RootElement).Select(e => $"result/v1: {e}").ToList();
var result = doc.RootElement.Deserialize<ResultContract>(ContractJson.Options)!;
var draft = result.Artifacts.FirstOrDefault(a => a.Kind == "draft");
var sent = request.Inputs!.Select(i => i.Id).ToHashSet();
var check = new (bool Ok, string What)[]
{
    (result.Status == ResultStatus.Completed, $"status completed (got {result.Status})"),
    (draft is { MediaType: "text/markdown", Content: not null }, "one inline text/markdown draft artifact"),
    (draft?.Content is { } c && draft.Sha256 == Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(c))), "the draft's sha256 matches its content"),
    (result.Claims.Count > 0, "the draft's statements come back as claims"),
    (result.Evidence.All(e => e.Kind == EvidenceKind.Input && sent.Contains(e.Locator)), "every evidence item cites an input that was sent"),
    (result.PromptChain.Blocks.Any(b => b.Source == BlockSource.Caller && b.Name == generator.Name && b.Sha256 == generator.Sha256), "prompt_chain records the generator block"),
};
failures.AddRange(check.Where(c => !c.Ok).Select(c => c.What));

Console.WriteLine($"run {result.TaskId}  status {result.Status}  usd {result.Usage.Usd}  claims {result.Claims.Count}  open {result.OpenQuestions.Count}");
Console.WriteLine($"prompt_chain: {string.Join(", ", result.PromptChain.Blocks.Select(b => $"{b.Name} {b.Version} ({b.Source}, {b.Sha256[..12]})"))}");
Console.WriteLine($"draft ({draft?.Sha256[..12]}):\n{draft?.Content}\n");
foreach (var (ok, what) in check)
    Console.WriteLine($"{(ok ? "PASS" : "FAIL")}  {what}");
foreach (var f in failures.Where(f => f.StartsWith("result/v1", StringComparison.Ordinal)))
    Console.WriteLine($"FAIL  {f}");
return failures.Count == 0 ? 0 : 1;
