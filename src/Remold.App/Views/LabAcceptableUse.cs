namespace Remold.App.Views;

/// <summary>
/// The first-run acceptable-use copy, verbatim. A line starting "** " renders as a section header. Editing
/// the text changes its content stamp, so an updated pop-up re-prompts.
/// </summary>
internal static class LabAcceptableUse
{
    public const string Title = "Read This Before Making Mods.";

    public const string Text =
        "** Don't Bite The Hand That Feeds\n" +
        "You agree not to bypass the game's release schedule or monetization.\n" +
        "This means: don't give paid or unreleased content to people who don't own it.\n" +
        "\n" +
        "** No Permanent Paywalls\n" +
        "You agree that if you release a file created by this tool in any paywalled form, you will release it publicly for free within 31 days afterward.\n" +
        "\n" +
        "** Mod Within The Law\n" +
        "You agree not to do anything with this tool that is illegal in your jurisdiction. Yes, this means you.\n" +
        "\n" +
        "** Support The Developers\n" +
        "This is a non-commercial fan project. I will never accept payments or donations.\n" +
        "If you like the tool, support the original game. Buy an outfit, V6 your waifu, show appreciation for the people who gave us these characters.";
}
