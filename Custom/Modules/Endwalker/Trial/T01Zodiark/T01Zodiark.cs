namespace BossMod.Endwalker.Trial.T01Zodiark;

class Kokytos(BossModule module) : Components.RaidwideCasts(module, [(uint)AID.KokytosInitial, (uint)AID.Kokytos]);
class Phobos(BossModule module) : Components.RaidwideCasts(module, [(uint)AID.PhobosInitial, (uint)AID.Phobos]);
class Ania(BossModule module) : Components.BaitAwayCast(module, (uint)AID.AniaAOE, 3f, tankbuster: true, damageType: AIHints.PredictedDamageType.Tankbuster);
class Phlegethon(BossModule module) : Components.SimpleAOEs(module, (uint)AID.PhlegethonAOE, 5f);
class Adikia(BossModule module) : Components.SimpleAOEGroups(module, [(uint)AID.AdikiaL, (uint)AID.AdikiaR], 21f);
class Algedon(BossModule module) : Components.SimpleAOEs(module, (uint)AID.AlgedonAOE, new AOEShapeRect(60f, 15f));

class Styx(BossModule module) : Components.UniformStackSpread(module, 5f, default, 8, 8)
{
    private int _numCasts;

    public override void OnEventCast(Actor caster, ActorCastEvent spell)
    {
        if (spell.Action.ID == (uint)AID.StyxAOE && ++_numCasts == 5)
        {
            Stacks.Clear();
            _numCasts = 0;
        }
    }

    public override void OnEventIcon(Actor actor, uint iconID, ulong targetID)
    {
        if (iconID == (uint)IconID.Styx)
        {
            Stacks.Clear();
            _numCasts = 0;
            AddStack(actor);
        }
    }
}

class Exoterikos(BossModule module) : BossComponent(module)
{
    private readonly List<(Actor Actor, AOEShape Shape)> _sources = [];
    private readonly List<(Actor Actor, AOEShape Shape)> _active = [];

    private static readonly AOEShapeRect _square = new(21f, 21f);
    private static readonly AOEShapeCone _triangle = new(47f, 30f.Degrees());
    private static readonly AOEShapeRect _ray = new(42f, 7f);

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        var aoes = ActiveAOEs();
        var count = aoes.Count;
        for (var i = 0; i < count; ++i)
        {
            var aoe = aoes[i];
            if (aoe.Shape.Check(actor.Position, aoe.Actor))
            {
                hints.Add("GTFO from exo aoe!");
                return;
            }
        }
    }

    public override void DrawArenaBackground(int pcSlot, Actor pc)
    {
        var aoes = ActiveAOEs();
        var count = aoes.Count;
        for (var i = 0; i < count; ++i)
        {
            var aoe = aoes[i];
            aoe.Shape.Draw(Arena, aoe.Actor);
        }
    }

    public override void OnTethered(Actor source, in ActorTetherInfo tether)
    {
        var target = WorldState.Actors.Find(tether.Target);
        if (source == Module.PrimaryActor && target != null && ShapeForSigil(target) is AOEShape shape)
            AddSource(target, shape);
    }

    public override void OnCastStarted(Actor caster, ActorCastInfo spell)
    {
        if (ShapeForSigil(caster) is AOEShape shape)
            AddSource(caster, shape);
    }

    public override void OnCastFinished(Actor caster, ActorCastInfo spell)
    {
        _sources.RemoveAll(source => source.Actor == caster);
    }

    private void AddSource(Actor actor, AOEShape shape)
    {
        var count = _sources.Count;
        for (var i = 0; i < count; ++i)
        {
            if (_sources[i].Actor == actor)
                return;
        }
        _sources.Add((actor, shape));
    }

    private static AOEShape? ShapeForSigil(Actor sigil) => sigil.OID switch
    {
        (uint)OID.ExoSquare => _square,
        (uint)OID.ExoTri => _triangle,
        (uint)OID.ExoGreen => _ray,
        _ => null,
    };

    private List<(Actor Actor, AOEShape Shape)> ActiveAOEs()
    {
        _active.Clear();
        var hadSideSquare = false;
        DateTime lastRay = default;
        var count = _sources.Count;
        for (var i = 0; i < count; ++i)
        {
            var source = _sources[i];
            if (source.Shape == _square && Math.Abs(source.Actor.PosRot.X - Arena.Center.X) > 10f)
            {
                if (hadSideSquare)
                    continue;
                hadSideSquare = true;
            }
            else if (source.Shape == _ray)
            {
                if (lastRay != default && (source.Actor.CastInfo == null || (Module.CastFinishAt(source.Actor.CastInfo) - lastRay).TotalSeconds > 2d))
                    continue;
                lastRay = Module.CastFinishAt(source.Actor.CastInfo);
            }
            _active.Add(source);
        }
        return _active;
    }
}

