namespace Remold.Core;

/// <summary>
/// What every surface says when the game's files cannot answer. The sentences are catalog entries, and this
/// class is the one place every surface reads them from: ② Edit's pane, ③ Build's gate and the open's
/// refusal all describe the same states, and three wordings of one state read as three states.
///
/// <para>Three states, because the app can be in three: no forward view at all, one item's read not landed
/// yet, and one item the loaded install could not answer for. Each is a different fact and a different way
/// out, so each has its own sentence rather than borrowing a neighbour's.</para>
/// </summary>
public static class GameFilesGate
{
    /// <summary>Problem, then the fix in the app's own menu path.</summary>
    public static string Unavailable => CoreStrings.GameFilesUnavailable;

    /// <summary>The install is there and this item's read has not landed. It lands on its own, so the fix
    /// is a wait rather than a menu path.</summary>
    public static string SubjectReading => CoreStrings.SubjectStillReading;

    /// <summary>The read FINISHED without this item — the roster does not carry it, or the files behind it
    /// could not be read — and nothing retries until the game is read again. Waiting is the one thing that
    /// cannot help, so the sentence sends the modder to the re-read instead.</summary>
    public static string SubjectUnreadable => CoreStrings.SubjectUnreadable;
}
