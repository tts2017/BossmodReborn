using BossMod.Heavensward.DeepDungeon.PalaceOfTheDead;

namespace BossMod.Autorotation.xan.Custom;

public sealed class DeepDungeonAI : AIBase<DeepDungeonAI.Strategy>
{
    private readonly EventSubscriptions _subscriptions;

    public DeepDungeonAI(RotationModuleManager manager, Actor player) : base(manager, player)
    {
        _subscriptions = new(World.Actors.CastEvent.Subscribe(OnCastEvent));
    }

    public override void Dispose()
    {
        _subscriptions.Dispose();
        base.Dispose();
    }

    public struct Strategy
    {
        public Track<EnabledByDefault> Potion;
        [Track("Kite enemies", InternalName = "Kite enemies")]
        public Track<EnabledByDefault> Kite;
    }

    public enum Track { Potion, Kite }

    public static RotationModuleDefinition Definition()
    {
        return new RotationModuleDefinition("Deep Dungeon AI [Custom]", "Utilities for deep dungeon - potion/pomander user", "AI (xan)", "xan", RotationModuleQuality.Basic, new BitMask(~0ul), 100, CanUseWhileRoleplaying: true).WithStrategies<Strategy>();
    }

    enum OID : uint
    {
        Unei = 0x3E1A,
    }

    enum Transformation : uint
    {
        None,
        Manticore,
        Succubus,
        Kuribu,
        Dreadnaught,
        Bomb,
        Mudball
    }

    enum SID : uint
    {
        Transfiguration = 565,
        ItemPenalty = 1094,
        Transfiguration2 = 4708,
        Anointed = 4587,
        Bind = 13,
        Heavy = 14,
    }

    private static readonly HashSet<uint> HeavyImmunePalaceEnemies =
    [
        2566, 4977, 4981, 4983, 4987, 4988, 4990, 4996, 4997,
        5008, 5013, 5014, 5017, 5019, 5020, 5022, 5026, 5028, 5030, 5032, 5033, 5034, 5036,
        5046, 5047, 5048, 5049, 5050, 5051, 5052, 5053,
        5283, 5284, 5285, 5286, 5287, 5288, 5289, 5290, 5291, 5292, 5293, 5294, 5295, 5296, 5297, 5298,
        5300, 5302, 5305, 5306, 5307, 5316, 5317, 5319, 5323, 5324, 5331, 5337, 5342, 5344, 5348, 5353, 5354,
        5362, 5366, 5372, 5373, 5375, 5381, 5393, 5398, 5399,
        5402, 5405, 5407, 5412, 5414, 5416, 5418, 5419, 5420, 5422,
        5430, 5432, 5434, 5435, 5436, 5444, 5445, 5447,
        5451, 5452, 5459, 5470, 5473, 5475, 5479, 5480
    ];

    private static readonly HashSet<uint> BindSusceptibleHeavyImmunePalaceEnemies = [4987, 5019, 5342, 5372];

    private ulong _orbitTargetID;
    private WPos _orbitProgressPosition;
    private DateTime _orbitProgressAt;
    private int _orbitDirection = 1;
    private ulong _rangedPullTargetID;
    private DateTime _rangedPullWaitStartedAt;
    private DateTime _rangedPullProgressAt;
    private float _rangedPullClosestDistance;
    private readonly HashSet<uint> _observedRangedEnemyOIDs = [];
    private readonly Dictionary<uint, float> _spinDistanceAdjustments = [];
    private readonly Dictionary<ulong, (DateTime Until, WDir Direction)> _observedPatrolEnemies = [];
    private readonly List<ulong> _expiredPatrolObservations = [];
    private ulong _patrolIsolationTargetID;
    private WPos _patrolIsolationPosition;
    private ulong _patrolAvoidanceThreatID;
    private DateTime _patrolAvoidanceUntil;

    public override void Execute(in Strategy strategy, Actor? primaryTarget, float estimatedAnimLockDelay, bool isMoving)
    {
        if (World.DeepDungeon.DungeonId == 0)
            return;

        var (regenAction, potAction) = PotionActions();

        if (primaryTarget != null && IsUnsafePalaceTarget(primaryTarget))
            primaryTarget = null;
        BossMod.Global.DeepDungeon.AutoClear.UpdatePatrolObservations(World, Hints, _observedPatrolEnemies, _expiredPatrolObservations);
        var prioritizePassage = World.DeepDungeon.DungeonId == DeepDungeonState.DungeonType.POTD && World.DeepDungeon.Floor >= 101 && World.DeepDungeon.PassageActive && !Player.InCombat;
        if (prioritizePassage)
        {
            ResetPatrolIsolation();
            ResetRangedPullWait();
            _patrolAvoidanceThreatID = 0;
            _patrolAvoidanceUntil = default;
            primaryTarget = null;
        }
        else
        {
            primaryTarget = MaintainPatrolIsolation(primaryTarget);
            if (!Player.InCombat && primaryTarget is Actor pullTarget && !pullTarget.InCombat && FindNearbyPatrolThreat(pullTarget, 24f, 20f) is Actor nearbyPatrol)
            {
                if (Player.DistanceToHitbox(nearbyPatrol) < Player.DistanceToHitbox(pullTarget) && !IsUnsafePalaceTarget(nearbyPatrol))
                {
                    Hints.FindEnemy(pullTarget)?.ForcePriority(AIHints.Enemy.PriorityForbidden);
                    Hints.FindEnemy(nearbyPatrol)?.ForcePriority(1);
                    Hints.ForbiddenZones.RemoveAll(zone => zone.Source == nearbyPatrol.InstanceID);
                    Hints.ForcedTarget = nearbyPatrol;
                    primaryTarget = nearbyPatrol;
                    ResetRangedPullWait();
                }
                else if (!TryStartPatrolIsolation(pullTarget))
                {
                    Hints.FindEnemy(pullTarget)?.ForcePriority(AIHints.Enemy.PriorityForbidden);
                    Hints.AddForbiddenZone(new SDCircle(pullTarget.Position, pullTarget.HitboxRadius + 10f), source: pullTarget.InstanceID);
                    primaryTarget = null;
                    ResetRangedPullWait();
                }
            }
        }

        var transformation = Transformation.None;
        var stat = Player.FindStatus(SID.Transfiguration) ?? Player.FindStatus(SID.Transfiguration2);
        if (stat is { } status)
        {
            transformation = (status.Extra & 0xFF) switch
            {
                42 => Transformation.Manticore,
                43 => Transformation.Succubus,
                49 => Transformation.Kuribu,
                54 => Transformation.Mudball,
                55 => Transformation.Bomb,
                244 => Transformation.Dreadnaught,
                _ => Transformation.None
            };
        }

        if (transformation != Transformation.None)
        {
            if (ShouldCancelTransformationForPotion(strategy, regenAction, potAction))
            {
                CancelTransformation();
                return;
            }

            if (Hints.StatusesToCancel.Any(s => s.sourceId == Player.InstanceID && s.statusId is (uint)SID.Transfiguration or (uint)SID.Transfiguration2))
                return;

            DoTransformActions(strategy, primaryTarget, transformation);
            return;
        }

        SetupRangedPull(primaryTarget);
        TryStartRangedEnemyControl(primaryTarget);
        if (!HoldPositionAfterRangedPull())
        {
            ApplyPalaceRangedPullControl(strategy, primaryTarget);
            if (!SetupPatrolEnemyAvoidance(strategy, primaryTarget))
                SetupKiteZone(strategy, primaryTarget);
        }

        if (Player.FindStatus(SID.ItemPenalty) != null)
            return;

        if (regenAction != default && ShouldPotion(strategy, regenAction))
            Hints.ActionsToExecute.Push(regenAction, Player, ActionQueue.Priority.ManualGCD - 3);

        if (potAction != default && strategy.Potion.IsEnabled() && Player.HPRatio <= 0.3f)
            Hints.ActionsToExecute.Push(potAction, Player, ActionQueue.Priority.ManualGCD - 1);
    }

