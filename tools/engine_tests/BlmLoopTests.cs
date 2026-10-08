using BossMod.Autorotation.Engine;
using BossMod.Autorotation.Engine.Jobs;
using Xunit;

namespace EngineTests;

// BLM level 100 (the search): the loop the search may build is the guide's (docs/rebuild/engine-design.md section 31) - Fire III opener,
// Astral Fire III spent before Despair / Manafont / the Transpose out, Blizzard III -> Blizzard IV -> Paradox before Fire III or the
// Transpose out of Umbral Ice III, Firestarter kept for the Fire III after a Transpose. Below 100 the definition is unchanged.
public sealed class BlmLoopTests
{
    private static readonly EngineTimeline Open = EngineTimeline.Open();

    private static EngineState State(JobDefinition job, int fire = 0, int ice = 0, int mp = 10000, int hearts = 0, int paradox = 0)
    {
        var s = EngineState.Create(job);
        s.Targets = 1;
        s.Gauges[job.GaugeIndex(BlmDefinition.AstralFire)] = (short)fire;
        s.Gauges[job.GaugeIndex(BlmDefinition.UmbralIce)] = (short)ice;
        s.Gauges[job.GaugeIndex(BlmDefinition.MP)] = (short)mp;
        s.Gauges[job.GaugeIndex(BlmDefinition.Hearts)] = (short)hearts;
        s.Gauges[job.GaugeIndex(BlmDefinition.Paradox)] = (short)paradox;
        return s;
    }

    private static bool Legal(JobDefinition job, in EngineState s, string skill) => Simulator.IsLegal(job, s, Open, job.Skills[job.SkillIndex(skill)]);

    [Fact]
    public void PullWithFullMpOpensOnFire()
    {
        var job = BlmDefinition.Build(2.5f, 100);
        var s = State(job);
        Assert.True(Legal(job, s, "Fire3Cold"));
        Assert.False(Legal(job, s, "Blizzard3Cold"));
        Assert.False(Legal(job, s, "HighBlizzard2Cold"));
        // below 100 (the synced rules' legality) the ice opener stays legal
        var job90 = BlmDefinition.Build(2.5f, 90);
        Assert.True(Legal(job90, State(job90), "Blizzard3Cold"));
    }

    [Fact]
    public void DespairManafontAndTransposeCloseAnAstralFireThreePhase()
    {
        var job = BlmDefinition.Build(2.5f, 100);
        Assert.False(Legal(job, State(job, fire: 1, mp: 3000), "Despair"));   // Astral Fire I (after a Transpose): no MP dump
        Assert.False(Legal(job, State(job, fire: 3, mp: 6000), "Despair"));   // Fire IV first
        Assert.True(Legal(job, State(job, fire: 3, mp: 2000), "Despair"));
        Assert.False(Legal(job, State(job, fire: 3, mp: 6000), "Manafont"));
        Assert.True(Legal(job, State(job, fire: 3, mp: 0), "Manafont"));
        Assert.False(Legal(job, State(job, fire: 1, mp: 0), "Transpose"));
        Assert.False(Legal(job, State(job, fire: 3, mp: 2000), "Transpose"));
        Assert.True(Legal(job, State(job, fire: 3, mp: 0), "Transpose"));
        Assert.False(Legal(job, State(job, fire: 1, mp: 0), "Blizzard3LowFire"));
    }

    [Fact]
    public void UmbralIceThreeIsFinishedBeforeLeavingIt()
    {
        var job = BlmDefinition.Build(2.5f, 100);
        // Blizzard IV in Umbral Ice III only (the Umbral Ice I of a double Transpose is spent on instants)
        Assert.False(Legal(job, State(job, ice: 1), "Blizzard4"));
        Assert.True(Legal(job, State(job, ice: 3), "Blizzard4"));
        // out of Umbral Ice III: hearts and the ice Paradox first, for the Transpose and the hard Fire III alike
        Assert.False(Legal(job, State(job, ice: 3, hearts: 0), "TransposeIce3"));
        Assert.False(Legal(job, State(job, ice: 3, hearts: 3, paradox: 1), "TransposeIce3"));
        Assert.True(Legal(job, State(job, ice: 3, hearts: 3), "TransposeIce3"));
        Assert.False(Legal(job, State(job, ice: 3, hearts: 3), "TransposeIce"));
        Assert.True(Legal(job, State(job, ice: 1), "TransposeIce"));
        Assert.False(Legal(job, State(job, ice: 3, hearts: 0), "Fire3"));
        Assert.True(Legal(job, State(job, ice: 3, hearts: 3), "Fire3"));
    }

    [Fact]
    public void FirestarterIsKeptForTheFireThreeAfterATranspose()
    {
        var job = BlmDefinition.Build(2.5f, 100);
        var fs = job.StatusIndex(BlmDefinition.Firestarter);
        var inFire = State(job, fire: 3, mp: 4000);
        inFire.StatusLeft[fs] = 20;
        inFire.StatusStacks[fs] = 1;
        Assert.False(Legal(job, inFire, "Fire3Proc"));
        var afterTranspose = State(job, fire: 1, mp: 0);
        afterTranspose.StatusLeft[fs] = 20;
        afterTranspose.StatusStacks[fs] = 1;
        Assert.True(Legal(job, afterTranspose, "Fire3Proc"));
        // the Transpose out of Umbral Ice III grants Paradox (Simulator: the variant's effect)
        var ice = State(job, ice: 3, hearts: 3);
        Simulator.Execute(job, ref ice, Open, job.Skills[job.SkillIndex("TransposeIce3")], new EvalContext());
        Assert.Equal(1, ice.Gauges[job.GaugeIndex(BlmDefinition.AstralFire)]);
        Assert.Equal(1, ice.Gauges[job.GaugeIndex(BlmDefinition.Paradox)]);
    }
}
