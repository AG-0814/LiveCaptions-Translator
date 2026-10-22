using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows.Automation;

using LiveCaptionsTranslator.apis;
using LiveCaptionsTranslator.models;
using LiveCaptionsTranslator.utils;

namespace LiveCaptionsTranslator
{
    public static class Translator
    {
        private static AutomationElement? window = null;
        private static Caption? caption = null;
        private static Setting? setting = null;

        private static readonly Queue<string> pendingTextQueue = new();
        private static readonly TranslationTaskQueue translationTaskQueue = new();

        // Raw prefix of the current sentence that has already been force-split and sent.
        private static string forcedPrefix = string.Empty;
        // Force-split heads waiting to be translated; treated as complete sentences.
        private static readonly ConcurrentDictionary<string, byte> forcedSegments = new();
        // Complete sentences sent recently, used to drop duplicates. Only touched by `SyncLoop`.
        private static readonly List<(string text, DateTime time)> recentComplete = new();

        public static AutomationElement? Window
        {
            get => window;
            set => window = value;
        }
        public static Caption? Caption => caption;
        public static Setting? Setting => setting;

        public static bool LogOnlyFlag { get; set; } = false;
        public static bool FirstUseFlag { get; set; } = false;

        public static event Action? TranslationLogged;

        static Translator()
        {
            window = LiveCaptionsHandler.LaunchLiveCaptions();
            LiveCaptionsHandler.FixLiveCaptions(Window);
            LiveCaptionsHandler.HideLiveCaptions(Window);

            if (!File.Exists(Path.Combine(Directory.GetCurrentDirectory(), models.Setting.FILENAME)))
                FirstUseFlag = true;

            caption = Caption.GetInstance();
            setting = Setting.Load();
        }

