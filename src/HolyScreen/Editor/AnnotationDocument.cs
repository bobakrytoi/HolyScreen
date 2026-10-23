using System;
using System.Collections.Generic;

namespace HolyScreen.Editor;

/// <summary>A reversible change to the document.</summary>
internal interface IEditCommand
{
    void Apply(AnnotationDocument document);
    void Revert(AnnotationDocument document);
}

/// <summary>
/// The annotations of one capture, in z-order (first = bottom), with an undo/redo history. Edits that happen live
/// during a drag are recorded afterwards as one command (<see cref="Record"/>), so a whole drag undoes in one step.
/// </summary>
internal sealed class AnnotationDocument
{
    private readonly List<Annotation> _items = new();
    private readonly Stack<IEditCommand> _undo = new();
    private readonly Stack<IEditCommand> _redo = new();

    public IReadOnlyList<Annotation> Items => _items;

    public bool CanUndo => _undo.Count > 0;
    public bool CanRedo => _redo.Count > 0;

    /// <summary>Raised after every change (including undo/redo).</summary>
    public event Action? Changed;

    public void Execute(IEditCommand command)
    {
        command.Apply(this);
        Record(command);
    }

    /// <summary>Adds an already-applied change to the history.</summary>
    public void Record(IEditCommand command)
    {
        _undo.Push(command);
        _redo.Clear();
        Changed?.Invoke();
    }

    public bool Undo()
    {
        if (_undo.Count == 0) return false;
        IEditCommand command = _undo.Pop();
        command.Revert(this);
        _redo.Push(command);
        Changed?.Invoke();
        return true;
    }

    public bool Redo()
    {
        if (_redo.Count == 0) return false;
        IEditCommand command = _redo.Pop();
        command.Apply(this);
        _undo.Push(command);
        Changed?.Invoke();
        return true;
    }

    /// <summary>Replaces the whole content without history (used when re-opening a capture from history).</summary>
    public void Load(IEnumerable<Annotation> annotations)
    {
        _items.Clear();
        _items.AddRange(annotations);
        _undo.Clear();
        _redo.Clear();
        Changed?.Invoke();
    }

    public int IndexOf(Annotation annotation) => _items.IndexOf(annotation);

    internal void Insert(int index, Annotation annotation) => _items.Insert(Math.Clamp(index, 0, _items.Count), annotation);

    internal void Remove(Annotation annotation) => _items.Remove(annotation);
}

/// <summary>Adds an annotation on top.</summary>
internal sealed class AddCommand : IEditCommand
{
    private readonly Annotation _annotation;
    private int _index = -1;

    public AddCommand(Annotation annotation) => _annotation = annotation;

    public void Apply(AnnotationDocument document)
    {
        if (_index < 0) _index = document.Items.Count;
        document.Insert(_index, _annotation);
    }

    public void Revert(AnnotationDocument document) => document.Remove(_annotation);
}

/// <summary>Removes an annotation, remembering where it was in the z-order.</summary>
internal sealed class RemoveCommand : IEditCommand
{
    private readonly Annotation _annotation;
    private int _index;

    public RemoveCommand(Annotation annotation) => _annotation = annotation;

    public void Apply(AnnotationDocument document)
    {
        _index = document.IndexOf(_annotation);
        document.Remove(_annotation);
    }

    public void Revert(AnnotationDocument document) => document.Insert(_index, _annotation);
}

/// <summary>
/// Changes an annotation from one state to another by copying values into the same object, so its identity (and
/// any selection pointing at it) survives undo/redo, and several edits of one object stack correctly.
/// </summary>
internal sealed class ModifyCommand : IEditCommand
{
    private readonly Annotation _target;
    private readonly Annotation _before;
    private readonly Annotation _after;

    /// <param name="target">The object in the document.</param>
    public ModifyCommand(Annotation target, Annotation before, Annotation after)
    {
        _target = target;
        _before = before.Clone();
        _after = after.Clone();
    }

    public void Apply(AnnotationDocument document) => _target.CopyFrom(_after);

    public void Revert(AnnotationDocument document) => _target.CopyFrom(_before);
}