class Paradeigma(BossModule module) : BossComponent(module)
{
    private enum FlowDirection
    {
        None,
        CW,
        CCW,
    }

    private FlowDirection _flow;
    private readonly Dictionary<byte, WDir> _behemoths = [];
    private readonly Dictionary<byte, (WDir Offset, Angle Rotation)[]> _snakes = [];

    private const float BehemothOffset = 10.5f;
    private const float SnakeNearOffset = 5.5f;
    private const float SnakeFarOffset = 15.5f;
    private const float SnakeOrthogonalOffset = 21f;
    private static readonly AOEShapeCircle _behemothAOE = new(15f);
    private static readonly AOEShapeRect _snakeAOE = new(42f, 5.5f);

    public override void AddHints(int slot, Actor actor, TextHints hints)
    {
        foreach (var position in _behemoths.Values)
        {
            if (_behemothAOE.Check(actor.Position, RotatedPosition(position)))
            {
                hints.Add("GTFO from behemoth aoe!");
                break;
            }
        }
        foreach (var snakes in _snakes.Values)
        {
            var hit = false;
            foreach (var snake in snakes)
            {
                if (_snakeAOE.Check(actor.Position, RotatedPosition(snake.Offset), RotatedRotation(snake.Rotation)))
                {
                    hints.Add("GTFO from snake aoe!");
                    hit = true;
                    break;
                }
            }
            if (hit)
                break;
        }
    }

    public override void DrawArenaBackground(int pcSlot, Actor pc)
    {
        foreach (var position in _behemoths.Values)
            _behemothAOE.Draw(Arena, RotatedPosition(position));
        foreach (var snakes in _snakes.Values)
        {
            foreach (var snake in snakes)
                _snakeAOE.Draw(Arena, RotatedPosition(snake.Offset), RotatedRotation(snake.Rotation));
        }
    }

    public override void OnMapEffect(byte index, uint state)
    {
        if (index == 2)
        {
            _flow = state switch
            {
                0x00020001 => FlowDirection.CW,
                0x00200010 => FlowDirection.CCW,
                _ => _flow,
            };
            return;
        }

        if (state == 0x00200010)
        {
            switch (index)
            {
                case 9: _behemoths[index] = new(-BehemothOffset, -BehemothOffset); break;
                case 10: _behemoths[index] = new(+BehemothOffset, -BehemothOffset); break;
                case 11: _behemoths[index] = new(-BehemothOffset, +BehemothOffset); break;
                case 12: _behemoths[index] = new(+BehemothOffset, +BehemothOffset); break;
                case 13: _snakes[index] = [(new(-SnakeFarOffset, -SnakeOrthogonalOffset), 0.Degrees()), (new(+SnakeNearOffset, -SnakeOrthogonalOffset), 0.Degrees())]; break;
                case 14: _snakes[index] = [(new(-SnakeNearOffset, -SnakeOrthogonalOffset), 0.Degrees()), (new(+SnakeFarOffset, -SnakeOrthogonalOffset), 0.Degrees())]; break;
                case 15: _snakes[index] = [(new(-SnakeFarOffset, +SnakeOrthogonalOffset), 180.Degrees()), (new(+SnakeNearOffset, +SnakeOrthogonalOffset), 180.Degrees())]; break;
                case 16: _snakes[index] = [(new(-SnakeNearOffset, +SnakeOrthogonalOffset), 180.Degrees()), (new(+SnakeFarOffset, +SnakeOrthogonalOffset), 180.Degrees())]; break;
                case 17: _snakes[index] = [(new(-SnakeOrthogonalOffset, -SnakeFarOffset), 90.Degrees()), (new(-SnakeOrthogonalOffset, +SnakeNearOffset), 90.Degrees())]; break;
                case 18: _snakes[index] = [(new(-SnakeOrthogonalOffset, -SnakeNearOffset), 90.Degrees()), (new(-SnakeOrthogonalOffset, +SnakeFarOffset), 90.Degrees())]; break;
                case 19: _snakes[index] = [(new(+SnakeOrthogonalOffset, -SnakeFarOffset), -90.Degrees()), (new(+SnakeOrthogonalOffset, +SnakeNearOffset), -90.Degrees())]; break;
                case 20: _snakes[index] = [(new(+SnakeOrthogonalOffset, -SnakeNearOffset), -90.Degrees()), (new(+SnakeOrthogonalOffset, +SnakeFarOffset), -90.Degrees())]; break;
            }
        }
        else if (state is 0x00080004 or 0x04000004 or 0x40000004)
        {
            _behemoths.Remove(index);
            _snakes.Remove(index);
            if (_behemoths.Count == 0 && _snakes.Count == 0)
                _flow = FlowDirection.None;
        }
    }