        public static void SyncLoop()
        {
            int idleCount = 0;
            int syncCount = 0;

            while (true)
            {
                if (Window == null)
                {
                    Thread.Sleep(2000);
                    continue;
                }

                string fullText = string.Empty;
                try
                {
                    // Check LiveCaptions.exe still alive
                    var info = Window.Current;
                    var name = info.Name;
                    // Get the text recognized by LiveCaptions (10-20ms)
                    fullText = LiveCaptionsHandler.GetCaptions(Window);
                }
                catch (ElementNotAvailableException)
                {
                    Window = null;
                    continue;
                }
                if (string.IsNullOrEmpty(fullText))
                    continue;

                // Preprocess
                // Use a full-width dot for decimals so that `2.5` is not split into two sentences.
                fullText = RegexPatterns.DecimalPoint().Replace(fullText, "$1．");
                fullText = RegexPatterns.Acronym().Replace(fullText, "$1$2");
                fullText = RegexPatterns.AcronymWithWords().Replace(fullText, "$1 $2");
                fullText = RegexPatterns.PunctuationSpace().Replace(fullText, "$1 ");
                fullText = RegexPatterns.CJPunctuationSpace().Replace(fullText, "$1");
                // Note: For certain languages (such as Japanese), LiveCaptions excessively uses `\n`.
                // Replace redundant `\n` within sentences with comma or period.
                fullText = TextUtil.ReplaceNewlines(fullText, TextUtil.MEDIUM_THRESHOLD);

                // Prevent adding the last sentence from previous running to log cards
                // before the first sentence is completed.
                if (fullText.IndexOfAny(TextUtil.PUNC_EOS) == -1 && Caption.Contexts.Count > 0)
                    ClearContexts();

                // Get the last sentence.
                int lastEOSIndex;
                if (Array.IndexOf(TextUtil.PUNC_EOS, fullText[^1]) != -1)
                    lastEOSIndex = fullText[0..^1].LastIndexOfAny(TextUtil.PUNC_EOS);
                else
                    lastEOSIndex = fullText.LastIndexOfAny(TextUtil.PUNC_EOS);
                string latestCaption = fullText.Substring(lastEOSIndex + 1);

                // If the last sentence is too short, extend it by adding the previous sentence.
                // Note: LiveCaptions may generate multiple characters including EOS at once.
                if (lastEOSIndex > 0 && Encoding.UTF8.GetByteCount(latestCaption) < TextUtil.SHORT_THRESHOLD)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    latestCaption = fullText.Substring(lastEOSIndex + 1);
                }

                // `OverlayOriginalCaption`: The sentence to be displayed on Overlay Window.
                Caption.OverlayOriginalCaption = latestCaption;
                for (int historyCount = Math.Min(Setting.DisplaySentences, Caption.Contexts.Count);
                     historyCount > 0 && lastEOSIndex > 0;
                     historyCount--)
                {
                    lastEOSIndex = fullText[0..lastEOSIndex].LastIndexOfAny(TextUtil.PUNC_EOS);
                    Caption.OverlayOriginalCaption = fullText.Substring(lastEOSIndex + 1);
                }

                // `DisplayOriginalCaption`: The sentence to be displayed on Main Window.
                if (string.CompareOrdinal(Caption.DisplayOriginalCaption, latestCaption) != 0)
                {
                    Caption.DisplayOriginalCaption = latestCaption;
                    // If the last sentence is too long, truncate it when displayed.
                    Caption.DisplayOriginalCaption =
                        TextUtil.ShortenDisplaySentence(Caption.DisplayOriginalCaption, TextUtil.VERYLONG_THRESHOLD);
                }

                // Drop the head of the current sentence that has already been force-split and sent.
                // LiveCaptions keeps revising recognized words, so match the head by length and similarity
                // instead of exact text, then re-sync `forcedPrefix` with the revised text.
                if (forcedPrefix.Length > 0)
                {
                    int n = forcedPrefix.Length;
                    if (latestCaption.Length < n ||
                        TextUtil.Similarity(latestCaption.Substring(0, n), forcedPrefix) < TextUtil.FORCE_PREFIX_SIM)
                        forcedPrefix = string.Empty;
                    else if (string.IsNullOrWhiteSpace(latestCaption.Substring(n)))
                    {
                        Thread.Sleep(25);
                        continue;
                    }
                    else
                    {
                        forcedPrefix = latestCaption.Substring(0, n);
                        latestCaption = latestCaption.Substring(n);
                    }
                }

                // Prepare for `OriginalCaption`. If Expanded, only retain the complete sentence.
                int lastEOS = latestCaption.LastIndexOfAny(TextUtil.PUNC_EOS);
                if (lastEOS != -1)
                    latestCaption = latestCaption.Substring(0, lastEOS + 1);

                // LiveCaptions seldom emits EOS during continuous speech, so a sentence can grow to
                // hundreds of characters. Force-split it at the last comma and send the head as complete;
                // without a comma, split before a transition word from `split_rules.json`; failing that,
                // send everything recognized so far once it gets very long.
                if (lastEOS == -1 && Encoding.UTF8.GetByteCount(latestCaption) >= TextUtil.FORCE_SPLIT_THRESHOLD)
                {
                    int splitIndex = latestCaption.LastIndexOfAny(SplitRules.SplitPunctuation(latestCaption));
                    if (splitIndex <= 0 || string.IsNullOrWhiteSpace(latestCaption.Substring(splitIndex + 1)))
                    {
                        int wordIndex = SplitRules.FindSplitIndex(latestCaption);
                        if (wordIndex > 0)
                            splitIndex = wordIndex - 1;
                        else
                            splitIndex = latestCaption.Length >= SplitRules.HardSplitChars(latestCaption)
                                ? latestCaption.Length - 1 : -1;
                    }
                    if (splitIndex > 0)
                    {
                        string head = latestCaption.Substring(0, splitIndex + 1).Trim();
                        forcedPrefix += latestCaption.Substring(0, splitIndex + 1);
                        EnqueueComplete(head, isForced: true);
                        latestCaption = latestCaption.Substring(splitIndex + 1);
                        syncCount = 0;
                    }
                }
                latestCaption = latestCaption.TrimStart();
                if (latestCaption.Length == 0)
                {
                    Thread.Sleep(25);
                    continue;
                }
                // `OriginalCaption`: The sentence to be really translated.
                if (string.CompareOrdinal(Caption.OriginalCaption, latestCaption) != 0)
                {
                    Caption.OriginalCaption = latestCaption;

                    idleCount = 0;
                    if (Array.IndexOf(TextUtil.PUNC_EOS, Caption.OriginalCaption[^1]) != -1)
                    {
                        syncCount = 0;
                        EnqueueComplete(Caption.OriginalCaption, isForced: false);
                    }
                    else if (Encoding.UTF8.GetByteCount(Caption.OriginalCaption) >= TextUtil.SHORT_THRESHOLD)
                        syncCount++;
                }
                else
                    idleCount++;

                // `TranslateFlag` determines whether this sentence should be translated.
                // When `OriginalCaption` remains unchanged, `idleCount` +1; when `OriginalCaption` changes, `MaxSyncInterval` +1.
                if (syncCount > Setting.MaxSyncInterval ||
                    idleCount == Setting.MaxIdleInterval)
                {
                    syncCount = 0;
                    pendingTextQueue.Enqueue(Caption.OriginalCaption);
                }

                Thread.Sleep(25);
            }
        }

