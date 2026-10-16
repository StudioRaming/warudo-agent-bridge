namespace StudioRaming.WarudoAgentBridge
{
    // Bookkeeping for warudo_ui_language: which language WARUDO had before the agent first changed it, and which one the agent
    // set last. No Unity or WARUDO types, so scripts/check-language-restore.mjs runs it under plain .NET.
    sealed class LanguageRestoreState
    {
        string original, applied;

        // The language that was active before the first change since the last restore. Null when nothing was changed.
        public string Original { get { return original; } }

        // The language the agent set last (what WARUDO should still show when the restore runs).
        public string Applied { get { return applied; } }

        // True while a restore would change something.
        public bool Pending { get { return original != null && original != applied; } }

        // Call before changing the language. The first change remembers `current`; later changes keep that original.
        public void NoteChange(string current, string target)
        {
            if (original == null) original = current;
            applied = target;
        }

        // Rebuilds the state a previous bridge instance left in language-state.json.
        public void Resume(string originalLanguage, string appliedLanguage)
        {
            original = originalLanguage;
            applied = appliedLanguage;
        }

        // Returns the language to put back, or null when nothing should change, and always clears the bookkeeping.
        // `active` is what WARUDO shows now: when it is no longer what the agent set, someone else (the user, in WARUDO's
        // settings) changed it since, and that choice is left alone.
        public string TakeRestore(string active)
        {
            string target = original;
            string expected = applied;
            original = applied = null;
            if (target == null || active != expected || active == target) return null;
            return target;
        }
    }
}
