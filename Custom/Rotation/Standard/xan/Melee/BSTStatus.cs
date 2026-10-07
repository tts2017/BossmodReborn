namespace BossMod.Autorotation.xan.Custom;

// BST status IDs the old fork added to upstream BossMod/ActionQueue/Melee/BST.cs (SID enum), kept here so that file stays untouched.
// Values identical to upstream's are repeated so the copied rotation can use this enum as its SID (see the alias in BST.cs).
public enum BSTStatus : uint
{
    None = 0,
    VolantHeart = 4595, // 7s, granted by a Volant instinctual skill (Gale Axe); next clockwise affinity is Rampant
    RampantHeart = 4596, // 7s, Avalanche Axe; next is Durant
    DurantHeart = 4597, // 7s, Mistral Axe; next is Eldritch
    EldritchHeart = 4598, // 7s, Spinning Axe; next is Volant
    Sunstrider = 4599, // 7s, intentional combo finisher / Brutal Rage / Risen Fall; a Moonstalker skill under it completes Universality (L50)
    Moonstalker = 4600, // 7s, intentional combo finisher / Hawkish Talons / Calamity; a Sunstrider skill under it completes Universality (L50)
    OneWithNature = 4601, // granted by summoning a familiar (L18 trait); required by Tempered Release and Borrow
    BeastKinship = 4602,
    VileKinship = 4603,
    CloudKinship = 4604,
    SeedKinship = 4605,
    WaveKinship = 4606,
    ScaleKinship = 4607,
    SoulKinship = 4608,
    AshKinship = 4609,
    Vileskin = 4620,
    Beastskin = 4621,
    SeedsSown = 4622,
    Scaleskin = 4623,
    CapturingInterest = 4624,
    InterestCaptured = 4626,
    WaveringHeart = 4643, // cannot combo with the familiar
    BeastKinship2 = 4644,
    VileKinship2 = 4645,
    CloudKinship2 = 4646,
    SeedKinship2 = 4647,
    WaveKinship2 = 4648,
    ScaleKinship2 = 4649,
    SoulKinship2 = 4650,
    AshKinship2 = 4651,
}
