using System.IO;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.utils
{
    // Per-language rules for splitting long captions and removing fillers, loaded from
    // `split_rules.json` next to the executable so that users can extend them without rebuilding.
    // To add a language: add an entry to the JSON and a detection rule in `DetectLanguage`.
    public class SplitRule
    {
        // Split before these words when a long caption has no comma.
        public List<string> SplitBefore { get; set; } = new();
        // Removed before the text is sent to the translate API.
        public List<string> Fillers { get; set; } = new();
        // Minimum length of the head before a split word.
        public int MinHeadChars { get; set; } = 15;
        // Send everything recognized so far when no comma or split word is found.
        public int HardSplitChars { get; set; } = 60;
    }

    public static class SplitRules
    {
        public const string FILENAME = "split_rules.json";

        private static readonly Dictionary<string, SplitRule> defaults = new()
        {
            ["zh"] = new SplitRule
            {
                SplitBefore = new() { "然後", "接下來", "所以", "另外", "但是", "不過", "因為", "好那", "那我們" },
                Fillers = new() { "呃", "嗯" },
                MinHeadChars = 15,
                HardSplitChars = 60,
            },
            ["ja"] = new SplitRule
            {
                SplitBefore = new() { "それで", "そして", "だから", "でも", "次に", "ただ" },
                Fillers = new() { "えー", "えっと", "あのー" },
                MinHeadChars = 15,
                HardSplitChars = 60,
            },
            ["en"] = new SplitRule
            {
                SplitBefore = new() { "so", "and then", "but", "because", "anyway", "next" },
                Fillers = new() { "um", "uh" },
                MinHeadChars = 60,
                HardSplitChars = 180,
            },
        };

        private static readonly Dictionary<string, SplitRule> rules = Load();

        private static Dictionary<string, SplitRule> Load()
        {
            string path = Path.Combine(Directory.GetCurrentDirectory(), FILENAME);
            var options = new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNameCaseInsensitive = true,
                Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
            };
            try
            {
                if (!File.Exists(path))
                {
                    // Write the defaults out so that users have a file to edit.
                    File.WriteAllText(path, JsonSerializer.Serialize(defaults, options));
                    return defaults;
                }
                var loaded = JsonSerializer.Deserialize<Dictionary<string, SplitRule>>(File.ReadAllText(path), options);
                return loaded ?? defaults;
            }
            catch (Exception)
            {
                // A broken file must not stop the app; fall back to the built-in rules.
                return defaults;
            }
        }

        // Pick the rule set by the script of the text itself, so it works whatever LiveCaptions is set to.
        public static string DetectLanguage(string text)
        {
            if (Regex.IsMatch(text, @"[぀-ヿ]"))
                return "ja";
            if (Regex.IsMatch(text, @"[一-鿿]"))
                return "zh";
            return "en";
        }

        public static SplitRule? Get(string text)
        {
            return rules.TryGetValue(DetectLanguage(text), out var rule) ? rule : null;
        }

        private static bool IsSpaced(string language) => language == "en";

        // Index to split at (the start of the last split word), or -1 if none qualifies.
        public static int FindSplitIndex(string text)
        {
            string language = DetectLanguage(text);
            if (!rules.TryGetValue(language, out var rule))
                return -1;
            int best = -1;
            foreach (string word in rule.SplitBefore)
            {
                if (string.IsNullOrWhiteSpace(word))
                    continue;
                string pattern = IsSpaced(language)
                    ? $@"(?<=\s){Regex.Escape(word)}\b"
                    : Regex.Escape(word);
                foreach (Match m in Regex.Matches(text, pattern, RegexOptions.IgnoreCase))
                {
                    if (m.Index >= rule.MinHeadChars && m.Index < text.Length - word.Length && m.Index > best)
                        best = m.Index;
                }
            }
            return best;
        }

        public static int HardSplitChars(string text) => Get(text)?.HardSplitChars ?? 60;

        // Remove fillers (with the punctuation right after them) before translation.
        public static string RemoveFillers(string text)
        {
            string language = DetectLanguage(text);
            if (!rules.TryGetValue(language, out var rule) || rule.Fillers.Count == 0)
                return text;
            foreach (string filler in rule.Fillers)
            {
                if (string.IsNullOrWhiteSpace(filler))
                    continue;
                string pattern = IsSpaced(language)
                    ? $@"\b{Regex.Escape(filler)}\b[,，]?\s*"
                    : $@"{Regex.Escape(filler)}[，、,—]*";
                text = Regex.Replace(text, pattern, string.Empty, RegexOptions.IgnoreCase);
            }
            return text.Trim();
        }
    }
}
