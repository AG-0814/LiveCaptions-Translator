# Modifications in this fork

This is a modified version of [SakiRinn/LiveCaptions-Translator](https://github.com/SakiRinn/LiveCaptions-Translator),
licensed under the Apache License 2.0 (see `LICENSE`). It is based on upstream commit `a6fee12` (after v1.7.1300.1822).
The changes target continuous speech in meetings with a local Ollama model, where LiveCaptions rarely emits
sentence-ending punctuation.

## Changes

**Sentence splitting** (`src/Translator.cs`, `src/utils/TextUtil.cs`, `src/utils/SplitRules.cs`, `src/utils/RegexPatterns.cs`)

- A caption that grows past about 40 CJK characters (120 bytes) without a sentence end is split at the last comma,
  and the head is translated as a complete sentence.
- Without a comma, it is split before a transition word, or sent as is once it reaches a hard limit.
- Per-language rules live in `split_rules.json` next to the executable (written with defaults on first run):
  split words, fillers removed before translation, split punctuation, minimum head length and hard-split length,
  for Chinese, Japanese and English. The language is detected from the text itself.
- The already-sent head is matched by similarity, because LiveCaptions keeps revising recognized words.
- Nearly identical sentences sent within 20 seconds are not sent again.
- A dot right after a digit (`2.5%`) is treated as a decimal point, not a sentence end.
- Heads that would be shorter than the minimum, and punctuation-only segments, are not sent.

**Translation queue** (`src/models/TranslationTaskQueue.cs`)

- A finished later translation no longer cancels earlier complete sentences; only earlier partial ones.
  Previously a long sentence could be dropped whenever a short one after it finished first.

**Ollama** (`src/models/TranslateAPIConfig.cs`)

- `keep_alive` defaults to -1, so the model is not unloaded after 10 idle minutes.
- `load_duration` is a `long`; loads over about 2 seconds used to fail with an Int32 conversion error.

**Settings** (`src/models/Setting.cs`, `src/pages/SettingPage.xaml`, `src/apis/TranslateAPI.cs`)

- The API Interval slider goes up to 30 and shows its value.
- A new API Timeout slider (5–30 s, default 12 s, `TranslateTimeout` in `setting.json`) replaces the fixed
  8-second HttpClient timeout. It is applied per request by a `DelegatingHandler`.

## Recommended prompt for local models

```
You are a subtitle translator. Translate the text inside 🔤 into the language with code {0} (zh-TW means Traditional Chinese as used in Taiwan, en-US means English, ja-JP means Japanese). Always output that language, even if the text is a fragment or an incomplete sentence; never repeat the original text unchanged. Keep personal names as they are. Output only the translation in one line, with no explanations and no Markdown. Remove the 🔤 symbols.
```

With the default prompt, qwen3:8b often returned sentence fragments unchanged instead of translating them.