    private WPos RotatedPosition(WDir offset) => _flow switch
    {
        FlowDirection.CW => Arena.Center + offset.OrthoR(),
        FlowDirection.CCW => Arena.Center + offset.OrthoL(),
        _ => Arena.Center + offset,
    };

    private Angle RotatedRotation(Angle rotation) => _flow switch
    {
        FlowDirection.CW => rotation - 90.Degrees(),
        FlowDirection.CCW => rotation + 90.Degrees(),
        _ => rotation,
    };
}

class T01ZodiarkStates : StateMachineBuilder
{
    public T01ZodiarkStates(BossModule module) : base(module)
    {
        DeathPhase(0, Timeline)
            .ActivateOnEnter<Kokytos>()
            .ActivateOnEnter<Phobos>()
            .ActivateOnEnter<Ania>()
            .ActivateOnEnter<Phlegethon>()
            .ActivateOnEnter<Adikia>()
            .ActivateOnEnter<Algedon>()
            .ActivateOnEnter<Styx>()
            .ActivateOnEnter<Exoterikos>()
            .ActivateOnEnter<Paradeigma>()
            .ActivateOnEnter<Extreme.Ex1Zodiark.AstralEclipse>();
    }

    private void Timeline(uint id)
    {
        Condition(id, 7.1f, KokytosInitialCasting, "", 5f, 2.1f);
        Timeout(id + 1, 4f, "Kokytos")
            .SetHint(StateMachine.StateHint.Raidwide);
        Timeout(id + 0x10, 9.1f, "Exoterikos");
        Timeout(id + 0x20, 7.3f, "Esoteric Sect");
        Timeout(id + 0x30, 4f, "Ania")
            .SetHint(StateMachine.StateHint.Tankbuster);
        Timeout(id + 0x40, 10.1f, "Exoterikos");
        Timeout(id + 0x50, 7.3f, "Esoteric Dyad");
        Timeout(id + 0x60, 9.4f, "Paradeigma");
        Timeout(id + 0x70, 12.7f, "Meteoros Eidolon");
        Timeout(id + 0x80, 5.1f, "Paradeigma");
        Timeout(id + 0x90, 12.2f, "Opheos Eidolon");
        Timeout(id + 0xA0, 6.6f, "Phlegethon x3");
        Timeout(id + 0xB0, 8.2f, "Styx x5")
            .SetHint(StateMachine.StateHint.Raidwide);
        Timeout(id + 0xC0, 12.6f, "Paradeigma");
        Timeout(id + 0xD0, 7.1f, "Exoterikos");
        Timeout(id + 0xE0, 6.3f, "Esoteric Dyad/Esoteric Sect");
        Timeout(id + 0xF0, 0.3f, "Meteoros Eidolon");
        Timeout(id + 0x100, 20.2f, "Complete Control");
        Targetable(id + 0x110, false, 0.6f, "--untargetable--");
        Targetable(id + 0x120, true, 24.1f, "--targetable--");
        Timeout(id + 0x130, 5.1f, "Paradeigma");
        Timeout(id + 0x140, 15.1f, "Astral Flow");
        Timeout(id + 0x150, 10.8f, "Meteoros Eidolon");
        Timeout(id + 0x160, 9.7f, "Adikia");
        Timeout(id + 0x170, 11.2f, "Phlegethon x3");
        Timeout(id + 0x180, 9.1f, "Triple Esoteric Ray");
        Timeout(id + 0x190, 0.1f, "Esoteric Ray 1");
        Timeout(id + 0x1A0, 3f, "Esoteric Ray 2");
        Timeout(id + 0x1B0, 9f, "Algedon");
        Timeout(id + 0x1C0, 6.2f, "Paradeigma");
        Timeout(id + 0x1D0, 15.1f, "Astral Flow");
        Timeout(id + 0x1E0, 5.9f, "Opheos Eidolon");
        Targetable(id + 0x1F0, false, 11.3f, "Astral Eclipse / --untargetable--");
        Targetable(id + 0x200, true, 12.1f, "--targetable--");
        Timeout(id + 0x210, 5f, "Explosion 1");
        Timeout(id + 0x220, 4f, "Explosion 2");
        Timeout(id + 0x230, 4f, "Explosion 3");
        Timeout(id + 0x240, 7.1f, "Styx x5")
            .SetHint(StateMachine.StateHint.Raidwide);
        Timeout(id + 0x250, 15.1f, "Kokytos")
            .SetHint(StateMachine.StateHint.Raidwide);
        Timeout(id + 0x260, 5.1f, "Paradeigma");
        Timeout(id + 0x270, 15.1f, "Astral Flow");
        Timeout(id + 0x280, 6.4f, "Meteoros Eidolon");
        Timeout(id + 0x290, 8.8f, "Styx")
            .SetHint(StateMachine.StateHint.Raidwide);
        Timeout(id + 0x2A0, 10.1f, "Exoterikos");
        Timeout(id + 0x2B0, 8.1f, "Adikia");
        Timeout(id + 0x2C0, 1.2f, "Esoteric Dyad");
        Condition(id + 0x2D0, 3f, () => Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.TrimorphosExoterikos, "Trimorphos Exoterikos", 5f);
        Timeout(id + 0x2E0, 10.7f, "Esoteric Dyad/Esoteric Sect");
        Timeout(id + 0x2F0, 4f, "Esoteric Dyad/Esoteric Sect");
        Timeout(id + 0x300, 4f, "Esoteric Dyad/Esoteric Sect");
        Timeout(id + 0x310, 6.5f, "Paradeigma");
        Timeout(id + 0x320, 15.2f, "Astral Flow");
        Timeout(id + 0x330, 5.8f, "Opheos Eidolon");
        Timeout(id + 0x340, 9.3f, "Styx x5")
            .SetHint(StateMachine.StateHint.Raidwide);
        Timeout(id + 0x350, 10.1f, "Exoterikos");
        Timeout(id + 0x360, 9.1f, "Triple Esoteric Ray");
        Timeout(id + 0x370, 0.1f, "Esoteric Ray 1");
        Timeout(id + 0x380, 0.1f, "Esoteric Sect");
        Timeout(id + 0x390, 2.9f, "Esoteric Ray 2");
        Timeout(id + 0x3A0, 4f, "Ania")
            .SetHint(StateMachine.StateHint.Tankbuster);
        Condition(id + 0x3B0, 6.2f, () => Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.TrimorphosExoterikos, "Trimorphos Exoterikos", 5f, 1.2f);
        Timeout(id + 0x3C0, 10.8f, "Esoteric Dyad/Esoteric Sect");
        Timeout(id + 0x3D0, 4f, "Esoteric Dyad/Esoteric Sect");
        Timeout(id + 0x3E0, 3.3f, "Phlegethon x3");
        Timeout(id + 0x3F0, 0.6f, "Esoteric Dyad/Esoteric Sect");
        Timeout(id + 0x400, 7.6f, "Styx x5")
            .SetHint(StateMachine.StateHint.Raidwide);
        Timeout(id + 0x410, 12.2f, "Algedon");
        Timeout(id + 0x420, 13.2f, "Paradeigma");
        Timeout(id + 0x430, 15.2f, "Astral Flow");
        Timeout(id + 0x440, 5.8f, "Opheos Eidolon");
        Timeout(id + 0x450, 9.3f, "Styx x5")
            .SetHint(StateMachine.StateHint.Raidwide);
        Timeout(id + 0x460, 10.2f, "Exoterikos");
        Timeout(id + 0x470, 9.1f, "Triple Esoteric Ray");
        Timeout(id + 0x480, 0.1f, "Esoteric Ray 1 / Esoteric Sect");
        Timeout(id + 0x490, 3f, "Esoteric Ray 2");
        Timeout(id + 0x4A0, 4f, "Ania")
            .SetHint(StateMachine.StateHint.Tankbuster);
        Condition(id + 0x4B0, 6.1f, () => Module.PrimaryActor.CastInfo?.Action.ID == (uint)AID.TrimorphosExoterikos, "Trimorphos Exoterikos", 5f, 1.1f);
        Timeout(id + 0x4C0, 10.8f, "Esoteric Dyad/Esoteric Sect");
        Timeout(id + 0x4D0, 4f, "Esoteric Dyad/Esoteric Sect");
        Timeout(id + 0x4E0, 3.3f, "Phlegethon x3");
        Timeout(id + 0x4F0, 0.6f, "Esoteric Dyad/Esoteric Sect");
        Timeout(id + 0x500, 7.6f, "Styx x5")
            .SetHint(StateMachine.StateHint.Raidwide);
        Timeout(id + 0x510, 12.2f, "Algedon");
    }

    private bool KokytosInitialCasting()
    {
        foreach (var actor in Module.WorldState.Actors.Actors.Values)
        {
            if (actor.CastInfo?.Action.ID == (uint)AID.KokytosInitial)
                return true;
        }
        return false;
    }
}

[ModuleInfo(BossModuleInfo.Maturity.WIP, GroupType = BossModuleInfo.GroupType.CFC, GroupID = 802, NameID = 10456, PlanLevel = 90)]
public class T01Zodiark(WorldState ws, Actor primary) : BossModule(ws, primary, new(100f, 100f), new ArenaBoundsSquare(20f));
