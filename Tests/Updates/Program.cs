using System.Net;
using System.Text.Json;
using CompactLoader;

var checks = 0;
async Task Check(string tag, bool x64, bool expected, bool prerelease = false, bool draft = false, string? prefix = null, bool missing = false)
{
    var names = missing ? new[] { "SHA256SUMS.txt" } : new[] { "CompactLoader-win-x64.exe", "CompactLoader-win-x86.exe", "SHA256SUMS.txt" };
    var json = JsonSerializer.Serialize(new {
        tag_name = tag, prerelease, draft,
        assets = names.Select(name => new { name, browser_download_url = (prefix ?? "https://github.com/bronemishka/compactloader/releases/download/") + tag + "/" + name })
    });
    using var client = new HttpClient(new ResponseHandler(json));
    var update = await UpdateService.Check(client, new Version(1, 2, 0, 0), x64, CancellationToken.None);
    if ((update is not null) != expected || update is not null && update.FileName != $"CompactLoader-win-{(x64 ? "x64" : "x86")}.exe")
        throw new Exception("Unexpected update result: " + tag);
    checks++;
}
await Check("v1.2.0", true, false);
await Check("v1.1.9", true, false);
await Check("v1.3.0", true, true);
await Check("v1.3.0", false, true);
await Check("v1.3.0", true, false, prerelease: true);
await Check("v1.3.0", true, false, draft: true);
await Check("v1.3.0-beta", true, false);
await Check("invalid", true, false);
try { await Check("v1.3.0", true, true, prefix: "https://example.com/"); throw new Exception("Untrusted asset accepted"); }
catch (System.IO.InvalidDataException) { checks++; }
try { await Check("v1.3.0", true, true, missing: true); throw new Exception("Missing asset accepted"); }
catch (InvalidOperationException) { checks++; }
Console.WriteLine($"Passed {checks} update checks.");

sealed class ResponseHandler(string json) : HttpMessageHandler
{
    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(json) });
}
