using System.Text.RegularExpressions;

namespace LiveCaptionsTranslator.utils
{
    public static partial class RegexPatterns
    {
        // Remove the `.` between two uppercase letters. (Cope with acronym)
        [GeneratedRegex(@"([A-Z])\s*\.\s*([A-Z])(?![A-Za-z]+)")]
        public static partial Regex Acronym();

        // If an acronym is followed by a word, preserve the space between them.
        [GeneratedRegex(@"([A-Z])\s*\.\s*([A-Z])(?=[A-Za-z]+)")]
        public static partial Regex AcronymWithWords();

        // A `.` right after a digit is a decimal point (e.g. `2.5%`), not the end of a sentence.
        // It also matches a trailing `2.` because the next digit may not have been recognized yet.
        [GeneratedRegex(@"(\d)\.(?=\d|$)")]
        public static partial Regex DecimalPoint();

        // Remove redundant spaces and `\n` around punctuation.
        [GeneratedRegex(@"\s*([.!?,])\s*")]
        public static partial Regex PunctuationSpace();

        // If it is Chinese or Japanese punctuation, no need to insert spaces.
        [GeneratedRegex(@"\s*([。！？，、])\s*")]
        public static partial Regex CJPunctuationSpace();

        [GeneratedRegex(@"^(\[.+\] )?(.*)$")]
        public static partial Regex NoticePrefixAndTranslation();

        [GeneratedRegex(@"^\[.+\] ")]
        public static partial Regex NoticePrefix();

        [GeneratedRegex(@"^(https?:\/\/)")]
        public static partial Regex HttpPrefix();

        [GeneratedRegex(@"\/{2,}")]
        public static partial Regex MultipleSlashes();

        [GeneratedRegex(@"<think>.*?<\/think>")]
        public static partial Regex ModelThinking();

        [GeneratedRegex(@"[^0-9.]")]
        public static partial Regex VersionNumber();

        [GeneratedRegex(@"🔤\s*(.+?)\s*🔤")]
        public static partial Regex TargetSentence();
    }
}