    private bool IsRanged => Player.Class.GetRole() is Role.Ranged or Role.Healer;

    private (ActionID Regen, ActionID Instant) PotionActions() => World.DeepDungeon.DungeonId switch
    {
        DeepDungeonState.DungeonType.POTD => (ActionDefinitions.IDPotionSustaining, ActionDefinitions.IDPotionSuper),
        DeepDungeonState.DungeonType.HOH => (ActionDefinitions.IDPotionEmpyrean, ActionDefinitions.IDPotionSuper),
        DeepDungeonState.DungeonType.EO => (ActionDefinitions.IDPotionOrthos, ActionDefinitions.IDPotionHyper),
        DeepDungeonState.DungeonType.PT => (ActionDefinitions.IDPotionPilgrim, ActionDefinitions.IDPotionUltra),
        _ => (default, default)
    };

    private bool ShouldCancelTransformationForPotion(in Strategy strategy, ActionID regenAction, ActionID potAction)
    {
        if (!strategy.Potion.IsEnabled() || Player.HPRatio > 0.3f || Player.FindStatus(SID.ItemPenalty) != null)
            return false;

        return CanUsePotion(potAction) || ShouldPotion(strategy, regenAction) && CanUsePotion(regenAction);
    }

    private bool CanUsePotion(ActionID action)
    {
        if (action == default || World.Client.GetInventoryItemQuantity(action.ID) == 0)
            return false;

        return ActionDefinitions.Instance[action]?.ReadyIn(World.Client.Cooldowns, World.Client.DutyActions) <= 0.1f;
    }

    private void CancelTransformation()
    {
        if (Player.FindStatus(SID.Transfiguration) != null)
            Hints.StatusesToCancel.Add(((uint)SID.Transfiguration, Player.InstanceID));
        if (Player.FindStatus(SID.Transfiguration2) != null)
            Hints.StatusesToCancel.Add(((uint)SID.Transfiguration2, Player.InstanceID));
    }

    private ActionID PalaceRangedAttack() => Player.Class switch
    {
        Class.GLA or Class.PLD => Spell(BossMod.PLD.AID.ShieldLob),
        Class.MRD or Class.WAR => Spell(BossMod.WAR.AID.Tomahawk),
        Class.DRK => Spell(BossMod.DRK.AID.Unmend),
        Class.GNB => Spell(BossMod.GNB.AID.LightningShot),
        Class.LNC or Class.DRG => Spell(BossMod.DRG.AID.PiercingTalon),
        Class.ROG or Class.NIN => Spell(BossMod.NIN.AID.ThrowingDagger),
        Class.SAM => Spell(BossMod.SAM.AID.Enpi),
        Class.RPR => Spell(BossMod.RPR.AID.Harpe),
        _ => default
    };

    private void SetupRangedPull(Actor? primaryTarget)
    {
        if (World.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.POTD || World.DeepDungeon.IsBossFloor || Player.InCombat || primaryTarget is not Actor target || target.InCombat || target.IsAlly)
            return;
        if (Hints.FindEnemy(target) is { Priority: < 0 })
            return;

        var pullAction = PalaceRangedAttack();

        var classCategory = Player.Class.GetClassCategory();
        if (classCategory is ClassCategory.Tank or ClassCategory.Melee && (pullAction == default || !ActionUnlocked(pullAction)))
        {
            if (classCategory == ClassCategory.Melee)
            {
                StartRangedPullWait(target);
            }
            else
            {
                ResetRangedPullWait();
            }
            Hints.PathfindTarget = null;
            Hints.GoalZones.Add(AIHints.GoalSingleTarget(target, 3f, 1100f));
            Hints.GoalZones.Add(AIHints.GoalProximity(target, MathF.Max(3f, Player.DistanceToHitbox(target) + 3f), 0.1f));
            return;
        }

        if (pullAction != default && !ActionUnlocked(pullAction))
            return;
        if (pullAction == default && !IsRanged)
            return;

        var range = pullAction != default ? ActionDefinitions.Instance[pullAction]!.Range : 25f;
        var targetRadius = target.HitboxRadius;
        Hints.PathfindTarget = null;
        Hints.GoalZones.Add(AIHints.GoalDonut(target.Position, MathF.Max(8f, range - 7f) + targetRadius, range + targetRadius + 0.5f, 1100f));
        Hints.GoalZones.Add(AIHints.GoalProximity(target, MathF.Max(range, Player.DistanceToHitbox(target) + range), 0.1f));

        StartRangedPullWait(target);

        if (pullAction != default)
            Hints.ActionsToExecute.Push(pullAction, target, ActionQueue.Priority.ManualGCD - 10);
    }

