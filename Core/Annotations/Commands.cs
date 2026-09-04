using System.Windows;

namespace SnipPin.Core.Annotations;

/// <summary>可撤销命令接口</summary>
public interface IAnnotationCommand
{
    void Execute();
    void Undo();
}

/// <summary>新增标注</summary>
public class AddAnnotationCommand : IAnnotationCommand
{
    private readonly IList<Annotation> _list;
    private readonly Annotation _item;

    public AddAnnotationCommand(IList<Annotation> list, Annotation item)
    {
        _list = list;
        _item = item;
    }

    public void Execute() => _list.Add(_item);
    public void Undo() => _list.Remove(_item);
}

/// <summary>删除标注</summary>
public class DeleteAnnotationCommand : IAnnotationCommand
{
    private readonly IList<Annotation> _list;
    private readonly Annotation _item;
    private int _index;

    public DeleteAnnotationCommand(IList<Annotation> list, Annotation item)
    {
        _list = list;
        _item = item;
    }

    public void Execute()
    {
        _index = _list.IndexOf(_item);
        _list.Remove(_item);
    }

    public void Undo()
    {
        if (_index < 0 || _index > _list.Count) _index = _list.Count;
        _list.Insert(_index, _item);
    }
}

/// <summary>移动标注</summary>
public class MoveAnnotationCommand : IAnnotationCommand
{
    private readonly Annotation _item;
    private readonly Vector _delta;

    public MoveAnnotationCommand(Annotation item, Vector delta)
    {
        _item = item;
        _delta = delta;
    }

    public void Execute() => _item.Translate(_delta);
    public void Undo() => _item.Translate(-_delta);
}

/// <summary>
/// 撤销/重做栈：承载一次截图编辑会话内的所有命令。
/// </summary>
public class UndoRedoStack
{
    private readonly Stack<IAnnotationCommand> _undo = new();
    private readonly Stack<IAnnotationCommand> _redo = new();

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>状态变化时触发（用于刷新 UI / 重绘）</summary>
    public event Action? Changed;

    /// <summary>执行新命令并清空重做栈</summary>
    public void Do(IAnnotationCommand command)
    {
        command.Execute();
        _undo.Push(command);
        _redo.Clear();
        Changed?.Invoke();
    }

    public void Undo()
    {
        if (!CanUndo) return;
        var cmd = _undo.Pop();
        cmd.Undo();
        _redo.Push(cmd);
        Changed?.Invoke();
    }

    public void Redo()
    {
        if (!CanRedo) return;
        var cmd = _redo.Pop();
        cmd.Execute();
        _undo.Push(cmd);
        Changed?.Invoke();
    }

    public void Clear()
    {
        _undo.Clear();
        _redo.Clear();
    }
}
