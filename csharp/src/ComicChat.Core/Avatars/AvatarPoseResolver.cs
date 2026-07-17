using ComicChat.Core.Geometry;

namespace ComicChat.Core.Avatars;

/// <summary>
/// Turns emotions into poses for one avatar. Port of the selection half of CAvatarComplex /
/// CAvatarSimple (avatar.cpp:222-452).
/// </summary>
/// <remarks>
/// <para>
/// Selection is deliberately separated from the artwork here: it needs nothing from an avatar but
/// its <see cref="IPoseTable"/> and a little scrap of history, so it takes only that.
/// </para>
/// <para>
/// This type is stateful — <see cref="LastFace"/> / <see cref="LastTorso"/> drive the anti-repeat
/// round robin — so it is per-avatar and not thread-safe, exactly like the CAvatarX it stands in for.
/// </para>
/// </remarks>
public sealed class AvatarPoseResolver
{
    /// <summary>
    /// Emotion values above this are gesture sentinels rather than wheel angles. The original
    /// literally wrote <c>bRec[index].emotion &gt; 7</c> (avatar.cpp:232, 279) as its "skip
    /// gestures" guard: the wheel's largest angle is 7*2*PI/8 ~= 5.5, and the sentinels start at
    /// 1001, so 7 is a safe fence between them.
    /// </summary>
    private const float GestureFence = 7.0f;

    /// <summary>The angular half-width of a wheel spoke's catchment: PI/8 (avatar.cpp:235).</summary>
    /// <remarks>
    /// The original's comment explains it: "from 2PI / (2*NEMOTIONS), since can go half dist in
    /// each direction". A pose only answers for an emotion within half a spoke of it, so an avatar
    /// with no sad art stays neutral instead of offering up its nearest-but-wrong pose.
    /// </remarks>
    private static readonly double EmotionGate = Math.PI / Em.NEmotions;

    private readonly IPoseTable _table;

    public AvatarPoseResolver(IPoseTable table, AvatarPoseStyle style = AvatarPoseStyle.Complex)
    {
        ArgumentNullException.ThrowIfNull(table);
        _table = table;
        Style = style;
    }

    /// <summary>The pose table being selected from.</summary>
    public IPoseTable Table => _table;

    /// <summary>Whether this avatar has separate head and torso art.</summary>
    public AvatarPoseStyle Style { get; }

    /// <summary>Whether the user has pinned the pose by hand (avatar.h:200). Checked by ChatPreSendText.</summary>
    public AvatarFreezeState Freeze { get; set; } = AvatarFreezeState.Unfrozen;

    /// <summary>Capability flags (avatar.h:201).</summary>
    public AvatarFlags Flags { get; set; } = AvatarFlags.None;

    /// <summary>Index of the last face actually drawn, or -1 (m_lastFace, avatar.h:296).</summary>
    public int LastFace { get; private set; } = -1;

    /// <summary>
    /// Index of the last torso (or, for a simple avatar, the last body) actually drawn, or -1
    /// (m_lastTorso / m_lastBody, avatar.h:263, 297).
    /// </summary>
    public int LastTorso { get; private set; } = -1;

    private IReadOnlyList<IPoseRecord> Faces => _table.Faces;
    private IReadOnlyList<IPoseRecord> Torsos => _table.Torsos;

    // ------------------------------------------------------------------
    // Entry points
    // ------------------------------------------------------------------