    private void TryStartRangedEnemyControl(Actor? primaryTarget)
    {
        if (_rangedPullTargetID != 0 || World.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.POTD || World.DeepDungeon.IsBossFloor || Player.Class.GetClassCategory() is not (ClassCategory.Tank or ClassCategory.Melee) || !Player.InCombat || primaryTarget is not Actor target || target.IsAlly || target.IsDeadOrDestroyed || !target.IsTargetable || !target.InCombat && !target.AggroPlayer)
            return;

        var distance = Player.DistanceToHitbox(target);
        if (distance <= 2.8f)
            return;

        var rangedCast = distance > 4.5f && target.CastInfo?.TargetID == Player.InstanceID;
        if (!rangedCast && !_observedRangedEnemyOIDs.Contains(target.OID))
            return;

        if (rangedCast)
            _observedRangedEnemyOIDs.Add(target.OID);
        StartRangedPullWait(target);
    }

    private void StartRangedPullWait(Actor target)
    {
        _rangedPullTargetID = target.InstanceID;
        _rangedPullWaitStartedAt = default;
        _rangedPullProgressAt = World.CurrentTime;
        _rangedPullClosestDistance = Player.DistanceToHitbox(target);
    }

    private bool HoldPositionAfterRangedPull()
    {
        var target = World.Actors.Find(_rangedPullTargetID);
        if (World.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.POTD || World.DeepDungeon.IsBossFloor || target == null || target.IsAlly || target.IsDeadOrDestroyed || !target.IsTargetable || IsUnsafePalaceTarget(target))
        {
            ResetRangedPullWait();
            return false;
        }

        if (!Player.InCombat && !target.InCombat)
            return false;
        if (Player.InCombat && !target.InCombat && !target.AggroPlayer)
        {
            ResetRangedPullWait();
            return false;
        }

        Hints.FindEnemy(target)?.ForcePriority(1);
        Hints.ForcedTarget = target;

        var obstacles = Hints.PathfindMapObstacles;
        if (obstacles.Bitmap != null && !obstacles.HasObstacleMapLineOfSight(Hints.PathfindMapCenter, Player.Position, target.Position))
        {
            ResetRangedPullWait();
            return false;
        }

        if (_rangedPullWaitStartedAt == default)
        {
            _rangedPullWaitStartedAt = World.CurrentTime;
            _rangedPullProgressAt = World.CurrentTime;
            _rangedPullClosestDistance = Player.DistanceToHitbox(target);
        }

        var distance = Player.DistanceToHitbox(target);
        var rangedCast = distance > 4.5f && target.CastInfo?.TargetID == Player.InstanceID;
        if (rangedCast)
            _observedRangedEnemyOIDs.Add(target.OID);

        var targetMovement = target.LastFrameMovement;
        var targetApproaching = targetMovement.LengthSq() >= 0.0001f && targetMovement.Dot(Player.Position - target.Position) > 0f;
        if (targetApproaching)
        {
            _rangedPullProgressAt = World.CurrentTime;
        }
        else if (distance <= _rangedPullClosestDistance - 0.75f)
        {
            _rangedPullClosestDistance = distance;
            _rangedPullProgressAt = World.CurrentTime;
        }

        var approachStopped = World.CurrentTime >= _rangedPullProgressAt.AddSeconds(2.5);
        if (approachStopped && distance > 4.5f && target.TargetID == Player.InstanceID)
            _observedRangedEnemyOIDs.Add(target.OID);

        var meleeSpinEngageDistance = Player.Class.GetClassCategory() is ClassCategory.Tank or ClassCategory.Melee ? 2.8f : 3.5f;
        if (distance <= meleeSpinEngageDistance)
        {
            ResetRangedPullWait();
            return false;
        }

        Hints.PathfindTarget = null;
        if (target.InstanceID == _patrolIsolationTargetID && _patrolIsolationPosition != default && !Player.Position.InCircle(_patrolIsolationPosition, 1.5f)
            && IsSafeCombatMovement(_patrolIsolationPosition))
            Hints.GoalZones.Add(AIHints.GoalSingleTarget(_patrolIsolationPosition, 1f, 120f));
        else
            Hints.GoalZones.Add(AIHints.GoalSingleTarget(target, meleeSpinEngageDistance, 120f));
        Hints.MaxCastTime = 0;
        return true;
    }

    private void ResetRangedPullWait()
    {
        _rangedPullTargetID = 0;
        _rangedPullWaitStartedAt = default;
        _rangedPullProgressAt = default;
        _rangedPullClosestDistance = 0;
    }

    private void ApplyPalaceRangedPullControl(in Strategy strategy, Actor? primaryTarget)
    {
        if (World.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.POTD || World.DeepDungeon.IsBossFloor || !strategy.Kite.IsEnabled() || Player.Class.GetRole() != Role.Ranged || primaryTarget is not Actor target)
            return;

        if (!Player.InCombat && !target.InCombat && !target.IsAlly && Player.DistanceToHitbox(target) is >= 5f and <= 18f && Player.FindStatus(ClassShared.SID.Sprint) == null && NextChargeIn(ClassShared.AID.Sprint) == 0)
        {
            Hints.ActionsToExecute.Push(ActionDefinitions.IDSprint, Player, ActionQueue.Priority.High);
            return;
        }

        if (!Player.InCombat || !target.InCombat)
            return;

        var sprint = Player.FindStatus(ClassShared.SID.Sprint);
        if (sprint != null && (sprint.Value.ExpireAt - World.CurrentTime).TotalSeconds > 3)
            return;

        if (!HeavyImmunePalaceEnemies.Contains(target.NameID))
        {
            if (target.FindStatus((uint)SID.Heavy) == null && Unlocked(ClassShared.AID.LegGraze) && NextChargeIn(ClassShared.AID.LegGraze) == 0)
                Hints.ActionsToExecute.Push(Spell(ClassShared.AID.LegGraze), target, ActionQueue.Priority.High);
        }
        else if (BindSusceptibleHeavyImmunePalaceEnemies.Contains(target.NameID) && target.FindStatus((uint)SID.Bind) == null && Unlocked(ClassShared.AID.FootGraze) && NextChargeIn(ClassShared.AID.FootGraze) == 0)
        {
            Hints.ActionsToExecute.Push(Spell(ClassShared.AID.FootGraze), target, ActionQueue.Priority.High);
        }
    }

