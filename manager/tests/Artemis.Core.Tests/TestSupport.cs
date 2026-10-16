namespace Artemis.Core.Tests;

public sealed class TempDir : IDisposable
{
    public string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "artemis-tests-" + Guid.NewGuid().ToString("N"));

    public TempDir() => Directory.CreateDirectory(Path);

    public string File(string relative, string content)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(full)!);
        System.IO.File.WriteAllText(full, content);
        return full;
    }

    public string Dir(string relative)
    {
        var full = System.IO.Path.Combine(Path, relative.Replace('/', System.IO.Path.DirectorySeparatorChar));
        Directory.CreateDirectory(full);
        return full;
    }

    public void Dispose()
    {
        try { Directory.Delete(Path, recursive: true); } catch (IOException) { }
    }
}
