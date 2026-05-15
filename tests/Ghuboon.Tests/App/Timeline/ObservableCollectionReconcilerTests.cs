using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using Ghuboon.App.Services;
using Xunit;

namespace Ghuboon.Tests.App.Timeline;

/// <summary>
/// Pure-logic tests for <see cref="ObservableCollectionReconciler"/>. Pins
/// the diff-apply behaviour that previously lived inline in
/// <c>TimelineViewModel.ReplaceItems</c>: reference-equality identity,
/// Move events for reorders of recycled refs, and minimal mutation count
/// on the no-op fast path.
/// </summary>
public class ObservableCollectionReconcilerTests
{
    private sealed class Row { public Row(string id) { Id = id; } public string Id { get; } public override string ToString() => Id; }

    private static List<NotifyCollectionChangedEventArgs> CaptureChanges(ObservableCollection<Row> col)
    {
        var log = new List<NotifyCollectionChangedEventArgs>();
        col.CollectionChanged += (_, e) => log.Add(e);
        return log;
    }

    [Fact]
    public void Apply_EmptyToEmpty_EmitsNothing()
    {
        var col = new ObservableCollection<Row>();
        var log = CaptureChanges(col);

        ObservableCollectionReconciler.Apply(col, new List<Row>());

        Assert.Empty(col);
        Assert.Empty(log);
    }

    [Fact]
    public void Apply_NoChange_EmitsNothing()
    {
        var a = new Row("a");
        var b = new Row("b");
        var col = new ObservableCollection<Row> { a, b };
        var log = CaptureChanges(col);

        ObservableCollectionReconciler.Apply(col, new[] { a, b });

        Assert.Equal(new[] { a, b }, col);
        Assert.Empty(log);
    }

    [Fact]
    public void Apply_AppendOnly_EmitsAddEventsOnly()
    {
        var a = new Row("a");
        var b = new Row("b");
        var col = new ObservableCollection<Row> { a };
        var log = CaptureChanges(col);

        ObservableCollectionReconciler.Apply(col, new[] { a, b });

        Assert.Equal(new[] { a, b }, col);
        Assert.Single(log);
        Assert.Equal(NotifyCollectionChangedAction.Add, log[0].Action);
    }

    [Fact]
    public void Apply_RemoveOnly_EmitsRemoveEvents()
    {
        var a = new Row("a");
        var b = new Row("b");
        var c = new Row("c");
        var col = new ObservableCollection<Row> { a, b, c };
        var log = CaptureChanges(col);

        ObservableCollectionReconciler.Apply(col, new[] { a, c });

        Assert.Equal(new[] { a, c }, col);
        Assert.Single(log);
        Assert.Equal(NotifyCollectionChangedAction.Remove, log[0].Action);
    }

    [Fact]
    public void Apply_Reorder_EmitsMoveNotReplace()
    {
        // Swap order [a,b,c] -> [c,a,b]. Recycled refs must produce Move
        // events, not Replace / Remove+Insert, so the ListBox keeps its
        // existing container visuals (the original UX motivation).
        var a = new Row("a");
        var b = new Row("b");
        var c = new Row("c");
        var col = new ObservableCollection<Row> { a, b, c };
        var log = CaptureChanges(col);

        ObservableCollectionReconciler.Apply(col, new[] { c, a, b });

        Assert.Equal(new[] { c, a, b }, col);
        Assert.All(log, e => Assert.Equal(NotifyCollectionChangedAction.Move, e.Action));
    }

    [Fact]
    public void Apply_MixedInsertRemoveAndReorder()
    {
        var a = new Row("a");
        var b = new Row("b");
        var c = new Row("c");
        var d = new Row("d");
        var e = new Row("e");
        // current: a b c
        // desired: d c e a   (b removed, d & e inserted, a/c reordered)
        var col = new ObservableCollection<Row> { a, b, c };

        ObservableCollectionReconciler.Apply(col, new[] { d, c, e, a });

        Assert.Equal(new[] { d, c, e, a }, col);
    }

    [Fact]
    public void Apply_IdentityIsReferenceEquality()
    {
        // Equal-by-value-but-different-instance items must NOT be treated
        // as the same row. Reference equality is what the production VM
        // depends on so a brand-new VM (after a row falls out and reappears)
        // gets a fresh container instead of stale binding state.
        var a1 = new Row("same");
        var a2 = new Row("same");
        var col = new ObservableCollection<Row> { a1 };

        ObservableCollectionReconciler.Apply(col, new[] { a2 });

        Assert.Single(col);
        Assert.Same(a2, col[0]);
        Assert.NotSame(a1, col[0]);
    }

    [Fact]
    public void Apply_FullReplacement_DropsAllAndInsertsAll()
    {
        var olds = new[] { new Row("o1"), new Row("o2") };
        var news = new[] { new Row("n1"), new Row("n2") };
        var col = new ObservableCollection<Row>(olds);

        ObservableCollectionReconciler.Apply(col, news);

        Assert.Equal(news, col);
    }
}