    private static readonly HashSet<uint> NoMeleeAutos = [
        // hoh
        0x22C3, // heavenly onibi
        0x22C5, // heavenly dhruva
        0x22C6, // heavenly sai taisui
        0x22DC, // heavenly dogu
        0x22DE, // heavenly ganseki
        0x22ED, // heavenly kongorei
        0x22EF, // heavenly maruishi
        0x22F3, // heavenly rachimonai
        0x22FC, // heavenly doguzeri
        0x2320, // heavenly nuppeppo (WHM) (uses stone)

        // orthos
        0x3DCC, // orthos imp
        0x3DCE, // orthos fachan
        0x3DD2, // orthos water sprite
        0x3DD4, // orthos microsystem
        0x3DD5, // orthosystem β
        0x3DE0, // orthodemolisher
        0x3DE2, // orthodroid
        0x3DFD, // orthos apa
        0x3E10, // orthos ice sprite
        0x3E5C, // orthos ahriman
        0x3E62, // orthos abyss
        0x3E63, // orthodrone
        0x3E64, // orthosystem γ
        0x3E66, // orthosystem α

        // PT
        0x3738, // forgiven bribery
        0x4934, // invoked dreamer
    ];

    private void OnCastEvent(Actor source, ActorCastEvent spell)
    {
        if (World.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.POTD || World.DeepDungeon.IsBossFloor || Player.Class.GetClassCategory() is not (ClassCategory.Tank or ClassCategory.Melee) || source.IsAlly || source.IsDeadOrDestroyed || !source.IsTargetable)
            return;
        if (spell.MainTargetID != Player.InstanceID && !spell.Targets.Any(target => target.ID == Player.InstanceID))
            return;

        var action = Service.LuminaRow<Lumina.Excel.Sheets.Action>(spell.Action.ID);
        if (action?.ActionCategory.RowId != 1)
            return;

        if (Player.DistanceToHitbox(source) > 4.5f)
        {
            _observedRangedEnemyOIDs.Add(source.OID);
            if (_rangedPullTargetID == 0 || _rangedPullTargetID == source.InstanceID)
                StartRangedPullWait(source);
        }

        if (_orbitTargetID != source.InstanceID)
            return;

        var minimumDistance = MathF.Max(0.35f, source.HitboxRadius * 0.65f);
        var maximumAdjustment = MathF.Min(1f, MathF.Max(0f, source.HitboxRadius + 0.05f - minimumDistance));
        var currentAdjustment = _spinDistanceAdjustments.GetValueOrDefault(source.OID);
        _spinDistanceAdjustments[source.OID] = MathF.Min(maximumAdjustment, currentAdjustment + 0.1f);
    }

    private bool SetupPatrolEnemyAvoidance(in Strategy strategy, Actor? primaryTarget)
    {
        if (World.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.POTD || World.DeepDungeon.IsBossFloor || !strategy.Kite.IsEnabled() || !Player.InCombat || primaryTarget == null || primaryTarget.IsDeadOrDestroyed || !primaryTarget.IsTargetable || !primaryTarget.InCombat && !primaryTarget.AggroPlayer)
        {
            _patrolAvoidanceThreatID = 0;
            _patrolAvoidanceUntil = default;
            return false;
        }

        if (primaryTarget.InstanceID == _patrolIsolationTargetID && _patrolIsolationPosition != default)
        {
            ForbidOtherPatrols(primaryTarget, 30f);
            if (IsSafeCombatMovement(_patrolIsolationPosition))
            {
                Hints.PathfindTarget = null;
                Hints.GoalZones.Add(AIHints.GoalProximity(_patrolIsolationPosition, 15f, 120f));
                Hints.MaxCastTime = 0;
                return true;
            }
            ResetPatrolIsolation();
        }

        const float detectionDistance = 24f;
        const float immediateDistance = 12f;
        const float retentionDistance = 28f;
        Actor? threat = null;
        var closestThreatDistance = float.MaxValue;
        var obstacleMap = Hints.PathfindMapObstacles;

        foreach (var candidate in Hints.PotentialTargets)
        {
            var actor = candidate.Actor;
            if (!IsUnengagedPatrolCandidate(actor, primaryTarget))
                continue;

            var distance = Player.DistanceToHitbox(actor);
            if (distance > detectionDistance || obstacleMap.Bitmap != null && !obstacleMap.HasObstacleMapLineOfSight(Hints.PathfindMapCenter, Player.Position, actor.Position))
                continue;

            var movement = actor.LastFrameMovement;
            var approaching = movement.LengthSq() >= 0.0001f && movement.Dot(Player.Position - actor.Position) > 0f;
            if (!IsObservedPatrol(actor) || !approaching && distance > immediateDistance)
                continue;

            if (distance < closestThreatDistance)
            {
                threat = actor;
                closestThreatDistance = distance;
            }
        }

        if (threat != null)
        {
            _patrolAvoidanceThreatID = threat.InstanceID;
            _patrolAvoidanceUntil = World.FutureTime(2);
        }
        else if (World.CurrentTime < _patrolAvoidanceUntil && World.Actors.Find(_patrolAvoidanceThreatID) is Actor retainedThreat && IsUnengagedPatrolCandidate(retainedThreat, primaryTarget) && Player.DistanceToHitbox(retainedThreat) <= retentionDistance && (obstacleMap.Bitmap == null || obstacleMap.HasObstacleMapLineOfSight(Hints.PathfindMapCenter, Player.Position, retainedThreat.Position)))
        {
            threat = retainedThreat;
        }
        else
        {
            _patrolAvoidanceThreatID = 0;
            _patrolAvoidanceUntil = default;
            return false;
        }

        var primaryEscapeDirection = _observedPatrolEnemies.TryGetValue(threat.InstanceID, out var observation) && observation.Until > World.CurrentTime
            ? -observation.Direction
            : (Player.Position - threat.Position).Normalized();
        if (primaryEscapeDirection == default)
            primaryEscapeDirection = (primaryTarget.Position - threat.Position).Normalized();
        if (primaryEscapeDirection == default)
            primaryEscapeDirection = primaryTarget.Rotation.ToDirection().OrthoR();

        var escapeDirection = 2f * primaryEscapeDirection;
        foreach (var candidate in Hints.PotentialTargets)
        {
            var actor = candidate.Actor;
            if (!IsUnengagedPatrolCandidate(actor, primaryTarget))
                continue;

            var distance = Player.DistanceToHitbox(actor);
            if (distance > retentionDistance || obstacleMap.Bitmap != null && !obstacleMap.HasObstacleMapLineOfSight(Hints.PathfindMapCenter, Player.Position, actor.Position))
                continue;

            var away = (Player.Position - actor.Position).Normalized();
            if (away != default)
                escapeDirection += Math.Clamp(1f - distance / retentionDistance, 0f, 1f) * away;
        }

        escapeDirection = escapeDirection.Normalized();
        if (escapeDirection == default || escapeDirection.Dot(primaryEscapeDirection) < 0.5f)
            escapeDirection = primaryEscapeDirection;

        var escapePoint = FindPatrolIsolationPosition(escapeDirection);
        if (escapePoint.AlmostEqual(Player.Position, 0.1f))
            return false;
        Hints.PathfindTarget = null;
        Hints.GoalZones.Add(AIHints.GoalProximity(escapePoint, 15f, 120f));
        Hints.MaxCastTime = 0;
        return true;
    }

