using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using UnityEngine;
using Warudo.Core;

namespace StudioRaming.WarudoAgentBridge
{
    // warudo_ui_language: switches the language WARUDO plugins read from Context.LocalizationManager.GetActiveLanguage(), so a plugin
    // menu can be captured in English and Japanese without anyone opening WARUDO's settings.
    // SetActiveLanguage only changes memory. WARUDO's own settings dropdown writes CorePlugin.Language (plugin data, saved to disk) and a
    // Watch on that field calls SetActiveLanguage; this tool skips the field, so nothing is saved and a WARUDO restart starts in the
    // saved language again. The original language is still put back by the bridge when the lease ends, on pointer.release and when the
    // bridge stops or reloads (language-state.json covers a reload that skipped OnDestroy).
    public sealed partial class AgentBridgePlugin
    {
        readonly LanguageRestoreState languageState = new LanguageRestoreState();
        string languageOwner;

        string LanguageStateFile { get { return Path.Combine(root, "language-state.json"); } }

        object UiLanguage(JObject p, string owner)
        {
            bool restore = (bool?)p["restore"] ?? false;
            string code = (string)p["code"];
            if (restore && code != null) throw new ArgumentException("Pass code or restore, not both.");
            var manager = Context.LocalizationManager;
            if (manager == null) throw new InvalidOperationException("NOT_READY: WARUDO's localization is not loaded yet.");
            string previous = manager.GetActiveLanguage();
            string[] available = manager.GetAllSupportLanguages().Keys.OrderBy(key => key, StringComparer.Ordinal).ToArray();
            if (restore)
            {
                string pending = languageState.Original;
                string back = RestoreLanguage("warudo_ui_language restore");
                return new { previous, active = manager.GetActiveLanguage(), restored = back != null, original = pending, persisted = false, available };
            }
            if (code == null)
                return new { previous, active = previous, original = languageState.Original, persisted = false, available };
            if (!available.Contains(code)) throw new ArgumentException("UNKNOWN_LANGUAGE: " + code + " is not a language WARUDO has strings for. Available: " + string.Join(", ", available) + ".");
            var scene = Context.OpenedScene;
            if (scene != null && scene.LoadedTime <= 0f) throw new InvalidOperationException("SCENE_LOADING: a scene is still loading. Change the language when it has finished.");
            languageState.NoteChange(previous, code);
            languageOwner = owner;
            SaveLanguageState();
            manager.SetActiveLanguage(code);
            return new
            {
                previous, active = manager.GetActiveLanguage(), original = languageState.Original, changed = previous != code,
                persisted = false, restoresOn = "warudo_ui_language restore=true, warudo_pointer_release, the lease ending, or the bridge stopping",
                available,
            };
        }

        // Puts back the language that was active before the first change. Returns the language restored, or null when there was nothing
        // to do (no change made, or the user picked another language in WARUDO since: that choice is left alone).
        string RestoreLanguage(string reason)
        {
            if (languageState.Original == null) return null;
            var manager = Context.LocalizationManager;
            string target = languageState.TakeRestore(manager == null ? null : manager.GetActiveLanguage());
            languageOwner = null;
            DeleteLanguageState();
            if (target == null || manager == null) return null;
            manager.SetActiveLanguage(target);
            Debug.Log("[WARUDO Agent Bridge] Language restored to " + target + ": " + reason);
            return target;
        }

        // Runs every frame from OnUpdate: the agent that changed the language no longer holds the lease.
        void LanguageTick()
        {
            if (languageState.Original == null) return;
            if (DateTime.UtcNow >= leaseUntil || leaseOwner != languageOwner)
            {
                try { RestoreLanguage("lease ended"); }
                catch (Exception e) { Debug.LogWarning("[WARUDO Agent Bridge] Could not restore the language: " + e.Message); }
            }
        }

        void SaveLanguageState()
        {
            try
            {
                if (!languageState.Pending) { DeleteLanguageState(); return; }
                AtomicWrite(LanguageStateFile, new JObject { ["original"] = languageState.Original, ["applied"] = languageState.Applied, ["utc"] = DateTime.UtcNow.ToString("o") }.ToString(Formatting.None));
            }
            catch (IOException e) { Debug.LogWarning("[WARUDO Agent Bridge] Could not save the language state: " + e.Message); }
        }

        void DeleteLanguageState()
        {
            try { if (File.Exists(LanguageStateFile)) File.Delete(LanguageStateFile); }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }

        // A bridge that ended without running OnDestroy leaves language-state.json behind. WARUDO keeps the language it had in memory
        // only when the same WARUDO process is still running, so restore it now; after a WARUDO restart the saved language is
        // already back and the restore finds nothing to change.
        void ResumeLanguageState()
        {
            try
            {
                if (!File.Exists(LanguageStateFile)) return;
                JObject saved = ParseJson(File.ReadAllText(LanguageStateFile)) as JObject;
                if (saved == null || (string)saved["original"] == null) { DeleteLanguageState(); return; }
                languageState.Resume((string)saved["original"], (string)saved["applied"]);
                RestoreLanguage("bridge restarted");
            }
            catch (Exception e) { Debug.LogWarning("[WARUDO Agent Bridge] Could not resume the language state: " + e.Message); }
        }
    }
}
