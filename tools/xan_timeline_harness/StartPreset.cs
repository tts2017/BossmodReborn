using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using BossMod;

namespace XanTimelineHarness;

// --start-cooldowns / --start-gauge: a run that starts in combat with cooldowns and gauges partly used and no countdown (a dungeon pull,
// a re-engage after a wipe or between pulls, a mid-fight return). The spec is applied to the simulated client state the modules read
// (the cooldown groups and the gauge struct) before the first frame.
//   --start-cooldowns anchors-<R>      the job's 2-minute burst anchors with R s left (clamped to their recast), the job's shorter burst
//                                      cooldowns at R x recast / 120 (60 s skills at half), everything else up
//   --start-cooldowns <Action>=<s>,... explicit: the job's AID enum names with seconds left (one charge missing on charge skills)
//   --start-gauge 0|50|full|<percent>  the job's main gauge at that fraction of its maximum
// With --party-buffs the party's buffs are likewise on cooldown: they were cast 120 - R s ago (RaidCooldowns learns it from a backdated
// cast, as it saw the cast in game) and are cast again at R.
internal sealed class StartPreset
{
    public required string Spec { get; init; }
    public required IReadOnlyList<(ActionID Action, float Remaining)> Cooldowns { get; init; }
    // seconds until the burst anchors return: the party's buffs are due then as well
    public required float AnchorRemaining { get; init; }

