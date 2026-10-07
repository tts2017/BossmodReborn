namespace BossMod;

// Which action categories the player's own statuses make the client refuse (ActionManager.GetActionStatus), and for how long.
// The client checks these only after the queue has chosen an action, and a refused pick costs the frame (ActionManagerEx:
// "Can't execute ... status", retried next frame), so a locked candidate at the top of the queue starves everything below it
// for the whole status. Status sheet (probed 2026-10-03 on the 7.5 client): rows with LockActions and StatusCategory 2 (Stun 2,
// Sleep 3, Down for the Count 625, petrification, ...) refuse every action; 6 Pacification "Unable to use weaponskills.",
// 7 Silence "A stifling magic is preventing casts." and 1092 Amnesia "Unable to use abilities." are plain category-2 rows
// without the flag, so their ids are listed here.
[Flags]
public enum ActionLockCategories : byte
{
    None = 0,
    Weaponskill = 1, // Pacification
    Spell = 2,       // Silence
    Ability = 4,     // Amnesia
    All = 8          // a LockActions status: every category, items included
}

// Locked flags follow the presence of the status, the way the client refuses; the *Left seconds are its remaining time clamped at 0.
public readonly struct ActionLockState(ActionLockCategories locked, float weaponskillLeft, float spellLeft, float abilityLeft, float allLeft)
{
    public readonly ActionLockCategories Locked = locked;
    public readonly float WeaponskillLeft = weaponskillLeft; // seconds Pacification still lasts (0 when none)
    public readonly float SpellLeft = spellLeft;             // seconds Silence still lasts
    public readonly float AbilityLeft = abilityLeft;         // seconds Amnesia still lasts
    public readonly float AllLeft = allLeft;                 // seconds the longest all-action lock (stun, sleep, down for the count, ...) still lasts

    public static readonly ActionLockState None = default;

    public bool AnyLocked => Locked != ActionLockCategories.None;
    public bool AllLocked => (Locked & ActionLockCategories.All) != 0;
    public bool WeaponskillsLocked => (Locked & (ActionLockCategories.Weaponskill | ActionLockCategories.All)) != 0;
    public bool SpellsLocked => (Locked & (ActionLockCategories.Spell | ActionLockCategories.All)) != 0;
    public bool AbilitiesLocked => (Locked & (ActionLockCategories.Ability | ActionLockCategories.All)) != 0;

    public bool IsLocked(ActionCategory category) => category switch
    {
        ActionCategory.Weaponskill => WeaponskillsLocked,
        ActionCategory.Spell => SpellsLocked,
        ActionCategory.Ability => AbilitiesLocked,
        _ => AllLocked
    };

    // the client would refuse this action right now; an action without a definition is refused only by an all-action lock
    public bool IsLocked(ActionDefinition? definition) => AllLocked || definition != null && (Locked & CategoryFlag(definition.Category)) != 0;
    public bool IsLocked(ActionID action) => IsLocked(ActionDefinitions.Instance[action]);

    // seconds the category's own lock (Pacification / Silence / Amnesia) still lasts, ignoring the all-action statuses
    public float CategoryLeft(ActionCategory category) => category switch
    {
        ActionCategory.Weaponskill => WeaponskillLeft,
        ActionCategory.Spell => SpellLeft,
        ActionCategory.Ability => AbilityLeft,
        _ => 0f
    };

    // seconds the client keeps refusing this category: its own lock or the all-action lock, whichever lasts longer
    public float Left(ActionCategory category) => category switch
    {
        ActionCategory.Weaponskill => Math.Max(WeaponskillLeft, AllLeft),
        ActionCategory.Spell => Math.Max(SpellLeft, AllLeft),
        ActionCategory.Ability => Math.Max(AbilityLeft, AllLeft),
        _ => AllLeft
    };

    private static ActionLockCategories CategoryFlag(ActionCategory category) => category switch
    {
        ActionCategory.Weaponskill => ActionLockCategories.Weaponskill,
        ActionCategory.Spell => ActionLockCategories.Spell,
        ActionCategory.Ability => ActionLockCategories.Ability,
        _ => ActionLockCategories.None
    };
}

public static class ActionLocks
{
    public const uint PacificationStatusID = 6;
    public const uint SilenceStatusID = 7;
    public const uint AmnesiaStatusID = 1092;

    // Reads the player's statuses; allocation-free, the Status sheet is consulted once per process (see StatusLocksAllActions).
    public static ActionLockState Read(Actor player, DateTime now)
    {
        var locked = ActionLockCategories.None;
        float weaponskill = 0f, spell = 0f, ability = 0f, all = 0f;
        foreach (ref readonly var status in player.Statuses.AsSpan())
        {
            switch (status.ID)
            {
                case 0:
                    continue;
                case PacificationStatusID:
                    locked |= ActionLockCategories.Weaponskill;
                    weaponskill = Math.Max(weaponskill, Left(status.ExpireAt, now));
                    break;
                case SilenceStatusID:
                    locked |= ActionLockCategories.Spell;
                    spell = Math.Max(spell, Left(status.ExpireAt, now));
                    break;
                case AmnesiaStatusID:
                    locked |= ActionLockCategories.Ability;
                    ability = Math.Max(ability, Left(status.ExpireAt, now));
                    break;
                default:
                    if (StatusLocksAllActions(status.ID))
                    {
                        locked |= ActionLockCategories.All;
                        all = Math.Max(all, Left(status.ExpireAt, now));
                    }
                    break;
            }
        }
        return new(locked, weaponskill, spell, ability, all);
    }

    private static float Left(DateTime expireAt, DateTime now) => Math.Max((float)(expireAt - now).TotalSeconds, 0f);

    // The Status sheet's LockActions flag, restricted to harmful statuses (category 2): stun, sleep, petrification, Down for the
    // Count, Fetters... The few beneficial rows carrying the flag (In Event, Preoccupied, duty transformations) are scripted states
    // that were never observed blocking a rotation, so they are left out. The table is built from the whole sheet on first use and
    // read without locking afterwards; without game data (a tool that never loads Lumina) nothing locks.
    private static bool[]? _locksAllActions;
    private static readonly object _buildLock = new();

    public static bool StatusLocksAllActions(uint statusID)
    {
        var table = _locksAllActions ?? BuildLocksAllActions();
        return table != null && statusID < (uint)table.Length && table[statusID];
    }

    private static bool[]? BuildLocksAllActions()
    {
        if (Service.LuminaGameData is null)
            return null;
        lock (_buildLock)
        {
            if (_locksAllActions != null)
                return _locksAllActions;
            var sheet = Service.LuminaSheet<Lumina.Excel.Sheets.Status>();
            if (sheet == null)
                return null;
            uint maxRow = 0;
            foreach (var row in sheet)
                maxRow = Math.Max(maxRow, row.RowId);
            var table = new bool[maxRow + 1];
            foreach (var row in sheet)
                if (row is { LockActions: true, StatusCategory: 2 })
                    table[row.RowId] = true;
            _locksAllActions = table;
            return table;
        }
    }
}
