﻿using System.Diagnostics.CodeAnalysis;
using System.Linq;
using System.Text;
using Content.Server.Administration.Logs;
using Content.Server.CartridgeLoader.Cartridges;
using Content.Server.DetailExaminable;
using Content.Server.GameTicking;
using Content.Server.GameTicking.Rules.Components;
using Content.Server.Humanoid;
using Content.Server.Jobs;
using Content.Server.Mind.Components;
using Content.Server.Objectives;
using Content.Server.Players;
using Content.Server.Prayer;
using Content.Server.Preferences.Managers;
using Content.Server.Shuttles.Components;
using Content.Server.Spawners.Components;
using Content.Server.Station.Systems;
using Content.Server.Traitor;
using Content.Server.Traits.Assorted;
using Content.Shared.CombatMode.Pacification;
using Content.Shared.Database;
using Content.Shared.Humanoid;
using Content.Shared.Humanoid.Prototypes;
using Content.Shared.Mobs;
using Content.Shared.Mobs.Systems;
using Content.Shared.Preferences;
using Content.Shared.Roles;
using Content.Shared.Tag;
using Robust.Server.GameObjects;
using Robust.Shared.Map;
using Robust.Shared.Prototypes;
using Robust.Shared.Random;
using Content.Server.Ghost.Roles.Events;
using Content.Server.Mind;
using Content.Server.PDA;
using Content.Server.Roles;
using Content.Server.Station.Components;
using Content.Shared.CartridgeLoader;
using Content.Shared.Inventory;
using Content.Shared.PDA;
using Robust.Shared.Utility;

namespace Content.Server.Backmen.EvilTwin;

public sealed class EvilTwinSystem : EntitySystem
{
    public override void Initialize()
    {
        base.Initialize();

        SubscribeLocalEvent<EvilTwinSpawnerComponent, PlayerAttachedEvent>(OnPlayerAttached);
        SubscribeLocalEvent<EvilTwinComponent, MindAddedMessage>(OnMindAdded);
        SubscribeLocalEvent<RoundEndTextAppendEvent>(OnRoundEnd);
        SubscribeLocalEvent<EvilTwinSpawnerComponent,GhostRoleSpawnerUsedEvent>(OnGhostRoleSpawnerUsed);
        SubscribeLocalEvent<EvilTwinComponent, MobStateChangedEvent>(OnHandleComponentState);
    }

    private void OnGhostRoleSpawnerUsed(EntityUid uid, EvilTwinSpawnerComponent component, GhostRoleSpawnerUsedEvent args)
    {
        //forward
        if(TryComp<EvilTwinSpawnerComponent>(args.Spawner, out var comp))
        {
            component.TargetForce = comp.TargetForce;
        }
    }

    private void OnHandleComponentState(EntityUid uid, EvilTwinComponent component, MobStateChangedEvent args)
    {
        if (args.NewMobState==MobState.Dead && TryComp<MindContainerComponent>(uid, out var mind) && mind.Mind!=null)
        {
            mind.Mind.PreventGhosting = false;
        }
    }

    public bool MakeTwin([NotNullWhen(true)] out EntityUid? TwinSpawn, EntityUid? uid = null)
    {
        TwinSpawn = null;

        var station = _stationSystem.GetStations().FirstOrNull(HasComp<StationEventEligibleComponent>);
        if (station == null || !TryComp<StationDataComponent>(station, out var stationDataComponent))
        {
            return false;
        }

        var spawnGrid = stationDataComponent.Grids.FirstOrNull(HasComp<BecomesStationComponent>);
        if (spawnGrid == null)
        {
            return false;
        }

        var latejoin = (from s in EntityQuery<SpawnPointComponent, TransformComponent>()
        where s.Item1.SpawnType == SpawnPointType.LateJoin && s.Item2.GridUid == spawnGrid
        select s.Item2.Coordinates).ToList();

        if(latejoin.Count == 0)
        {
            return false;
        }

        var coords = _random.Pick(latejoin);
        TwinSpawn = Spawn(SpawnPointPrototype, coords);

        if(uid.HasValue && TwinSpawn.HasValue)
        {
            EnsureComp<EvilTwinSpawnerComponent>(TwinSpawn.Value).TargetForce = uid.Value;
        }

        return true;
    }