    public static StartPreset Parse(string spec, Class job, int level)
    {
        var cooldowns = new List<(ActionID, float)>();
        float anchorRemaining;
        if (spec.StartsWith("anchors-", StringComparison.OrdinalIgnoreCase))
        {
            anchorRemaining = float.Parse(spec["anchors-".Length..], CultureInfo.InvariantCulture);
            if (anchorRemaining <= 0)
                throw new ArgumentOutOfRangeException(nameof(spec), "--start-cooldowns anchors-<seconds> must be positive.");
            var (anchors, secondary) = JobBurstCooldowns(job);
            foreach (var aid in anchors)
                cooldowns.Add((aid, MathF.Min(anchorRemaining, Recast(aid, level))));
            foreach (var aid in secondary)
                cooldowns.Add((aid, MathF.Min(anchorRemaining * Recast(aid, level) / 120, Recast(aid, level))));
        }
        else
        {
            anchorRemaining = 0;
            foreach (var part in spec.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0)
                    throw new ArgumentException($"--start-cooldowns entry '{part}' is not <Action>=<seconds>.");
                var remaining = float.Parse(part[(eq + 1)..], CultureInfo.InvariantCulture);
                var action = ParseAction(part[..eq], job);
                cooldowns.Add((action, MathF.Min(remaining, Recast(action, level))));
                anchorRemaining = MathF.Max(anchorRemaining, remaining);
            }
        }
        return new() { Spec = spec, Cooldowns = cooldowns, AnchorRemaining = anchorRemaining };
    }

    // 0 / 50 / full or a percentage
    public static float ParseGauge(string spec) => spec.ToLowerInvariant() switch
    {
        "full" or "max" => 1,
        _ => Math.Clamp(float.Parse(spec, CultureInfo.InvariantCulture) / 100, 0, 1),
    };

    // sets the cooldown groups: the same model as the combat states' StartCooldown (total = recast x charges at cap, one charge missing
    // on charge skills with `remaining` seconds to go, the other charges up)
    public void Apply(WorldState world, Actor player)
    {
        foreach (var (action, remaining) in Cooldowns)
        {
            var definition = ActionDefinitions.Instance[action] ?? throw new ArgumentException($"--start-cooldowns: unknown action {action}.");
            var group = definition.ActualMainCooldownGroup(world.Client.DutyActions);
            if (group < 0 || group == ActionDefinitions.GCDGroup)
                continue;
            var recast = Recast(action, player.Level);
            var capCharges = Math.Max(1, definition.MaxChargesAtCap());
            var levelCharges = Math.Clamp(definition.MaxChargesAtLevel(player.Level), 1, capCharges);
            var elapsed = recast * levelCharges - MathF.Min(remaining, recast);
            world.Execute(new ClientState.OpCooldown(false, [(group, new(elapsed, recast * capCharges))]));
        }
    }

    public string Describe() => string.Join(",", Cooldowns.Select(c => FormattableString.Invariant($"{c.Action}={c.Remaining:f1}")));

    // the recast the harness's combat states use (traits the action sheet does not carry)
    private static float Recast(ActionID action, int level)
    {
        var definition = ActionDefinitions.Instance[action] ?? throw new ArgumentException($"--start-cooldowns: unknown action {action}.");
        if (action == ActionID.MakeSpell(BossMod.SAM.AID.HissatsuSenei) || action == ActionID.MakeSpell(BossMod.SAM.AID.HissatsuGuren))
            return level >= 94 ? 60 : definition.Cooldown;
        if (action == ActionID.MakeSpell(BossMod.BLM.AID.Manafont))
            return level >= 84 ? 100 : definition.Cooldown;
        if (action == ActionID.MakeSpell(BossMod.BLM.AID.Swiftcast))
            return level >= 94 ? 40 : definition.Cooldown;
        if (action == ActionID.MakeSpell(BossMod.RPR.AID.Enshroud))
            return 5;
        return definition.Cooldown;
    }

    // the 2-minute burst anchors (R left) and the shorter burst cooldowns (R x recast / 120 left) of each job
    private static (ActionID[] Anchors, ActionID[] Secondary) JobBurstCooldowns(Class job) => job switch
    {
        Class.RPR => ([Spell(BossMod.RPR.AID.ArcaneCircle)], [Spell(BossMod.RPR.AID.Gluttony)]),
        Class.NIN => ([Spell(BossMod.NIN.AID.Dokumori), Spell(BossMod.NIN.AID.TenChiJin), Spell(BossMod.NIN.AID.Meisui)],
                      [Spell(BossMod.NIN.AID.KunaisBane), Spell(BossMod.NIN.AID.Kassatsu), Spell(BossMod.NIN.AID.DreamWithinADream), Spell(BossMod.NIN.AID.Bunshin)]),
        Class.MNK => ([Spell(BossMod.MNK.AID.Brotherhood), Spell(BossMod.MNK.AID.RiddleOfFire)], [Spell(BossMod.MNK.AID.RiddleOfWind)]),
        Class.SAM => ([Spell(BossMod.SAM.AID.Ikishoten)], [Spell(BossMod.SAM.AID.HissatsuSenei)]),
        Class.BLM => ([Spell(BossMod.BLM.AID.LeyLines), Spell(BossMod.BLM.AID.Amplifier), Spell(BossMod.BLM.AID.Manafont)], [Spell(BossMod.BLM.AID.Triplecast)]),
        Class.GNB => ([Spell(BossMod.GNB.AID.NoMercy), Spell(BossMod.GNB.AID.Bloodfest)], [Spell(BossMod.GNB.AID.DoubleDown), Spell(BossMod.GNB.AID.SonicBreak), Spell(BossMod.GNB.AID.BowShock)]),
        Class.PLD => ([Spell(BossMod.PLD.AID.FightOrFlight), Spell(BossMod.PLD.AID.Imperator)], []),
        Class.DRG => ([Spell(BossMod.DRG.AID.LanceCharge), Spell(BossMod.DRG.AID.BattleLitany), Spell(BossMod.DRG.AID.Geirskogul), Spell(BossMod.DRG.AID.DragonfireDive)], []),
        Class.VPR => ([Spell(BossMod.VPR.AID.SerpentsIre)], []),
        Class.MCH => ([Spell(BossMod.MCH.AID.Wildfire), Spell(BossMod.MCH.AID.BarrelStabilizer)], []),
        _ => throw new ArgumentException($"--start-cooldowns anchors-<seconds> has no burst table for {job}."),
    };

    private static ActionID Spell<T>(T aid) where T : Enum => ActionID.MakeSpell(aid);

    private static ActionID ParseAction(string name, Class job)
    {
        var type = job switch
        {
            Class.RPR => typeof(BossMod.RPR.AID),
            Class.NIN => typeof(BossMod.NIN.AID),
            Class.MNK => typeof(BossMod.MNK.AID),
            Class.SAM => typeof(BossMod.SAM.AID),
            Class.BLM => typeof(BossMod.BLM.AID),
            Class.GNB => typeof(BossMod.GNB.AID),
            Class.PLD => typeof(BossMod.PLD.AID),
            Class.DRG => typeof(BossMod.DRG.AID),
            Class.VPR => typeof(BossMod.VPR.AID),
            Class.MCH => typeof(BossMod.MCH.AID),
            _ => throw new ArgumentException($"--start-cooldowns has no action table for {job}."),
        };
        if (!Enum.TryParse(type, name, true, out var value) || value == null)
            throw new ArgumentException($"--start-cooldowns: '{name}' is not a {job} action.");
        return new(ActionType.Spell, Convert.ToUInt32(value, CultureInfo.InvariantCulture));
    }
}
