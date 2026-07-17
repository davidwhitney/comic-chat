using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using ComicChat.App.Rendering;
using ComicChat.Core.Avatars;
using ComicChat.Core.Comic;
using ComicChat.Core.Geometry;
using ComicChat.Core.History;
using ComicChat.Irc;

namespace ComicChat.App;

/// <summary>
/// The Comic Chat window. Roughly the role of CMainFrame + CChatView in the original.
/// </summary>
public partial class MainWindow : Window
{
    private readonly ArtLibrary _art;
    private readonly ArtBitmapCache _bitmaps = new();
    private readonly AvatarBodyRenderer _bodyRenderer;
    private readonly BackDropRenderer _backDropRenderer;
    private readonly AvaloniaTextMeasurer _measurer;
    private readonly ComicSession? _session;

    private IrcConnection? _connection;
    private IrcProtocol? _protocol;
    private string? _room;

    /// <summary>The pose the user pinned on the wheel, or null when the expert system is in charge.</summary>
    private Emotion? _pinnedEmotion;

    public MainWindow()
    {
        InitializeComponent();

        _art = ArtLibrary.Load();
        _bodyRenderer = new AvatarBodyRenderer(_bitmaps);
        _backDropRenderer = new BackDropRenderer(_bitmaps);
        _measurer = new AvaloniaTextMeasurer();

        if (_art.Avatars.Count == 0)
        {
            StatusText.Text = "No .avb art found — expected v2.5-beta-1/comicart in the repo.";
            SayBox.IsEnabled = SayButton.IsEnabled = ConnectButton.IsEnabled = false;
            return;
        }

        var font = _measurer.CreateFontInfo();
        var metrics = new PageMetrics
        {
            PanelsPerRow = 2,
            BalloonFont = font,
            WhisperFont = font,
            ShoutFont = font,
            TitleFont = font,
        };

        _session = new ComicSession(new LayoutContext(_measurer, metrics), _art.Avatars)
        {
            BackDropIdByName = BackDropIdByName,
        };
        _session.SetSelf(NickBox.Text ?? "me");

        // A reload replaces the page object, so the view has to be re-pointed at the new one.
        _session.ComicChanged += () => PageView.SetPage(_session.Page, _bodyRenderer, _backDropRenderer);

        PopulateArtLists();
        RegisterBackdrops();
        PageView.SetPage(_session.Page, _bodyRenderer, _backDropRenderer);

        Wheel.EmotionChanged += OnWheelChanged;
        ComicScroll.SizeChanged += (_, _) => Relayout();

        Dispatcher.UIThread.Post(Relayout, DispatcherPriority.Loaded);
        RefreshMembers();
    }