    private void OnPlayerAttached(EntityUid uid, EvilTwinSpawnerComponent component, PlayerAttachedEvent args)
    {
        HumanoidCharacterProfile? pref = null;

        EntityUid? targetUid = null;

        if(component.TargetForce != EntityUid.Invalid){
            if(IsEligibleHumanoid(component.TargetForce)){
                targetUid = component.TargetForce;
            }
        }else{
            TryGetEligibleHumanoid(out targetUid);
        }

        if (targetUid.HasValue)
        {
            var xform = Transform(uid);
            (var twinMob, pref) = SpawnEvilTwin(targetUid.Value, xform.Coordinates);
            if (twinMob != null)
            {
                var playerData = args.Player.ContentData();
                if (playerData != null)
                {
                    var mind = playerData.Mind;
                    _mindSystem.TransferTo(mind!,twinMob);

                    var station = _stationSystem.GetOwningStation(targetUid.Value) ?? _stationSystem.GetStations().FirstOrNull(HasComp<StationEventEligibleComponent>);
                    if (pref != null && station!= null && TryComp<MindContainerComponent>(targetUid, out var targetMind) && mind!=null)
                    {
                        RaiseLocalEvent(new PlayerSpawnCompleteEvent(twinMob.Value, targetMind.Mind!.Session!, targetMind.Mind.CurrentJob?.Prototype.ID, false,
                            0, station.Value, pref));
                        if (_inventory.TryGetSlotEntity(targetUid.Value,"id",out var targetPda) &&
                            _inventory.TryGetSlotEntity(twinMob.Value,"id",out var twinPda) &&
                            TryComp<CartridgeLoaderComponent>(targetPda, out var targetPdaComp) &&
                            TryComp<CartridgeLoaderComponent>(twinPda, out var twinPdaComp))
                        {
                            var twinProgram = twinPdaComp.InstalledPrograms.FirstOrDefault(HasComp<NotekeeperCartridgeComponent>);
                            var targetProgram = targetPdaComp.InstalledPrograms.FirstOrDefault(HasComp<NotekeeperCartridgeComponent>);
                            if (twinProgram.Valid &&
                                targetProgram.Valid &&
                                TryComp<NotekeeperCartridgeComponent>(targetProgram, out var targetNotesComp) &&
                                TryComp<NotekeeperCartridgeComponent>(twinProgram, out var twinNotesComp))
                            {
                                foreach (var note in targetNotesComp.Notes)
                                {
                                    twinNotesComp.Notes.Add(note);
                                }
                            }
                        }

                    }

                    _adminLogger.Add(LogType.Action, LogImpact.Extreme, $"{_entityManager.ToPrettyString(twinMob.Value)} take EvilTwin with target {_entityManager.ToPrettyString(targetUid.Value)}");
                }
            }
        }
        else
        {

            _adminLogger.Add(LogType.Action, LogImpact.Extreme, $"{_entityManager.ToPrettyString(uid)} take EvilTwin with no target (delete)");
            _prayerSystem.SendSubtleMessage(args.Player, args.Player, Loc.GetString("evil-twin-error-message"),  Loc.GetString("prayer-popup-subtle-default"));
        }


        QueueDel(uid);
    }

    private void OnMindAdded(EntityUid uid, EvilTwinComponent component, MindAddedMessage args)
    {
        if (!TryComp<MindContainerComponent>(uid, out var mindComponent) || mindComponent.Mind == null)
        {
            return;
        }

        var mind = mindComponent.Mind;
        _mindSystem.AddRole(mind,new TraitorRole(mind, _prototype.Index<AntagPrototype>(EvilTwinRole)));

        _mindSystem.TryAddObjective(mind, _prototype.Index<ObjectivePrototype>(KillObjective));
        _mindSystem.TryAddObjective(mind, _prototype.Index<ObjectivePrototype>(EscapeObjective));

        mind.PreventGhosting = true;

        RemComp<PacifistComponent>(uid);
        RemComp<PacifiedComponent>(uid);

        EnsureComp<PendingClockInComponent>(uid);

        _tagSystem.AddTag(uid,"CannotSuicide");
    }

#region  OnRoundEnd


