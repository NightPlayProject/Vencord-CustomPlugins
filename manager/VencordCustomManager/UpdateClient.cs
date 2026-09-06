using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;

namespace VencordCustomManager;

public sealed class UpdateClient : IDisposable
{
    public const string ManifestUrl = "https://raw.githubusercontent.com/NightPlayProject/Vencord-CustomPlugins/main/update-manifest.json";

    private readonly HttpClient _httpClient;

    public UpdateClient()
    {
        _httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromMinutes(10)
        };
        _httpClient.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("VencordCustomManager", "0.1.0"));
        _httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue { NoCache = true };
    }

    public async Task<UpdateManifest> GetManifestAsync(CancellationToken cancellationToken = default)
    {
        using var response = await _httpClient.GetAsync(ManifestUrl, cancellationToken);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        var manifest = await JsonSerializer.DeserializeAsync<UpdateManifest>(stream, JsonOptions, cancellationToken)
            ?? throw new InvalidDataException("The update manifest was empty.");

        ValidateManifest(manifest);
        return manifest;
    }

    public async Task DownloadFileAsync(
        string url,
        string destination,
        IProgress<OperationProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var total = response.Content.Headers.ContentLength;

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(destination, FileMode.Create, FileAccess.Write, FileShare.None, 1024 * 128, true);

        var buffer = new byte[1024 * 128];
        long received = 0;
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            received += read;

            if (total is > 0)
            {
                var percent = (double)received / total.Value * 100d;
                progress?.Report(new OperationProgress($"Downloading update… {percent:0}%", percent));
            }
        }
    }

    public static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken = default)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1024 * 128, true);
        using var sha = SHA256.Create();
        var hash = await sha.ComputeHashAsync(stream, cancellationToken);
        return Convert.ToHexString(hash);
    }

    public static int CompareVersions(string left, string right)
    {
        static Version Parse(string value)
        {
            value = value.Trim().TrimStart('v', 'V');
            if (Version.TryParse(value, out var parsed)) return parsed;
            throw new InvalidDataException($"Invalid version in update data: {value}");
        }

        return Parse(left).CompareTo(Parse(right));
    }

    private static void ValidateManifest(UpdateManifest manifest)
    {
        if (manifest.Schema != 1)
            throw new InvalidDataException($"Unsupported update manifest schema: {manifest.Schema}");
        if (string.IsNullOrWhiteSpace(manifest.Version))
            throw new InvalidDataException("The update manifest has no version.");
        if (!Uri.TryCreate(manifest.Assets.WindowsRelease.Url, UriKind.Absolute, out var assetUri) || assetUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("The Windows release URL in the update manifest is invalid.");
        if (manifest.Assets.WindowsRelease.Sha256.Length != 64)
            throw new InvalidDataException("The Windows release SHA-256 in the update manifest is invalid.");
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public void Dispose() => _httpClient.Dispose();
}