    private Actor? FindNearbyPatrolThreat(Actor pullTarget, float targetDistance, float playerDistance)
    {
        var obstacleMap = Hints.PathfindMapObstacles;
        Actor? closestThreat = null;
        var closestThreatDistance = float.MaxValue;
        foreach (var candidate in Hints.PotentialTargets)
        {
            var actor = candidate.Actor;
            if (!IsUnengagedPatrolCandidate(actor, pullTarget) || !IsObservedPatrol(actor))
                continue;

            var nearTarget = (actor.Position - pullTarget.Position).Length() <= targetDistance + actor.HitboxRadius + pullTarget.HitboxRadius;
            var distance = Player.DistanceToHitbox(actor);
            var nearPlayer = distance <= playerDistance;
            if ((nearTarget || nearPlayer) && distance < closestThreatDistance && (obstacleMap.Bitmap == null || obstacleMap.HasObstacleMapLineOfSight(Hints.PathfindMapCenter, Player.Position, actor.Position)))
            {
                closestThreat = actor;
                closestThreatDistance = distance;
            }
        }
        return closestThreat;
    }

    private Actor? MaintainPatrolIsolation(Actor? primaryTarget)
    {
        if (_patrolIsolationTargetID == 0)
            return primaryTarget;

        var target = World.Actors.Find(_patrolIsolationTargetID);
        if (World.DeepDungeon.DungeonId != DeepDungeonState.DungeonType.POTD || World.DeepDungeon.IsBossFloor || target == null || target.IsAlly || target.IsDeadOrDestroyed || target.PendingDead || !target.IsTargetable || IsUnsafePalaceTarget(target))
        {
            ResetPatrolIsolation();
            return primaryTarget;
        }

        foreach (var candidate in Hints.PotentialTargets)
        {
            var actor = candidate.Actor;
            if (actor.InstanceID != target.InstanceID && actor.AggroPlayer && !actor.IsDeadOrDestroyed && actor.IsTargetable)
            {
                ResetPatrolIsolation();
                return primaryTarget;
            }
        }

        Hints.FindEnemy(target)?.ForcePriority(1);
        Hints.ForcedTarget = target;
        ForbidOtherPatrols(target, 30f);
        return target;
    }

    private bool TryStartPatrolIsolation(Actor pullTarget)
    {
        if (IsUnsafePalaceTarget(pullTarget))
            return false;
        if (_patrolIsolationTargetID != 0)
            return _patrolIsolationTargetID == pullTarget.InstanceID;
        if (!IsObservedPatrol(pullTarget))
            return false;

        const float nearbyDistance = 30f;
        var obstacleMap = Hints.PathfindMapObstacles;
        var separationDirection = default(WDir);
        var nearbyPatrols = 0;
        foreach (var candidate in Hints.PotentialTargets)
        {
            var actor = candidate.Actor;
            if (!IsUnengagedPatrolCandidate(actor, pullTarget) || !IsObservedPatrol(actor))
                continue;

            var nearTarget = (actor.Position - pullTarget.Position).Length() <= nearbyDistance + actor.HitboxRadius + pullTarget.HitboxRadius;
            var nearPlayer = Player.DistanceToHitbox(actor) <= nearbyDistance;
            if (!nearTarget && !nearPlayer || obstacleMap.Bitmap != null && !obstacleMap.HasObstacleMapLineOfSight(Hints.PathfindMapCenter, Player.Position, actor.Position))
                continue;

            var away = (Player.Position - actor.Position).Normalized();
            if (away != default)
                separationDirection += away;
            ++nearbyPatrols;
        }

        if (nearbyPatrols == 0)
            return false;

        var routeTarget = BossMod.AI.AIManager.Instance?.Controller.NaviTargetPos ?? pullTarget.Position;
        var oppositeRoute = _observedPatrolEnemies.TryGetValue(pullTarget.InstanceID, out var observation) && observation.Until > World.CurrentTime
            ? -observation.Direction
            : (Player.Position - routeTarget).Normalized();
        if (oppositeRoute == default)
            oppositeRoute = (Player.Position - pullTarget.Position).Normalized();
        if (oppositeRoute == default)
            oppositeRoute = pullTarget.Rotation.ToDirection().OrthoR();

        var isolationDirection = (2f * oppositeRoute + separationDirection).Normalized();
        if (isolationDirection == default || isolationDirection.Dot(oppositeRoute) < 0.5f)
            isolationDirection = oppositeRoute;

        _patrolIsolationTargetID = pullTarget.InstanceID;
        _patrolIsolationPosition = FindPatrolIsolationPosition(isolationDirection);
        Hints.FindEnemy(pullTarget)?.ForcePriority(1);
        Hints.ForcedTarget = pullTarget;
        ForbidOtherPatrols(pullTarget, nearbyDistance);
        return true;
    }

