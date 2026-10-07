using System;
using System.Collections.Generic;
using System.Linq;

namespace Remold.Core.Migoto;

/// <summary>The bound resources a mesh section keys on: the index-buffer hash and the static vertex-stream
/// hashes bound beside it. A null slot means the mesh HAS NO such stream — its draws bind nothing there —
/// never "unmeasured": every producer derives the selector from the mesh's own stream list.
///
/// <para>One selector reads two ways. As a SECTION, a null slot is unconstrained (the section fires whatever
/// is bound there) and a hashed slot demands that hash. As a DRAW, a null slot is unbound, which fails any
/// section constraining it. <see cref="FiresOn"/> is that runtime rule; <see cref="Reduce"/> keeps only the
/// slots a section needs to exclude the other selectors measured on its index buffer.</para></summary>
public sealed record DrawSelector(string Hash, string? Vb0 = null, string? Vb1 = null, string? Vb2 = null)
{
    public bool IsCompound => Vb0 is not null || Vb1 is not null || Vb2 is not null;
    public string Key => IsCompound ? $"{Hash}_v0{Vb0 ?? "x"}_v1{Vb1 ?? "x"}_v2{Vb2 ?? "x"}" : Hash;

    /// <summary>Every hash a section on this selector acts on: the index buffer, then each constrained
    /// vertex slot.</summary>
    public IEnumerable<(string Slot, string Hash)> Bindings()
    {
        yield return ("ib", Hash);
        foreach (var binding in SlotBindings()) yield return binding;
    }

    /// <summary>The constrained vertex slots alone — the terms of a section's predicate. The index buffer is
    /// the section's own match and is never tested again inside it.</summary>
    public IEnumerable<(string Slot, string Hash)> SlotBindings()
    {
        if (Vb0 is not null) yield return ("vb0", Vb0);
        if (Vb1 is not null) yield return ("vb1", Vb1);
        if (Vb2 is not null) yield return ("vb2", Vb2);
    }

    public static DrawSelector Parse(string key)
    {
        if (!key.Contains("_v0", StringComparison.Ordinal)) return new(key);
        var pieces = key.Split('_');
        if (pieces.Length != 4 || !Hex(pieces[0]) || !pieces[1].StartsWith("v0", StringComparison.Ordinal)
            || !pieces[2].StartsWith("v1", StringComparison.Ordinal) || !pieces[3].StartsWith("v2", StringComparison.Ordinal))
            throw new FormatException("Invalid draw selector");
        string? Slot(string value) => value == "x" ? null : Hex(value) ? value
            : throw new FormatException("Invalid draw selector slot");
        return new(pieces[0], Slot(pieces[1][2..]), Slot(pieces[2][2..]), Slot(pieces[3][2..]));
    }

    public static bool IsValidKey(string key)
    {
        try { var selector = Parse(key); return Hex(selector.Hash); }
        catch (FormatException) { return false; }
    }

    static bool Hex(string value) => value.Length == 8 && value.All(Uri.IsHexDigit);

    /// <summary>Whether a section keyed on this selector runs at a draw of <paramref name="draw"/>: the same
    /// index buffer, and every slot this selector constrains bound to that hash. A slot the draw does not
    /// bind fails the constraint, so a section on the fuller selector never fires on the barer mesh's draws,
    /// while a bare section fires on every mesh sharing its index buffer.</summary>
    public bool FiresOn(DrawSelector draw) => Hash == draw.Hash
        && Bound(Vb0, draw.Vb0) && Bound(Vb1, draw.Vb1) && Bound(Vb2, draw.Vb2);

    static bool Bound(string? constraint, string? bound) => constraint is null || bound == constraint;

    /// <summary>Whether sections on the two MEASURED selectors can act on one draw, in either direction.
    /// Both sides must be full selectors: a reduced (emitted) key on the draw side reads its dropped slots as
    /// unbound and misses a real collision — a build judges emitted keys against each other's full
    /// selector instead.</summary>
    public bool Overlaps(DrawSelector other) => FiresOn(other) || other.FiresOn(this);

    /// <summary>The smallest selector on this index buffer whose section fires on none of
    /// <paramref name="others"/> — the distinct selectors measured on the same index buffer, this one itself
    /// ignored: the bare hash when nothing else shares the buffer, one slot when that slot alone separates,
    /// and the full selector when even that cannot, since no section excludes a mesh whose bound streams are
    /// all identical. A slot this mesh lacks is never constrained.</summary>
    public DrawSelector Reduce(IEnumerable<DrawSelector> others)
    {
        var rivals = others.Where(other => other.Hash == Hash && other != this).ToList();
        foreach (var candidate in Candidates())
            if (!rivals.Any(candidate.FiresOn)) return candidate;
        return this;
    }

    // every sub-selector of this one, fewest constrained slots first and in one fixed slot order, so two
    // builds of one mesh emit one predicate
    private IEnumerable<DrawSelector> Candidates()
    {
        var slots = new List<Func<DrawSelector, DrawSelector>>();
        if (Vb1 is not null) slots.Add(s => s with { Vb1 = Vb1 });
        if (Vb2 is not null) slots.Add(s => s with { Vb2 = Vb2 });
        if (Vb0 is not null) slots.Add(s => s with { Vb0 = Vb0 });
        for (int count = 0; count <= slots.Count; count++)
            foreach (var chosen in Choose(slots, count))
            {
                var selector = new DrawSelector(Hash);
                foreach (var constrain in chosen) selector = constrain(selector);
                yield return selector;
            }
    }

    private static IEnumerable<IReadOnlyList<T>> Choose<T>(IReadOnlyList<T> items, int count, int from = 0)
    {
        if (count == 0) { yield return Array.Empty<T>(); yield break; }
        for (int i = from; i <= items.Count - count; i++)
            foreach (var rest in Choose(items, count - 1, i + 1))
                yield return new[] { items[i] }.Concat(rest).ToList();
    }
}
