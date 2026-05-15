using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;

namespace Ghuboon.App.Services;

/// <summary>
/// Diff-applies a desired order onto an existing
/// <see cref="ObservableCollection{T}"/> in place, emitting the minimal
/// sequence of Remove / Move / Insert mutations.
/// <para>
/// Extracted from <see cref="ViewModels.TimelineViewModel"/>'s
/// <c>ReplaceItems</c>: the original Clear() + AddRange path fired
/// N + 1 <c>NotifyCollectionChanged</c> events per reload, which the
/// ListBox processed one at a time and the user saw as a TL flicker
/// every sync tick. With Move events, the bound list re-uses the
/// existing container visuals.
/// </para>
/// <para>
/// Identity is reference equality so view-model rows recycled across
/// reloads (kept by stable Id in the master cache) collapse to Move
/// events rather than triggering a full rebuild.
/// </para>
/// </summary>
public static class ObservableCollectionReconciler
{
    /// <summary>
    /// Mutates <paramref name="current"/> so its element order matches
    /// <paramref name="desired"/>. Uses reference equality for identity.
    /// </summary>
    public static void Apply<T>(ObservableCollection<T> current, IReadOnlyList<T> desired)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(current);
        ArgumentNullException.ThrowIfNull(desired);

        var targetSet = new HashSet<T>(desired, ReferenceEqualityComparer.Instance);

        // 1. Remove rows that are no longer in the target.
        for (var i = current.Count - 1; i >= 0; i--)
        {
            if (!targetSet.Contains(current[i]))
            {
                current.RemoveAt(i);
            }
        }

        // 2. Walk the target order and reconcile in place. At each index:
        //    a) if the existing item matches, advance;
        //    b) if the target already exists later, Move it forward;
        //    c) otherwise Insert it.
        for (var i = 0; i < desired.Count; i++)
        {
            var target = desired[i];
            if (i >= current.Count)
            {
                current.Add(target);
                continue;
            }
            if (ReferenceEquals(current[i], target))
            {
                continue;
            }
            var existingIdx = -1;
            for (var j = i + 1; j < current.Count; j++)
            {
                if (ReferenceEquals(current[j], target))
                {
                    existingIdx = j;
                    break;
                }
            }
            if (existingIdx >= 0)
            {
                current.Move(existingIdx, i);
            }
            else
            {
                current.Insert(i, target);
            }
        }
    }
}
