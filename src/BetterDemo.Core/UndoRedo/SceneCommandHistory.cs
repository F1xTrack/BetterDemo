using BetterDemo.Core.Scene;

namespace BetterDemo.Core.UndoRedo;

public class SceneCommandHistory
{
    private readonly Stack<HistoryEntry> undo = new();
    private readonly Stack<HistoryEntry> redo = new();

    public int UndoCount => undo.Count;
    public int RedoCount => redo.Count;
    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;

    public void Execute(SceneDocument document, ISceneCommand command)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(command);

        var before = document.Snapshot();
        try
        {
            command.Execute(document);
        }
        catch
        {
            document.Restore(before);
            throw;
        }

        undo.Push(new HistoryEntry(before, document.Snapshot()));
        redo.Clear();
    }

    public bool Undo(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (undo.Count == 0) return false;
        var entry = undo.Pop();
        document.Restore(entry.Before);
        redo.Push(entry);
        return true;
    }

    public bool Redo(SceneDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        if (redo.Count == 0) return false;
        var entry = redo.Pop();
        document.Restore(entry.After);
        undo.Push(entry);
        return true;
    }

    private sealed record HistoryEntry(SceneDocument Before, SceneDocument After);
}

public sealed class CommandHistory : SceneCommandHistory
{
}
