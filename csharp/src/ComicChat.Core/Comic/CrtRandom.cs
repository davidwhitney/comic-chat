namespace ComicChat.Core.Comic;

/// <summary>
/// A bit-exact reimplementation of the Microsoft CRT's <c>srand</c>/<c>rand</c>.
/// </summary>
/// <remarks>
/// The engine reseeds this per panel (<c>srand(m_seed)</c>, panel.cpp:867) so that a panel
/// always lays out "the same random way" — which is what lets the whole comic be replayed
/// deterministically from history when the window is resized (ExecuteHistory(HM_RELOAD),
/// pageview.cpp:1113).
///
/// System.Random would satisfy the determinism requirement but would produce a *different*
/// comic from the original for the same seed. Since balloon widths and x-placement are drawn
/// from this stream (GetCloudEstimate, panel.cpp:899/916), reproducing the exact LCG keeps
/// our output faithful to Comic Chat's.
/// </remarks>
public sealed class CrtRandom
{
    public const int RandMax = 0x7fff;

    private uint _holdrand;

    public CrtRandom(uint seed = 1) => Srand(seed);

    /// <summary>Port of the CRT's srand.</summary>
    public void Srand(uint seed) => _holdrand = seed;

    /// <summary>Port of the CRT's rand: a 32-bit LCG, returning bits 16..30.</summary>
    public int Rand()
    {
        _holdrand = unchecked(_holdrand * 214013u + 2531011u);
        return (int)((_holdrand >> 16) & 0x7fff);
    }

    /// <summary>Port of randfloat (balloon.cpp:428) — rand()/RAND_MAX, so inclusive of 1.0.</summary>
    public double RandFloat() => (double)Rand() / RandMax;
}
