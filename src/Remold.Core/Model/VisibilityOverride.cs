namespace Remold.Core.Model;

/// <summary>
/// A game-side mechanism that can leave a part undrawn while its slot name and wardrobe variant both say
/// it should draw. Each is authored data the game ships alongside the model, so a part carrying one may
/// still be replaced, retextured or hidden — those fire at the part's own draw — but nothing else may
/// LEAN on it: it feeds no other part's palette recovery, carries no other part's tier bones, and
/// witnesses no presence.
///
/// <para><see cref="None"/> is the default on every carrier, so a part whose prefab was never read for
/// these lists is never demoted by an unmeasured absence.</para>
///
/// <para>A node several mechanisms name reports the FIRST in declaration order, so one part answers with
/// one stable reason however the prefab happens to order its components.</para>
/// </summary>
public enum VisibilityOverride
{
    /// <summary>Nothing measured says the game withholds this part.</summary>
    None = 0,

    /// <summary>Named in the dorm context component's coat list, which separate scene logic shows and
    /// hides independently of whether the doll is in the dorm or in combat.</summary>
    CoatList,

    /// <summary>Named in the dorm hide list: the game withholds it in the dorm whatever its name
    /// says.</summary>
    DormHidden,

    /// <summary>Named in the lobby hide list: the game withholds it in the lobby, so it can be absent
    /// while same-context siblings draw.</summary>
    LobbyHidden,

    /// <summary>Named by a show/hide list on one of the dorm or lobby timelines the doll plays, which
    /// flips individual nodes mid-scene while the part is worn.</summary>
    TimelineNamed,
}