    /// <summary>Resolve a backdrop name to the id the renderer registered it under.</summary>
    private ushort BackDropIdByName(string name)
    {
        int i = _art.Backdrops.FindIndex(b => b.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        return i < 0 ? (ushort)0 : (ushort)(i + 1);
    }

    private void PopulateArtLists()
    {
        foreach (var a in _art.Avatars)
            AvatarBox.Items.Add(a.Name ?? "(unnamed)");
        if (AvatarBox.ItemCount > 0) AvatarBox.SelectedIndex = 0;

        foreach (var (name, _) in _art.Backdrops)
            BackdropBox.Items.Add(name);
        if (BackdropBox.ItemCount > 0) BackdropBox.SelectedIndex = 0;
    }

    /// <summary>
    /// Register every backdrop under a 1-based id, and record the first as the room's backdrop.
    /// </summary>
    /// <remarks>
    /// Recording it through history (rather than assigning it to the panels) is what makes the
    /// backdrop apply to panels created later, and what carries it into a saved archive.
    /// </remarks>
    private void RegisterBackdrops()
    {
        for (int i = 0; i < _art.Backdrops.Count; i++)
            _backDropRenderer.Register((ushort)(i + 1), _art.Backdrops[i].backdrop);

        if (_art.Backdrops.Count > 0 && _session is not null)
            _session.RecordBackDrop(_art.Backdrops[0].name);
    }

    /// <summary>The panel geometry the comic was last built at, so we only rebuild when it changes.</summary>
    private (int perRow, int unitWidth) _builtAt;

    /// <summary>
    /// Fit the panel grid to the window and rebuild the comic at the new size.
    /// Port of CPageView::SetPanelsWide (pageview.cpp:1101).
    /// </summary>
    /// <remarks>
    /// Panels are sized in TWIPS from the window and the view draws 1:1. Pinning the panel size
    /// and scaling the view instead would change the panel's size <i>relative to the font</i>,
    /// so balloons would break and split differently at different window sizes.
    ///
    /// Because the panel size changes what fits in a balloon, the existing strip is no longer
    /// valid: the original discards it and replays the whole history
    /// (ResetExistingPanels + ExecuteHistory(HM_RELOAD), pageview.cpp:1111). We do the same. The
    /// per-panel seeds come back off the master stream in the same order, so the rebuilt comic is
    /// deterministic rather than reshuffled.
    /// </remarks>
    private void Relayout()
    {
        if (_session is null) return;

        double availDip = Math.Max(240, ComicScroll.Bounds.Width - 24);
        int availTwips = (int)(availDip * AvaloniaTextMeasurer.TwipsPerDip);

        // ~250 DIP per panel keeps panels near the size the engine's constants are tuned for.
        int perRow = Math.Clamp((int)(availDip / 250), 1, 6);

        var m = _session.Ctx.Metrics;
        m.SetPanelsWide(perRow, availTwips);
        PageView.Scale = AvaloniaTextMeasurer.TwipsPerDip;

        // Replaying is O(history); skip it when the geometry is unchanged (e.g. a vertical resize).
        if (_builtAt != (perRow, m.UnitWidth))
        {
            _builtAt = (perRow, m.UnitWidth);
            if (_session.History.Entries.Count > 0)
                _session.Reload();
        }

        PageView.InvalidateMeasure();
        PageView.Refresh();
    }

    private BalloonMode CurrentMode => ModeBox.SelectedIndex switch
    {
        1 => BalloonMode.Think,
        2 => BalloonMode.Whisper,
        3 => BalloonMode.Action,
        _ => BalloonMode.Say,
    };

    private static SayMode ToSayMode(BalloonMode m) => m switch
    {
        BalloonMode.Think => SayMode.Think,
        BalloonMode.Whisper => SayMode.Whisper,
        BalloonMode.Action => SayMode.Action,
        _ => SayMode.Say,
    };

    private void OnSayKeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        _ = SayAsync();
    }

    private void OnSayClick(object? sender, RoutedEventArgs e) => _ = SayAsync();

    /// <summary>
    /// Say something: draw it locally, and if connected, put it on the wire with its pose.
    /// </summary>
    /// <remarks>
    /// The pose is resolved once, here on the sender, and shipped as an annotation — receivers
    /// render it rather than re-inferring it. That asymmetry is deliberate in the protocol
    /// (textpose.cpp runs only on the sender's machine).
    /// </remarks>
    private async Task SayAsync()
    {
        var text = SayBox.Text?.Trim();
        if (string.IsNullOrEmpty(text) || _session is null) return;

        SayBox.Text = "";
        var nick = NickBox.Text ?? "me";
        var mode = CurrentMode;

        _session.Say(nick, text, mode);
        RefreshComic();

        if (_protocol is null || _room is null) return;

        try
        {
            // Send the pose that was actually drawn. CurrentBody is what the session resolved
            // for this line — whether from the text or from the wheel — so the peers' comics
            // match ours. LastFace/LastTorso are the anti-repeat cursor, not the pose.
            var self = _session.Participants[_session.SelfId];
            var drawn = self.Resolver.CurrentBody;

            var ann = Annotations.FromEmotions(
                gestureIndex: (sbyte)Math.Max(0, drawn.TorsoIndex),
                torso: _pinnedEmotion ?? new Emotion(0, Em.Neutral),
                expressionIndex: (sbyte)Math.Max(0, drawn.FaceIndex),
                face: _pinnedEmotion ?? new Emotion(0, Em.Neutral),
                requested: _pinnedEmotion is not null,
                mode: ToSayMode(mode));

            await _protocol.SayAsync(_room, text, ann);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Send failed: {ex.Message}";
        }
    }

