using BossMod.Autorotation.xan;
using BossMod.SAM;

namespace BossMod.Autorotation.Standard.xan.Utility.Custom;

public class ThirdEye(RotationModuleManager manager, Actor player) : Attackxan<AID, TraitID, ThirdEye.Strategy>(manager, player)
{
    public struct Strategy
    {
        [Track(Actions = [AID.ThirdEye, AID.Tengentsu])]
        public Track<ThirdEyeStrategy> ThirdEye;
    }

    public enum ThirdEyeStrategy
    {
        [Option("Use ~3s before predicted damage", Cooldown = 15, Effect = 4)]
        Automatic,
        [Option("Use 4s before predicted damage", Cooldown = 15, Effect = 4)]
        AutoMax,
        [Option("Don't use")]
        Delay
    }

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("Auto-ThirdEye [Custom]", "Third Eye before incoming damage", "Utility (xan)", "xan", RotationModuleQuality.Excellent, BitMask.Build(Class.SAM), 100, 6).WithStrategies<Strategy>();
    }

    private const uint SIDThirdEye = 1232;

    private AID ThirdEyeAction()
    {
        if (Unlocked(AID.Tengentsu))
            return AID.Tengentsu;

        if (Unlocked(AID.ThirdEye))
            return AID.ThirdEye;

        return default;
    }

    private bool ThirdEyeActive()
        => Player.FindStatus(SIDThirdEye) != null
            || Player.FindStatus(SID.Tengentsu) != null;

    private bool PredictedSelfDamageImminent(AID action, float advance)
        => advance > 0 && Hints.PredictedDamage.Any(x => x.Players[PartyState.PlayerSlot]
            && x.Activation > World.CurrentTime
            && x.Activation <= World.FutureTime(advance)
            && ReadyIn(action) <= (float)(x.Activation - World.CurrentTime).TotalSeconds);

    private bool VariantDamageImminent(AID action, float advance)
    {
        if (advance <= 0 || World.CurrentCFCID is not (868 or 945 or 978 or 1066))
            return false;

        foreach (var enemy in Hints.PotentialTargets)
        {
            var actor = enemy.Actor;
            var cast = actor.CastInfo;
            if (!actor.InCombat || cast == null || !cast.IsSpell() || cast.NPCRemainingTime <= 0 || cast.NPCRemainingTime >= advance)
                continue;

            var actionData = Service.LuminaRow<Lumina.Excel.Sheets.Action>(cast.Action.ID);
            if (actionData == null)
                continue;

            if (actionData.Value.CastType == 1)
            {
                var targetID = cast.TargetID != actor.InstanceID ? cast.TargetID : actor.TargetID;
                if (targetID == Player.InstanceID && ReadyIn(action) <= cast.NPCRemainingTime)
                    return true;
            }

            if (actionData.Value.CastType is 2 or 5 && actionData.Value.EffectRange >= 30 && ReadyIn(action) <= cast.NPCRemainingTime)
                return true;
        }

        return false;
    }

    private bool ShouldUseThirdEyeNow(AID action, float advance)
        => PredictedSelfDamageImminent(action, advance) || VariantDamageImminent(action, advance);

    public override void Exec(in Strategy strategy, AIHints.Enemy? primaryTarget)
    {
        if (Player.FindStatus(SID.Meditate) != null)
            return;

        if (strategy.ThirdEye.Value == ThirdEyeStrategy.Delay)
            return;

        var action = ThirdEyeAction();
        if (action == default || ThirdEyeActive())
            return;

        var advance = strategy.ThirdEye.Value switch
        {
            ThirdEyeStrategy.Automatic => 3,
            ThirdEyeStrategy.AutoMax => 4,
            _ => 0
        };

        if (ShouldUseThirdEyeNow(action, advance))
            PushOGCD(action, Player, -100);
    }
}