    /// <summary>
    /// Choose a body for a set of competing candidate emotions — the expert system's path.
    /// Port of CAvatarComplex::GetBodyFromEmotion (avatar.cpp:354) and
    /// CAvatarSimple::GetBodyFromEmotion (avatar.cpp:386).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a priority-ordered constraint fill, and it is the most interesting fifteen lines in
    /// Comic Chat. Repeatedly: take the highest-priority candidate still standing, ask what face
    /// and/or torso it maps to, and let it claim whichever of the two slots is still empty. Stop
    /// as soon as both are filled. Slots nobody claimed fall back to neutral.
    /// </para>
    /// <para>
    /// The consequence is the behaviour that made the product feel alive. "I'm laughing LOL"
    /// proposes LAUGH at 11 and POINTSELF at 7. LAUGH is an expression, so it takes the face and
    /// leaves the torso open; POINTSELF is a gesture, so it takes the torso. You get an avatar
    /// pointing at itself with a laughing head — a pose no single rule ever described. Expressions
    /// and gestures never compete for a slot, so the ladder resolves them independently.
    /// </para>
    /// <para>
    /// DESTRUCTIVE: this zeroes each candidate's priority in <paramref name="emOpts"/> as it
    /// consumes it ("nuke this entry, so we don't kill again", avatar.cpp:369) — that zeroing is
    /// the loop's only termination condition, so it is preserved. The <see cref="EmotionOpts"/> is
    /// spent afterwards and must not be reused. The original made this far worse by passing the
    /// same file-scope global in on every message (textpose.cpp:117); callers here own a local.
    /// </para>
    /// </remarks>
    public ResolvedBody GetBodyFromEmotion(EmotionOpts emOpts)
    {
        ArgumentNullException.ThrowIfNull(emOpts);

        int foundF = -1, foundT = -1;

        while (true)
        {
            // Highest priority still standing. Ties go to the earliest, i.e. rule declaration order.
            int bestIndex = -1;
            int minPriority = 0;
            for (int i = 0; i < emOpts.Count; i++)
            {
                if (emOpts.Priorities[i] > minPriority)
                {
                    bestIndex = i;
                    minPriority = emOpts.Priorities[i];
                }
            }
            if (minPriority == 0) break;   // nothing left with any priority

            var (fIndex, tIndex) = GetHeadAndBodyFromEmotion(emOpts.Emotions[bestIndex]);
            emOpts.Priorities[bestIndex] = 0;   // nuke this entry, so we don't kill again

            if (fIndex >= 0 && foundF < 0) foundF = fIndex;
            if (tIndex >= 0 && foundT < 0) foundT = tIndex;

            if (Style == AvatarPoseStyle.Simple)
            {
                // CAvatarSimple fills its single slot and stops dead (avatar.cpp:406) -- it has no
                // second constraint to keep looking for.
                if (foundT >= 0) break;
            }
            else if (foundF >= 0 && foundT >= 0) break;   // filled constraints -- don't need to continue
        }

        if (Style == AvatarPoseStyle.Simple)
            return new ResolvedBody(-1, foundT >= 0 ? foundT : FindNeutral(Torsos, LastTorso));

        return new ResolvedBody(
            foundF >= 0 ? foundF : FindNeutral(Faces, LastFace),
            foundT >= 0 ? foundT : FindNeutral(Torsos, LastTorso));
    }

    /// <summary>
    /// Choose a body for one definite emotion — the manual emotion wheel's path.
    /// Port of CAvatarComplex::GetBodyFromEmotion (avatar.cpp:252) and
    /// CAvatarSimple::GetBodyFromEmotion (avatar.cpp:222).
    /// </summary>
    /// <remarks>
    /// Nearest-neighbour on the wheel, gated, with a round robin over the torso/body table.
    /// Note the asymmetry in the complex case, which is in the original: the face search is
    /// ungated (some head always wins, since a head must be drawn) while the torso search is gated
    /// at <see cref="EmotionGate"/> and falls back to neutral.
    /// </remarks>
    public ResolvedBody GetBodyFromEmotion(Emotion emotion)
    {
        if (Style == AvatarPoseStyle.Simple)
        {
            int b = FindNearestBodySimple(emotion);
            return new ResolvedBody(-1, b >= 0 ? b : FindNeutral(Torsos, LastTorso));
        }

        int face = FindNearestFace(emotion);
        int torso = FindNearestTorso(emotion);

        return new ResolvedBody(
            face >= 0 ? face : FindNeutral(Faces, LastFace),
            torso >= 0 ? torso : FindNeutral(Torsos, LastTorso));
    }

    /// <summary>
    /// Remember what was drawn, so the next selection can avoid repeating it.
    /// Port of CAvatarComplex::RecordBody / CAvatarSimple::RecordBody (avatar.cpp:750, 759).
    /// </summary>
    /// <remarks>
    /// The original called this at panel layout time (panel.cpp:619), not when the pose was chosen
    /// — the round robin advances once per drawn panel. ChatPreSendText deliberately does not call it.
    /// </remarks>
    public void RecordBody(ResolvedBody body)
    {
        if (Style == AvatarPoseStyle.Complex && body.FaceIndex >= 0) LastFace = body.FaceIndex;
        if (body.TorsoIndex >= 0) LastTorso = body.TorsoIndex;
    }

