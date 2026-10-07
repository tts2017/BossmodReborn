using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using BossMod;
using AID = BossMod.NIN.AID;
using SID = BossMod.NIN.SID;

namespace XanTimelineHarness;

// Measures, from recorded replays, the client behaviour the NIN simulation has to reproduce: how long after the press a
// target debuff appears (Kunai's Bane, Dokumori), which presses end which self statuses (Shadow Walker, Higi, Meisui, Raiju
// Ready), when Ten Chi Jin grants Tenri Jindo Ready, and the opening sequence of every ninja in the replay. Any ninja in the
// party counts - the replays belong to the user, the ninjas usually do not.
internal static class NinReplayScan
{
    private static readonly uint[] MeleeWeaponskills = [(uint)AID.SpinningEdge, (uint)AID.GustSlash, (uint)AID.AeolianEdge, (uint)AID.ArmorCrush, (uint)AID.DeathBlossom, (uint)AID.HakkeMujinsatsu];

    public static int Run(IReadOnlyList<string> paths)
    {
        // NIN_DAMAGE_CSV=<file>: one row per damage hit of every ninja, with the press time relative to that ninja's Kunai's Bane on
        // the same target, so whether a hit got the 10% can be told from its damage instead of assumed
        var damagePath = Environment.GetEnvironmentVariable("NIN_DAMAGE_CSV");
        using var damageCsv = string.IsNullOrEmpty(damagePath) ? null : new System.IO.StreamWriter(damagePath);
        damageCsv?.WriteLine("replay,nin,action,press,land,since_kunai,since_doku,value,crit,dh,meisui,kassatsu,potion,hit_index,targets");
        var samples = new Dictionary<string, List<float>>();
        void Add(string key, float value)
        {
            if (!samples.TryGetValue(key, out var list))
                samples[key] = list = [];
            list.Add(value);
        }

        foreach (var path in paths)
        {
            var progress = 0f;
            Replay replay;
            try
            {
                replay = ReplayParserLog.Parse(path, ref progress, CancellationToken.None);
            }
            catch (Exception ex)
            {
                Console.WriteLine($"skip {path}: {ex.Message}");
                continue;
            }

            var ninjas = replay.Actions.Where(a => a.ID.Type == ActionType.Spell && a.ID.ID is (uint)AID.KunaisBane or (uint)AID.Kassatsu or (uint)AID.TenChiJin or (uint)AID.Dokumori)
                .Select(a => a.Source).Distinct().ToList();
            if (ninjas.Count == 0)
                continue;
            Console.WriteLine($"replay {System.IO.Path.GetFileName(path)} ninjas={ninjas.Count}");

            foreach (var nin in ninjas)
            {
                var actions = replay.Actions.Where(a => a.Source == nin && a.ID.Type == ActionType.Spell).OrderBy(a => a.Timestamp).ToList();
                var selfStatuses = replay.Statuses.Where(s => s.Target == nin).ToList();
                var appliedStatuses = replay.Statuses.Where(s => s.Source == nin && s.Target != nin).ToList();

                Replay.Status? ActiveSelf(uint sid, DateTime t) => selfStatuses.FirstOrDefault(s => s.ID == sid && s.Time.Start <= t && s.Time.End > t);

                foreach (var a in actions)
                {
                    var t = a.Timestamp;
                    var id = (AID)a.ID.ID;
                    switch (id)
                    {
                        case AID.KunaisBane:
                        case AID.Dokumori:
                        {
                            var sid = id == AID.KunaisBane ? (uint)SID.KunaisBane : (uint)SID.Dokumori;
                            var applied = appliedStatuses.Where(s => s.ID == sid && s.Time.Start >= t.AddSeconds(-0.05) && s.Time.Start <= t.AddSeconds(4)).OrderBy(s => s.Time.Start).FirstOrDefault();
                            if (applied != null)
                            {
                                Add($"{id}.debuff_delay", (float)(applied.Time.Start - t).TotalSeconds);
                                Add($"{id}.debuff_duration", applied.InitialDuration);
                                // when the status record ends relative to the press (the record only exists while the status is on the target)
                                var end = appliedStatuses.Where(s => s.ID == sid && s.Target == applied.Target && s.Time.Start >= applied.Time.Start && s.Time.Start <= t.AddSeconds(4)).Max(s => s.Time.End);
                                if (end < t.AddSeconds(60))
                                    Add($"{id}.debuff_end_after_press", (float)(end - t).TotalSeconds);
                            }
                            var confirm = a.Targets.FirstOrDefault()?.ConfirmationTarget ?? default;
                            if (confirm != default)
                                Add($"{id}.confirm_delay", (float)(confirm - t).TotalSeconds);
                            if (id == AID.KunaisBane)
                            {
                                var sw = ActiveSelf((uint)SID.ShadowWalker, t.AddSeconds(-0.05));
                                if (sw != null)
                                    Add("KunaisBane.shadow_walker_ended_within_1s", sw.Time.End <= t.AddSeconds(1) ? 1 : 0);
                                var hidden = ActiveSelf((uint)SID.Hidden, t.AddSeconds(-0.05));
                                if (hidden != null)
                                    Add("KunaisBane.hidden_ended_within_1s", hidden.Time.End <= t.AddSeconds(1) ? 1 : 0);
                            }
                            break;
                        }
                        case AID.Meisui:
                        {
                            var sw = ActiveSelf((uint)SID.ShadowWalker, t.AddSeconds(-0.05));
                            if (sw != null)
                                Add("Meisui.shadow_walker_ended_within_1s", sw.Time.End <= t.AddSeconds(1) ? 1 : 0);
                            var meisui = selfStatuses.Where(s => s.ID == (uint)SID.Meisui && s.Time.Start >= t.AddSeconds(-0.05) && s.Time.Start <= t.AddSeconds(2)).FirstOrDefault();
                            if (meisui != null)
                                Add("Meisui.buff_duration", meisui.InitialDuration);
                            break;
                        }
                        case AID.TenChiJin:
                        {
                            var tenri = selfStatuses.Where(s => s.ID == (uint)SID.TenriJindoReady && s.Time.Start >= t.AddSeconds(-0.05) && s.Time.Start <= t.AddSeconds(8)).OrderBy(s => s.Time.Start).FirstOrDefault();
                            if (tenri != null)
                                Add("TenChiJin.tenri_ready_delay", (float)(tenri.Time.Start - t).TotalSeconds);
                            var tcj = selfStatuses.Where(s => s.ID == (uint)SID.TenChiJin && s.Time.Start >= t.AddSeconds(-0.05) && s.Time.Start <= t.AddSeconds(2)).FirstOrDefault();
                            if (tcj != null)
                                Add("TenChiJin.status_duration", tcj.InitialDuration);
                            break;
                        }
                        case AID.ZeshoMeppo:
                        case AID.DeathfrogMedium:
                        {
                            var higi = ActiveSelf((uint)SID.Higi, t.AddSeconds(-0.05));
                            if (higi != null)
                                Add($"{id}.higi_ended_within_1s", higi.Time.End <= t.AddSeconds(1) ? 1 : 0);
                            var meisui = ActiveSelf((uint)SID.Meisui, t.AddSeconds(-0.05));
                            if (meisui != null)
                                Add($"{id}.meisui_ended_within_1s", meisui.Time.End <= t.AddSeconds(1) ? 1 : 0);
                            break;
                        }
                        case AID.Bhavacakra:
                        case AID.HellfrogMedium:
                        {
                            var meisui = ActiveSelf((uint)SID.Meisui, t.AddSeconds(-0.05));
                            if (meisui != null)
                                Add($"{id}.meisui_ended_within_1s", meisui.Time.End <= t.AddSeconds(1) ? 1 : 0);
                            break;
                        }
                        case AID.PhantomKamaitachi:
                        {
                            // the shadow executes the damaging action itself; its cast event is when target debuffs are checked
                            var pet = replay.Actions.Where(p => p.Source.OwnerID == nin.InstanceID && p.Timestamp >= t && p.Timestamp <= t.AddSeconds(4)).OrderBy(p => p.Timestamp).FirstOrDefault();
                            if (pet != null)
                            {
                                Add($"PhantomKamaitachi.pet_action_delay({pet.ID.ID})", (float)(pet.Timestamp - t).TotalSeconds);
                                var petConfirm = pet.Targets.FirstOrDefault()?.ConfirmationTarget ?? default;
                                if (petConfirm != default)
                                    Add("PhantomKamaitachi.pet_confirm_delay", (float)(petConfirm - t).TotalSeconds);
                            }
                            var bunshin = ActiveSelf((uint)SID.Bunshin, t.AddSeconds(-0.05));
                            if (bunshin != null)
                            {
                                // a stack change shows up as a new status record (same id, lower extra) starting right after the press
                                var next = selfStatuses.Where(s => s.ID == (uint)SID.Bunshin && s.Time.Start > t.AddSeconds(-0.05) && s.Time.Start <= t.AddSeconds(1.5)).FirstOrDefault();
                                Add("PhantomKamaitachi.bunshin_stack_changed", next != null ? 1 : 0);
                            }
                            break;
                        }
                        case AID.Kassatsu:
                        {
                            var k = selfStatuses.Where(s => s.ID == (uint)SID.Kassatsu && s.Time.Start >= t.AddSeconds(-0.05) && s.Time.Start <= t.AddSeconds(2)).FirstOrDefault();
                            if (k != null)
                                Add("Kassatsu.status_duration", k.InitialDuration);
                            break;
                        }
                    }

                    if (MeleeWeaponskills.Contains(a.ID.ID))
                    {
                        var raiju = ActiveSelf((uint)SID.RaijuReady, t.AddSeconds(-0.05));
                        if (raiju != null)
                            Add("melee_ws.raiju_ended_within_1s", raiju.Time.End <= t.AddSeconds(1) ? 1 : 0);
                        var bunshin = ActiveSelf((uint)SID.Bunshin, t.AddSeconds(-0.05));
                        if (bunshin != null)
                        {
                            var next = selfStatuses.Where(s => s.ID == (uint)SID.Bunshin && s.Time.Start > t.AddSeconds(-0.05) && s.Time.Start <= t.AddSeconds(1.5)).FirstOrDefault();
                            Add("melee_ws.bunshin_stack_changed", next != null || bunshin.Time.End <= t.AddSeconds(1.5) ? 1 : 0);
                        }
                    }
                    if (a.ID.ID is (uint)AID.FleetingRaiju or (uint)AID.ForkedRaiju)
                    {
                        var bunshin = ActiveSelf((uint)SID.Bunshin, t.AddSeconds(-0.05));
                        if (bunshin != null)
                        {
                            var next = selfStatuses.Where(s => s.ID == (uint)SID.Bunshin && s.Time.Start > t.AddSeconds(-0.05) && s.Time.Start <= t.AddSeconds(1.5)).FirstOrDefault();
                            Add("raiju.bunshin_stack_changed", next != null || bunshin.Time.End <= t.AddSeconds(1.5) ? 1 : 0);
                        }
                    }
                }

                if (damageCsv != null)
                    DumpDamage(damageCsv, System.IO.Path.GetFileName(path), nin, actions, selfStatuses, appliedStatuses);

                // opening sequence per encounter: every ninja action in the first 30s after the pull
                foreach (var enc in replay.Encounters)
                {
                    var pull = enc.Time.Start;
                    var opener = actions.Where(a => a.Timestamp >= pull.AddSeconds(-12) && a.Timestamp <= pull.AddSeconds(30)).ToList();
                    if (opener.Count < 8)
                        continue;
                    Console.WriteLine($"  opener enc={enc.OID:X} zone={enc.Zone} nin={nin.InstanceID:X}: " + string.Join(' ', opener.Select(a => FormattableString.Invariant($"{(a.Timestamp - pull).TotalSeconds:f2}:{Short((AID)a.ID.ID)}"))));
                }
            }
        }

        Console.WriteLine("summary:");
        foreach (var (key, list) in samples.OrderBy(kv => kv.Key))
        {
            list.Sort();
            var median = list[list.Count / 2];
            Console.WriteLine(FormattableString.Invariant($"  {key}: n={list.Count} min={list[0]:f3} median={median:f3} max={list[^1]:f3} mean={list.Average():f3}"));
        }
        return 0;
    }