    private WPos FindPatrolIsolationPosition(WDir direction)
    {
        for (var directionIndex = 0; directionIndex < 5; ++directionIndex)
        {
            var candidateDirection = directionIndex switch
            {
                1 => direction.Rotate(30f.Degrees()),
                2 => direction.Rotate((-30f).Degrees()),
                3 => direction.Rotate(60f.Degrees()),
                4 => direction.Rotate((-60f).Degrees()),
                _ => direction
            };

            for (var distance = 14f; distance >= 6f; distance -= 2f)
            {
                var candidate = Player.Position + distance * candidateDirection;
                if (IsSafeCombatMovement(candidate))
                    return candidate;
            }
        }
        return Player.Position;
    }

    private bool IsSafeCombatMovement(WPos destination)
    {
        var obstacles = Hints.PathfindMapObstacles;
        var origin = Player.Position;
        if (obstacles.Bitmap == null || !obstacles.HasObstacleMapMovementLineOfSight(Hints.PathfindMapCenter, origin, destination))
            return false;

        var clearance = MathF.Max(Player.HitboxRadius + 0.15f, obstacles.Bitmap.PixelSize * 0.75f);
        var offset = destination - origin;
        var distance = offset.Length();
        var steps = Math.Max(1, (int)MathF.Ceiling(distance / 0.25f));
        if (!ClearOfWalls(destination))
            return false;
        var clearedWall = ClearOfWalls(origin);
        for (var i = 1; i <= steps; ++i)
        {
            var clear = ClearOfWalls(origin + offset * (i / (float)steps));
            if (clearedWall && !clear)
                return false;
            clearedWall |= clear; // Allow movement away from a wall we are already touching.
        }

        foreach (var obstacle in Hints.TemporaryObstacles)
            if (!Avoids(obstacle))
                return false;
        foreach (var zone in Hints.ForbiddenZones)
            if (!Avoids(zone.shapeDistance))
                return false;
        foreach (var enemy in Hints.PotentialTargets)
        {
            var actor = enemy.Actor;
            if (!actor.IsAlly && actor.IsTargetable && !actor.IsDeadOrDestroyed && !actor.PendingDead && !actor.AggroPlayer && !actor.InCombat
                && !AvoidsCircle(actor.Position, PalaceFloorModule.AggroRadius(actor)))
                return false;
        }
        return true;

        bool ClearOfWalls(WPos point)
        {
            for (var x = -1; x <= 1; ++x)
                for (var z = -1; z <= 1; ++z)
                {
                    var probe = point + new WDir(x * clearance, z * clearance);
                    if (!Hints.PathfindMapBounds.Contains(probe - Hints.PathfindMapCenter)
                        || !obstacles.TryWorldToBitmapCell(Hints.PathfindMapCenter, probe, out var cellX, out var cellZ) || obstacles.Bitmap[cellX, cellZ])
                        return false;
                }
            return true;
        }

        bool Avoids(ShapeDistance shape)
        {
            const float margin = 0.25f;
            if (shape.Distance(destination) <= margin)
                return false;
            var previousDistance = shape.Distance(origin);
            if (previousDistance > margin)
                return distance <= 0.01f || !shape.RowIntersectsShape(origin, offset / distance, distance, margin);

            // Leaving an existing hazard is allowed; crossing deeper into it or re-entering is not.
            for (var i = 1; i <= steps; ++i)
            {
                var currentDistance = shape.Distance(origin + offset * (i / (float)steps));
                if (currentDistance <= margin && currentDistance < previousDistance - 0.01f)
                    return false;
                previousDistance = currentDistance;
            }
            return true;
        }

        // Avoids() for an idle enemy's aggro circle. This runs for every enemy on every call (up to ~27 calls a frame), so it
        // uses SDCircle's arithmetic directly instead of allocating an SDCircle each time; the results are the same.
        bool AvoidsCircle(WPos center, float radius)
        {
            const float margin = 0.25f;
            if (CircleDistance(center, radius, destination) <= margin)
                return false;
            var previousDistance = CircleDistance(center, radius, origin);
            if (previousDistance > margin)
                return distance <= 0.01f || !CircleRowIntersects(center, radius, origin, offset / distance, distance, margin);

            for (var i = 1; i <= steps; ++i)
            {
                var currentDistance = CircleDistance(center, radius, origin + offset * (i / (float)steps));
                if (currentDistance <= margin && currentDistance < previousDistance - 0.01f)
                    return false;
                previousDistance = currentDistance;
            }
            return true;
        }
    }

    // same arithmetic as SDCircle.Distance
    private static float CircleDistance(WPos center, float radius, WPos p)
    {
        var dx = p.X - center.X;
        var dz = p.Z - center.Z;
        return MathF.Sqrt(dx * dx + dz * dz) - radius;
    }

    // same arithmetic as SDCircle.RowIntersectsShape
    private static bool CircleRowIntersects(WPos center, float radius, WPos rowStart, WDir dir, float width, float cushion)
    {
        var aX = rowStart.X - center.X;
        var aZ = rowStart.Z - center.Z;
        var b = rowStart + width * dir;
        var dX = b.X - rowStart.X;
        var dZ = b.Z - rowStart.Z;
        var A = dX * dX + dZ * dZ;
        var R = radius + cushion;
        var R2 = R * R;
        if (A <= ShapeDistance.Epsilon)
            return aX * aX + aZ * aZ <= R2 + ShapeDistance.Epsilon;
        var t = -(aX * dX + aZ * dZ) / A;
        if (t < 0f)
            t = 0f;
        else if (t > 1f)
            t = 1f;
        var cX = aX + t * dX;
        var cZ = aZ + t * dZ;
        return cX * cX + cZ * cZ <= R2 + ShapeDistance.Epsilon;
    }

