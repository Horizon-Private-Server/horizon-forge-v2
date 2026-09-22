namespace Forge.Host.Domain;

internal sealed class EditorHistory
{
    private const int MaxEntries = 100;
    private const long MaxBytes = 128L * 1024 * 1024;
    private readonly List<Entry> _undo = [];
    private readonly List<Entry> _redo = [];
    private long _bytes;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
        _bytes = 0;
    }

    public void Push(
        ForgeProjectState before,
        ForgeProjectState after,
        EntityId[] selectionBefore,
        EntityId[] selectionAfter,
        IReadOnlyList<EntityId> entityIds,
        long estimatedBytes)
    {
        foreach (var entry in _redo) _bytes -= entry.EstimatedBytes;
        _redo.Clear();
        var value = new Entry(before, after, selectionBefore, selectionAfter, entityIds.ToArray(), estimatedBytes);
        _undo.Add(value);
        _bytes += value.EstimatedBytes;
        while (_undo.Count > MaxEntries || _bytes > MaxBytes)
        {
            _bytes -= _undo[0].EstimatedBytes;
            _undo.RemoveAt(0);
        }
    }

    public bool TryUndo(out ForgeProjectState state, out EntityId[] selection, out EntityId[] entityIds)
    {
        if (_undo.Count == 0) return Empty(out state, out selection, out entityIds);
        var entry = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        _redo.Add(entry);
        state = entry.Before;
        selection = entry.SelectionBefore;
        entityIds = entry.EntityIds;
        return true;
    }

    public bool TryRedo(out ForgeProjectState state, out EntityId[] selection, out EntityId[] entityIds)
    {
        if (_redo.Count == 0) return Empty(out state, out selection, out entityIds);
        var entry = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        _undo.Add(entry);
        state = entry.After;
        selection = entry.SelectionAfter;
        entityIds = entry.EntityIds;
        return true;
    }

    private static bool Empty(out ForgeProjectState state, out EntityId[] selection, out EntityId[] entityIds)
    {
        state = null!;
        selection = [];
        entityIds = [];
        return false;
    }

    private sealed record Entry(
        ForgeProjectState Before,
        ForgeProjectState After,
        EntityId[] SelectionBefore,
        EntityId[] SelectionAfter,
        EntityId[] EntityIds,
        long EstimatedBytes);
}