    /// <summary>Reset the anti-repeat history. The CAvatarX constructors start at -1 (avatar.h:265, 299).</summary>
    public void ResetHistory() => LastFace = LastTorso = -1;

    /// <summary>
    /// Neutral head and torso, round-robined. Port of CAvatarComplex::SetNeutral (avatar.cpp:463).
    /// </summary>
    public ResolvedBody GetNeutralBody() =>
        Style == AvatarPoseStyle.Simple
            ? new ResolvedBody(-1, FindNeutral(Torsos, LastTorso))
            : new ResolvedBody(FindNeutral(Faces, LastFace), FindNeutral(Torsos, LastTorso));

    // ------------------------------------------------------------------
    // Single-emotion lookup
    // ------------------------------------------------------------------

    /// <summary>
    /// Map one emotion to a face index, a torso index, or neither.
    /// Port of CAvatarComplex::GetHeadAndBodyFromEmotion (avatar.cpp:298) — and, for a simple
    /// avatar, CAvatarSimple::GetBodyIndexFromEmotion (avatar.cpp:326), which is the same
    /// algorithm over one table.
    /// </summary>
    /// <remarks>
    /// The branch is the whole design: a wheel emotion (&lt;= 2*PI) is an expression and resolves
    /// against faces by angular distance; a gesture sentinel is not on the wheel at all — there is
    /// no meaningful angle between "wave" and "shrug" — so it resolves against torsos by EXACT
    /// float equality. An expression therefore never claims the torso slot and a gesture never
    /// claims the face slot, which is precisely what makes the constraint fill in
    /// <see cref="GetBodyFromEmotion(EmotionOpts)"/> compose them.
    /// </remarks>
    private (int FaceIndex, int TorsoIndex) GetHeadAndBodyFromEmotion(Emotion emotion)
    {
        if (Style == AvatarPoseStyle.Simple)
        {
            // One table, so the same emotion may resolve to a body either way.
            return (-1, Em.IsWheelEmotion(emotion.EmotionValue)
                ? FindNearestUngated(Torsos, emotion)
                : FindExactGesture(Torsos, emotion.EmotionValue));
        }

        return Em.IsWheelEmotion(emotion.EmotionValue)
            ? (FindNearestUngated(Faces, emotion), -1)                        // Otherwise, can't use this metric
            : (-1, FindExactGesture(Torsos, emotion.EmotionValue));
    }

    /// <summary>
    /// Nearest row by angle, ties broken by closest intensity, no gate.
    /// Port of the face loop at avatar.cpp:305-315.
    /// </summary>
    private static int FindNearestUngated(IReadOnlyList<IPoseRecord> rows, Emotion emotion)
    {
        double nearestAngle = 3 * Math.PI;
        double intensityOfNearest = 2.0;
        int nearestI = -1;

        for (int i = 0; i < rows.Count; i++)
        {
            double thisAngle = Math.Abs(AngleUtil.SubtractAngles(rows[i].Emotion, emotion.EmotionValue));
            if (thisAngle > nearestAngle) continue;

            double deltaI = Math.Abs(emotion.Intensity - rows[i].Intensity);
            if (thisAngle == nearestAngle && deltaI >= intensityOfNearest) continue;

            nearestAngle = thisAngle;
            intensityOfNearest = deltaI;
            nearestI = i;
        }
        return nearestI;
    }

    /// <summary>
    /// Exact float match on a gesture sentinel. Port of avatar.cpp:317-322.
    /// First match wins; no round robin, so an avatar with two waves always uses the first.
    /// </summary>
    private static int FindExactGesture(IReadOnlyList<IPoseRecord> rows, float emotion)
    {
        for (int i = 0; i < rows.Count; i++)
            if (rows[i].Emotion == emotion)
                return i;
        return -1;
    }

    /// <summary>Port of the face loop in CAvatarComplex::GetBodyFromEmotion (avatar.cpp:260-270).</summary>
    private int FindNearestFace(Emotion emotion) => FindNearestUngated(Faces, emotion);

