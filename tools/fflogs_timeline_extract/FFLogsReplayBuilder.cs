using System.Text.Json;
using BossMod;

namespace FFLogsTimelineExtract;

// One fight of a report. Times are milliseconds: ReportStartMs is a unix epoch time, StartMs/EndMs and event timestamps are relative to it.
public sealed record FFLogsFight(string Code, int Id, long ReportStartMs, double StartMs, double EndMs, ushort Zone, bool Kill)
{
    public int EncounterID { get; init; }

    public DateTime ToTime(double ms) => DateTime.UnixEpoch.AddMilliseconds(ReportStartMs + ms);
}

// An NPC of the report's master data; GameId is the BMR OID and IsBoss comes from subType == "Boss".
public sealed record FFLogsActor(int Id, uint GameId, string Name, bool IsBoss);

// Turns the enemy event streams of one fight into a synthetic Replay for ReplayTimelineExtractor. Pure: no network, no game data.
public static class FFLogsReplayBuilder
{
    // The extractor calls anything with at least half of the pull's biggest max HP a boss; these two values make the Boss flag decide.
    private const uint BossMaxHP = 1_000_000;
    private const uint TrashMaxHP = 10_000;
    private const float CastAnimationLock = 0.6f;

    // events: the raw JSON arrays returned by the Casts, Deaths, targetabilityupdate and friendly DamageDone queries.
    // FFLogs reports targetability changes only, so an enemy that was never targetable (a helper actor that only casts) has no
    // update at all; the player damage stream tells the attackable enemies apart: whatever never took a hit starts untargetable.
    public static Replay Build(FFLogsFight fight, IReadOnlyList<FFLogsActor> actors, JsonElement casts, JsonElement deaths, JsonElement targetability, JsonElement damageDone)
    {
        var byId = actors.ToDictionary(a => a.Id);
        // (actorID, instance) -> participant, with the working state needed to close the existence at the end.
        Dictionary<(int Actor, int Instance), Entry> entries = [];
        var replay = new Replay();

        // Everything is applied in time order so a participant's history lists stay sorted and its first event opens its existence.
        // Damage taken counts as an event too: an enemy exists at least while the players are hitting it.
        var damage = Rows(damageDone, true).ToList();
        var damaged = damage.Select(e => (e.Actor, e.Instance)).ToHashSet();
        var events = Rows(casts, false).Concat(Rows(deaths, true)).Concat(Rows(targetability, true)).Concat(damage)
            .Where(e => byId.ContainsKey(e.Actor))
            .OrderBy(e => e.Timestamp).ThenBy(e => e.Order).ToList();

        Entry EntryFor(EventRow e)
        {
            if (!entries.TryGetValue((e.Actor, e.Instance), out var entry))
            {
                var actor = byId[e.Actor];
                var maxHP = actor.IsBoss ? BossMaxHP : TrashMaxHP;
                // A boss is always attackable: it must anchor the pull even if the damage stream is missing for it.
                var attackable = actor.IsBoss || damaged.Contains((e.Actor, e.Instance));
                var start = fight.ToTime(e.Timestamp);
                var participant = new Replay.Participant(((ulong)(uint)e.Actor << 8) | (uint)(e.Instance & 0xFF))
                {
                    OID = actor.GameId,
                    Type = ActorType.Enemy,
                    ZoneID = fight.Zone,
                    HasAnyActions = true,
                    IsTargetOfAnyActions = attackable,
                };
                participant.NameHistory.Add(start, (actor.Name, 0u));
                participant.TargetableHistory.Add(start, attackable);
                participant.HPMPHistory.Add(start, new(maxHP, maxHP, 0, 0, 0));
                entry = new(participant, start);
                entries[(e.Actor, e.Instance)] = entry;
                replay.Participants.Add(participant);
            }
            return entry;
        }

        foreach (var e in events)
        {
            var entry = EntryFor(e);
            var p = entry.Participant;
            var time = fight.ToTime(e.Timestamp);
            entry.LastEvent = time;
            switch (e.Type)
            {
                case "begincast":
                {
                    var seconds = (float)(e.DurationMs / 1000.0);
                    p.Casts.Add(new(new ActionID(ActionType.Spell, e.AbilityId), seconds, null, default, default, false) { Time = new(time, time.AddMilliseconds(e.DurationMs)) });
                    break;
                }
                case "cast":
                    replay.Actions.Add(new(new ActionID(ActionType.Spell, e.AbilityId), time, p, null, default, CastAnimationLock, 0, 0, default));
                    break;
                case "death":
                    // Only the first death closes the existence; a later one for the same instance is ignored.
                    if (entry.Death == null)
                    {
                        entry.Death = time;
                        p.DeadHistory[time] = true;
                    }
                    break;
                case "targetabilityupdate":
                    p.TargetableHistory[time] = e.Targetable;
                    break;
            }
        }

        // FFLogs has no despawn: an enemy that never dies (an add removed at the boss kill, a helper actor that casts once) is
        // closed at its last event. Letting it live until the fight end would bridge every later boss into one pull cluster.
        foreach (var entry in entries.Values)
        {
            var end = entry.Death ?? entry.LastEvent;
            if (end < entry.Start)
                end = entry.Start;
            entry.Participant.WorldExistence.Add(new(entry.Start, end));
            entry.Participant.EffectiveExistence = new(entry.Start, end);
        }
        replay.Actions.Sort((a, b) => a.Timestamp.CompareTo(b.Timestamp));
        return replay;
    }

    private sealed class Entry(Replay.Participant participant, DateTime start)
    {
        public Replay.Participant Participant { get; } = participant;
        public DateTime Start { get; } = start;
        public DateTime LastEvent { get; set; } = start;
        public DateTime? Death { get; set; }
    }

    private sealed record EventRow(double Timestamp, int Order, string Type, int Actor, int Instance, uint AbilityId, double DurationMs, bool Targetable);

    // Cast events name the acting enemy by sourceID; death and targetability events name the affected one by targetID.
    private static IEnumerable<EventRow> Rows(JsonElement array, bool byTarget)
    {
        if (array.ValueKind != JsonValueKind.Array)
            yield break;
        var order = 0;
        foreach (var e in array.EnumerateArray())
        {
            var type = Str(e, "type");
            if (type == null)
                continue;
            var actorField = byTarget ? "targetID" : "sourceID";
            var instanceField = byTarget ? "targetInstance" : "sourceInstance";
            var actor = Int(e, actorField) ?? (byTarget ? Int(e, "sourceID") : null);
            if (actor == null)
                continue;
            var instance = Int(e, instanceField) ?? (byTarget && !e.TryGetProperty("targetID", out _) ? Int(e, "sourceInstance") : null) ?? 1;
            yield return new(Dbl(e, "timestamp") ?? 0, order++, type, actor.Value, instance, (uint)(Int64(e, "abilityGameID") ?? 0), Dbl(e, "duration") ?? 0, (Int(e, "targetable") ?? 1) != 0);
        }
    }

    private static string? Str(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static int? Int(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : null;
    private static long? Int64(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt64() : null;
    private static double? Dbl(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;
}
