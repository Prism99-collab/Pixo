using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DynamicNotch.Services;

namespace DynamicNotch;

/// <summary>
/// Talking to the AI: the mic beside the message box (or Ctrl+M) listens; your words appear as you speak (Windows'
/// dictation, free) or once you stop (Gemini: free with its key, any language; or OpenRouter, with credit); a pause ends it, and what you said is
/// sent. Answers to what you said are read aloud (or always, or never: Preferences), with a Windows voice that
/// speaks the answer's language; Esc, the speaker button, or talking again stops the voice.
/// </summary>
public partial class NotchWindow
{
    TextBlock? _micButton, _speakButton;
    StackPanel? _levelBars;
    VoiceRecorder? _recorder;
    Windows.Foundation.IAsyncOperation<Windows.Media.SpeechRecognition.SpeechRecognitionResult>? _dictation;
    Windows.Media.SpeechRecognition.SpeechRecognizer? _recognizer;
    bool _listening, _transcribing, _spokenTurn;
    string _engineNow = "windows"; // how this time's words are written down: "local", "ai" or "windows"
    int _liveRun;                 // the live words' loop (a new recording starts a new one)
    string[] _voiceLangs = { "en" }; // the languages this recording can be in (yours: see LocalSpeech.Candidates)
    string _voiceLang = "en";     // the one it's written in (the last one heard, then checked once from the speech)
    bool _voiceLangChecked;
    CancellationTokenSource? _liveCancel;
    Task<(string Text, string Language, float Sure)?>? _early; // the final words, started as soon as you went quiet
    long _earlyFor = -1;          // …for the speech up to here (talking again makes it stale)
    CancellationTokenSource? _earlyCancel;
    string _beforeVoice = "", _lastHypothesis = "";

    // ───────────────────────────── the mic and the level ─────────────────────────────

    FrameworkElement BuildMic()
    {
        _micButton = new TextBlock
        {
            Text = "", FontFamily = (FontFamily)FindResource("IconFont"), FontSize = 13, Width = 26, Height = 26, TextAlignment = TextAlignment.Center, Padding = new Thickness(0, 6, 0, 0),
            Cursor = Cursors.Hand, ToolTip = "Talk  ·  Ctrl+M", VerticalAlignment = VerticalAlignment.Center, Foreground = DimBrush, Background = Brushes.Transparent, Margin = new Thickness(4, 0, 0, 0),
        };
        _micButton.MouseEnter += (_, _) => { if (!_listening) _micButton.Foreground = Frozen(ChatAccent); };
        _micButton.MouseLeave += (_, _) => { if (!_listening) _micButton.Foreground = DimBrush; };
        _micButton.MouseLeftButtonDown += (_, e) => e.Handled = true;
        _micButton.MouseLeftButtonUp += (_, e) => { e.Handled = true; ToggleListening(); };
        VoiceOut.Changed += speaking => Dispatcher.BeginInvoke(() => PaintSpeaker(speaking));
        return _micButton;
    }

