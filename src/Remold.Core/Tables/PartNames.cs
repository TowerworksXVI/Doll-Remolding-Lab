using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;

namespace Remold.Core.Tables;

/// <summary>
/// The ONE render-time home for turning a part's slot token (<c>cloth2</c>, <c>P1_body_Fight</c>,
/// <c>Silencer_WL</c>) into the name the modder reads in the UI language. The token is the tail of the
/// game's mesh name and stays the part's identity everywhere else: keys, file names, manifests, repair
/// records, the asset name shown beside a row and the app log all keep it as it is.
///
/// <para>A token is read word by word on <c>_</c>. A word the catalog knows reads in the UI language and
/// keeps its trailing number (<c>cloth2</c> → <c>衣物2</c>); a numbered-piece prefix (<c>P1</c>), the game's
/// <c>slg</c> marker and any word the catalog does not know keep the game's spelling. Only a token that
/// opens with a known word is read this way; any other token, such as a whole mesh name or a renderer slot,
/// is shown as it is, so passing a name that is not a token returns it unchanged.</para>
///
/// <para>A word whose name in the UI language is the same word, as every word is in English, keeps the
/// token's own spelling, so the English names are the tokens themselves.</para>
/// </summary>
public static partial class PartNames
{
    private static readonly Dictionary<string, Func<string>> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["acc"] = () => CoreStrings.PartWordAcc,
        ["accessory"] = () => CoreStrings.PartWordAccessory,
        ["addon"] = () => CoreStrings.PartWordAddon,
        ["arm"] = () => CoreStrings.PartWordArm,
        ["arrow"] = () => CoreStrings.PartWordArrow,
        ["axe"] = () => CoreStrings.PartWordAxe,
        ["bag"] = () => CoreStrings.PartWordBag,
        ["boby"] = () => CoreStrings.PartWordBoby,
        ["body"] = () => CoreStrings.PartWordBody,
        ["bomb"] = () => CoreStrings.PartWordBomb,
        ["bow"] = () => CoreStrings.PartWordBow,
        ["brow"] = () => CoreStrings.PartWordBrow,
        ["bullet"] = () => CoreStrings.PartWordBullet,
        ["cloth"] = () => CoreStrings.PartWordCloth,
        ["coat"] = () => CoreStrings.PartWordCoat,
        ["crystal"] = () => CoreStrings.PartWordCrystal,
        ["dorm"] = () => CoreStrings.PartWordDorm,
        ["effect"] = () => CoreStrings.PartWordEffect,
        ["explosive"] = () => CoreStrings.PartWordExplosive,
        ["eye"] = () => CoreStrings.PartWordEye,
        ["face"] = () => CoreStrings.PartWordFace,
        ["fight"] = () => CoreStrings.PartWordFight,
        ["flag"] = () => CoreStrings.PartWordFlag,
        ["flagon"] = () => CoreStrings.PartWordFlagon,
        ["flamethrower"] = () => CoreStrings.PartWordFlamethrower,
        ["flashgrenade"] = () => CoreStrings.PartWordFlashGrenade,
        ["fur"] = () => CoreStrings.PartWordFur,
        ["glasses"] = () => CoreStrings.PartWordGlasses,
        ["glove"] = () => CoreStrings.PartWordGlove,
        ["goggles"] = () => CoreStrings.PartWordGoggles,
        ["grenade"] = () => CoreStrings.PartWordGrenade,
        ["grip"] = () => CoreStrings.PartWordGrip,
        ["gun"] = () => CoreStrings.PartWordGun,
        ["hair"] = () => CoreStrings.PartWordHair,
        ["hand"] = () => CoreStrings.PartWordHand,
        ["handcuffs"] = () => CoreStrings.PartWordHandcuffs,
        ["hat"] = () => CoreStrings.PartWordHat,
        ["head"] = () => CoreStrings.PartWordHead,
        ["helmet"] = () => CoreStrings.PartWordHelmet,
        ["hip"] = () => CoreStrings.PartWordHip,
        ["horn"] = () => CoreStrings.PartWordHorn,
        ["intercom"] = () => CoreStrings.PartWordIntercom,
        ["knife"] = () => CoreStrings.PartWordKnife,
        ["lace"] = () => CoreStrings.PartWordLace,
        ["landmine"] = () => CoreStrings.PartWordLandmine,
        ["left"] = () => CoreStrings.PartWordLeft,
        ["light"] = () => CoreStrings.PartWordLight,
        ["limbs"] = () => CoreStrings.PartWordLimbs,
        ["logo"] = () => CoreStrings.PartWordLogo,
        ["lower"] = () => CoreStrings.PartWordLower,
        ["muzzle"] = () => CoreStrings.PartWordMuzzle,
        ["notepad"] = () => CoreStrings.PartWordNotepad,
        ["peijian"] = () => CoreStrings.PartWordPeijian,
        ["pistol"] = () => CoreStrings.PartWordPistol,
        ["plush"] = () => CoreStrings.PartWordPlush,
        ["props"] = () => CoreStrings.PartWordProps,
        ["qiang"] = () => CoreStrings.PartWordQiang,
        ["right"] = () => CoreStrings.PartWordRight,
        ["shield"] = () => CoreStrings.PartWordShield,
        ["shoes"] = () => CoreStrings.PartWordShoes,
        ["sight"] = () => CoreStrings.PartWordSight,
        ["silencer"] = () => CoreStrings.PartWordSilencer,
        ["skirt"] = () => CoreStrings.PartWordSkirt,
        ["smoke"] = () => CoreStrings.PartWordSmoke,
        ["smokegrenade"] = () => CoreStrings.PartWordSmokeGrenade,
        ["summon"] = () => CoreStrings.PartWordSummon,
        ["sword"] = () => CoreStrings.PartWordSword,
        ["tail"] = () => CoreStrings.PartWordTail,
        ["teeth"] = () => CoreStrings.PartWordTeeth,
        ["tongue"] = () => CoreStrings.PartWordTongue,
        ["truncheon"] = () => CoreStrings.PartWordTruncheon,
        ["upper"] = () => CoreStrings.PartWordUpper,
        ["weapon"] = () => CoreStrings.PartWordWeapon,
        ["wing"] = () => CoreStrings.PartWordWing,
        ["WL"] = () => CoreStrings.PartWordWl,
        ["wrench"] = () => CoreStrings.PartWordWrench,
        ["xianglian"] = () => CoreStrings.PartWordXianglian,
        ["xingxing"] = () => CoreStrings.PartWordXingxing,
    };

    /// <summary>The words <see cref="Display"/> reads in the UI language, as the game spells them in a
    /// token, ignoring case.</summary>
    public static IEnumerable<string> KnownWords => Words.Keys;

    [GeneratedRegex(@"^(?<word>[A-Za-z]+?)(?<number>\d*)$")]
    private static partial Regex WordAndNumber();

    [GeneratedRegex(@"^P\d+$")]
    private static partial Regex PiecePrefix();

    /// <summary>The name <paramref name="token"/> is shown under in the UI language; the token itself when it
    /// does not open with a word the catalog knows. Null or empty comes back as it is.</summary>
    public static string Display(string token)
    {
        if (string.IsNullOrEmpty(token)) return token;
        var words = token.Split('_');
        int head = 0;
        while (head < words.Length && IsKept(words[head])) head++;
        if (head == words.Length || Read(words[head]) is null) return token;
        for (int i = head; i < words.Length; i++)
            words[i] = Read(words[i]) ?? words[i];
        return string.Join('_', words);
    }

    /// <summary>A word that is part of the token's shape rather than a word to read: a numbered-piece prefix
    /// such as <c>P1</c>, or the game's <c>slg</c> marker.</summary>
    private static bool IsKept(string word) =>
        PiecePrefix().IsMatch(word) || string.Equals(word, "slg", StringComparison.OrdinalIgnoreCase);

    /// <summary>One word in the UI language with its trailing number, or null for a word the catalog does
    /// not know.</summary>
    private static string? Read(string word)
    {
        var match = WordAndNumber().Match(word);
        if (!match.Success || !Words.TryGetValue(match.Groups["word"].Value, out var name)) return null;
        string spelled = match.Groups["word"].Value;
        string shown = name();
        return (string.Equals(shown, spelled, StringComparison.OrdinalIgnoreCase) ? spelled : shown)
            + match.Groups["number"].Value;
    }
}
