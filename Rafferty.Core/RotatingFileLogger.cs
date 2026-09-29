using System.Text;

namespace Rafferty.Core;

public sealed class RotatingFileLogger
{
    private readonly string _directory;
    private readonly string _baseName;
    private readonly long _maxBytes;
    private readonly int _maxFiles;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public RotatingFileLogger(string directory, string baseName, long maxBytes = 5 * 1024 * 1024, int maxFiles = 5)
    {
        _directory = directory;
        _baseName = baseName;
        _maxBytes = maxBytes;
        _maxFiles = maxFiles;
        Directory.CreateDirectory(directory);
    }

    public Task InfoAsync(string message, CancellationToken token = default) => WriteAsync("INFO", message, token);
    public Task SuccessAsync(string message, CancellationToken token = default) => WriteAsync("SUCCESS", message, token);
    public Task WarningAsync(string message, CancellationToken token = default) => WriteAsync("WARNING", message, token);
    public Task ErrorAsync(string message, CancellationToken token = default) => WriteAsync("ERROR", message, token);
    public Task DebugAsync(string message, CancellationToken token = default) => WriteAsync("DEBUG", message, token);

    private async Task WriteAsync(string level, string message, CancellationToken cancellationToken)
    {
        var clean = message.Replace('\r', ' ').Replace('\n', ' ');
        var line = $"{DateTimeOffset.Now:O} [{level}] {clean}{Environment.NewLine}";
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RotateIfNeeded(Encoding.UTF8.GetByteCount(line));
            await File.AppendAllTextAsync(CurrentPath, line, Encoding.UTF8, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _gate.Release();
        }
    }

    private string CurrentPath => Path.Combine(_directory, $"{_baseName}.log");

    private void RotateIfNeeded(int incomingBytes)
    {
        var current = new FileInfo(CurrentPath);
        if (!current.Exists || current.Length + incomingBytes <= _maxBytes)
        {
            return;
        }

        var oldest = Path.Combine(_directory, $"{_baseName}.{_maxFiles - 1}.log");
        if (File.Exists(oldest))
        {
            File.Delete(oldest);
        }

        for (var index = _maxFiles - 2; index >= 1; index--)
        {
            var source = Path.Combine(_directory, $"{_baseName}.{index}.log");
            var destination = Path.Combine(_directory, $"{_baseName}.{index + 1}.log");
            if (File.Exists(source))
            {
                File.Move(source, destination, true);
            }
        }

        File.Move(CurrentPath, Path.Combine(_directory, $"{_baseName}.1.log"), true);
    }
}

