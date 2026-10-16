using System;
using StudioRaming.WarudoAgentBridge;

// Checks the restore bookkeeping of warudo_ui_language (runtime/LanguageRestore.cs) under plain .NET. Run it with: npm run check:language
static class Program
{
    static int failures;

    static void Check(bool ok, string what)
    {
        Console.WriteLine((ok ? "ok   " : "FAIL ") + what);
        if (!ok) failures++;
    }

    static int Main()
    {
        // The first change remembers the language WARUDO had.
        var state = new LanguageRestoreState();
        Check(state.Original == null && !state.Pending, "a fresh state has nothing to restore");
        state.NoteChange("en", "ja");
        Check(state.Original == "en" && state.Applied == "ja" && state.Pending, "the first change remembers the original and the applied language");

        // The second change keeps that original: restoring goes back to what the user had, not to the language before the last call.
        state.NoteChange("ja", "ko");
        Check(state.Original == "en" && state.Applied == "ko", "a second change keeps the first original");
        Check(state.TakeRestore("ko") == "en", "restore returns the first original");
        Check(state.Original == null && state.Applied == null && !state.Pending, "restore clears the bookkeeping");
        Check(state.TakeRestore("en") == null, "a second restore has nothing to do");

        // After a restore, the next change starts over with the language active at that time.
        state.NoteChange("zh_CN", "en");
        Check(state.Original == "zh_CN", "a change after a restore remembers the new original");
        Check(state.TakeRestore("en") == "zh_CN", "and restores that one");

        // The user picked another language in WARUDO while the agent had one set: leave their choice alone.
        state = new LanguageRestoreState();
        state.NoteChange("en", "ja");
        Check(state.TakeRestore("ko") == null, "a language someone else chose since is not overwritten");
        Check(state.Original == null && !state.Pending, "and the bookkeeping is cleared anyway");

        // Setting the language WARUDO already shows changes nothing, so there is nothing to restore.
        state = new LanguageRestoreState();
        state.NoteChange("en", "en");
        Check(!state.Pending, "setting the active language again is not a pending change");
        Check(state.TakeRestore("en") == null, "and restores nothing");

        // Changing and changing back by hand leaves nothing to restore either.
        state = new LanguageRestoreState();
        state.NoteChange("en", "ja");
        state.NoteChange("ja", "en");
        Check(!state.Pending && state.TakeRestore("en") == null, "changing back to the original is not pending");

        // A bridge that restarted rebuilds the state from language-state.json and restores the same way.
        state = new LanguageRestoreState();
        state.Resume("en", "ja");
        Check(state.Pending && state.TakeRestore("ja") == "en", "a resumed state restores while the applied language is still shown");
        state.Resume("en", "ja");
        Check(state.TakeRestore("en") == null, "a resumed state restores nothing when WARUDO already shows another language (restarted WARUDO)");

        Console.WriteLine(failures == 0 ? "ALL OK" : failures + " FAILED");
        return failures == 0 ? 0 : 1;
    }
}
