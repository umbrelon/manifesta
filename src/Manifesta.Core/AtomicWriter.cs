using Manifesta.Core.Pipeline;

namespace Manifesta.Core;

/// <summary>
/// Production IWriter. Writes via a temp file → atomic rename,
/// so a failed run never produces a partially-written file.
/// </summary>
public sealed class AtomicWriter : IWriter
{
    public bool IsDryRun => false;

    public async Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        var tmp = path + ".tmp_" + Guid.NewGuid().ToString("N");
        try
        {
            await File.WriteAllTextAsync(tmp, content, ct);
            File.Move(tmp, path, overwrite: true);
        }
        catch
        {
            if (File.Exists(tmp)) File.Delete(tmp);
            throw;
        }
    }
}

/// <summary>
/// Dry-run IWriter. Prints the resolved path and a content preview to stdout.
/// No files are written.
/// </summary>
public sealed class DryRunWriter : IWriter
{
    private readonly TextWriter _out;

    public DryRunWriter(TextWriter? output = null)
        => _out = output ?? Console.Out;

    public bool IsDryRun => true;

    public Task WriteAsync(string path, string content, CancellationToken ct = default)
    {
        _out.WriteLine($"[dry-run] Would write: {path}");
        _out.WriteLine($"          ({content.Length:N0} chars)");
        return Task.CompletedTask;
    }
}
