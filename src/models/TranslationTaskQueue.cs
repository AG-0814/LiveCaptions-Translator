namespace LiveCaptionsTranslator.models
{
    public class TranslationTaskQueue
    {
        private readonly object _lock = new object();
        private readonly List<TranslationTask> tasks;

        private (string translatedText, bool isChoke) output;
        public (string translatedText, bool isChoke) Output => output;

        public TranslationTaskQueue()
        {
            tasks = new List<TranslationTask>();
            output = (string.Empty, false);
        }

        public void Enqueue(Func<CancellationToken, Task<(string, bool)>> worker, string originalText,
            bool isComplete = false)
        {
            var newTranslationTask = new TranslationTask(worker, originalText, new CancellationTokenSource(), isComplete);
            lock (_lock)
            {
                tasks.Add(newTranslationTask);
            }
            // Run `OnTaskCompleted` in a new thread.
            newTranslationTask.Task.ContinueWith(
                task => OnTaskCompleted(newTranslationTask),
                TaskContinuationOptions.OnlyOnRanToCompletion
            );
        }

        private async Task OnTaskCompleted(TranslationTask translationTask)
        {
            lock (_lock)
            {
                // Cancel earlier partial translations only; earlier complete sentences keep running
                // so that they still get displayed and logged.
                var index = tasks.IndexOf(translationTask);
                for (int i = index - 1; i >= 0; i--)
                {
                    if (tasks[i].IsComplete)
                        continue;
                    tasks[i].CTS.Cancel();
                    tasks.RemoveAt(i);
                }
                tasks.Remove(translationTask);
            }

            output = translationTask.Task.Result;
            var translatedText = output.Item1;

            // Log after translation.
            bool isOverwrite = await Translator.IsOverwrite(translationTask.OriginalText);
            if (!isOverwrite)
                await Translator.AddContexts();
            await Translator.Log(translationTask.OriginalText, translatedText, isOverwrite);
        }
    }

    public class TranslationTask
    {
        public Task<(string, bool)> Task { get; }
        public string OriginalText { get; }
        public CancellationTokenSource CTS { get; }
        public bool IsComplete { get; }

        public TranslationTask(Func<CancellationToken, Task<(string, bool)>> worker,
            string originalText, CancellationTokenSource cts, bool isComplete = false)
        {
            Task = worker(cts.Token);
            OriginalText = originalText;
            CTS = cts;
            IsComplete = isComplete;
        }
    }
}