    private void ForbidOtherPatrols(Actor isolationTarget, float maxDistance)
    {
        foreach (var candidate in Hints.PotentialTargets)
        {
            var actor = candidate.Actor;
            if (!IsUnengagedPatrolCandidate(actor, isolationTarget) || !IsObservedPatrol(actor) || Player.DistanceToHitbox(actor) > maxDistance)
                continue;

            candidate.ForcePriority(AIHints.Enemy.PriorityForbidden);
            Hints.AddForbiddenZone(new SDCircle(actor.Position, actor.HitboxRadius + 10f), source: actor.InstanceID);
        }
    }

    private void ResetPatrolIsolation()
    {
        _patrolIsolationTargetID = 0;
        _patrolIsolationPosition = default;
    }

    private bool IsObservedPatrol(Actor actor) => _observedPatrolEnemies.TryGetValue(actor.InstanceID, out var observation) && observation.Until > World.CurrentTime;

    private bool IsUnsafePalaceTarget(Actor actor) => World.DeepDungeon.DungeonId == DeepDungeonState.DungeonType.POTD && !World.DeepDungeon.IsBossFloor
        && (Hints.FindEnemy(actor) is { Spikes: true, Priority: AIHints.Enemy.PriorityForbidden }
            || !actor.InCombat && !actor.AggroPlayer && BossMod.Global.DeepDungeon.AutoClear.HasDangerousOutOfCombatStatus(actor));

    private static bool IsUnengagedPatrolCandidate(Actor actor, Actor primaryTarget) => actor.InstanceID != primaryTarget.InstanceID && !actor.IsAlly && actor.IsTargetable && !actor.IsDeadOrDestroyed && !actor.InCombat && !actor.AggroPlayer;

    private void SetupKiteZone(in Strategy strategy, Actor? primaryTarget)
    {
        var classCategory = Player.Class.GetClassCategory();
        var canMeleeSpin = World.DeepDungeon.DungeonId == DeepDungeonState.DungeonType.POTD && classCategory is ClassCategory.Tank or ClassCategory.Melee;
        var canRangedOrbit = World.DeepDungeon.DungeonId == DeepDungeonState.DungeonType.POTD && classCategory == ClassCategory.PhysRanged;
        var canOrbit = canMeleeSpin || canRangedOrbit;

        if ((!IsRanged && !canMeleeSpin) || primaryTarget == null || !Player.InCombat || !strategy.Kite.IsEnabled() || World.DeepDungeon.IsBossFloor)
        {
            _orbitTargetID = 0;
            return;
        }

        // anointed = full heal every tick, no need to worry about autos
        if (Player.FindStatus(SID.Anointed) != null)
        {
            _orbitTargetID = 0;
            return;
        }

        // wew
        if (NoMeleeAutos.Contains(primaryTarget.OID))
        {
            _orbitTargetID = 0;
            return;
        }

        // stationary jobs can stop kiting while the mob is busy casting; orbit-capable jobs keep moving
        if (primaryTarget.CastInfo != null && !canOrbit)
        {
            _orbitTargetID = 0;
            return;
        }

        var primaryPos = primaryTarget.Position;
        var goalFactor = 0.05f;

        if (canOrbit)
        {
            if (_orbitTargetID != primaryTarget.InstanceID)
            {
                _orbitTargetID = primaryTarget.InstanceID;
                _orbitProgressPosition = Player.Position;
                _orbitProgressAt = World.CurrentTime;
                var initialFacing = primaryTarget.Rotation.ToDirection();
                _orbitDirection = initialFacing.OrthoR().Dot(Player.Position - primaryPos) >= 0 ? 1 : -1;
            }
            else if ((Player.Position - _orbitProgressPosition).LengthSq() >= 2.25f)
            {
                _orbitProgressPosition = Player.Position;
                _orbitProgressAt = World.CurrentTime;
            }
            else if ((World.CurrentTime - _orbitProgressAt).TotalSeconds >= 2)
            {
                if (!canMeleeSpin)
                    _orbitDirection = -_orbitDirection;
                _orbitProgressPosition = Player.Position;
                _orbitProgressAt = World.CurrentTime;
            }

            if (canMeleeSpin)
            {
                const float meleeRange = 2.6f;
                var facing = primaryTarget.Rotation.ToDirection();
                var playerRadial = (Player.Position - primaryPos).Normalized();
                if (playerRadial == default)
                    playerRadial = facing;
                var maximumCenterDistance = primaryTarget.HitboxRadius + meleeRange - 0.2f;
                var minimumCenterDistance = MathF.Max(0.35f, primaryTarget.HitboxRadius * 0.65f);
                var spinCenterDistance = Math.Clamp(primaryTarget.HitboxRadius + 0.05f - _spinDistanceAdjustments.GetValueOrDefault(primaryTarget.OID), minimumCenterDistance, maximumCenterDistance);
                Hints.GoalZones.Add(AIHints.GoalSingleTarget(primaryTarget, meleeRange, 30f));
                var spinPoint = SpinPoint(_orbitDirection);
                if (!IsSafeCombatMovement(spinPoint))
                {
                    spinPoint = SpinPoint(-_orbitDirection);
                    if (!IsSafeCombatMovement(spinPoint))
                    {
                        if (Player.DistanceToHitbox(primaryTarget) <= meleeRange)
                            Hints.GoalZones.Add(AIHints.GoalSingleTarget(Player.Position, 0.5f, 35f));
                        return;
                    }
                    _orbitDirection = -_orbitDirection;
                }
                // Reward the reachable side only, rather than the entire ring behind a wall or another enemy.
                Hints.GoalZones.Add(AIHints.GoalSingleTarget(spinPoint, MathF.Max(0.4f, Hints.PathfindMapObstacles.Bitmap.PixelSize * 0.75f), 40f));
                Hints.GoalZones.Add(AIHints.GoalProximity(spinPoint, 3f, 60f));
                Hints.MaxCastTime = 0;

                WPos SpinPoint(int direction)
                {
                    var movementTangent = direction > 0 ? playerRadial.OrthoR() : playerRadial.OrthoL();
                    var facingTangent = direction > 0 ? facing.OrthoR() : facing.OrthoL();
                    var spinDirection = facingTangent.Dot(movementTangent) >= 0.5f ? facingTangent : (playerRadial + movementTangent).Normalized();
                    return primaryPos + spinCenterDistance * spinDirection;
                }
            }
            else
            {
                const float maxRange = 25f;
                const float maxKite = 9f;
                var total = maxRange + Player.HitboxRadius + primaryTarget.HitboxRadius;
                var totalKite = maxKite + Player.HitboxRadius + primaryTarget.HitboxRadius;
                var desiredRange = (totalKite + total) * 0.5f;
                var radial = (Player.Position - primaryPos).Normalized();
                if (radial == default)
                    radial = primaryTarget.Rotation.ToDirection();
                Hints.GoalZones.Add(AIHints.GoalDonut(primaryPos, totalKite, total, 20f));
                var orbitPoint = OrbitPoint(_orbitDirection);
                if (!IsSafeCombatMovement(orbitPoint))
                {
                    orbitPoint = OrbitPoint(-_orbitDirection);
                    if (!IsSafeCombatMovement(orbitPoint))
                    {
                        if (Player.DistanceToHitbox(primaryTarget) <= maxRange)
                            Hints.GoalZones.Add(AIHints.GoalSingleTarget(Player.Position, 0.5f, 25f));
                        return;
                    }
                    _orbitDirection = -_orbitDirection;
                }
                Hints.GoalZones.Add(AIHints.GoalProximity(orbitPoint, total * 2f, 20f));

                WPos OrbitPoint(int direction)
                {
                    var tangent = direction > 0 ? radial.OrthoR() : radial.OrthoL();
                    return primaryPos + desiredRange * (desiredRange * radial + 6f * tangent).Normalized();
                }
            }
        }
        else
        {
            _orbitTargetID = 0;
            const float maxRange = 25f;
            const float maxKite = 9f;
            var total = maxRange + Player.HitboxRadius + primaryTarget.HitboxRadius;
            var totalKite = maxKite + Player.HitboxRadius + primaryTarget.HitboxRadius;
            Hints.GoalZones.Add(pos =>
            {
                var dist = (pos - primaryPos).Length();
                return dist <= total && dist >= totalKite ? goalFactor : default;
            });
        }
    }