    /// <summary>
    /// Round-robin, gated nearest torso. Port of avatar.cpp:277-290.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The scan starts at <c>(LastTorso + 1 + i) % nTorsos</c> and takes the STRICTLY better
    /// intensity match, so among equally good candidates it keeps the first one it meets — which,
    /// thanks to the offset start, is the one furthest from what was drawn last. That is the
    /// anti-repeat: an avatar with three happy torsos cycles through them across consecutive
    /// lines instead of striking the same pose over and over. It is a comic strip, and identical
    /// panels look broken.
    /// </para>
    /// <para>
    /// DIVERGENCE: the original reused <c>nearestI</c> across the face and torso loops
    /// (avatar.cpp:258, 287), so a torso search that matched nothing silently passed the FACE
    /// index to SetTorso — reading a BODYREC at a face's index. We return -1 and let the caller
    /// fall back to a neutral torso, which is what line 283's "consider neutrals, too" was
    /// reaching for anyway.
    /// </para>
    /// </remarks>
    private int FindNearestTorso(Emotion emotion)
    {
        double intensityOfNearest = 2.0;
        int nearestI = -1;
        int n = Torsos.Count;

        for (int i = 0; i < n; i++)
        {
            int index = (LastTorso + 1 + i) % n;   // start search from index after last body used
            var row = Torsos[index];
            if (row.Emotion > GestureFence) continue;

            double thisAngle = Math.Abs(AngleUtil.SubtractAngles(row.Emotion, emotion.EmotionValue));
            if (thisAngle < EmotionGate ||
                (row.Emotion == Em.Neutral && row.Intensity == 0))   // consider neutrals, too
            {
                double deltaI = Math.Abs(emotion.Intensity - row.Intensity);
                if (deltaI < intensityOfNearest)
                {
                    intensityOfNearest = deltaI;
                    nearestI = index;
                }
            }
        }
        return nearestI;
    }

    /// <summary>
    /// Round-robin, gated nearest whole body. Port of CAvatarSimple::GetBodyFromEmotion (avatar.cpp:222).
    /// </summary>
    /// <remarks>
    /// Same shape as <see cref="FindNearestTorso"/>, with one wrinkle: the first neutral row is
    /// allowed in as a candidate even when it fails the gate, but is then scored at a delta of 1.5
    /// (avatar.cpp:237) — deliberately worse than any real match can score, since intensity is in
    /// [0, 1]. So neutral is the safety net and never beats a genuine hit.
    /// </remarks>
    private int FindNearestBodySimple(Emotion emotion)
    {
        double intensityOfNearest = 2.0;
        int nearestI = -1;
        int n = Torsos.Count;

        for (int i = 0; i < n; i++)
        {
            int index = (LastTorso + 1 + i) % n;   // start search from index after last body used
            var row = Torsos[index];
            if (row.Emotion > GestureFence) continue;

            double thisAngle = Math.Abs(AngleUtil.SubtractAngles(row.Emotion, emotion.EmotionValue));
            bool isFirstNeutral = row.Emotion == Em.Neutral && row.Intensity == 0.0f && nearestI == -1;

            if (thisAngle < EmotionGate || isFirstNeutral)
            {
                double deltaI = isFirstNeutral && emotion.Intensity > 0.0f
                    ? 1.5                                             // less powerful than any correct match
                    : Math.Abs(emotion.Intensity - row.Intensity);
                if (deltaI < intensityOfNearest)
                {
                    intensityOfNearest = deltaI;
                    nearestI = index;
                }
            }
        }
        return nearestI;
    }

    /// <summary>
    /// The next neutral row after <paramref name="last"/>, or row 0 if the avatar has none.
    /// Port of SetTorsoNeutral / SetFaceNeutral / SetBodyNeutral (avatar.cpp:415, 428, 441),
    /// which are the same function three times.
    /// </summary>
    /// <remarks>
    /// Neutral is (angle 0, intensity 0) — note it is indistinguishable from Happy by angle alone,
    /// so the zero intensity is load-bearing. Round-robined here too, for the same anti-repeat reason.
    /// </remarks>
    private static int FindNeutral(IReadOnlyList<IPoseRecord> rows, int last)
    {
        int n = rows.Count;
        if (n == 0) return -1;

        int c = last;
        for (int i = 0; i < n; i++)
        {
            c = (c + 1) % n;
            if (c < 0) c += n;
            if (rows[c].Emotion == Em.Neutral && rows[c].Intensity == 0.0f)
                return c;
        }
        return 0;   // Oh well, just set it to first
    }
}