        // Enqueue a complete sentence or a force-split head, skipping it if (nearly) the same text
        // was sent recently. Revised captions otherwise flood the translate API with duplicates.
        private static void EnqueueComplete(string text, bool isForced)
        {
            var now = DateTime.UtcNow;
            recentComplete.RemoveAll(r => (now - r.time).TotalSeconds > TextUtil.DEDUP_WINDOW_SECONDS);
            // Plain edit-distance ratio: `TextUtil.Similarity` treats any prefix as identical, which
            // would wrongly drop a full sentence whose head was sent before.
            if (recentComplete.Any(r =>
                    1.0 - (double)TextUtil.LevenshteinDistance(r.text, text) /
                    Math.Max(1, Math.Max(r.text.Length, text.Length)) >= TextUtil.DEDUP_SIM))
                return;
            recentComplete.Add((text, now));
            if (isForced)
                forcedSegments.TryAdd(text, 0);
            pendingTextQueue.Enqueue(text);
        }

        public static async Task TranslateLoop()
        {
            while (true)
            {
                // Check LiveCaptions.exe still alive
                if (Window == null)
                {
                    Caption.DisplayTranslatedCaption = "[WARNING] LiveCaptions was unexpectedly closed, restarting...";
                    Window = LiveCaptionsHandler.LaunchLiveCaptions();
                    Caption.DisplayTranslatedCaption = "";
                }

                // Translate
                if (pendingTextQueue.Count > 0)
                {
                    var originalSnapshot = pendingTextQueue.Dequeue();
                    // Complete sentences (EOS or force-split heads) must not be cancelled by later partial ones.
                    bool isComplete = forcedSegments.TryRemove(originalSnapshot, out _) ||
                                      Array.IndexOf(TextUtil.PUNC_EOS, originalSnapshot[^1]) != -1;

                    if (LogOnlyFlag)
                    {
                        bool isOverwrite = await IsOverwrite(originalSnapshot);
                        await LogOnly(originalSnapshot, isOverwrite);
                    }
                    else
                    {
                        // Fillers only add noise such as "uh" or "Hmm" to the translation.
                        string textToTranslate = SplitRules.RemoveFillers(originalSnapshot);
                        if (!string.IsNullOrWhiteSpace(textToTranslate))
                            translationTaskQueue.Enqueue(token => Task.Run(
                                () => Translate(textToTranslate, token), token), originalSnapshot, isComplete);
                    }
                }

                Thread.Sleep(40);
            }
        }

        public static async Task DisplayLoop()
        {
            while (true)
            {
                var (translatedText, isChoke) = translationTaskQueue.Output;

                if (LogOnlyFlag)
                {
                    Caption.TranslatedCaption = string.Empty;
                    Caption.DisplayTranslatedCaption = "[Paused]";
                    Caption.OverlayNoticePrefix = "[Paused]";
                    Caption.OverlayCurrentTranslation = string.Empty;
                }
                else if (!string.IsNullOrEmpty(RegexPatterns.NoticePrefix().Replace(
                             translatedText, string.Empty).Trim()) &&
                         string.CompareOrdinal(Caption.TranslatedCaption, translatedText) != 0)
                {
                    // Main page
                    Caption.TranslatedCaption = translatedText;
                    Caption.DisplayTranslatedCaption =
                        TextUtil.ShortenDisplaySentence(Caption.TranslatedCaption, TextUtil.VERYLONG_THRESHOLD);

                    // Overlay window
                    if (Caption.TranslatedCaption.Contains("[ERROR]") || Caption.TranslatedCaption.Contains("[WARNING]"))
                        Caption.OverlayCurrentTranslation = Caption.TranslatedCaption;
                    else
                    {
                        var match = RegexPatterns.NoticePrefixAndTranslation().Match(Caption.TranslatedCaption);
                        Caption.OverlayNoticePrefix = match.Groups[1].Value.Trim();
                        Caption.OverlayCurrentTranslation = match.Groups[2].Value.Trim();
                    }
                }

                // If the original sentence is a complete sentence, choke for better visual experience.
                if (isChoke)
                    Thread.Sleep(720);
                Thread.Sleep(40);
            }
        }