    private void DoTransformActions(in Strategy strategy, Actor? primaryTarget, Transformation t)
    {
        if (primaryTarget == null)
            return;

        Func<WPos, float> goal;
        ActionID attack;
        int numTargets;
        var castTime = 0f;

        switch (t)
        {
            case Transformation.Manticore:
                goal = Hints.GoalSingleTarget(primaryTarget, Player, World.Actors, 3f);
                numTargets = 1;
                attack = ActionID.MakeSpell(Roleplay.AID.Pummel);
                break;
            case Transformation.Succubus:
                goal = Hints.GoalSingleTarget(primaryTarget, Player, World.Actors, 25f);
                numTargets = Hints.NumPriorityTargetsInAOECircle(primaryTarget.Position, 5f);
                attack = ActionID.MakeSpell(Roleplay.AID.VoidFireII);
                castTime = 2.5f;
                break;
            case Transformation.Kuribu:
                // heavenly judge is ground targeted
                goal = AIHints.GoalSingleTarget(primaryTarget.Position, 25f);
                numTargets = Hints.NumPriorityTargetsInAOECircle(primaryTarget.Position, 6f);
                attack = ActionID.MakeSpell(Roleplay.AID.HeavenlyJudge);
                castTime = 2.5f;
                break;
            case Transformation.Dreadnaught:
                goal = Hints.GoalSingleTarget(primaryTarget, Player, World.Actors, 3f);
                numTargets = 1;
                attack = ActionID.MakeSpell(Roleplay.AID.Rotosmash);
                break;
            case Transformation.Bomb:
                numTargets = 1;
                goal = Hints.GoalSingleTarget(primaryTarget, Player, World.Actors, 14f);
                attack = ActionID.MakeSpell(Roleplay.AID.BigBurst);
                break;
            case Transformation.Mudball:
                numTargets = 1;
                goal = AIHints.GoalSingleTarget(primaryTarget.Position, 25f);
                attack = ActionID.MakeSpell(Roleplay.AID.RockyRoll);
                break;
            default:
                return;
        }

        if (numTargets == 0)
            return;

        Hints.GoalZones.Add(goal);
        if (t == Transformation.Mudball)
        {
            if (primaryTarget.Position.InCircle(Player.Position, 25))
                Hints.ActionsToExecute.Push(attack, Player, ActionQueue.Priority.High, facingAngle: Player.AngleTo(primaryTarget));
        }
        else if (t == Transformation.Bomb)
        {
            if (primaryTarget.Position.InCircle(Player.Position, 15 + primaryTarget.HitboxRadius))
                Hints.ActionsToExecute.Push(attack, Player, ActionQueue.Priority.High, castTime: 1);
        }
        else
            Hints.ActionsToExecute.Push(attack, primaryTarget, ActionQueue.Priority.High, targetPos: primaryTarget.PosRot.XYZ(), castTime: castTime - 0.5f);
    }

    private bool ShouldPotion(in Strategy strategy, ActionID regenAction)
    {
        if (!strategy.Potion.IsEnabled())
            return false;

        // external heals
        if (World.Actors.Any(w => w.OID == (uint)OID.Unei) || Player.FindStatus(SID.Anointed) != null)
            return false;

        var sustainingPotion = regenAction == ActionDefinitions.IDPotionSustaining;
        var ratio = sustainingPotion
            ? 0.7f
            : Player.ClassCategory is ClassCategory.Tank ? 0.4f : 0.6f;
        var hpThresholdReached = sustainingPotion ? Player.PendingHPRatio <= ratio : Player.PendingHPRatio < ratio;
        return hpThresholdReached && Player.FindStatus(648u) == null && (Player.InCombat || sustainingPotion);
    }
}
