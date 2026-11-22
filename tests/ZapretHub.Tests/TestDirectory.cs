namespace ZapretHub.Tests;

internal sealed class TestDirectory : IDisposable
{
    public string Path { get; } = Directory.CreateTempSubdirectory("zaprethub-").FullName;
    public void Dispose() => Directory.Delete(Path, recursive: true);
}
