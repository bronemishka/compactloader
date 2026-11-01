using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace CompactLoader;

internal sealed record AvailableUpdate(Version Version, Uri Executable, Uri Checksums, string FileName);

internal static class UpdateService
{
    static readonly HttpClient Client = new() { Timeout = TimeSpan.FromMinutes(3), MaxResponseContentBufferSize = 128 * 1024 * 1024 };
    const string Repository = "https://github.com/bronemishka/compactloader/releases/download/";

    public static async Task<AvailableUpdate?> Check(CancellationToken cancellationToken)
        => await Check(Client, typeof(UpdateService).Assembly.GetName().Version!, Environment.Is64BitProcess, cancellationToken);

    internal static async Task<AvailableUpdate?> Check(HttpClient client, Version current, bool is64Bit, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/repos/bronemishka/compactloader/releases/latest");
        request.Headers.UserAgent.ParseAdd("CompactLoader/" + typeof(UpdateService).Assembly.GetName().Version);
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        using var response = await client.SendAsync(request, timeout.Token);
        response.EnsureSuccessStatusCode();
        using var json = JsonDocument.Parse(await response.Content.ReadAsStringAsync(timeout.Token));
        var root = json.RootElement;
        var tag = root.GetProperty("tag_name").GetString() ?? "";
        if (root.GetProperty("draft").GetBoolean() || root.GetProperty("prerelease").GetBoolean() ||
            !Version.TryParse(tag.TrimStart('v'), out var version)) return null;
        var normalized = new Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
        if (normalized <= current) return null;
        string fileName = $"CompactLoader-win-{(is64Bit ? "x64" : "x86")}.exe";
        Uri Asset(string name)
        {
            var asset = root.GetProperty("assets").EnumerateArray().Single(a => a.GetProperty("name").GetString() == name);
            var uri = new Uri(asset.GetProperty("browser_download_url").GetString()!);
            if (uri.AbsoluteUri != Repository + Uri.EscapeDataString(tag) + "/" + name)
                throw new InvalidDataException("Unexpected release asset URL.");
            return uri;
        }
        return new(version, Asset(fileName), Asset("SHA256SUMS.txt"), fileName);
    }

    public static async Task<string> Prepare(AvailableUpdate update, CancellationToken cancellationToken)
    {
        // Development builds contain multiple files and must not be replaced by a published bundle.
#pragma warning disable IL3000 // An empty Location deliberately identifies a published bundle.
        if (!string.IsNullOrEmpty(typeof(UpdateService).Assembly.Location))
            throw new InvalidOperationException("Automatic installation requires a published single-file EXE. Download the release from GitHub.");
#pragma warning restore IL3000
        var target = Environment.ProcessPath ?? throw new IOException("Executable path is unavailable.");
        var directory = Path.GetDirectoryName(target)!;
        var staged = Path.Combine(directory, ".CompactLoader-update-" + Guid.NewGuid().ToString("N") + ".exe");
        try
        {
            var sums = await Client.GetStringAsync(update.Checksums, cancellationToken);
            var line = sums.Split('\n').Select(l => l.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
                .Single(parts => parts.Length == 2 && parts[1].TrimStart('*') == update.FileName);
            var expected = Convert.FromHexString(line[0]);
            if (expected.Length != 32) throw new InvalidDataException("Invalid SHA-256 checksum.");
            var bytes = await Client.GetByteArrayAsync(update.Executable, cancellationToken);
            if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes), expected))
                throw new InvalidDataException("The downloaded update failed SHA-256 verification.");
            await File.WriteAllBytesAsync(staged, bytes, cancellationToken);
            return staged;
        }
        catch
        {
            if (File.Exists(staged)) File.Delete(staged);
            throw;
        }
    }

    public static void InstallOnExit(string staged)
    {
        var target = Environment.ProcessPath!;
        static string Quote(string value) => "'" + value.Replace("'", "''") + "'";
        // File.Replace is atomic; failure leaves the installed executable intact.
        var script = $$"""
            $ErrorActionPreference = 'Stop'
            $target = {{Quote(target)}}
            $staged = {{Quote(staged)}}
            $backup = $target + '.previous'
            try {
                $process = Get-Process -Id {{Environment.ProcessId}} -ErrorAction SilentlyContinue
                if ($process) { $process.WaitForExit() }
                $replaced = $false
                for ($attempt = 0; $attempt -lt 30; $attempt++) {
                    try { [System.IO.File]::Replace($staged, $target, $backup); $replaced = $true; break }
                    catch { Start-Sleep -Milliseconds 500 }
                }
                if (!$replaced) { throw 'Could not replace the executable.' }
                try { Start-Process -FilePath $target }
                catch { [System.IO.File]::Copy($backup, $target, $true); throw }
            } catch {
                $_ | Out-String | Set-Content -LiteralPath ($target + '.update-error.txt')
            } finally {
                if (Test-Path -LiteralPath $staged) { Remove-Item -LiteralPath $staged -Force }
            }
            """;
        var start = new ProcessStartInfo(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe"))
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden
        };
        start.ArgumentList.Add("-NoProfile");
        start.ArgumentList.Add("-NonInteractive");
        start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var helper = Process.Start(start) ?? throw new IOException("Could not start the update helper.");
    }
}