    private void OnRoundEnd(RoundEndTextAppendEvent ev)
    {
        var twins = EntityQuery<EvilTwinComponent, MindContainerComponent>().ToArray();
        if (twins.Length < 1)
        {
            return;
        }

        var result = new StringBuilder();
        result.Append(Loc.GetString("evil-twin-round-end-result", ("evil-twin-count", twins.Length)));
        foreach (var (twin, mind) in twins)
        {
            if (mind.Mind == null)
                continue;
            var name = mind.Mind.CharacterName;
            var username = mind.Mind.Session?.Name;
            var objectives = mind.Mind.AllObjectives.ToArray();
            if (objectives.Length == 0)
            {
                if (username != null)
                {
                    if (name == null)
                    {
                        result.Append("\n" + Loc.GetString("evil-twin-user-was-an-evil-twin", ("user", username)));
                    }
                    else
                    {
                        result.Append("\n" + Loc.GetString("evil-twin-user-was-an-evil-twin-named", ("user", username), ("name", name)));
                    }
                }
                else if (name != null)
                {
                    result.Append("\n" + Loc.GetString("evil-twin-was-an-evil-twin-named", ("name", name)));
                }

                continue;
            }
            if (username != null)
            {
                if (name == null)
                {
                    result.Append("\n" + Loc.GetString("evil-twin-user-was-an-evil-twin-with-objectives", ("user", username)));
                }
                else
                {
                    result.Append("\n" + Loc.GetString("evil-twin-user-was-an-evil-twin-with-objectives-named", ("user", username), ("name", name)));
                }
            }
            else if (name != null)
            {
                result.Append("\n" + Loc.GetString("evil-twin-was-an-evil-twin-with-objectives-named", ("name", name)));
            }

            foreach (var condition in objectives.GroupBy(x=>x.Prototype.Issuer).SelectMany(x=>x.SelectMany(z=>z.Conditions)))
            {
                if (condition is Mind.MindNoteCondition)
                {
                    continue;
                }
                var progress = condition.Progress;
                if (progress > 0.99f)
                {
                    result.Append("\n- " + Loc.GetString("traitor-objective-condition-success", ("condition", condition.Title), ("markupColor", "green")));
                }
                else
                {
                    result.Append("\n- " + Loc.GetString("traitor-objective-condition-fail", ("condition", condition.Title), ("progress", (int) (progress * 100f)), ("markupColor", "red")));
                }
            }
        }

        ev.AddLine(result.ToString());
    }
#endregion
    private bool IsEligibleHumanoid(EntityUid? uid)
    {
        if(!uid.HasValue || !uid.Value.IsValid() || uid.Value.IsClientSide())
        {
            return false;
        }
        return !HasComp<EvilTwinComponent>(uid) && !HasComp<NukeOperativeComponent>(uid);
    }
    private bool TryGetEligibleHumanoid([NotNullWhen(true)] out EntityUid? uid)
    {
        var targets = EntityQuery<ActorComponent, MindContainerComponent, HumanoidAppearanceComponent>().ToList();
        _random.Shuffle(targets);
        foreach (var (_, target, _) in targets)
        {
            if(target?.Mind == null)
            {
                continue;
            }
            var mind = target.Mind!;
            if (mind?.CurrentJob == null)
            {
                continue;
            }

            if (mind is not { CurrentEntity: not null })
                continue;
            var targetUid = mind.CurrentEntity;
            if (!IsEligibleHumanoid(targetUid))
                continue;
            uid = targetUid;
            return true;

        }

        uid = null;
        return false;
    }

    private (EntityUid?, HumanoidCharacterProfile? pref) SpawnEvilTwin(EntityUid target, EntityCoordinates coords)
    {
        if (!TryComp<MindContainerComponent>(target, out var mind) ||
            !TryComp<HumanoidAppearanceComponent>(target, out var humanoid) ||
            !TryComp<ActorComponent>(target, out var actor) ||
            !_prototype.TryIndex<SpeciesPrototype>(humanoid.Species, out var species))
        {
            return (null,null);
        }

        var pref = (HumanoidCharacterProfile) _prefs.GetPreferences(actor.PlayerSession.UserId).SelectedCharacter;
        var twinUid = Spawn(species.Prototype, coords);
        _humanoid.LoadProfile(twinUid, pref);
        _metaDataSystem.SetEntityName(twinUid,MetaData(target).EntityName);
        if (TryComp<DetailExaminableComponent>(target, out var detail))
        {
            EnsureComp<DetailExaminableComponent>(twinUid).Content = detail.Content;
        }

        var twinTargetMindJob = mind.Mind?.CurrentJob;
        if (twinTargetMindJob?.StartingGear != null)
        {
            if (_prototype.TryIndex<StartingGearPrototype>(twinTargetMindJob?.StartingGear!, out var gear))
            {
                _stationSpawning.EquipStartingGear(twinUid, gear, pref);
                _stationSpawning.EquipIdCard(twinUid, pref.Name, twinTargetMindJob!.Prototype, _stationSystem.GetOwningStation(target));
            }

            foreach (var special in twinTargetMindJob!.Prototype.Special)
            {
                if (special is AddComponentSpecial)
                {
                    special.AfterEquip(twinUid);
                }
            }
        }

        EnsureComp<EvilTwinComponent>(twinUid).TwinMind = mind?.Mind;

        return (twinUid,pref);
    }
    [Dependency] private readonly InventorySystem _inventory = default!;

    [Dependency] private readonly IRobustRandom _random = default!;

    [Dependency] private readonly IPrototypeManager _prototype = default!;

    [Dependency] private readonly IServerPreferencesManager _prefs = default!;

    [Dependency] private readonly HumanoidAppearanceSystem _humanoid = default!;

    [Dependency] private readonly StationSpawningSystem _stationSpawning = default!;

    [Dependency] private readonly StationSystem _stationSystem = default!;

    [Dependency] private readonly PrayerSystem _prayerSystem = default!;

    [Dependency] private readonly IAdminLogManager _adminLogger = default!;

    [Dependency] private readonly IEntityManager _entityManager = default!;

    [Dependency] private readonly TagSystem _tagSystem = default!;

    [Dependency] private readonly MindSystem _mindSystem = default!;

    [Dependency] private readonly MetaDataSystem _metaDataSystem = default!;

    private const string EvilTwinRole = "EvilTwin";

    private const string KillObjective = "KillObjectiveEvilTwin";

    private const string EscapeObjective = "EscapeShuttleObjectiveEvilTwin";

    private const string SpawnPointPrototype = "SpawnPointEvilTwin";
}