    private void RefreshComic()
    {
        RefreshMembers();
        PageView.InvalidateMeasure();
        PageView.Refresh();
        Dispatcher.UIThread.Post(ComicScroll.ScrollToEnd, DispatcherPriority.Background);
    }

    private void RefreshMembers()
    {
        if (_session is null) return;
        MemberList.Items.Clear();
        foreach (var p in _session.Participants.Values)
            MemberList.Items.Add($"{p.Nick}  ({p.AvatarName})");
    }

    /// <summary>
    /// Pose our avatar by hand.
    /// </summary>
    /// <remarks>
    /// Two things have to happen together, and missing either makes the wheel do nothing:
    /// the resolved pose is applied to the avatar's body (UpdateBody), and the avatar is
    /// temporarily frozen so the expert system stands down for the next line
    /// (textpose.cpp:125). "Frozen" preserves the body — it does not mean "go neutral".
    ///
    /// The freeze is temporary: ResetAvatar expires it once the panel is drawn (avatar.cpp:456),
    /// so a hand-picked pose applies to exactly the next thing you say and then auto-posing
    /// resumes. That is the original's behaviour, and the "Freeze" toggle is what makes it stick.
    /// </remarks>
    private void OnWheelChanged(Emotion emotion)
    {
        if (_session is null) return;

        var self = _session.Participants[_session.SelfId];
        var resolved = self.Resolver.GetBodyFromEmotion(emotion);

        self.Resolver.UpdateBody(resolved);
        if (!_holdPose) self.Resolver.Freeze = AvatarFreezeState.TempFrozen;
        _pinnedEmotion = emotion;

        PoseText.Text = emotion.Intensity == 0
            ? "Neutral (pinned)"
            : $"{DescribeEmotion(emotion)} · {emotion.Intensity:F1}"
              + (self.Resolver.Style == AvatarPoseStyle.Complex
                    ? $" · face {resolved.FaceIndex}, torso {resolved.TorsoIndex}"
                    : $" · body {resolved.TorsoIndex}");
    }

    /// <summary>Name the nearest wheel spoke, so the pose readout means something.</summary>
    private static string DescribeEmotion(Emotion e)
    {
        (float v, string name)[] spokes =
        [
            (Em.Happy, "Happy"), (Em.Coy, "Coy"), (Em.Bored, "Bored"), (Em.Scared, "Scared"),
            (Em.Sad, "Sad"), (Em.Angry, "Angry"), (Em.Shout, "Shout"), (Em.Laugh, "Laugh"),
        ];

        var best = spokes.MinBy(s => AngleUtil.SubtractAngles(s.v, e.EmotionValue));
        return best.name;
    }

    /// <summary>True while the freeze toggle holds the pose across lines (AF_FROZEN).</summary>
    private bool _holdPose;

    /// <summary>
    /// Toggle between holding the hand-picked pose and letting the text drive it.
    /// Port of the freeze toggle (bodycam.cpp:1032).
    /// </summary>
    private void OnUnfreezeClick(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        var resolver = _session.Participants[_session.SelfId].Resolver;
        _holdPose = !_holdPose;

        if (_holdPose)
        {
            resolver.Freeze = AvatarFreezeState.Frozen;
            FreezeButton.Content = "Frozen — click to auto-pose";
            PoseText.Text = "Holding this pose";
        }
        else
        {
            resolver.Freeze = AvatarFreezeState.Unfrozen;
            resolver.SetNeutral();
            _pinnedEmotion = null;
            FreezeButton.Content = "Hold pose (freeze)";
            PoseText.Text = "Auto-posing from text";
        }
    }

