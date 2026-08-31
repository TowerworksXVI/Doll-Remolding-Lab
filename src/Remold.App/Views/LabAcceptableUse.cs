namespace Remold.App.Views;

/// <summary>
/// The first-run acceptable-use copy, verbatim. A line starting "** " renders as a section header. Editing
/// the text changes its content stamp, so an updated pop-up re-prompts.
/// </summary>
internal static class LabAcceptableUse
{
    // The TEXT below is the accepted terms and is exempt from the UI text style by owner ruling — its
    // voice is deliberate, and editing it re-prompts every user. The title is UI chrome and is not
    // part of the acceptance stamp.
    public const string Title = "Read this before making mods";

    public const string Text =
        "** Don't Bite The Hand That Feeds\n" +
        "You agree not to bypass the game's release schedule or monetization.\n" +
        "This means: don't give paid or unreleased content to people who don't own it.\n" +
        "\n" +
        "** Mod At Your Own Risk\n" +
        "You accept that modding may violate the game's terms of service.\n" +
        "The Lab is deliberately read-only and hooks no running process, but I am not responsible for your account.\n" +
        "\n" +
        "** Mod Within The Law\n" +
        "You agree not to do anything with this tool that is illegal in your jurisdiction. Yes, this means you.\n" +
        "\n" +
        "** Support The Developers\n" +
        "This is a non-commercial fan project. I will never accept payments or donations.\n" +
        "If you like the tool, support the original game. Buy an outfit, V6 your waifu, show appreciation for the people who gave us these characters.";
}
