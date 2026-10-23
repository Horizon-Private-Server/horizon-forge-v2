namespace Forge.Host.Domain;

public sealed partial class EditorRuntime
{
    internal async Task<TResult> ReadWorkspaceAsync<TResult>(
        Func<ForgeProjectWorkspace, CancellationToken, Task<TResult>> reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(reader);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            return await reader(RequireWorkspace(), cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }

    internal async Task<TResult> ReadWorkspaceAsync<TResult>(
        string projectPath,
        Func<ForgeProjectWorkspace, CancellationToken, Task<TResult>> reader,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectPath);
        ArgumentNullException.ThrowIfNull(reader);
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ThrowIfDisposed();
            var workspace = RequireWorkspace();
            if (!string.Equals(
                    Path.GetFullPath(projectPath),
                    Path.GetFullPath(workspace.RootPath),
                    OperatingSystem.IsWindows()
                        ? StringComparison.OrdinalIgnoreCase
                        : StringComparison.Ordinal))
                throw new InvalidOperationException("The preview project is not the active editor project.");
            return await reader(workspace, cancellationToken);
        }
        finally
        {
            _gate.Release();
        }
    }
}