    /// <summary>
    /// Pick our character.
    /// </summary>
    /// <remarks>
    /// Recorded as a `changeavatar` entry rather than applied directly: who appears as whom
    /// decides each character's art and therefore their width and tail attach point, so a comic
    /// replayed without it would lay its balloons out differently.
    /// </remarks>
    private void OnAvatarChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_session is null || AvatarBox.SelectedIndex < 0) return;
        if (_session.History.Replaying) return;

        var chosen = _art.Avatars[AvatarBox.SelectedIndex];
        var nick = NickBox.Text ?? "me";

        _session.RecordAvatarChange(nick, chosen.Name ?? "NONE");
        _session.SetSelf(nick, chosen.Name);

        if (_protocol is not null)
        {
            _protocol.MyAvatarName = chosen.Name;
            if (_room is not null) _ = _protocol.AnnounceAvatarAsync(_room, chosen.Name);
        }

        RefreshMembers();
        PageView.Refresh();
    }

    /// <summary>
    /// Change the room's backdrop.
    /// </summary>
    /// <remarks>
    /// This goes through history rather than stamping the panels directly. Stamping only fixes
    /// the panels that exist right now, so every panel created afterwards would come out blank —
    /// the page has to know the current backdrop so it can apply it to new panels too.
    /// </remarks>
    private void OnBackdropChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (_session is null || BackdropBox.SelectedIndex < 0) return;
        if (_suppressBackdropEvent || _session.History.Replaying) return;

        var name = _art.Backdrops[BackdropBox.SelectedIndex].name;
        _session.RecordBackDrop(name);

        if (_protocol is not null && _room is not null)
            _ = _protocol.AnnounceBackdropAsync(_room, name);

        PageView.Refresh();
    }

    // ---- .ccc archives ------------------------------------------------------

    private static readonly FilePickerFileType CccFileType = new("Comic Chat conversation")
    {
        Patterns = ["*.ccc"],
    };

    /// <summary>Save the comic. Port of CChatDoc::ChatSaveConversation (histent.cpp:653).</summary>
    private async void OnSaveClick(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save comic",
            SuggestedFileName = "conversation.ccc",
            DefaultExtension = "ccc",
            FileTypeChoices = [CccFileType],
        });
        if (file?.TryGetLocalPath() is not { } path) return;

        try
        {
            _session.History.Save(path);
            StatusText.Text = $"Saved {Path.GetFileName(path)} " +
                              $"({_session.History.Entries.Count} entries)";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Save failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Open a saved comic. Port of CChatDoc::ChatLoadConversation (histent.cpp:670).
    /// </summary>
    /// <remarks>
    /// Loading replays the archive through the same entry pipeline a live session uses, so the
    /// comic is rebuilt rather than deserialised — panels are never stored, only the events.
    /// </remarks>
    private async void OnOpenClick(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Open comic",
            AllowMultiple = false,
            FileTypeFilter = [CccFileType],
        });
        if (files.Count == 0 || files[0].TryGetLocalPath() is not { } path) return;

        try
        {
            _session.History.Clear();
            _session.ResetComic();

            if (!_session.History.Load(path, out var unknown))
            {
                StatusText.Text = "Not a Comic Chat conversation file.";
                return;
            }

            PageView.SetPage(_session.Page, _bodyRenderer, _backDropRenderer);
            RefreshComic();

            StatusText.Text = unknown.Count == 0
                ? $"Opened {Path.GetFileName(path)} ({_session.History.Entries.Count} entries)"
                : $"Opened {Path.GetFileName(path)} — skipped unknown: {string.Join(", ", unknown)}";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Open failed: {ex.Message}";
        }
    }

    private async void OnConnectClick(object? sender, RoutedEventArgs e)
    {
        if (_session is null) return;

        if (_connection is not null)
        {
            await DisconnectAsync();
            return;
        }

        var host = ServerBox.Text?.Trim();
        var nick = NickBox.Text?.Trim();
        _room = RoomBox.Text?.Trim();

        if (string.IsNullOrEmpty(host) || string.IsNullOrEmpty(nick) || string.IsNullOrEmpty(_room))
        {
            StatusText.Text = "Server, nick and room are all required.";
            return;
        }

        if (!int.TryParse(PortBox.Text, out int port)) port = 6667;

        try
        {
            StatusText.Text = $"Connecting to {host}…";
            ConnectButton.IsEnabled = false;

            _connection = new IrcConnection();
            _protocol = new IrcProtocol(_connection)
            {
                MyNick = nick,
                MyAvatarName = _session.Participants[_session.SelfId].AvatarName,
            };

            _protocol.MessageReceived += OnComicMessage;
            _protocol.VerbReceived += OnComicVerb;
            _connection.Disconnected += (_, _) =>
                Dispatcher.UIThread.Post(() => StatusText.Text = "Disconnected.");

            await _connection.ConnectAsync(host, port);
            await _connection.RegisterAsync(new IrcRegistration { Nick = nick, RealName = "Comic Chat User" });
            await _protocol.JoinAsync(_room);

            // Tell the room who we look like. Art itself never crosses IRC — only the name.
            await _protocol.AnnounceAvatarAsync(_room, _protocol.MyAvatarName);

            StatusText.Text = $"Connected to {host} · {_room}";
            ConnectButton.Content = "Disconnect";
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Connect failed: {ex.Message}";
            _connection = null;
            _protocol = null;
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async Task DisconnectAsync()
    {
        try
        {
            if (_connection is not null) await _connection.DisconnectAsync("Comic Chat");
        }
        catch { /* tearing down anyway */ }

        _connection = null;
        _protocol = null;
        StatusText.Text = "Offline";
        ConnectButton.Content = "Connect";
    }

    /// <summary>
    /// An inbound message becomes a panel.
    /// </summary>
    /// <remarks>
    /// If it arrived "cooked" (from a Comic Chat client) we honour the sender's pose. If not —
    /// a plain mIRC user — we run our own expert system over the words, which is exactly why
    /// text-only users still appear as expressive characters (chatdoc.cpp:451).
    /// </remarks>
    private void OnComicMessage(object? sender, ComicMessageEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_session is null) return;

            var msg = e.Message;
            if (msg.Sender.Name.Equals(_protocol?.MyNick, StringComparison.OrdinalIgnoreCase))
                return;   // we already drew our own line locally

            // Map the wire annotation onto the archive's pose representation and record it.
            // Resolution happens inside the session, so it runs the same way here and on replay.
            var a = msg.Annotations;
            var pose = new SayPose
            {
                GestureIndex = a.GestureIndex,
                GestureEmotion = a.GestureEmotion,
                GestureIntensity = a.GestureIntensity,
                ExpressionIndex = a.ExpressionIndex,
                ExpressionEmotion = a.ExpressionEmotion,
                ExpressionIntensity = a.ExpressionIntensity,
                Requested = a.Requested ? 1 : 0,
                Modes = ToBalloonMode(a.Mode),
                Cooked = msg.Cooked,
                TalkTos = [.. a.TalkTos],
            };

            _session.SayWithPose(msg.Sender.Name, msg.Text, pose);
            RefreshComic();
        });
    }

    private static BalloonMode ToBalloonMode(SayMode m) => m switch
    {
        SayMode.Think => BalloonMode.Think,
        SayMode.Whisper => BalloonMode.Whisper,
        SayMode.Action => BalloonMode.Action,
        _ => BalloonMode.Say,
    };

    /// <summary>Handle the '#' verbs: who someone appears as, and backdrop changes.</summary>
    private void OnComicVerb(object? sender, ComicVerbEventArgs e)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_session is null) return;

            switch (e.Verb.Kind)
            {
                case ComicVerbKind.AppearsAs when e.Verb.Name is { } avName:
                    _session.RecordAvatarChange(e.Sender.Name, avName, e.Verb.Url);
                    break;

                // A peer changed the room's backdrop; follow them.
                case ComicVerbKind.BackdropDrop2 or ComicVerbKind.BackdropDrop when e.Verb.Name is { } bdName:
                    _session.RecordBackDrop(bdName, e.Verb.Url);
                    SyncBackdropBox(bdName);
                    break;
            }

            RefreshMembers();
            PageView.Refresh();
        });
    }

    /// <summary>Reflect a backdrop change into the dropdown without re-recording it.</summary>
    private void SyncBackdropBox(string name)
    {
        int i = _art.Backdrops.FindIndex(b => b.name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (i >= 0 && BackdropBox.SelectedIndex != i)
        {
            _suppressBackdropEvent = true;
            BackdropBox.SelectedIndex = i;
            _suppressBackdropEvent = false;
        }
    }

    private bool _suppressBackdropEvent;
}
