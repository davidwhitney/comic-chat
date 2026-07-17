using ComicChat.Core.Geometry;

namespace ComicChat.Core.Avatars;

/// <summary>
/// The emotion wheel's spokes and the gesture sentinels. Port of avatar.h:328-346.
/// </summary>
/// <remarks>
/// Expressions are 8 evenly spaced angles around a circle; gestures are magic values
/// far outside [0, 2*PI). That encoding is load-bearing — matching code branches on
/// <c>emotion &lt;= 2*PI</c> to choose between the angular wheel metric and exact
/// equality (avatar.cpp:304, 332), so it is reproduced rather than tidied into an enum.
/// </remarks>
public static class Em
{
    public const int NEmotions = 8;

    public const float Happy = (float)(0 * 2 * Math.PI / 8);
    public const float Coy = (float)(1 * 2 * Math.PI / 8);
    public const float Bored = (float)(2 * 2 * Math.PI / 8);
    public const float Scared = (float)(3 * 2 * Math.PI / 8);
    public const float Sad = (float)(4 * 2 * Math.PI / 8);
    public const float Angry = (float)(5 * 2 * Math.PI / 8);
    public const float Shout = (float)(6 * 2 * Math.PI / 8);
    public const float Laugh = (float)(7 * 2 * Math.PI / 8);

    /// <summary>Note: numerically identical to <see cref="Happy"/>. Intensity 0 is what marks it neutral.</summary>
    public const float Neutral = 0.0f;

    public const float Wave = 1001.0f;
    public const float PointOther = 1002.0f;
    public const float PointSelf = 1003.0f;
    public const float DoublePoint = 1004.0f;
    public const float Shrug = 1005.0f;
    public const float ThreeQRWalk = 1006.0f;
    public const float SideWalk = 1007.0f;
    public const float ThreeQFWalk = 1008.0f;

    /// <summary>True if this value is a wheel angle rather than a gesture sentinel (avatar.cpp:304).</summary>
    public static bool IsWheelEmotion(float emotion) => emotion <= (float)AngleUtil.TwoPi;

    public static bool IsGesture(float emotion) => !IsWheelEmotion(emotion);

    /// <summary>
    /// The wire/file emotion table. Port of emFloats[] (avatario.cpp:45).
    /// The index into this array is what travels on IRC and what .avb pose records store.
    /// </summary>
    public static readonly float[] EmFloats =
    [
        0.0f,        // 0 — none
        Happy,       // 1
        Coy,         // 2
        Bored,       // 3
        Scared,      // 4
        Sad,         // 5
        Angry,       // 6
        Shout,       // 7
        Laugh,       // 8
        Neutral,     // 9
        Wave,        // 10
        PointOther,  // 11
        PointSelf,   // 12
        DoublePoint, // 13
        Shrug,       // 14
        ThreeQRWalk, // 15
        SideWalk,    // 16
        ThreeQFWalk, // 17
    ];

    /// <summary>Port of EmotionToFloat (avatario.cpp:89). Out-of-range yields 0.0.</summary>
    public static float EmotionToFloat(int index) =>
        index < 0 || index >= EmFloats.Length ? 0.0f : EmFloats[index];

    /// <summary>
    /// Reverse lookup by exact float equality. Port of the search in EmotionToBytes
    /// (avatario.cpp:73-77), which defaults to 9 (neutral) on a miss.
    /// </summary>
    /// <remarks>
    /// The scan starts at 1, so an emotion of 0.0 resolves to index 1 (Happy) rather
    /// than 9 (Neutral) — Happy and Neutral share the angle 0.0. Faithful to the original;
    /// intensity is what disambiguates them downstream.
    /// </remarks>
    public static byte FloatToEmotionIndex(float emotion)
    {
        for (int i = 1; i < EmFloats.Length; i++)
            if (EmFloats[i] == emotion)
                return (byte)i;
        return 9;
    }
}

/// <summary>
/// A point on the emotion wheel: an angle plus a magnitude. Port of CEmotion (avatar.h:59).
/// </summary>
/// <remarks>Fields are float, not double — the file and wire formats compare them by exact equality.</remarks>
public struct Emotion(double intensity, double emotion)
{
    public float Intensity = (float)intensity;
    public float EmotionValue = (float)emotion;

    public void Set(double intensity, double emotion)
    {
        Intensity = (float)intensity;
        EmotionValue = (float)emotion;
    }

    public readonly bool IsGesture => Em.IsGesture(EmotionValue);

    public override readonly string ToString() => $"Emotion(v={EmotionValue:F3}, i={Intensity:F2})";
}

/// <summary>How <see cref="EmotionOpts.Add"/> resolves a collision on the same emotion.</summary>
public enum EmotionOptFlags
{
    /// <summary>Keep the higher priority. The default (avatar.h:68).</summary>
    OverrideByPriority = 1,

    /// <summary>Sum the priorities (avatar.h:69).</summary>
    AddPriority = 2,
}

/// <summary>
/// A small fixed set of candidate emotions with priorities, produced by the text rules
/// and consumed by pose resolution. Port of CEmotionOpts (avatar.h:72).
/// </summary>
public sealed class EmotionOpts
{
    public const int MaxEmOpts = 10;

    public int Count;
    public readonly Emotion[] Emotions = new Emotion[MaxEmOpts];
    public readonly byte[] Priorities = new byte[MaxEmOpts];

    public void Reset() => Count = 0;

    /// <summary>
    /// Port of CEmotionOpts::Add (avatar.cpp:725). Dedupes on exact emotion equality;
    /// silently drops anything past <see cref="MaxEmOpts"/>, as the original does.
    /// </summary>
    public void Add(double emotion, double intensity, int priority,
                    EmotionOptFlags flags = EmotionOptFlags.OverrideByPriority)
    {
        var em = (float)emotion;

        for (int i = 0; i < Count; i++)
        {
            if (Emotions[i].EmotionValue != em) continue;

            if (flags == EmotionOptFlags.AddPriority)
                Priorities[i] = (byte)Math.Min(255, Priorities[i] + priority);
            else if (priority > Priorities[i])
                Priorities[i] = (byte)priority;
            return;
        }

        if (Count >= MaxEmOpts) return;

        Emotions[Count] = new Emotion(intensity, emotion);
        Priorities[Count] = (byte)priority;
        Count++;
    }

    public void Add(int emotion, double intensity, int priority,
                    EmotionOptFlags flags = EmotionOptFlags.OverrideByPriority) =>
        Add((double)emotion, intensity, priority, flags);
}
