namespace CraftPlanner.Core.History;

/// <summary>
/// スナップショット（メメント）方式の Undo/Redo。編集 1 回ごとに状態全体の複製を積む。
/// コマンド方式より単純で、「Undo がデータを壊さない」ことを確認しやすい。
/// 対象の状態は小さい（ノード数百件程度）ので複製コストは問題にならない。
/// </summary>
public sealed class HistoryManager<T>
{
    private readonly Func<T, T> _clone;
    private readonly int _capacity;
    private readonly List<T> _undo = new();
    private readonly List<T> _redo = new();

    public HistoryManager(Func<T, T> clone, int capacity = 200)
    {
        _clone = clone;
        _capacity = Math.Max(1, capacity);
    }

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;
    public int UndoCount => _undo.Count;
    public int RedoCount => _redo.Count;

    /// <summary>編集を加える「前」の状態を記録する。新しい編集が入ると Redo 履歴は破棄する。</summary>
    public void Record(T stateBeforeEdit)
    {
        _undo.Add(_clone(stateBeforeEdit));
        if (_undo.Count > _capacity) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>既に複製済みのスナップショット（ドラッグ開始時に取ったものなど）をそのまま記録する。</summary>
    public void RecordPrepared(T snapshot)
    {
        _undo.Add(snapshot);
        if (_undo.Count > _capacity) _undo.RemoveAt(0);
        _redo.Clear();
    }

    /// <summary>現在の状態を Redo 側に保存し、直前の状態を返す。</summary>
    public T Undo(T current)
    {
        if (!CanUndo) throw new InvalidOperationException("Undo できる操作がありません。");
        _redo.Add(_clone(current));
        var state = _undo[^1];
        _undo.RemoveAt(_undo.Count - 1);
        return _clone(state);
    }

    public T Redo(T current)
    {
        if (!CanRedo) throw new InvalidOperationException("Redo できる操作がありません。");
        _undo.Add(_clone(current));
        var state = _redo[^1];
        _redo.RemoveAt(_redo.Count - 1);
        return _clone(state);
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