    /// <summary>Five bars that dance with your voice, in the prompt's place while listening.</summary>
    FrameworkElement BuildLevelBars()
    {
        _levelBars = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Visibility = Visibility.Collapsed, IsHitTestVisible = false };
        for (int i = 0; i < 5; i++)
            _levelBars.Children.Add(new Rectangle { Width = 3, Height = 4, RadiusX = 1.5, RadiusY = 1.5, Margin = new Thickness(1, 0, 1, 0), VerticalAlignment = VerticalAlignment.Center, Fill = Brushes.White });
        return _levelBars;
    }

    void ShowLevel(double level)
    {
        if (_levelBars == null) return;
        var acc = ChatAccent;
        double[] shape = { 0.55, 0.85, 1, 0.8, 0.5 };
        for (int i = 0; i < _levelBars.Children.Count; i++)
        {
            var bar = (Rectangle)_levelBars.Children[i];
            double h = 4 + 16 * Math.Clamp(level * shape[i] * (0.8 + 0.4 * Random.Shared.NextDouble()), 0, 1);
            bar.Fill = Frozen(acc);
            bar.BeginAnimation(HeightProperty, new DoubleAnimation(h, TimeSpan.FromMilliseconds(70)));
        }
    }

    void PaintMic()
    {
        if (_micButton == null) return;
        _micButton.Visibility = _settings.AiChat && _settings.AiVoice ? Visibility.Visible : Visibility.Collapsed;
        _micButton.Text = _listening ? "" : "";
        _micButton.Foreground = _listening ? Frozen(Color.FromRgb(0xFF, 0x45, 0x3A)) : DimBrush;
        _micButton.ToolTip = _listening ? "Stop  ·  Ctrl+M" : _transcribing ? "Writing down what you said…" : "Talk  ·  Ctrl+M";
        if (_listening && !Eco)
        {
            var pulse = new DoubleAnimation(1, 0.45, TimeSpan.FromMilliseconds(600)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(pulse, 30);
            _micButton.BeginAnimation(OpacityProperty, pulse);
        }
        else _micButton.BeginAnimation(OpacityProperty, null);
        if (_levelBars != null && _launchPrompt != null)
        {
            _levelBars.Visibility = _listening ? Visibility.Visible : Visibility.Collapsed;
            _launchPrompt.Visibility = _listening ? Visibility.Hidden : Visibility.Visible;
        }
    }

    // ───────────────────────────── listening ─────────────────────────────

    void ToggleListening()
    {
        if (_listening) { FinishListening(cancel: false); return; }
        StartListening();
    }

    async void StartListening()
    {
        if (_listening || _transcribing || _launchBox == null) return;
        VoiceOut.Stop();
        if (!_chatMode && _pop == null) EnterChat(null, resume: true); // talking is for the AI
        if (_chatView == ChatView.History) ShowChatView(ChatView.Chat);
        _listening = true;
        _spokenTurn = true;
        _beforeVoice = _launchBox.Text.Length > 0 ? _launchBox.Text.TrimEnd() + " " : "";
        if (_launchHint != null) _launchHint.Text = _cyber ? "LISTENING…" : "Listening…";
        PaintMic();
        ChatKeys();
        try
        {
            // the level, from our own recording (and, for OpenRouter, what's sent)
            _recorder = new VoiceRecorder();
            _recorder.Level += l => Dispatcher.BeginInvoke(() => ShowLevel(l));
            _engineNow = VoiceEngineNow();
            bool recorded = _engineNow != "windows";
            if (recorded) _recorder.Ended += heard => Dispatcher.BeginInvoke(() => { if (_listening) FinishListening(cancel: !heard); });
            _recorder.Start();
            if (_engineNow == "local") StartLiveWords();
            if (!recorded) await ListenWithWindows();
        }
        catch (Exception ex)
        {
            AppLog.Write("voice in: " + ex.Message);
            StopRecorder();
            try { _recognizer?.Dispose(); } catch { } // (a half-started dictation never lingers: the next try starts clean)
            _recognizer = null;
            _dictation = null;
            _listening = false;
            PaintMic();
            ChatNote("", "The microphone isn't available: " + ex.Message, tone: Warn);
            StyleChatLine();
            ChatKeys();
        }
    }

    bool UseCloudVoice => _settings.AiVoiceEngine is "ai" or "openrouter";

    LocalSpeech.Model VoiceModel => LocalSpeech.Pick(_settings.AiVoiceModel);

    /// <summary>
    /// How this time's words are written down. "auto" (the default) and "local": on this PC when its model is here; if not
    /// yet, it's downloaded in the background (once) and this time Gemini writes it down (free with a Google key), else
    /// Windows' dictation. "windows" and "ai" are used as chosen.
    /// </summary>
    string VoiceEngineNow()
    {
        if (!_settings.AiVoiceLangMoved)
        {
            // (once) English by default: another language is picked in Preferences
            _settings.AiVoiceLangMoved = true;
            if (_settings.AiVoiceLanguage == "auto") _settings.AiVoiceLanguage = "en-US";
            _settings.Save();
        }
        if (!_settings.AiVoiceSendMoved)
        {
            // (once) what you say waits in the box, like a normal chat: you check it and press Enter
            _settings.AiVoiceSendMoved = true;
            _settings.AiVoiceSend = false;
            _settings.Save();
        }
        if (!_settings.AiVoiceAutoMoved)
        {
            // (once) the old default "windows" becomes "auto": Windows' dictation needs a privacy setting most PCs have off
            _settings.AiVoiceAutoMoved = true;
            if (_settings.AiVoiceEngine == "windows") _settings.AiVoiceEngine = "auto";
            _settings.Save();
        }
        string pick = _settings.AiVoiceEngine == "openrouter" ? "ai" : _settings.AiVoiceEngine;
        if (pick is not ("auto" or "local")) return pick;
        var model = VoiceModel;
        if (!LocalSpeech.Broken && LocalSpeech.Ready(model)) return "local";
        if (!LocalSpeech.Broken) GetVoiceModel(model);
        return OpenRouter.IsGemini && OpenRouter.HasKey ? "ai" : "windows";
    }

    bool _voiceModelAsked;

    /// <summary>The offline model, downloaded in the background (once), said in the chat; "ready" when it's done.</summary>
    void GetVoiceModel(LocalSpeech.Model model)
    {
        if (_voiceModelAsked && LocalSpeech.Progress >= 0) return; // already on its way
        bool first = !_voiceModelAsked;
        _voiceModelAsked = true;
        if (first) ChatNote("\uE896", $"Getting offline voice ({model.Bytes / 1048576} MB, once). Until it's here, {(OpenRouter.IsGemini && OpenRouter.HasKey ? "Gemini" : "Windows")} writes down what you say.", tone: ChatAccent);
        _ = LocalSpeech.Get(model).ContinueWith(t => Dispatcher.BeginInvoke(() =>
        {
            if (t.Result) Toast("Offline voice is ready: works without internet");
            else ChatNote("\uE7BA", "Offline voice couldn't be downloaded (no internet?). It's tried again next time you talk.", tone: Warn);
        }));
    }

    /// <summary>Windows' dictation: free; your words appear as you say them; it ends on a pause.</summary>
    async Task ListenWithWindows()
    {
        // (Windows' dictation wants a region: "fr" is taken as fr-FR, "es" as es-ES…)
        var lang = _settings.AiVoiceLanguage is { Length: > 0 } tag0 && tag0 != "auto" && (tag0.Length > 2 ? tag0 : tag0 switch { "en" => "en-US", "fr" => "fr-FR", "es" => "es-ES", "de" => "de-DE", "it" => "it-IT", "pt" => "pt-BR", _ => tag0 }) is var tag
            ? new Windows.Globalization.Language(tag) : Windows.Media.SpeechRecognition.SpeechRecognizer.SystemSpeechLanguage;
        // a language this PC can't take dictation in (English isn't installed everywhere): Windows' own instead
        if (!Windows.Media.SpeechRecognition.SpeechRecognizer.SupportedTopicLanguages.Any(l => l.LanguageTag.Equals(lang.LanguageTag, StringComparison.OrdinalIgnoreCase)))
            lang = Windows.Media.SpeechRecognition.SpeechRecognizer.SystemSpeechLanguage;
        _recognizer = new Windows.Media.SpeechRecognition.SpeechRecognizer(lang);
        _recognizer.Constraints.Add(new Windows.Media.SpeechRecognition.SpeechRecognitionTopicConstraint(Windows.Media.SpeechRecognition.SpeechRecognitionScenario.Dictation, "dictation"));
        _recognizer.Timeouts.InitialSilenceTimeout = TimeSpan.FromSeconds(7);
        _recognizer.Timeouts.EndSilenceTimeout = TimeSpan.FromSeconds(1.3);
        _lastHypothesis = "";
        _recognizer.HypothesisGenerated += (_, e) => Dispatcher.BeginInvoke(() =>
        {
            if (!_listening || _launchBox == null) return;
            _lastHypothesis = e.Hypothesis.Text;
            _launchBox.Text = _beforeVoice + e.Hypothesis.Text + "…";
            _launchBox.CaretIndex = _launchBox.Text.Length;
        });
        var compiled = await _recognizer.CompileConstraintsAsync();
        if (compiled.Status != Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success)
            throw new InvalidOperationException("dictation isn't available for " + lang.DisplayName + " (" + compiled.Status + ")");
        try
        {
            _dictation = _recognizer.RecognizeAsync();
            var result = await _dictation;
            bool ok = result.Status == Windows.Media.SpeechRecognition.SpeechRecognitionResultStatus.Success;
            // stopped early: what it had already shown counts
            string text = ok && result.Text.Trim().Length > 0 ? result.Text.Trim() : _lastHypothesis.Trim();
            Heard(text, ok || text.Length > 0 ? null : result.Status.ToString());
        }
        catch (Exception ex) when (ex.HResult == unchecked((int)0x80045509))
        {
            EndListeningUi();
            ChatNote("", "Windows needs “Online speech recognition” on to take dictation.", tone: Warn,
                action: ("Open the setting", () => { try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("ms-settings:privacy-speech") { UseShellExecute = true }); } catch { } }));
        }
        catch (TaskCanceledException) { EndListeningUi(); }
        catch (OperationCanceledException) { EndListeningUi(); }
    }

    /// <summary>Stop now (and use what was said), or cancel (Esc).</summary>
    async void FinishListening(bool cancel)
    {
        if (!_listening) return;
        if (_recognizer != null)
        {
            // Windows: stopping early still gives what it heard so far
            if (cancel) { try { _dictation?.Cancel(); } catch { } EndListeningUi(); if (_launchBox != null) _launchBox.Text = _beforeVoice.TrimEnd(); }
            else
            {
                try { await _recognizer.StopRecognitionAsync(); }
                catch { string heard = _lastHypothesis; try { _dictation?.Cancel(); } catch { } if (heard.Length > 0) Heard(heard, null); }
            }
            return;
        }
        // recorded: written down on this PC, or sent out (Gemini / OpenRouter)
        var rec = _recorder;
        _listening = false;
        StopLiveWords();
        rec?.Stop();
        await Task.Delay(120);
        PaintMic();
        if (cancel || rec == null || !rec.Heard || rec.Length < TimeSpan.FromMilliseconds(400)) { DropEarlyWords(); StopRecorder(); StyleChatLine(); ChatKeys(); if (_launchBox != null && cancel) _launchBox.Text = _beforeVoice.TrimEnd(); if (!cancel) Toast("Didn't hear anything"); return; }
        byte[] wav = rec.Wav();
        float[] samples = rec.SpeechSamples();
        long lastVoice = rec.LastVoiceAt;
        StopRecorder();
        if (_engineNow == "local")
        {
            // the final words may be ready already: started when you went quiet, and you didn't speak again since
            var early = _early != null && _earlyFor == lastVoice ? _early : null;
            _early = null;
            await WriteDownHere(samples, wav, early);
            return;
        }
        _transcribing = true;
        if (_launchHint != null && _launchBox != null) { _launchBox.Text = ""; _launchHint.Text = _cyber ? "DECODING SPEECH…" : "Writing down what you said…"; }
        PaintMic();
        try
        {
            if (OpenRouter.LoadKey() is not { } key) { Heard("", "no key"); return; }
            string? model = await OpenRouter.AudioModel(ChatModel);
            if (model == null) { Heard("", "no model can hear speech right now"); return; }
            var (text, error) = await OpenRouter.Transcribe(key, model, wav, CancellationToken.None);
            if (error != null && error.Contains("credits", StringComparison.OrdinalIgnoreCase))
                error = OpenRouter.IsGemini ? error : "OpenRouter needs at least $0.50 of credit to hear audio. Switch voice to Windows or Gemini in Preferences → AI chat.";
            Heard(text, error);
        }
        finally { _transcribing = false; PaintMic(); }
    }

    /// <summary>
    /// Written down on this PC. When it isn't sure (a low score, or Arabic: Darija is where it's weakest) and you allow it,
    /// the same recording is checked again with Gemini and its words are used. If this PC can't run it, Gemini does it.
    /// </summary>
    async Task WriteDownHere(float[] samples, byte[] wav, Task<(string Text, string Language, float Sure)?>? early = null)
    {
        _transcribing = true;
        // the live words stay in the box while the final ones come (no blank moment)
        if (_launchHint != null) _launchHint.Text = _cyber ? "DECODING SPEECH…" : "Writing down what you said…";
        PaintMic();
        try
        {
            var got = early != null ? await early : null;
            if (got == null && !_voiceLangChecked && samples.Length >= 12800)
            {
                _voiceLangChecked = true; // (a short phrase: its language is checked now, on all of it)
                if (await LocalSpeech.Detect(VoiceModel, samples, _voiceLangs, CancellationToken.None) is { } d && d.Sure >= 0.5f) _voiceLang = d.Language;
            }
            got ??= await LocalSpeech.Write(VoiceModel, samples, _voiceLang, partial: false, CancellationToken.None);
            if (got is { Sure: >= 0.6f } sure && sure.Language.Length >= 2 && sure.Language != _settings.AiVoiceLastLang)
            {
                _settings.AiVoiceLastLang = sure.Language; // the next recording starts in it
                _settings.Save();
            }
            string text = got?.Text ?? "";
            bool unsure = got == null || got.Value.Sure < 0.6f || got.Value.Language == "ar";
            bool canCheck = OpenRouter.IsGemini && OpenRouter.LoadKey() != null && (_settings.AiVoiceDoubleCheck || got == null && LocalSpeech.Broken);
            if (unsure && canCheck && OpenRouter.LoadKey() is { } key)
            {
                if (_launchHint != null) _launchHint.Text = _cyber ? "CROSS-CHECKING…" : "Checking with Gemini…";
                if (await OpenRouter.AudioModel(ChatModel) is { } audioModel)
                {
                    var (better, error) = await OpenRouter.Transcribe(key, audioModel, wav, CancellationToken.None);
                    if (error == null && better.Trim().Length > 0) text = better.Trim();
                }
            }
            AppLog.Write($"voice: on this PC{(got is { } g ? $" ({g.Language}, {g.Sure:0.00})" : " (nothing)")}{(text != (got?.Text ?? "") ? ", checked with Gemini" : "")}");
            Heard(text, text.Length == 0 ? "NoRecognition" : null);
        }
        finally { _transcribing = false; PaintMic(); }
    }

    /// <summary>
    /// While you talk, on this PC: your words appear and keep up (each look starts as soon as the last one is done,
    /// on the speech only, a quarter of the cores so nothing else slows down; PCs with 4 cores or more, not on battery
    /// saver). The moment you go quiet (0.6 s), the final words are started with the full engine, so they're usually
    /// ready when the pause ends; if you speak again they're dropped and started again at the next pause.
    /// </summary>
    async void StartLiveWords()
    {
        StopLiveWords();
        int run = ++_liveRun;
        _early = null; _earlyFor = -1; // (nothing from a recording before this one)
        _voiceLangs = LocalSpeech.Candidates(_settings.AiVoiceLanguage);
        _voiceLang = _voiceLangs.Contains(_settings.AiVoiceLastLang) ? _settings.AiVoiceLastLang : _voiceLangs[0];
        _voiceLangChecked = _voiceLangs.Length == 1;
        var model = VoiceModel;
        bool live = !Eco && Environment.ProcessorCount >= 4;
        _liveCancel = new CancellationTokenSource();
        while (_listening && run == _liveRun)
        {
            await Task.Delay(120);
            var rec = _recorder;
            if (!_listening || run != _liveRun || rec == null || !rec.Heard) continue;
            long voiceAt = rec.LastVoiceAt;
            // the language: checked once, from the first second and a half of speech (until then: the last one heard)
            if (!_voiceLangChecked && rec.SpeechLength >= TimeSpan.FromSeconds(1.5))
            {
                _voiceLangChecked = true;
                if (await LocalSpeech.Detect(model, rec.SpeechSamples(), _voiceLangs, CancellationToken.None) is { } d && d.Sure >= 0.5f) _voiceLang = d.Language;
                continue;
            }
            // gone quiet for a second: the final words, started now (on the speech so far: usually ready when the pause ends)
            if (rec.SilentFor >= TimeSpan.FromMilliseconds(1000))
            {
                if (_early == null || _earlyFor != voiceAt)
                {
                    if (!_voiceLangChecked && rec.SpeechLength >= TimeSpan.FromSeconds(0.8))
                    {
                        _voiceLangChecked = true; // (a short phrase: its language checked now, before its final words)
                        if (await LocalSpeech.Detect(model, rec.SpeechSamples(), _voiceLangs, CancellationToken.None) is { } d2 && d2.Sure >= 0.5f) _voiceLang = d2.Language;
                        if (!_listening || run != _liveRun) break;
                        voiceAt = rec.LastVoiceAt;
                    }
                    _liveCancel?.Cancel(); // (a live look still running gives way)
                    _earlyCancel?.Cancel();
                    _earlyCancel = new CancellationTokenSource();
                    _earlyFor = voiceAt;
                    _early = LocalSpeech.Write(model, rec.SpeechSamples(), _voiceLang, partial: false, _earlyCancel.Token);
                }
                continue;
            }
            // speaking: an early final that's out of date goes
            if (_early != null && _earlyFor != voiceAt) { _earlyCancel?.Cancel(); _early = null; _earlyFor = -1; }
            if (!live || rec.SpeechLength > TimeSpan.FromSeconds(25)) continue; // (a long one: written down at the end)
            if (_liveCancel == null || _liveCancel.IsCancellationRequested) _liveCancel = new CancellationTokenSource();
            var got = await LocalSpeech.Write(model, rec.SpeechSamples(), _voiceLang, partial: true, _liveCancel.Token);
            if (got is { } g && _listening && run == _liveRun && _launchBox != null)
            {
                _launchBox.Text = _beforeVoice + g.Text + "…";
                _launchBox.CaretIndex = _launchBox.Text.Length;
            }
        }
    }

    void StopLiveWords()
    {
        _liveRun++;
        _liveCancel?.Cancel();
    }

    /// <summary>Cancelled (Esc): the final words being written early stop too.</summary>
    void DropEarlyWords()
    {
        _earlyCancel?.Cancel();
        _early = null;
        _earlyFor = -1;
    }

    /// <summary>What you said: into the box, and sent (unless Preferences say to check it first).</summary>
    void Heard(string text, string? error)
    {
        EndListeningUi();
        if (_launchBox == null) return;
        if (error != null && text.Length == 0)
        {
            _launchBox.Text = _beforeVoice.TrimEnd();
            if (error is "NoRecognition" or "TimeoutExceeded" or "UserCanceled") Toast("Sorry, I didn't catch that");
            else ChatNote("", "Voice: " + error, tone: Warn);
            return;
        }
        string said = (_beforeVoice + text).Trim();
        _launchBox.Text = said;
        _launchBox.CaretIndex = said.Length;
        _launchBox.Focus();
        if (said.Length > 0 && _settings.AiVoiceSend) SendTyped();
    }

    void EndListeningUi()
    {
        _listening = false;
        StopLiveWords();
        try { _recognizer?.Dispose(); } catch { }
        _recognizer = null;
        _dictation = null;
        StopRecorder();
        PaintMic();
        StyleChatLine();
        ChatKeys();
    }

    void StopRecorder()
    {
        try { _recorder?.Stop(); } catch { }
        var r = _recorder;
        _recorder = null;
        if (r != null) _ = Task.Delay(300).ContinueWith(_ => r.Dispose());
    }

    // ───────────────────────────── speaking ─────────────────────────────

    /// <summary>Read the answer aloud when you spoke the question (or always / never, as you chose).</summary>
    void MaybeSpeak(string answer)
    {
        bool spoken = _spokenTurn;
        _spokenTurn = false;
        if (answer.Length == 0 || !_settings.AiVoice) return;
        if (_settings.AiSpeak == "always" || _settings.AiSpeak == "voice" && spoken) Speak(answer);
    }

    void Speak(string text) => _ = VoiceOut.Speak(text, _settings.AiVoiceId, _settings.AiVoiceRate);

    TextBlock BuildSpeakerButton()
    {
        _speakButton = HeadButton("", "Read answers aloud", () =>
        {
            if (VoiceOut.Speaking) { VoiceOut.Stop(); return; }
            // off → when you talk → always → off
            _settings.AiSpeak = _settings.AiSpeak switch { "never" => "voice", "voice" => "always", _ => "never" };
            _settings.Save();
            PaintSpeaker(false);
            Toast(_settings.AiSpeak switch { "always" => "Answers are always read aloud", "voice" => "Read aloud when you talk", _ => "Answers aren't read aloud" });
        });
        PaintSpeaker(false);
        return _speakButton;
    }

    void PaintSpeaker(bool speaking)
    {
        if (_speakButton == null) return;
        _speakButton.Visibility = _settings.AiVoice ? Visibility.Visible : Visibility.Collapsed;
        bool always = _settings.AiSpeak == "always", off = _settings.AiSpeak == "never";
        _speakButton.Text = speaking ? "" : always ? "" : off ? "\uE74F" : "";
        _speakButton.ToolTip = speaking ? "Stop reading  ·  Esc" : always ? "Answers are always read aloud · click to turn reading off" : off ? "Answers aren't read aloud · click to read them when you talk" : "Answers are read aloud when you talk · click to always read them";
        _speakButton.Foreground = speaking || always ? Frozen(ChatAccent) : DimBrush;
        if (speaking && !Eco)
        {
            var pulse = new DoubleAnimation(1, 0.5, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever };
            Timeline.SetDesiredFrameRate(pulse, 30);
            _speakButton.BeginAnimation(OpacityProperty, pulse);
        }
        else _speakButton.BeginAnimation(OpacityProperty, null);
    }
}