    private static void DumpDamage(System.IO.StreamWriter csv, string replay, Replay.Participant nin, List<Replay.Action> actions, List<Replay.Status> selfStatuses, List<Replay.Status> appliedStatuses)
    {
        var kunais = actions.Where(a => a.ID.ID is (uint)AID.KunaisBane or (uint)AID.TrickAttack).ToList();
        var dokumoris = actions.Where(a => a.ID.ID is (uint)AID.Dokumori or (uint)AID.Mug).ToList();
        bool SelfActive(uint sid, DateTime t) => selfStatuses.Any(s => s.ID == sid && s.Time.Start <= t && s.Time.End > t);
        foreach (var a in actions)
        {
            var t = a.Timestamp;
            var targetIndex = 0;
            foreach (var target in a.Targets)
            {
                var kunai = kunais.Where(k => k.MainTarget == target.Target && k.Timestamp <= t.AddSeconds(3)).Select(k => (float)(t - k.Timestamp).TotalSeconds).Where(dt => dt >= -3 && dt <= 25).DefaultIfEmpty(float.NaN).OrderBy(dt => MathF.Abs(dt)).First();
                var doku = dokumoris.Where(k => k.Timestamp <= t.AddSeconds(3)).Select(k => (float)(t - k.Timestamp).TotalSeconds).Where(dt => dt >= -3 && dt <= 30).DefaultIfEmpty(float.NaN).OrderBy(dt => MathF.Abs(dt)).First();
                var land = target.ConfirmationTarget != default ? (float)(target.ConfirmationTarget - t).TotalSeconds : float.NaN;
                var hit = 0;
                foreach (var e in target.Effects.ValidEffects())
                {
                    if (e.Type != ActionEffectType.Damage || e.FromTarget)
                        continue;
                    var crit = (e.Param0 & 0x20) != 0;
                    var dh = (e.Param0 & 0x40) != 0;
                    csv.WriteLine(string.Create(CultureInfo.InvariantCulture,
                        $"{replay},{nin.InstanceID:X},{(AID)a.ID.ID},{t:HH:mm:ss.fff},{land:f3},{kunai:f3},{doku:f3},{e.DamageHealValue},{(crit ? 1 : 0)},{(dh ? 1 : 0)},{(SelfActive((uint)SID.Meisui, t) ? 1 : 0)},{(SelfActive((uint)SID.Kassatsu, t) ? 1 : 0)},{(SelfActive(49, t) ? 1 : 0)},{targetIndex}.{hit++},{a.Targets.Count}"));
                }
                ++targetIndex;
            }
        }
    }

    private static string Short(AID aid) => aid switch
    {
        AID.SpinningEdge => "SE",
        AID.GustSlash => "GS",
        AID.AeolianEdge => "AE",
        AID.ArmorCrush => "AC",
        AID.Ten1 or AID.Ten2 => "ten",
        AID.Chi1 or AID.Chi2 => "chi",
        AID.Jin1 or AID.Jin2 => "jin",
        AID.KunaisBane => "KUNAI",
        AID.Dokumori => "DOKU",
        AID.Kassatsu => "KASS",
        AID.HyoshoRanryu => "HYOSHO",
        AID.Raiton => "RAITON",
        AID.Suiton => "SUITON",
        AID.FleetingRaiju => "RAIJU",
        AID.ForkedRaiju => "fRAIJU",
        AID.PhantomKamaitachi => "PK",
        AID.Bunshin => "BUNSHIN",
        AID.TenChiJin => "TCJ",
        AID.Meisui => "MEISUI",
        AID.TenriJindo => "TENRI",
        AID.ZeshoMeppo => "ZESHO",
        AID.Bhavacakra => "BHAVA",
        AID.DreamWithinADream => "DWAD",
        _ => aid.ToString()
    };
}
