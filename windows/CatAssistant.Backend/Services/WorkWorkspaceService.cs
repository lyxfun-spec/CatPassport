using System.Collections.Concurrent;
using CatAssistant.Backend.Models;

namespace CatAssistant.Backend.Services;

public sealed class WorkWorkspaceService : IDisposable
{
    private readonly string _root;
    private readonly Func<string, string>? _conversationRoot;
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _gates = new(StringComparer.Ordinal);

    public WorkWorkspaceService(string? root = null, Func<string, string>? conversationRoot = null)
    {
        _root = Path.GetFullPath(root ?? Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "FoloToy", "CatAssistant", "Workspaces"));
        _conversationRoot = conversationRoot;
    }

    public async Task<WorkWorkspacePaths> EnsureAsync(string conversationId, CancellationToken cancellationToken = default)
    {
        ValidateConversationId(conversationId);
        var gate = _gates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var paths = Paths(conversationId);
            Directory.CreateDirectory(paths.Inputs);
            Directory.CreateDirectory(paths.Work);
            Directory.CreateDirectory(Path.Combine(paths.Work, "python-packages"));
            Directory.CreateDirectory(paths.Outputs);
            return paths;
        }
        finally
        {
            gate.Release();
        }
    }

    public async Task<string> CopyInputAsync(string conversationId, string sourcePath, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sourcePath)) throw new ArgumentException("输入文件路径不能为空。", nameof(sourcePath));
        var fullSourcePath = Path.GetFullPath(sourcePath);
        var source = new FileInfo(fullSourcePath);
        if (!source.Exists) throw new FileNotFoundException("输入文件不存在。", fullSourcePath);

        var gate = _gates.GetOrAdd(conversationId, _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken);
        try
        {
            var workspace = EnsureWithoutGate(conversationId);
            var fileName = Path.GetFileName(fullSourcePath);
            if (string.IsNullOrWhiteSpace(fileName)) throw new ArgumentException("输入文件名无效。", nameof(sourcePath));

            var stem = Path.GetFileNameWithoutExtension(fileName);
            var extension = Path.GetExtension(fileName);
            for (var suffix = 1; ; suffix++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var candidateName = suffix == 1 ? fileName : $"{stem} ({suffix}){extension}";
                var destination = Path.Combine(workspace.Inputs, candidateName);
                FileStream output;
                try
                {
                    output = new FileStream(destination, FileMode.CreateNew, FileAccess.Write, FileShare.None, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan);
                }
                catch (IOException) when (File.Exists(destination))
                {
                    // Another upload or an existing input owns this name. Try the next deterministic suffix.
                    continue;
                }
                try
                {
                    await using (output)
                    await using (var input = new FileStream(fullSourcePath, FileMode.Open, FileAccess.Read, FileShare.Read, 81920, FileOptions.Asynchronous | FileOptions.SequentialScan))
                        await input.CopyToAsync(output, cancellationToken);
                    return destination;
                }
                catch
                {
                    try { File.Delete(destination); } catch { }
                    throw;
                }
            }
        }
        finally
        {
            gate.Release();
        }
    }

    private WorkWorkspacePaths EnsureWithoutGate(string conversationId)
    {
        ValidateConversationId(conversationId);
        var paths = Paths(conversationId);
        Directory.CreateDirectory(paths.Inputs);
        Directory.CreateDirectory(paths.Work);
        Directory.CreateDirectory(Path.Combine(paths.Work, "python-packages"));
        Directory.CreateDirectory(paths.Outputs);
        return paths;
    }

    private WorkWorkspacePaths Paths(string conversationId)
    {
        var root = _conversationRoot is null
            ? Path.GetFullPath(Path.Combine(_root, conversationId))
            : Path.GetFullPath(_conversationRoot(conversationId));
        if (_conversationRoot is null)
        {
            var expectedPrefix = _root.EndsWith(Path.DirectorySeparatorChar) ? _root : _root + Path.DirectorySeparatorChar;
            if (!root.StartsWith(expectedPrefix, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("工作区路径越界。");
        }
        return new WorkWorkspacePaths(root, Path.Combine(root, "inputs"), Path.Combine(root, "work"), Path.Combine(root, "outputs"));
    }

    private static void ValidateConversationId(string conversationId)
    {
        if (!Guid.TryParseExact(conversationId, "N", out _))
            throw new ArgumentException("Work 对话 ID 无效。", nameof(conversationId));
    }

    public void Dispose()
    {
        foreach (var gate in _gates.Values) gate.Dispose();
        _gates.Clear();
    }
}