        public static async Task<(string, bool)> Translate(string text, CancellationToken token = default)
        {
            string translatedText;
            bool isChoke = Array.IndexOf(TextUtil.PUNC_EOS, text[^1]) != -1;

            try
            {
                var sw = Setting.MainWindow.LatencyShow ? Stopwatch.StartNew() : null;

                if (Setting.ContextAware && !TranslateAPI.IsLLMBased)
                {
                    translatedText = await TranslateAPI.TranslateFunction($"{Caption.AwareContextsCaption} 🔤 {text} 🔤", token);
                    translatedText = RegexPatterns.TargetSentence().Match(translatedText).Groups[1].Value;
                }
                else
                {
                    translatedText = await TranslateAPI.TranslateFunction(text, token);
                    translatedText = translatedText.Replace("🔤", "");
                }

                if (sw != null)
                {
                    sw.Stop();
                    translatedText = $"[{sw.ElapsedMilliseconds,4} ms] " + translatedText;
                }
            }
            catch (OperationCanceledException ex)
            {
                throw;
            }
            catch (Exception ex)
            {
                return ($"[ERROR] Translation Failed: {ex.Message}", isChoke);
            }

            return (translatedText, isChoke);
        }

        public static async Task Log(string originalText, string translatedText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            string targetLanguage, apiName;
            if (Setting != null)
            {
                targetLanguage = Setting.TargetLanguage;
                apiName = Setting.ApiName;
            }
            else
            {
                targetLanguage = "N/A";
                apiName = "N/A";
            }

            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, translatedText, targetLanguage, apiName);
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task LogOnly(string originalText,
            bool isOverwrite = false, CancellationToken token = default)
        {
            try
            {
                if (isOverwrite)
                    await SQLiteHistoryLogger.DeleteLastTranslation(token);
                await SQLiteHistoryLogger.LogTranslation(originalText, "N/A", "N/A", "LogOnly");
                TranslationLogged?.Invoke();
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception ex)
            {
                SnackbarHost.Show("[ERROR] Logging history failed.", ex.Message, SnackbarType.Error,
                    timeout: 2, closeButton: true);
            }
        }

        public static async Task AddContexts(CancellationToken token = default)
        {
            var lastLog = await SQLiteHistoryLogger.LoadLastTranslation(token);
            if (lastLog == null)
                return;

            if (Caption?.Contexts.Count >= Caption.MAX_CONTEXTS)
                Caption.Contexts.Dequeue();
            Caption?.Contexts.Enqueue(lastLog);

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        public static void ClearContexts()
        {
            Caption?.Contexts.Clear();

            Caption?.OnPropertyChanged("DisplayLogCards");
            Caption?.OnPropertyChanged("OverlayPreviousTranslation");
        }

        // If this text is too similar to the last one, overwrite it when logging.
        public static async Task<bool> IsOverwrite(string originalText, CancellationToken token = default)
        {
            string lastOriginalText = await SQLiteHistoryLogger.LoadLastSourceText(token);
            if (lastOriginalText == null)
                return false;

            int minLen = Math.Min(originalText.Length, lastOriginalText.Length);
            originalText = originalText.Substring(0, minLen);
            lastOriginalText = lastOriginalText.Substring(0, minLen);

            double similarity = TextUtil.Similarity(originalText, lastOriginalText);
            return similarity > TextUtil.SIM_THRESHOLD;
        }
    }
}
