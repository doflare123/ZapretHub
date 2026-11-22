using System.IO.Compression;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace ZapretHub.Core;

public sealed partial class GitHubReleaseStore(HttpClient client, string directory) : IReleaseStore
{
    private const string Repository = "Flowseal/zapret-discord-youtube";
    private const int MaximumZipSize = 32 * 1024 * 1024;
    private const long MaximumExtractedSize = 128 * 1024 * 1024;
    private const string Marker = ".zaprethub-release";

    public IReadOnlyList<Strategy> ListStrategies() => InstalledDirectories()
        .SelectMany(folder => ListScripts(folder.FullName).Select(path =>
            new Strategy(new StrategyId(folder.Name, Path.GetFileNameWithoutExtension(path)), path)))
        .ToArray();

    public async Task<string?> RefreshAsync(CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://api.github.com/repos/{Repository}/releases/latest");
        request.Headers.UserAgent.ParseAdd("ZapretHub/1.0");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");
        using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        var release = await response.Content.ReadFromJsonAsync<Release>(cancellationToken).ConfigureAwait(false);
        if (release?.Tag is not { } tag || !VersionName().IsMatch(tag))
            throw new InvalidDataException("GitHub вернул некорректное имя релиза.");

        string destination = Path.Combine(directory, tag);
        if (File.Exists(Path.Combine(destination, Marker)))
            return null;

        string assetName = $"zapret-discord-youtube-{tag}.zip";
        var asset = release.Assets?.SingleOrDefault(candidate => candidate.Name == assetName);
        var downloadUrl = new Uri($"https://github.com/{Repository}/releases/download/{tag}/{assetName}");
        if (asset?.Digest is not { } digest || !Sha256Digest().IsMatch(digest) || asset.Url != downloadUrl
            || asset.Size is <= 0 or > MaximumZipSize)
            throw new InvalidDataException("Для релиза нет ZIP-архива с проверяемой контрольной суммой.");

        using var archiveResponse = await client.GetAsync(downloadUrl, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        archiveResponse.EnsureSuccessStatusCode();
        await using var source = await archiveResponse.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var bytes = new MemoryStream(asset.Size);
        await CopyZipAsync(source, bytes, asset.Size, cancellationToken).ConfigureAwait(false);
        if (!Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)))
                .Equals(digest[7..], StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Контрольная сумма архива Zapret не совпала.");

        bytes.Position = 0;
        Directory.CreateDirectory(directory);
        string temporary = Path.Combine(directory, $".stage-{Guid.NewGuid():N}");
        Directory.CreateDirectory(temporary);
        try
        {
            await ExtractZipAsync(bytes, temporary, cancellationToken).ConfigureAwait(false);
            string? bundle = new[] { temporary }.Concat(Directory.EnumerateDirectories(temporary)).FirstOrDefault(path =>
                ListScripts(path).Length > 0 && File.Exists(Path.Combine(path, "bin", "winws.exe")));
            if (bundle is null)
                throw new InvalidDataException("В архиве отсутствуют конфигурации Flowseal или winws.exe.");
            await File.WriteAllTextAsync(Path.Combine(bundle, Marker), tag, cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            Directory.Move(bundle, destination);
            return tag;
        }
        finally
        {
            if (Directory.Exists(temporary))
                Directory.Delete(temporary, recursive: true);
        }
    }

    public void Prune(int keep, IReadOnlySet<string> protectedVersions)
    {
        DirectoryInfo[] installed = InstalledDirectories();
        var retained = new HashSet<string>(installed.Take(keep).Select(folder => folder.Name), StringComparer.OrdinalIgnoreCase);
        retained.UnionWith(protectedVersions);
        foreach (DirectoryInfo folder in installed.Where(folder => !retained.Contains(folder.Name)))
            folder.Delete(recursive: true);
    }

    private DirectoryInfo[] InstalledDirectories()
    {
        if (!Directory.Exists(directory))
            return [];
        return new DirectoryInfo(directory).EnumerateDirectories()
            .Where(folder => VersionName().IsMatch(folder.Name) && !folder.Attributes.HasFlag(FileAttributes.ReparsePoint))
            .Select(folder => (Folder: folder, Marker: new FileInfo(Path.Combine(folder.FullName, Marker))))
            .Where(entry => entry.Marker.Exists)
            .OrderByDescending(entry => entry.Marker.LastWriteTimeUtc)
            .ThenByDescending(entry => entry.Folder.Name, StringComparer.OrdinalIgnoreCase)
            .Select(entry => entry.Folder).ToArray();
    }

    private static string[] ListScripts(string path) => Directory.EnumerateFiles(path, "*.bat")
        .Where(file => ScriptName().IsMatch(Path.GetFileName(file)))
        .OrderBy(file => !Path.GetFileName(file).Equals("general.bat", StringComparison.OrdinalIgnoreCase))
        .ThenBy(file => file, StringComparer.OrdinalIgnoreCase).ToArray();

    private static async Task CopyZipAsync(Stream source, Stream destination, int length, CancellationToken cancellationToken)
    {
        byte[] buffer = new byte[81920];
        int count;
        while ((count = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) != 0)
        {
            if (destination.Length + count > length)
                throw new InvalidDataException("Размер ZIP-архива превышает размер из метаданных релиза.");
            await destination.WriteAsync(buffer.AsMemory(0, count), cancellationToken).ConfigureAwait(false);
        }
        if (destination.Length != length)
            throw new InvalidDataException("ZIP-архив загружен не полностью.");
    }

    private static async Task ExtractZipAsync(Stream zip, string destination, CancellationToken cancellationToken)
    {
        using var archive = new ZipArchive(zip);
        if (archive.Entries.Count > 2048 || archive.Entries.Sum(entry => entry.Length) > MaximumExtractedSize)
            throw new InvalidDataException("ZIP-архив слишком большой после распаковки.");

        string root = Path.GetFullPath(destination) + Path.DirectorySeparatorChar;
        foreach (ZipArchiveEntry entry in archive.Entries)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.FullName.Contains(':') || entry.FullName.Split('/', '\\').Any(part => part is ".." or "."))
                throw new InvalidDataException("Некорректный путь в ZIP-архиве.");
            string output = Path.GetFullPath(Path.Combine(root, entry.FullName));
            if (!output.StartsWith(root, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Путь в ZIP-архиве выходит за папку распаковки.");
            if (entry.Name.Length == 0)
            {
                Directory.CreateDirectory(output);
                continue;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            await using Stream input = entry.Open();
            await using var file = new FileStream(output, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, useAsync: true);
            await input.CopyToAsync(file, cancellationToken).ConfigureAwait(false);
        }
    }

    [GeneratedRegex(@"\A[0-9][A-Za-z0-9.-]{0,63}(?<!\.)\z", RegexOptions.CultureInvariant)]
    private static partial Regex VersionName();

    [GeneratedRegex(@"\Asha256:[a-fA-F0-9]{64}\z", RegexOptions.CultureInvariant)]
    private static partial Regex Sha256Digest();

    [GeneratedRegex(@"\Ageneral(?: \([A-Za-z0-9 +_.-]+\))?\.bat\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    internal static partial Regex ScriptName();

    private sealed record Release([property: JsonPropertyName("tag_name")] string? Tag, Asset[]? Assets);
    private sealed record Asset(string Name, string? Digest, int Size, [property: JsonPropertyName("browser_download_url")] Uri? Url);
}
