using System;
using System.Collections.Generic;

namespace Remold.Core.Migoto;

/// <summary>
/// The ps registers ONE build probes, saves and restores, read off a <see cref="ShaderSlotCatalog"/>. The
/// emitter takes this rather than a range of its own: a register a shader might bind an input at is a
/// measurement, and a measurement belongs in shipped data where it can be re-measured.
///
/// <para><see cref="StockFloor"/> is what a build with no readable catalog carries. The probe is what every
/// slot-aware section is built around — a twin guard's identification, a scoped retexture's bind, a
/// replacement's donor maps — so probing nothing would silently disable features that never needed the
/// catalog. The floor keeps them exactly as they were and gives up only what the measurement was for.</para>
/// </summary>
public sealed record ShaderSlotPlan(IReadOnlyList<int> StockMaps, IReadOnlyList<int> Ramp,
    IReadOnlyDictionary<string, IReadOnlyList<int>>? Properties = null)
{
    /// <summary>The stock-map registers a build probes when no measurement is readable: the range every
    /// release before the catalog shipped probed, which covers every slot layout seen at the time. It is a
    /// FLOOR and not a ceiling — a catalog-driven build probes whatever the catalog states, which reaches
    /// further — and it is used only where the shipped measurement cannot be read.</summary>
    public static readonly IReadOnlyList<int> StockFloorSlots = new[] { 0, 1, 2, 3, 4, 5, 6 };

    /// <summary>No catalog was readable: the classic stock range is probed and no ramp register is. The
    /// ramp is the one thing the measurement was needed for, so it is the one thing held back.</summary>
    public static ShaderSlotPlan StockFloor { get; } = new(StockFloorSlots, Array.Empty<int>());

    /// <summary>The plan <paramref name="catalog"/> states, whichever install reads it.
    ///
    /// <para>The install's own game build does not narrow this. A candidate register is accepted at draw
    /// time only when the texture bound there answers the tag the build put on it, so a register the
    /// measurement no longer describes binds nothing — a stale catalog can under-cover and cannot
    /// mis-bind. Narrowing on the build would instead cost coverage on every install the measurement
    /// happens not to name. The catalog's identity and the build it was measured on are RECORDED in the
    /// mod, so the drift is auditable from a published folder.</para></summary>
    public static ShaderSlotPlan For(ShaderSlotCatalog catalog) =>
        new(catalog.StockMapSlots, catalog.RampSlots, catalog.PropertySlots);

    /// <summary>The measured candidate registers for one exact shader property. The classic fallback does
    /// not claim property-specific evidence and therefore answers empty.</summary>
    public IReadOnlyList<int> ForProperty(string shaderProperty)
    {
        string key = ShaderSlotCatalog.CatalogKey(shaderProperty);
        return Properties?.TryGetValue(key, out var slots) == true ? slots : Array.Empty<int>();
    }

    /// <summary>The plan from the catalog shipped beside the assemblies, or <see cref="StockFloor"/> when
    /// there isn't one. Read once. A build states its own plan instead of leaning on this — it exists so
    /// the emitter has a stated default rather than an authored constant.</summary>
    public static ShaderSlotPlan Shipped => _shipped ??=
        ShaderSlotCatalog.TryLoad(LabPaths.ShaderSlotCatalogFile, out _) is { } c ? For(c) : StockFloor;

    private static ShaderSlotPlan? _shipped;
}
