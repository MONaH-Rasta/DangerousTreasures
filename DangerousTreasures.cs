using Facepunch;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Oxide.Core;
using Oxide.Core.Libraries.Covalence;
using Oxide.Core.Plugins;
using Oxide.Plugins.DangerousTreasuresExtensionMethods;
using Rust;
using Rust.Ai.Gen2;
using Rust.Ai.Gen2.Nav;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;

namespace Oxide.Plugins
{
    [Info("Dangerous Treasures", "nivex", "3.0.0")]
    [Description("Event with treasure chests.")]
    internal class DangerousTreasures : RustPlugin
    {
        [PluginReference] Plugin ZoneManager, Economics, ServerRewards, GUIAnnouncements, MarkerManager, Kits, Duelist, RaidableBases, AbandonedBases, Notify, AdvancedAlerts, Clans, Friends;

        private new const string Name = "Dangerous Treasures";
        private bool wipeChestsSeed;
        private StoredData data = new();
        private List<int> BlockedLayers = new() { (int)Layer.Water, (int)Layer.Construction, (int)Layer.Trigger, (int)Layer.Prevent_Building, (int)Layer.Deployed, (int)Layer.Tree, (int)Layer.Clutter };
        private Dictionary<ulong, HumanoidBrain> HumanoidBrains = new();
        private List<MonumentInfoEx> _allowedMonuments = new();
        private List<MonumentInfoEx> monuments = new();
        private Dictionary<Vector3, ZoneInfo> managedZones = new();
        private Dictionary<NetworkableId, TreasureChest> treasureChests = new();
        private Dictionary<NetworkableId, string> looters = new();
        private Dictionary<string, ItemDefinition> _definitions = new();
        private Dictionary<string, SkinInfo> Skins = new();
        private List<ulong> newmanProtections = new();
        private List<ulong> indestructibleWarnings = new(); // indestructible messages limited to once every 10 seconds
        private List<ulong> drawGrants = new(); // limit draw to once every 15 seconds by default
        private List<int> obstructionLayers = new() { Layers.Mask.Player_Server, Layers.Mask.Construction, Layers.Mask.Deployed };
        private List<string> _blockedColliders = new() { "powerline_", "invisible_", "TopCol", "train", "swamp_", "floating_" };
        private List<string> underground = new() { "Cave", "Sewer Branch", "Military Tunnel", "Underwater Lab", "Train Tunnel" };
        private List<Vector3> _gridPositions = new();
        private List<Vector3> _gridPositionsSrc = new();
        private bool IsGridReady;
        private bool IsMonumentsReady;
        private Coroutine _gridCo;
        private RaycastHit[] sharedRockHits = new RaycastHit[256];
        private Collider[] sharedRockColliders = new Collider[256];
        private const int TARGET_MASK = 8454145;
        private const int targetMask = Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Default;
        private const int visibleMask = Layers.Mask.Deployed | Layers.Mask.Construction | targetMask;
        private const int obstructionLayer = Layers.Mask.Player_Server | Layers.Mask.Construction | Layers.Mask.Deployed;
        private const int heightLayer = TARGET_MASK | Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.Clutter;
        private StringBuilder _sb = new();
        private Vector3 sd_customPos;
        private ulong BotIdCounter = 624922525;
        private Dictionary<int, double> eventRetries = new();

        public class ZoneInfo
        {
            public Vector3 Position;
            public Vector3 Size;
            public float Distance;
            public OBB OBB;
        }

        public class SkinInfo
        {
            public List<ulong> skins = new();
            public List<ulong> workshopSkins = new();
            public List<ulong> importedSkins = new();
            public List<ulong> allSkins = new();
        }

        private class PlayerInfo
        {
            public int StolenChestsTotal;
            public int StolenChestsSeed;
            public PlayerInfo() { }
        }

        private class StoredData
        {
            public Dictionary<string, PlayerInfo> Players = new();
            [JsonProperty(PropertyName = "Seconds Until Event")]
            public Dictionary<int, double> SecondsUntilEvent = new() { [0] = double.MinValue };
            public string CustomPosition;
            public int TotalEvents = 0;
            public StoredData() { }
        }

        public class HumanoidNPC : ScientistNPC
        {
            public new HumanoidBrain Brain;

            public TreasureChest tc;

            public string DisplayNameOverride;

            public DifficultyLevel Options;

            public Configuration config;

            public DangerousTreasures Instance;

            public new Translate.Phrase LootPanelTitle => displayName;

            public override string Categorize() => "Humanoid";

            public override bool ShouldDropActiveItem() => false;

            public override string displayName => DisplayNameOverride;

            public override void AttackerInfo(ProtoBuf.PlayerLifeStory.DeathInfo info)
            {
                info.attackerName = displayName;
                info.attackerSteamID = userID;
                info.inflictorName = inventory?.containerBelt?.GetSlot(0)?.info?.shortname;
                if (Brain != null) info.attackerDistance = Vector3.Distance(Brain.ServerPosition, Brain.AttackPosition);
            }

            public override void OnDied(HitInfo info)
            {
                BasePlayer.bots.Remove(this);
                if (Brain != null) Brain.DisableShouldThink();
                if (Instance != null && tc != null)
                {
                    svActiveItemID = default;
                    SendNetworkUpdate(BasePlayer.NetworkQueue.Update);
                    tc.npcs.Remove(this);
                    if (tc.whenNpcsDie && tc.npcs.Count == 0) tc.Unlock();
                    if (config.Unlock.LockToPlayerOnNpcDeath) tc.TrySetOwner(info);
                    if (Options.Event.DestructTimeResetsWhenKilled && info != null && info.Initiator.Is(out BasePlayer attacker) && attacker.userID.IsSteamId()) tc.SetDestructTime();
                }
                base.OnDied(info);
            }

            public override void DoServerDestroy()
            {
                if (tc != null && tc.npcs != null) tc.npcs.Remove(this);
                BasePlayer.bots.Remove(this);
                if (Brain != null)
                {
                    AttackEntity attackEntity = Brain._attackEntity;
                    if (!attackEntity.IsKilled()) attackEntity.SetHeld(false);
                    Brain.DisableShouldThink();
                }

                base.DoServerDestroy();
                BasePlayer.freeBotIds.Remove(userID);
            }

            public override BaseCorpse CreateCorpse(PlayerFlags flagsOnDeath, Vector3 posOnDeath, Quaternion rotOnDeath, List<TriggerBase> triggersOnDeath, bool forceServerSide = false)
            {
                if (inventory == null || Brain == null)
                {
                    inventory.SafelyStrip();
                    CheckCorpse(null);
                    return null;
                }
                bool keepInventory = !(Brain.isMurderer ? Options.NPC.Murderers.DespawnInventory : Options.NPC.Scientists.DespawnInventory);
                bool hasPrefabLoot = !LootSpawnSlots.IsNullOrEmpty();
                if (!keepInventory && !hasPrefabLoot)
                {
                    inventory.SafelyStrip();
                    CheckCorpse(null);
                    return null;
                }
                if (keepInventory) inventory.containerWear.SafelyRemove("gloweyes");
                else inventory.SafelyStrip();
                if (!RemoveOwnershipPass() && !hasPrefabLoot)
                {
                    CheckCorpse(null);
                    return null;
                }
                PlayerCorpse corpse = DropCorpse("assets/prefabs/player/player_corpse.prefab") as PlayerCorpse;
                if (corpse == null)
                {
                    CheckCorpse(null);
                    return null;
                }
                if (NavAgent != null) corpse.transform.position += Vector3.down * NavAgent.baseOffset;
                corpse.TakeFrom(this, inventory.containerMain, inventory.containerWear, inventory.containerBelt);
                corpse.playerName = displayName;
                corpse.playerSteamID = userID;
                corpse.skinID = 14922525;
                corpse.Spawn();
                if (corpse.IsKilled())
                {
                    CheckCorpse(null);
                    return null;
                }
                corpse.TakeChildren(this);
                var alternate = Brain.isMurderer ? Options.NPC.Murderers.Alternate : Options.NPC.Scientists.Alternate;
                bool canPopulateLoot = !alternate.CallHook || Interface.CallHook("OnCorpsePopulate", this, corpse) == null;
                if (corpse.IsKilled())
                {
                    CheckCorpse(null);
                    return null;
                }
                if (canPopulateLoot && !LootSpawnSlots.IsNullOrEmpty())
                {
                    foreach (var lootSpawnSlot in LootSpawnSlots)
                    {
                        for (int k = 0; k < lootSpawnSlot.numberToSpawn; k++)
                        {
                            if (UnityEngine.Random.Range(0f, 1f) <= lootSpawnSlot.probability) lootSpawnSlot.definition.SpawnIntoContainer(corpse.containers[0]);
                        }
                    }
                }
                CheckCorpse(corpse);
                return corpse;
            }

            private void CheckCorpse(PlayerCorpse corpse)
            {
                if (corpse != null && Brain != null)
                {
                    float time = Brain.isMurderer ? (Options.NPC.Murderers.DespawnInventory ? Options.NPC.Murderers.DespawnInventoryTime : Options.NPC.Murderers.CorpseDespawnTime) : (Options.NPC.Scientists.DespawnInventory ? Options.NPC.Scientists.DespawnInventoryTime : Options.NPC.Scientists.CorpseDespawnTime);
                    corpse.Invoke(corpse.SafelyKill, time);
                    corpse.playerName = displayName;
                }
                if (Brain != null) UnityEngine.Object.Destroy(Brain);
            }

            private bool RemoveOwnershipPass()
            {
                if (!Instance.config.BlockPaidContent) return true;
                using var itemList = Facepunch.Pool.Get<PooledList<Item>>();
                inventory.GetAllItems(itemList);
                bool hasItems = false;
                for (int i = itemList.Count - 1; i >= 0; i--)
                {
                    Item item = itemList[i];
                    if (!Instance.RequiresOwnership(item.info, item.skin))
                    {
                        hasItems = true;
                        continue;
                    }
                    item.GetHeldEntity().SafelyKill();
                    item.RemoveFromContainer();
                    item.Remove(0f);
                }
                return hasItems;
            }
        }

        public class HumanoidBrain : ScientistBrain
        {
            public void DisableShouldThink()
            {
                isDisabled = true;
                lastWarpTime = float.MaxValue;
                sleeping = true;
                SetEnabled(false);
                try { CancelInvoke(); } catch { }
                AIThinkManager._processQueue.Remove(npc);
                if (HumanoidBrains.Remove(uid) && HumanoidBrains.Count == 0 && Instance.Manager != null)
                {
                    if (!config.NewmanMode.Harm) Instance.Unsubscribe(nameof(OnNpcTarget));
                    Instance.Unsubscribe(nameof(OnNpcResume));
                    Instance.Unsubscribe(nameof(OnNpcDestinationSet));
                    Instance.Unsubscribe(nameof(CanBradleyApcTarget));
                }
                if (Rust.Application.isQuitting)
                {
                    return;
                }
                if (npc != null && BaseEntity.Query.Server != null)
                {
                    BaseEntity.Query.Server.RemoveBrain(npc);
                }
                LeaveGroup();
            }

            internal DangerousTreasures Instance;
            internal Dictionary<ulong, HumanoidBrain> HumanoidBrains;

            internal enum AttackType { BaseProjectile, FlameThrower, Melee, Water, None }
            internal string displayName;
            internal Transform NpcTransform;
            internal HumanoidNPC npc;
            internal AttackEntity _attackEntity;
            internal FlameThrower flameThrower;
            internal LiquidWeapon liquidWeapon;
            internal BaseMelee baseMelee;
            internal BaseProjectile baseProjectile;
            internal BasePlayer AttackTarget;
            internal Transform AttackTransform;
            internal TreasureChest tc;
            internal NpcSettings Settings;
            internal List<Vector3> positions;
            internal Vector3 DestinationOverride;
            internal bool isDisabled;
            internal bool InitializedAI;
            internal bool isMurderer;
            internal ulong uid;
            internal float lastWarpTime;
            internal float softLimitSenseRange;
            internal float nextAttackTime;
            internal float attackRange;
            internal float attackCooldown;
            internal AttackType attackType = AttackType.None;
            internal BaseNavigator.NavigationSpeed CurrentSpeed = BaseNavigator.NavigationSpeed.Normal;

            internal DifficultyLevel Options => tc.Options;
            internal Vector3 AttackPosition => AttackTransform == null ? default : AttackTransform.position;

            internal Vector3 ServerPosition => NpcTransform == null ? default : NpcTransform.position;

            internal Configuration config;

            internal AttackEntity AttackEntity
            {
                get
                {
                    if (_attackEntity.IsNull())
                    {
                        IdentifyWeapon();
                    }

                    return _attackEntity;
                }
            }

            public void UpdateWeapon(AttackEntity attackEntity, ItemId uid)
            {
                npc.UpdateActiveItem(uid);

                if (attackEntity is Chainsaw cs)
                {
                    cs.ServerNPCStart();
                }

                npc.damageScale = 1f;

                attackEntity.TopUpAmmo();
                attackEntity.SetHeld(true);
            }

            internal void IdentifyWeapon()
            {
                _attackEntity = GetEntity().GetAttackEntity();

                attackRange = 0f;
                attackCooldown = 99999f;
                attackType = AttackType.None;
                baseMelee = null;
                flameThrower = null;
                liquidWeapon = null;

                if (_attackEntity.IsNull())
                {
                    return;
                }

                Action action = _attackEntity.ShortPrefabName switch
                {
                    "double_shotgun.entity" or "shotgun_pump.entity" or "shotgun_waterpipe.entity" or "spas12.entity" or "blowpipe.entity" or "boomerang.entity" => () =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 30f, 0f, 30f);
                    }
                    ,
                    "ak47u.entity" or "ak47u_ice.entity" or "bolt_rifle.entity" or "glock.entity" or "hmlmg.entity" or "l96.entity" or "lr300.entity" or "m249.entity" or "m39.entity" or "m92.entity" or "mp5.entity" or "nailgun.entity" or "pistol_eoka.entity" or "pistol_revolver.entity" or "pistol_semiauto.entity" or "python.entity" or "semi_auto_rifle.entity" or "thompson.entity" or "smg.entity" => () =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 300f, 0f, 150f);
                    }
                    ,
                    "snowballgun.entity" => () =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 15f, 0.1f, 15f);
                    }
                    ,
                    "chainsaw.entity" or "jackhammer.entity" => () =>
                    {
                        baseMelee = _attackEntity as BaseMelee;
                        SetAttackRestrictions(AttackType.Melee, 2.5f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 2f);
                    }
                    ,
                    "axe_salvaged.entity" or "bone_club.entity" or "butcherknife.entity" or "candy_cane.entity" or "hammer_salvaged.entity" or "hatchet.entity" or "icepick_salvaged.entity" or "knife.combat.entity" or "knife_bone.entity" or "longsword.entity" or "mace.baseballbat" or "mace.entity" or "machete.weapon" or "pickaxe.entity" or "pitchfork.entity" or "salvaged_cleaver.entity" or "salvaged_sword.entity" or "sickle.entity" or "spear_stone.entity" or "spear_wooden.entity" or "stone_pickaxe.entity" or "stonehatchet.entity" => () =>
                    {
                        baseMelee = _attackEntity as BaseMelee;
                        SetAttackRestrictions(AttackType.Melee, 2.5f, _attackEntity.animationDelay + _attackEntity.deployDelay);
                    }
                    ,
                    "flamethrower.entity" => () =>
                    {
                        flameThrower = _attackEntity as FlameThrower;
                        SetAttackRestrictions(AttackType.FlameThrower, 10f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 2f);
                    }
                    ,
                    "compound_bow.entity" or "crossbow.entity" or "speargun.entity" or "bow_hunting.entity" => () =>
                    {
                        SetAttackRestrictions(AttackType.BaseProjectile, 200f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 1.25f, 150f);
                    }
                    ,
                    "watergun.entity" or "waterpistol.entity" => () =>
                    {
                        if ((liquidWeapon = _attackEntity as LiquidWeapon) != null)
                        {
                            liquidWeapon.AutoPump = true;
                            SetAttackRestrictions(AttackType.Water, 10f, 2f);
                        }
                    }
                    ,
                    _ => () => _attackEntity = null
                };

                action();
            }

            private void SetAttackRestrictions(AttackType attackType, float attackRange, float attackCooldown, float effectiveRange = 0f)
            {
                if (attackType == AttackType.BaseProjectile)
                {
                    baseProjectile = _attackEntity as BaseProjectile;
                    if (baseProjectile != null)
                    {
                        baseProjectile.MuzzlePoint ??= baseProjectile.transform;
                    }
                }

                if (effectiveRange != 0f)
                {
                    _attackEntity.effectiveRange = effectiveRange;
                }

                this.attackType = attackType;
                this.attackRange = attackRange;
                this.attackCooldown = attackCooldown;
            }

            public bool ValidTarget => AttackTransform != null && !AttackTarget.IsKilled() && !ShouldForgetTarget(AttackTarget);

            public override void OnDestroy()
            {
                DisableShouldThink();
                if (InitializedAI) Count--;
            }

            public override void InitializeAI()
            {
                if (isDisabled)
                {
                    return;
                }

                base.InitializeAI();
                InitializedAI = true;
                base.ForceSetAge(0f);

                NpcTransform = GetEntity().transform;
                Pet = false;
                sleeping = false;
                UseAIDesign = true;
                AllowedToSleep = false;
                HostileTargetsOnly = false;
                AttackRangeMultiplier = 2f;
                MaxGroupSize = 0;

                Senses.Init(
                    owner: GetEntity(),
                    brain: this,
                    memoryDuration: 5f,
                    range: 50f,
                    targetLostRange: 75f,
                    visionCone: -1f,
                    checkVision: false,
                    checkLOS: true,
                    ignoreNonVisionSneakers: true,
                    listenRange: 15f,
                    hostileTargetsOnly: false,
                    senseFriendlies: false,
                    ignoreSafeZonePlayers: false,
                    senseTypes: EntityType.Player,
                    refreshKnownLOS: true
                );
                //senseTypes: config.Settings.Management.TargetNpcs? EntityType.Player | EntityType.BasePlayerNPC : EntityType.Player,

                CanUseHealingItems = true;
            }

            public override void AddStates()
            {
                if (isDisabled)
                {
                    return;
                }

                base.AddStates();

                states[AIState.Attack] = new AttackState(this);
            }

            public class AttackState : BaseAttackState
            {
                private new HumanoidBrain brain;
                private global::HumanNPC npc;
                private Transform NpcTransform;

                private new IAIAttack attack => brain.Senses.ownerAttack;

                public AttackState(HumanoidBrain humanoidBrain)
                {
                    base.brain = brain = humanoidBrain;
                    base.AgrresiveState = true;
                    npc = brain.GetBrainBaseEntity() as global::HumanNPC;
                    NpcTransform = npc.transform;
                }

                public override void StateEnter(BaseAIBrain _brain, BaseEntity _entity)
                {
                    if (_brain != null && NpcTransform != null && brain.ValidTarget)
                    {
                        if (InAttackRange())
                        {
                            StartAttacking();
                        }
                        if (brain.CanUseNavMesh())
                        {
                            brain.Navigator.SetDestination(brain.DestinationOverride, BaseNavigator.NavigationSpeed.Fast, 0f, 0f);
                        }
                    }
                }

                public override void StateLeave(BaseAIBrain _brain, BaseEntity _entity)
                {

                }

                private new void StopAttacking()
                {
                    if (attack != null)
                    {
                        attack.StopAttacking();
                        brain.AttackTarget = null;
                        brain.AttackTransform = null;
                        brain.Navigator.ClearFacingDirectionOverride();
                    }
                }

                public override StateStatus StateThink(float delta, BaseAIBrain _brain, BaseEntity _entity)
                {
                    if (_brain == null || NpcTransform == null || attack == null)
                    {
                        return StateStatus.Error;
                    }
                    if (brain.isDisabled || !brain.ValidTarget)
                    {
                        StopAttacking();

                        return StateStatus.Finished;
                    }
                    if (brain.Senses.ignoreSafeZonePlayers && brain.AttackTarget.InSafeZone())
                    {
                        return StateStatus.Error;
                    }
                    if (brain.CanUseNavMesh() && !brain.Navigator.SetDestination(brain.DestinationOverride, BaseNavigator.NavigationSpeed.Fast, 0f, 0f))
                    {
                        return StateStatus.Error;
                    }
                    if (!brain.CanShoot())
                    {
                        brain.Forget();

                        StopAttacking();

                        return StateStatus.Finished;
                    }
                    if (InAttackRange())
                    {
                        StartAttacking();
                    }
                    return StateStatus.Running;
                }

                private bool InAttackRange()
                {
                    return !npc.IsWounded() && brain.AttackTransform != null && attack.CanAttack(brain.AttackTarget) && brain.IsInAttackRange() && brain.CanSeeTarget(brain.AttackTarget);
                }

                private void StartAttacking()
                {
                    if (brain.AttackTransform == null)
                    {
                        return;
                    }

                    brain.SetAimDirection();

                    if (!brain.CanShoot() || brain.IsAttackOnCooldown())
                    {
                        return;
                    }

                    if (brain.attackType == AttackType.BaseProjectile)
                    {
                        RealisticShotTest();
                    }
                    else if (brain.attackType == AttackType.FlameThrower)
                    {
                        brain.UseFlameThrower();
                    }
                    else if (brain.attackType == AttackType.Water)
                    {
                        brain.UseWaterGun();
                    }
                    else brain.MeleeAttack();
                }

                private void RealisticShotTest()
                {
                    if (NpcTransform == null || brain.baseProjectile == null || brain.baseProjectile.primaryMagazine == null)
                    {
                        return;
                    }
                    if (brain.AttackTarget.IsNpc)
                    {
                        var faction = brain.AttackTarget.faction;
                        brain.AttackTarget.faction = BaseCombatEntity.Faction.Horror;
                        npc.ShotTest(brain.AttackPosition.Distance(brain.ServerPosition));
                        if (brain.AttackTarget != null) brain.AttackTarget.faction = faction;
                    }
                    else npc.ShotTest(brain.AttackPosition.Distance(brain.ServerPosition));
                }
            }

            private bool init;

            public void Init()
            {
                if (init) return;
                init = true;
                lastWarpTime = Time.time;
                npc.spawnPos = tc.containerPos;
                npc.AdditionalLosBlockingLayer = visibleMask;
                SetupNavigator(GetEntity(), GetComponent<BaseNavigator>(), tc.Radius);
            }

            private void Converge()
            {
                foreach (var brain in HumanoidBrains.Values)
                {
                    if (brain != null && brain.NpcTransform != null && brain != this && brain.attackType == attackType && brain.CanConverge(npc))
                    {
                        brain.SetTarget(AttackTarget, false);
                    }
                }
            }

            public void Forget()
            {
                Senses.Players.Clear();
                Senses.Memory.All.Clear();
                Senses.Memory.Threats.Clear();
                Senses.Memory.Targets.Clear();
                Senses.Memory.Players.Clear();
                Navigator.ClearFacingDirectionOverride();
                DestinationOverride = GetRandomRoamPosition();

                SenseRange = ListenRange = isMurderer ? Settings.Murderers.AggressionRange : Settings.Scientists.AggressionRange;
                Senses.targetLostRange = TargetLostRange = SenseRange * 1.25f;
                AttackTarget = null;
                AttackTransform = null;

                TryReturnHome();
            }

            private void RandomMove(float radius)
            {
                var to = AttackPosition + UnityEngine.Random.onUnitSphere * radius;

                to.y = TerrainMeta.HeightMap.GetHeight(to);

                SetDestination(to);
            }

            public void SetupNavigator(BaseCombatEntity owner, BaseNavigator navigator, float distance)
            {
                navigator.CanUseNavMesh = !Rust.Ai.AiManager.nav_disable;

                navigator.MaxRoamDistanceFromHome = navigator.BestMovementPointMaxDistance = navigator.BestRoamPointMaxDistance = distance * 0.85f;
                navigator.DefaultArea = "Walkable";
                navigator.topologyPreference = ((TerrainTopology.Enum)TerrainTopology.EVERYTHING);
                navigator.Agent.agentTypeID = NavMesh.GetSettingsByIndex(1).agentTypeID; // 0:0, 1: -1372625422, 2: 1479372276, 3: -1923039037
                navigator.MaxWaterDepth = 3f;

                if (navigator.CanUseNavMesh)
                {
                    navigator.Init(owner, navigator.Agent);
                }
            }

            public Vector3 GetAimDirection()
            {
                if (Navigator.IsOverridingFacingDirection)
                {
                    return Navigator.FacingDirectionOverride;
                }
                if (InRange2D(AttackPosition, ServerPosition, 1f))
                {
                    return npc.eyes.BodyForward();
                }
                return (AttackPosition - ServerPosition).normalized;
            }

            private void SetAimDirection()
            {
                Navigator.SetFacingDirectionEntity(AttackTarget);
                npc.SetAimDirection(GetAimDirection());
            }

            private void SetDestination()
            {
                SetDestination(GetRandomRoamPosition());
            }

            private void SetDestination(Vector3 destination)
            {
                if (!CanLeave(destination))
                {
                    if (attackType != AttackType.BaseProjectile)
                    {
                        destination = ((destination.XZ3D() - tc.containerPos.XZ3D()).normalized * (tc.Radius * 0.75f)) + tc.containerPos;

                        destination += UnityEngine.Random.onUnitSphere * (tc.Radius * 0.2f);
                    }
                    else
                    {
                        destination = GetRandomRoamPosition();
                    }

                    CurrentSpeed = BaseNavigator.NavigationSpeed.Normal;
                }

                if (destination != DestinationOverride)
                {
                    destination.y = TerrainMeta.HeightMap.GetHeight(destination);

                    DestinationOverride = destination;
                }

                Navigator.SetCurrentSpeed(CurrentSpeed);

                if (Navigator.CurrentNavigationType == BaseNavigator.NavigationType.None && !Rust.Ai.AiManager.ai_dormant && !Rust.Ai.AiManager.nav_disable)
                {
                    Navigator.SetCurrentNavigationType(BaseNavigator.NavigationType.NavMesh);
                }

                if (CanUseNavMesh() && !Navigator.SetDestination(destination, CurrentSpeed))
                {
                    Navigator.Destination = destination;
                    npc.finalDestination = destination;
                }
            }

            public bool CanUseNavMesh() => Navigator.CanUseNavMesh && !Navigator.StuckOffNavmesh;

            public bool SetTarget(BasePlayer player, bool converge = true)
            {
                if (isDisabled)
                {
                    return false;
                }

                if (NpcTransform == null)
                {
                    DisableShouldThink();
                    Destroy(this);
                    return false;
                }

                if (player.IsKilled() || player.limitNetworking)
                {
                    return false;
                }

                if (AttackTarget == player)
                {
                    return true;
                }

                if (npc.lastGunShotTime < Time.time + 2f)
                {
                    npc.nextTriggerTime = Time.time + 0.2f;
                    nextAttackTime = Time.realtimeSinceStartup + 0.2f;
                }

                Senses.Memory.SetKnown(player, npc, null);
                npc.lastAttacker = player;
                AttackTarget = player;
                AttackTransform = player.transform;

                if (!IsInSenseRange(AttackPosition))
                {
                    SenseRange = ListenRange = (isMurderer ? Settings.Murderers.AggressionRange : Settings.Scientists.AggressionRange) + AttackPosition.Distance(ServerPosition);
                    TargetLostRange = SenseRange + (SenseRange * 0.25f);
                }
                else
                {
                    SenseRange = ListenRange = softLimitSenseRange;
                    TargetLostRange = softLimitSenseRange * 1.25f;
                }

                if (converge)
                {
                    Converge();
                }

                return true;
            }
            private void TryReturnHome()
            {
                if (Settings.CanLeave && !IsInHomeRange())
                {
                    CurrentSpeed = BaseNavigator.NavigationSpeed.Normal;

                    Warp();
                }
            }

            private void TryToAttack() => TryToAttack(null);

            private void TryToAttack(BasePlayer attacker)
            {
                if (isDisabled)
                {
                    return;
                }

                if ((attacker ??= GetBestTarget()).IsNull())
                {
                    return;
                }

                if (ShouldForgetTarget(attacker))
                {
                    Forget();

                    return;
                }

                if (!SetTarget(attacker) || AttackTransform == null || !CanSeeTarget(attacker))
                {
                    return;
                }

                if (attackType == AttackType.BaseProjectile)
                {
                    TryScientistActions();
                }
                else
                {
                    TryMurdererActions();
                }

                SwitchToState(AIState.Attack, -1);
            }

            private void TryMurdererActions()
            {
                CurrentSpeed = BaseNavigator.NavigationSpeed.Fast;

                if (!IsInReachableRange())
                {
                    RandomMove(15f);
                }
                else if (!IsInAttackRange())
                {
                    if (attackType == AttackType.FlameThrower)
                    {
                        RandomMove(attackRange);
                    }
                    else
                    {
                        SetDestination(AttackPosition);
                    }
                }
            }

            private void TryScientistActions()
            {
                CurrentSpeed = BaseNavigator.NavigationSpeed.Fast;

                SetDestination();
            }

            public void SetupMovement(List<Vector3> positions)
            {
                if (isDisabled || tc == null || tc.killed || tc.IsUnloading || npc.IsKilled() || npc.IsDead() || npc.Health() <= 0)
                {
                    return;
                }

                this.positions = positions;

                InvokeRepeating(TryToRoam, 0f, 7.5f);
                InvokeRepeating(TryToAttack, 1f, 1f);
            }

            private void TryToRoam()
            {
                if (isDisabled)
                {
                    return;
                }

                if (Settings.KillUnderwater && npc.playerCollider != null && npc.IsSwimming())
                {
                    npc.SafelyKill();
                    return;
                }

                if (ValidTarget)
                {
                    return;
                }

                if (IsStuck())
                {
                    Warp();

                    Navigator.stuckTimer = 0f;
                }

                CurrentSpeed = BaseNavigator.NavigationSpeed.Normal;

                SetDestination();
            }

            private bool IsStuck() => false; //InRange(npc.transform.position, Navigator.stuckCheckPosition, Navigator.StuckDistance);

            public void Warp()
            {
                if (Time.time < lastWarpTime)
                {
                    return;
                }

                lastWarpTime = Time.time + 1f;

                DestinationOverride = GetRandomRoamPosition();

                Navigator.Warp(DestinationOverride);
            }

            private void UseFlameThrower()
            {
                if (flameThrower.ammo < flameThrower.maxAmmo * 0.25)
                {
                    flameThrower.SetFlameState(false);
                    flameThrower.ServerReload();
                    return;
                }
                npc.triggerEndTime = Time.time + attackCooldown;
                flameThrower.SetFlameState(true);
                flameThrower.Invoke(() => flameThrower.SetFlameState(false), 2f);
            }

            private void UseWaterGun()
            {
                if (Physics.Raycast(npc.eyes.BodyRay(), out var hit, 10f, 1218652417))
                {
                    WaterBall.DoSplash(hit.point, 2f, ItemManager.FindItemDefinition("water"), 10);
                    DamageUtil.RadiusDamage(npc, liquidWeapon.LookupPrefab(), hit.point, 0.15f, 0.15f, new(), 131072, true);
                }
            }

            private void UseChainsaw()
            {
                AttackEntity.TopUpAmmo();
                AttackEntity.ServerUse();
                AttackTarget.Hurt(10f * AttackEntity.npcDamageScale, DamageType.Bleeding, npc);
            }

            private void MeleeAttack()
            {
                if (baseMelee.IsNull())
                {
                    return;
                }

                if (AttackEntity is Chainsaw)
                {
                    UseChainsaw();
                    return;
                }

                Vector3 position = AttackPosition;
                AttackEntity.StartAttackCooldown(AttackEntity.repeatDelay * 2f);
                npc.SignalBroadcast(BaseEntity.Signal.Attack, string.Empty, null);
                if (baseMelee.swingEffect.isValid)
                {
                    Effect.server.Run(baseMelee.swingEffect.resourcePath, position, Vector3.forward, npc.Connection, false);
                }
                HitInfo hitInfo = new()
                {
                    damageTypes = new(),
                    DidHit = true,
                    Initiator = npc,
                    HitEntity = AttackTarget,
                    HitPositionWorld = position,
                    HitPositionLocal = AttackTransform.InverseTransformPoint(position),
                    HitNormalWorld = npc.eyes.BodyForward(),
                    HitMaterial = StringPool.Get("Flesh"),
                    PointStart = ServerPosition,
                    PointEnd = position,
                    Weapon = AttackEntity,
                    WeaponPrefab = AttackEntity
                };

                hitInfo.damageTypes.Set(DamageType.Slash, baseMelee.TotalDamage() * AttackEntity.npcDamageScale);
                Effect.server.ImpactEffect(hitInfo);
                AttackTarget.OnAttacked(hitInfo);
            }

            private bool CanConverge(global::HumanNPC other)
            {
                if (ValidTarget || other.IsKilled() || other.IsDead()) return false;
                return IsInTargetRange(other.transform.position);
            }

            private bool CanLeave(Vector3 destination)
            {
                return Settings.CanLeave || IsInLeaveRange(destination);
            }

            private bool CanSeeTarget(BasePlayer target)
            {
                if (Navigator.CurrentNavigationType == BaseNavigator.NavigationType.None && (attackType == AttackType.FlameThrower || attackType == AttackType.Melee))
                {
                    return true;
                }

                if (ServerPosition.Distance(target.ServerPosition) < 10f || Senses.Memory.IsLOS(target))
                {
                    return true;
                }

                nextAttackTime = Time.realtimeSinceStartup + 1f;

                return false;
            }

            public bool CanRoam(Vector3 destination)
            {
                return destination == DestinationOverride && IsInSenseRange(destination);
            }

            private bool CanShoot()
            {
                if (attackType == AttackType.None)
                {
                    return false;
                }

                return true;
            }

            public BasePlayer GetBestTarget()
            {
                if (npc.IsWounded())
                {
                    return null;
                }
                float delta = -1f;
                BasePlayer target = null;
                foreach (var player in Senses.Memory.Targets.OfType<BasePlayer>())
                {
                    if (ShouldForgetTarget(player) || !player.IsHuman() && !Options.NPC.TargetNpcs) continue;
                    float dist = player.transform.position.Distance(npc.transform.position);
                    float rangeDelta = 1f - Mathf.InverseLerp(1f, SenseRange, dist);
                    rangeDelta += (CanSeeTarget(player) ? 2f : 0f);
                    if (rangeDelta <= delta) continue;
                    target = player;
                    delta = rangeDelta;
                }
                return target;
            }

            private Vector3 GetRandomRoamPosition()
            {
                return positions.GetRandom();
            }

            private bool IsAttackOnCooldown()
            {
                if (attackType == AttackType.None || Time.realtimeSinceStartup < nextAttackTime)
                {
                    return true;
                }

                if (attackCooldown > 0f)
                {
                    nextAttackTime = Time.realtimeSinceStartup + attackCooldown;
                }

                return false;
            }

            private bool IsInAttackRange(float range = 0f)
            {
                return InRange(ServerPosition, AttackPosition, range == 0f ? attackRange : range);
            }

            private bool IsInHomeRange()
            {
                return InRange(ServerPosition, tc.containerPos, Mathf.Max(tc.Radius, TargetLostRange));
            }

            private bool IsInLeaveRange(Vector3 destination)
            {
                return InRange(tc.containerPos, destination, tc.Radius);
            }

            private bool IsInReachableRange()
            {
                if (AttackPosition.y - ServerPosition.y > attackRange)
                {
                    return false;
                }

                return attackType != AttackType.Melee || InRange(AttackPosition, ServerPosition, 15f);
            }

            private bool IsInSenseRange(Vector3 destination)
            {
                return InRange2D(tc.containerPos, destination, SenseRange);
            }

            private bool IsInTargetRange(Vector3 destination)
            {
                return InRange2D(tc.containerPos, destination, TargetLostRange);
            }

            private bool ShouldForgetTarget(BasePlayer target)
            {
                return target.IsKilled() || target.health <= 0f || target.limitNetworking || target.IsDead() || target.skinID == 14922524 || !IsInTargetRange(target.transform.position);
            }
        }

        private class GuidanceSystem : FacepunchBehaviour
        {
            private TimedExplosive missile;
            private ServerProjectile projectile;
            private BaseEntity target;
            private Vector3 launchPos;
            private List<ulong> newmans = new();
            internal DangerousTreasures Instance;
            internal Configuration config;
            internal DifficultyLevel Options;

            private void Awake()
            {
                missile = GetComponent<TimedExplosive>();
                projectile = missile.GetComponent<ServerProjectile>();

                launchPos = missile.transform.position;
                launchPos.y = TerrainMeta.HeightMap.GetHeight(launchPos);

                projectile.gravityModifier = 0f;
                projectile.speed = 0.1f;
                projectile.InitializeVelocity(Vector3.up);

                missile.explosionRadius = 0f;

                missile.damageTypes = new(); // no damage
            }

            public void SetTarget(BaseEntity target)
            {
                this.target = target;
            }

            public void Launch(float targettingTime)
            {
                missile.timerAmountMin = Options.MissileLauncher.Lifetime;
                missile.timerAmountMax = Options.MissileLauncher.Lifetime;

                missile.Spawn();

                Instance.timer.Once(targettingTime, () =>
                {
                    if (missile.IsKilled())
                        return;

                    using var list = Pool.Get<PooledList<BasePlayer>>();
                    using var players = FindEntitiesOfType<BasePlayer>(launchPos, Options.Event.Radius + Options.MissileLauncher.Distance, Layers.Mask.Player_Server);

                    for (int i = 0; i < players.Count; i++)
                    {
                        var player = players[i];

                        if (player.IsKilled() || !player.IsHuman() || !player.CanInteract())
                            continue;

                        if (Options.MissileLauncher.IgnoreFlying && player.IsFlying)
                            continue;

                        if (newmans.Contains(player.userID) || Instance.newmanProtections.Contains(player.userID))
                            continue;

                        list.Add(player); // acquire a player target 
                    }

                    if (list.Count > 0)
                    {
                        target = list.GetRandom(); // pick a random player
                    }
                    else if (!Options.MissileLauncher.TargetChest)
                    {
                        missile.SafelyKill();
                        return;
                    }

                    projectile.speed = config.Rocket.Speed * 2f;
                    InvokeRepeating(GuideMissile, 0.1f, 0.1f);
                });
            }

            public void Exclude(List<ulong> newmans)
            {
                if (newmans != null && newmans.Count > 0)
                {
                    this.newmans = newmans.ToList();
                }
            }

            private void GuideMissile()
            {
                if (target == null)
                    return;

                if (target.IsDestroyed)
                {
                    missile.SafelyKill();
                    return;
                }

                if (missile.IsKilled() || projectile == null)
                {
                    Destroy(this);
                    return;
                }

                if (InRange(target.transform.position, missile.transform.position, 1f))
                {
                    missile.Explode();
                    return;
                }

                var direction = (target.transform.position - missile.transform.position) + Vector3.down; // direction to guide the missile
                projectile.InitializeVelocity(direction); // guide the missile to the target's position
            }

            private void OnDestroy()
            {
                try { CancelInvoke(); } catch { }
                Destroy(this);
            }
        }

        public class TreasureChest : FacepunchBehaviour
        {
            internal Dictionary<ulong, HumanoidBrain> HumanoidBrains;
            internal DangerousTreasures Instance;
            internal ulong userid;
            internal GameObject go;
            internal StorageContainer container;
            internal Vector3 containerPos;
            internal Vector3 lastFirePos;
            internal int npcMaxAmountMurderers;
            internal int npcMaxAmountScientists;
            internal int npcSpawnedAmount;
            internal int countdownTime;
            internal bool started;
            internal bool opened;
            internal bool firstEntered;
            internal bool markerCreated;
            internal bool killed;
            internal bool IsUnloading;
            internal bool requireAllNpcsDie;
            internal bool whenNpcsDie;
            internal float claimTime;
            internal float _radius;
            internal long _unlockTime;
            internal NetworkableId uid;

            private Dictionary<string, List<string>> npcKits = Pool.Get<Dictionary<string, List<string>>>();
            private Dictionary<ulong, float> fireticks = Pool.Get<Dictionary<ulong, float>>();
            private List<FireBall> fireballs = Pool.Get<List<FireBall>>();
            private List<ulong> newmans = Pool.Get<List<ulong>>();
            private List<ulong> traitors = Pool.Get<List<ulong>>();
            private List<ulong> protects = Pool.Get<List<ulong>>();
            private List<ulong> players = Pool.Get<List<ulong>>();
            private List<TimedExplosive> missiles = Pool.Get<List<TimedExplosive>>();
            private List<int> times = Pool.Get<List<int>>();
            private List<SphereEntity> spheres = Pool.Get<List<SphereEntity>>();
            private List<Vector3> missilePositions = Pool.Get<List<Vector3>>();
            private List<Vector3> firePositions = Pool.Get<List<Vector3>>();
            public List<HumanoidNPC> npcs = Pool.Get<List<HumanoidNPC>>();
            private Timer destruct, unlock, countdown, announcement;
            private MapMarkerExplosion explosionMarker;
            private MapMarkerGenericRadius genericMarker;
            private VendingMachineMapMarker vendingMarker;

            private string FormatGridReference(Vector3 position) => Instance.FormatGridReference(position, config.Settings.ShowGrid);

            private void Message(BasePlayer player, string key, params object[] args) => Instance.Message(player, key, args);

            internal Configuration config;

            public float Radius
            {
                get
                {
                    return _radius;
                }
                set
                {
                    _radius = value;
                    Awaken();
                }
            }

            public float SqrRadius => Radius * Radius;

            private void Free()
            {
                if (fireballs != null) Pool.FreeUnmanaged(ref fireballs);
                if (newmans != null) Pool.FreeUnmanaged(ref newmans);
                if (traitors != null) Pool.FreeUnmanaged(ref traitors);
                if (protects != null) Pool.FreeUnmanaged(ref protects);
                if (missiles != null) Pool.FreeUnmanaged(ref missiles);
                if (times != null) Pool.FreeUnmanaged(ref times);
                if (spheres != null) Pool.FreeUnmanaged(ref spheres);
                if (missilePositions != null) Pool.FreeUnmanaged(ref missilePositions);
                if (firePositions != null) Pool.FreeUnmanaged(ref firePositions);
                if (npcKits != null) Pool.FreeUnmanaged(ref npcKits);
                if (npcs != null) Pool.FreeUnmanaged(ref npcs);
                destruct?.Destroy();
                unlock?.Destroy();
                countdown?.Destroy();
                announcement?.Destroy();
            }

            private class NewmanTracker : FacepunchBehaviour
            {
                BasePlayer player;
                TreasureChest chest;
                DangerousTreasures Instance;
                Configuration config;
                DifficultyLevel Level;
                private void Message(BasePlayer player, string key, params object[] args) => Instance.Message(player, key, args);

                public void Assign(DangerousTreasures instance, TreasureChest chest, BasePlayer player)
                {
                    this.player = player;
                    Instance = instance;
                    config = instance.config;
                    this.chest = chest;
                    Level = chest.Options;
                    InvokeRepeating(Track, 1f, 0.1f);
                }

                private void Track()
                {
                    if (chest == null || chest.started || player.IsKilled() || !player.IsConnected || !chest.players.Contains(player.userID))
                    {
                        Destroy(this);
                        return;
                    }

                    if (!InRange2D(player.transform.position, chest.containerPos, chest.Radius))
                    {
                        return;
                    }

                    if (config.NewmanMode.Aura || config.NewmanMode.Harm)
                    {
                        using var itemList = player.GetAllItems();
                        int sum = itemList.Sum(item => player.IsHostileItem(item) ? 1 : 0);

                        if (sum == 0)
                        {
                            if (config.NewmanMode.Aura && !chest.newmans.Contains(player.userID) && !chest.traitors.Contains(player.userID))
                            {
                                Message(player, "Newman Enter");
                                chest.newmans.Add(player.userID);
                            }

                            if (config.NewmanMode.Harm && !Instance.newmanProtections.Contains(player.userID) && !chest.protects.Contains(player.userID) && !chest.traitors.Contains(player.userID))
                            {
                                Message(player, "Newman Protect");
                                Instance.newmanProtections.Add(player.userID);
                                chest.protects.Add(player.userID);
                            }

                            if (!chest.traitors.Contains(player.userID))
                            {
                                return;
                            }
                        }

                        if (chest.newmans.Remove(player.userID))
                        {
                            Message(player, Level.Fireballs.Enabled ? "Newman Traitor Burn" : "Newman Traitor");

                            if (!chest.traitors.Contains(player.userID))
                                chest.traitors.Add(player.userID);

                            Instance.newmanProtections.Remove(player.userID);
                            chest.protects.Remove(player.userID);
                        }
                    }

                    if (!Level.Fireballs.Enabled || player.IsFlying)
                    {
                        return;
                    }

                    var stamp = Time.realtimeSinceStartup;

                    if (!chest.fireticks.ContainsKey(player.userID))
                    {
                        chest.fireticks[player.userID] = stamp + Level.Fireballs.SecondsBeforeTick;
                    }

                    if (chest.fireticks[player.userID] - stamp <= 0)
                    {
                        chest.fireticks[player.userID] = stamp + Level.Fireballs.SecondsBeforeTick;
                        chest.SpawnFire(player.transform.position);
                    }
                }

                private void OnDestroy()
                {
                    try { CancelInvoke(Track); } catch { }
                    Destroy(this);
                }
            }

            public void Kill(bool isUnloading)
            {
                Instance.treasureChests.Remove(uid);
                IsUnloading = isUnloading;
                if (killed) return;
                killed = true;

                if (!container.IsKilled())
                {
                    container.inventory.Clear();
                    ItemManager.DoRemoves();
                    container.Kill();
                }

                RemoveMapMarkers();
                KillNpc();
                CancelInvoke();
                DestroyLauncher();
                DestroySphere();
                DestroyFire();
                Interface.CallHook("OnDangerousEventEnded", containerPos);
                Destroy(go);
                Destroy(this);
            }

            public bool HasRustMarker
            {
                get
                {
                    return explosionMarker != null || vendingMarker != null;
                }
            }

            public void Awaken()
            {
                SetupNpcKits();

                var collider = gameObject.GetComponent<SphereCollider>() ?? gameObject.AddComponent<SphereCollider>();
                collider.center = Vector3.zero;
                collider.radius = Radius;
                collider.isTrigger = true;
                collider.enabled = true;

                requireAllNpcsDie = config.Unlock.RequireAllNpcsDie;
                whenNpcsDie = config.Unlock.WhenNpcsDie;

                if (Options.Event.Spheres && Options.Event.SphereAmount > 0)
                {
                    for (int i = 0; i < Options.Event.SphereAmount; i++)
                    {
                        var sphere = GameManager.server.CreateEntity(StringPool.Get(3211242734), containerPos) as SphereEntity;

                        if (sphere == null)
                        {
                            Puts(Instance._("Invalid Constant", null, 3211242734));
                            Options.Event.Spheres = false;
                            break;
                        }

                        sphere.currentRadius = 1f;
                        sphere.Spawn();
                        sphere.LerpRadiusTo(Radius * 2f, 5f);
                        spheres.Add(sphere);
                    }
                }

                if (config.Rocket.Enabled)
                {
                    foreach (var position in GetRandomPositions(containerPos, Radius * 3f, config.Rocket.Amount, 0f))
                    {
                        var prefab = config.Rocket.FireRockets ? "assets/prefabs/ammo/rocket/rocket_fire.prefab" : "assets/prefabs/ammo/rocket/rocket_basic.prefab";
                        var missile = GameManager.server.CreateEntity(prefab, position) as TimedExplosive;
                        var gs = missile.gameObject.AddComponent<GuidanceSystem>();

                        gs.Options = Options;
                        gs.Instance = Instance;
                        gs.config = config;
                        gs.SetTarget(container);
                        gs.Launch(0.1f);
                    }
                }

                if (Options.Fireballs.Enabled)
                {
                    firePositions = GetRandomPositions(containerPos, Radius, 25, containerPos.y + 25f);

                    if (firePositions.Count > 0)
                        InvokeRepeating(SpawnFire, 0.1f, Options.Fireballs.SecondsBeforeTick);
                }

                if (Options.MissileLauncher.Enabled)
                {
                    missilePositions = GetRandomPositions(containerPos, Radius, 25, 1);

                    if (missilePositions.Count > 0)
                    {
                        InvokeRepeating(LaunchMissile, 0.1f, Options.MissileLauncher.Frequency);
                        LaunchMissile();
                    }
                }

                InvokeRepeating(UpdateMarker, 5f, 30f);
                Interface.CallHook("OnDangerousEventStarted", containerPos);
            }

            void Awake()
            {
                gameObject.layer = (int)Layer.Reserved1;
                container = GetComponent<StorageContainer>();
                container.OwnerID = 0;
                container.dropsLoot = false;
                containerPos = container.transform.position;
                uid = container.net.ID;
                container.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
            }

            public void SpawnLoot(StorageContainer container, List<LootItem> treasure)
            {
                if (container.IsKilled() || treasure == null || treasure.Count == 0 || Options.Event.TreasureAmount == 0)
                {
                    return;
                }

                var loot = treasure.ToList();
                int j = 0;
                int capacity = Math.Min(Options.Event.TreasureAmount, loot.Count);

                container.inventory.Clear();
                container.inventory.capacity = Mathf.Clamp(capacity, 1, 48);

                while (j++ < container.inventory.capacity && loot.Count > 0)
                {
                    var lootItem = loot.GetRandom();

                    loot.Remove(lootItem);

                    if (UnityEngine.Random.value > lootItem.probability)
                    {
                        continue;
                    }

                    var definition = lootItem.definition;

                    if (definition == null)
                    {
                        Instance.PrintError("Invalid shortname in config: {0}", lootItem.shortname);
                        continue;
                    }

                    int amount = UnityEngine.Random.Range(lootItem.amountMin, lootItem.amount + 1);

                    if (amount <= 0)
                    {
                        j--;
                        continue;
                    }

                    if (definition.stackable == 1) // || (definition.condition.enabled && definition.condition.max > 0f))
                    {
                        amount = 1;
                    }

                    using var skins = Facepunch.Pool.Get<PooledList<ulong>>();
                    skins.AddRange(lootItem.skins);
                    Instance.RemoveRequiresOwnership(definition, skins);

                    ulong skin = skins.Count > 0 ? skins.GetRandom() : !Instance.RequiresOwnership(definition, lootItem.skin) ? lootItem.skin : 0;
                    Item item = ItemManager.CreateByItemID(definition.itemid, amount, skin);

                    if (item.info.stackable > 1 && !item.hasCondition)
                    {
                        item.amount = Instance.GetPercentIncreasedAmount(Options, amount);
                    }

                    if (item.hasCondition)
                    {
                        item.condition = lootItem.condition * item.info.condition.max;
                    }

                    if (Options.Treasure.RandomSkins && skin == 0)
                    {
                        item.skin = GetItemSkin(definition, lootItem.skin, false);
                    }

                    if (skin != 0 && item.GetHeldEntity())
                    {
                        item.GetHeldEntity().skinID = skin;
                    }

                    if (!string.IsNullOrEmpty(lootItem.name))
                    {
                        item.name = lootItem.name;
                    }

                    if (!string.IsNullOrEmpty(lootItem.text) && !BuildingMaterials.Contains(lootItem.shortname))
                    {
                        item.text = lootItem.text;
                    }

                    if (lootItem.slots != null)
                    {
                        lootItem.slots.TryAdd(item);
                    }

                    item.MarkDirty();

                    if (!item.MoveToContainer(container.inventory, -1, true))
                    {
                        item.Remove(0.1f);
                    }
                }
            }

            private List<string> BuildingMaterials = new()
            {
                "hq.metal.ore", "metal.refined", "metal.fragments", "metal.ore", "stones", "sulfur.ore", "sulfur", "wood"
            };

            private Dictionary<string, ulong> skinIds { get; set; } = new();

            private bool IsBlacklistedSkin(ItemDefinition def, int num)
            {
                if (Instance.RequiresOwnership(def, (ulong)num)) return true;
                var skinId = ItemDefinition.FindSkin(def.isRedirectOf?.itemid ?? def.itemid, num);
                var dirSkin = def.isRedirectOf == null ? def.skins.FirstOrDefault(x => (ulong)x.id == skinId) : def.isRedirectOf.skins.FirstOrDefault(x => (ulong)x.id == skinId);
                var itemSkin = (dirSkin.id == 0) ? null : (dirSkin.invItem as ItemSkin);

                return itemSkin?.Redirect != null || def.isRedirectOf != null;
            }

            public ulong GetItemSkin(ItemDefinition def, ulong defaultSkin, bool unique)
            {
                ulong skin = defaultSkin;

                if (def.shortname != "explosive.satchel" && def.shortname != "grenade.f1")
                {
                    if (!skinIds.TryGetValue(def.shortname, out skin)) // apply same skin once randomly chosen so items with skins can stack properly
                    {
                        skin = defaultSkin;
                    }

                    if (!unique || skin == 0)
                    {
                        var si = GetItemSkins(def);
                        var random = new List<ulong>();

                        if ((def.shortname == "box.wooden.large" && config.Skins.RandomWorkshopSkins) || (def.shortname != "box.wooden.large" && Options.Treasure.RandomWorkshopSkins))
                        {
                            if (si.workshopSkins.Count > 0)
                            {
                                random.Add(si.workshopSkins.GetRandom());
                            }
                        }

                        if (config.Skins.RandomSkins && si.skins.Count > 0)
                        {
                            random.Add(si.skins.GetRandom());
                        }

                        if (random.Count != 0)
                        {
                            skinIds[def.shortname] = skin = random.GetRandom();
                        }
                    }
                }

                return skin;
            }

            public SkinInfo GetItemSkins(ItemDefinition def)
            {
                if (!Instance.Skins.TryGetValue(def.shortname, out var si))
                {
                    Instance.Skins[def.shortname] = si = new();

                    if (config.BlockPaidContent)
                    {
                        return si;
                    }

                    foreach (var skin in def.skins)
                    {
                        if (IsBlacklistedSkin(def, skin.id))
                        {
                            continue;
                        }

                        var id = Convert.ToUInt64(skin.id);

                        si.skins.Add(id);
                        si.allSkins.Add(id);
                    }

                    if (def.skins2 == null)
                    {
                        return si;
                    }

                    foreach (var skin in def.skins2)
                    {
                        if (IsBlacklistedSkin(def, (int)skin.WorkshopId))
                        {
                            continue;
                        }

                        if (!si.workshopSkins.Contains(skin.WorkshopId))
                        {
                            si.workshopSkins.Add(skin.WorkshopId);
                            si.allSkins.Add(skin.WorkshopId);
                        }
                    }
                }

                return si;
            }

            public List<ulong> invaders = new();

            void OnTriggerEnter(Collider col)
            {
                if (col == null || col.ObjectName() == "ZoneManager")
                    return;

                var player = col.ToBaseEntity() as BasePlayer;

                if (player == null || !player.IsHuman())
                    return;

                if (!invaders.Contains(player.userID))
                    invaders.Add(player.userID);

                if (players.Contains(player.userID))
                    return;

                Interface.CallHook("OnPlayerEnteredDangerousEvent", player, containerPos, config.TruePVE.AllowPVPAtEvents);

                if (started)
                    return;

                if (config.Unlock.LockToPlayerFirstEntered && !userid.IsSteamId())
                {
                    userid = player.userID;
                }

                if (config.EventMessages.FirstEntered && !firstEntered && !player.IsFlying)
                {
                    firstEntered = true;
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        Message(target, "OnFirstPlayerEntered", player.displayName, FormatGridReference(containerPos));
                    }
                }

                if (config.EventMessages.NoobWarning)
                {
                    Message(player, whenNpcsDie && npcSpawnedAmount > 0 ? "Npc Event" : requireAllNpcsDie && npcSpawnedAmount > 0 ? "Timed Npc Event" : "Timed Event");
                }
                else if (config.EventMessages.Entered)
                {
                    Message(player, Options.Fireballs.Enabled ? "Dangerous Zone Protected" : "Dangerous Zone Unprotected");
                }

                var tracker = player.gameObject.GetComponent<NewmanTracker>() ?? player.gameObject.AddComponent<NewmanTracker>();

                tracker.Assign(Instance, this, player);

                players.Add(player.userID);
            }

            void OnTriggerExit(Collider col)
            {
                if (col == null || col.ObjectName() == "ZoneManager")
                    return;

                var player = col.ToBaseEntity() as BasePlayer;

                if (!player.IsValid())
                    return;

                if (player.IsHuman())
                {
                    invaders.Remove(player.userID);
                    Interface.CallHook("OnPlayerExitedDangerousEvent", player, containerPos, config.TruePVE.AllowPVPAtEvents);
                }
                else if (player is HumanoidNPC npc && npcs.Contains(npc))
                {
                    if (npc.NavAgent != null && npc.NavAgent.isOnNavMesh)
                        npc.NavAgent.SetDestination(containerPos);

                    npc.finalDestination = containerPos;
                }

                if (config.NewmanMode.Harm)
                {
                    if (protects.Remove(player.userID))
                    {
                        Instance.newmanProtections.Remove(player.userID);
                        Message(player, "Newman Protect Fade");
                    }

                    newmans.Remove(player.userID);
                }
            }

            public void SpawnNpcs() => SpawnNpcs(false);

            public static bool CanSamplePosition() => RustNavigation.Instance != null && RustNavigation.Instance.IsDefaultNavmeshBuilt();
            public void SpawnNpcs(bool force)
            {
                if ((!force && !Options.NPC.Enabled) || container.IsKilled()) return;
                container.SendNetworkUpdate();

                if (!CanSamplePosition())
                {
                    if (IsInvoking(SpawnNpcs))
                    {
                        CancelInvoke(SpawnNpcs);
                    }
                    Invoke(SpawnNpcs, 1f);
                    return;
                }

                npcMaxAmountMurderers = Options.NPC.Murderers.SpawnAmount > 0 ? UnityEngine.Random.Range(Options.NPC.Murderers.SpawnMinAmount, Options.NPC.Murderers.SpawnAmount + 1) : Options.NPC.Murderers.SpawnAmount;
                npcMaxAmountScientists = Options.NPC.Scientists.SpawnAmount > 0 ? UnityEngine.Random.Range(Options.NPC.Scientists.SpawnMinAmount, Options.NPC.Scientists.SpawnAmount + 1) : Options.NPC.Scientists.SpawnAmount;

                if (npcMaxAmountMurderers > 0)
                {
                    for (int i = 0; i < npcMaxAmountMurderers; i++)
                    {
                        SpawnNpc(true);
                    }
                }

                if (npcMaxAmountScientists > 0)
                {
                    for (int i = 0; i < npcMaxAmountScientists; i++)
                    {
                        SpawnNpc(false);
                    }
                }

                npcSpawnedAmount = npcs.Count;
            }

            private Vector3 FindPointOnNavmesh(Vector3 target, float radius)
            {
                if (!RustNavMeshHelpers.SamplePosition(target, out var hit, radius, 25))
                {
                    return Vector3.zero;
                }

                Vector3 position = hit.position;
                if (position.y < TerrainMeta.HeightMap.GetHeight(position))
                {
                    return Vector3.zero;
                }

                if (!InRange2D(position, containerPos, Radius - 2.5f))
                {
                    return Vector3.zero;
                }

                if (!IsAcceptableWaterDepth(position) || TestInsideObject(position))
                {
                    return Vector3.zero;
                }

                return position;
            }

            internal RaycastHit[] SharedRockHits;

            internal Collider[] SharedRockColliders;

            public bool IsAcceptableWaterDepth(Vector3 point) => WaterLevel.GetOverallWaterDepth(point, true, true, null) <= 0.75f;

            private bool TestInsideObject(Vector3 point) => GamePhysics.CheckSphere(point, 0.5f, Layers.Mask.Player_Server | Layers.Server.Deployed, QueryTriggerInteraction.Ignore) || IsPointInsideRock(point) || HasRockHit(point, true, true) || IsRockInsideSpawnVolume(point);

            private bool IsPointInsideRock(Vector3 point) => HasRockCollider(Physics.OverlapSphereNonAlloc(point + new Vector3(0f, 0.1f, 0f), 0.01f, SharedRockColliders, Layers.World, QueryTriggerInteraction.Ignore));

            private bool IsRockInsideSpawnVolume(Vector3 point, float radius = 0.4f, float height = 1.8f) => HasRockCollider(Physics.OverlapCapsuleNonAlloc(point + new Vector3(0f, radius + 0.1f, 0f), point + new Vector3(0f, height - radius, 0f), radius, SharedRockColliders, Layers.World, QueryTriggerInteraction.Ignore));

            private bool HasRockHit(Vector3 point, bool aboveOnly, bool includeTerrain)
            {
                Vector3 origin = point + new Vector3(0f, 30f, 0f);
                int mask = Layers.World | (includeTerrain ? Layers.Terrain : 0);
                int count = Physics.RaycastNonAlloc(origin, Vector3.down, SharedRockHits, 31f, mask, QueryTriggerInteraction.Ignore);
                if (count == SharedRockHits.Length) return true;
                for (int i = 0; i < count; i++)
                {
                    RaycastHit hit = SharedRockHits[i];
                    Collider collider = hit.collider;
                    bool above = hit.point.y - point.y > 0.01f;
                    if (collider == null || (aboveOnly && !above)) continue;
                    if (collider.IsOnLayer(Layer.Terrain) ? includeTerrain && above : IsRock(collider.ObjectName())) return true;
                }
                return false;
            }

            private bool HasRockCollider(int count)
            {
                bool blocked = count == SharedRockColliders.Length;
                for (int i = 0; i < count; i++)
                {
                    Collider collider = SharedRockColliders[i];
                    SharedRockColliders[i] = null;
                    if (!blocked && collider != null && IsRock(collider.ObjectName()))
                    {
                        blocked = true;
                    }
                }
                return blocked;
            }

            private List<string> _prefabs = new() { "rock", "formation", "cliff" };

            private bool IsRock(string name)
            {
                foreach (string value in _prefabs)
                {
                    if (name.Contains(value, CompareOptions.OrdinalIgnoreCase)) return true;
                }
                return false;
            }

            private static void CopySerializableFields<T>(T src, T dst)
            {
                var srcFields = typeof(T).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance);
                foreach (var field in srcFields)
                {
                    object value = field.GetValue(src);
                    field.SetValue(dst, value);
                }
            }

            private bool InstantiateEntity(Vector3 position, bool isMurderer, out HumanoidBrain brain, out HumanoidNPC npc)
            {
                var prefabName = StringPool.Get(1536035819);
                var prefab = GameManager.server.FindPrefab(prefabName);
                var go = Facepunch.Instantiate.GameObject(prefab, position, Quaternion.identity);

                go.SetActive(false);

                go.name = prefabName;

                ScientistBrain scientistBrain = go.GetComponent<ScientistBrain>();
                ScientistNPC scientistNpc = go.GetComponent<ScientistNPC>();

                npc = go.AddComponent<HumanoidNPC>();
                npc.tc = this;

                brain = go.AddComponent<HumanoidBrain>();
                brain.Instance = Instance;
                brain.config = config;
                brain.DestinationOverride = position;
                brain.CheckLOS = brain.RefreshKnownLOS = true;
                brain.SenseRange = isMurderer ? Options.NPC.Murderers.AggressionRange : Options.NPC.Scientists.AggressionRange;
                brain.softLimitSenseRange = brain.SenseRange + (brain.SenseRange * 0.25f);
                brain.TargetLostRange = brain.SenseRange * 1.25f;
                brain.Settings = Options.NPC;
                brain.UseAIDesign = false;
                brain._baseEntity = npc;
                brain.tc = this;
                brain.npc = npc;
                brain.Navigator = go.GetComponent<BaseNavigator>();
                brain.NpcTransform = npc.transform;
                brain.states ??= new();
                npc.Instance = Instance;
                npc.config = config;
                npc.Brain = brain;
                npc.Options = Options;
                brain.HumanoidBrains = HumanoidBrains;

                int scientistCount = ScientistBrain.Count;
                CopySerializableFields(scientistNpc, npc);
                DestroyImmediate(scientistBrain, true);
                DestroyImmediate(scientistNpc, true);
                ScientistBrain.Count = scientistCount;

                SceneManager.MoveGameObjectToScene(go, Rust.Server.EntityScene);

                go.SetActive(true);

                return npc != null;
            }

            public DifficultyLevel Options;

            private List<Vector3> RandomWanderPositions(float radius)
            {
                var positions = new List<Vector3>();

                for (int i = 0; i < 10; i++)
                {
                    var target = GetRandomPoint(radius);
                    var vector = FindPointOnNavmesh(target, radius);

                    if (vector != Vector3.zero)
                    {
                        positions.Add(vector);
                    }
                }

                return positions;
            }

            private Vector3 GetRandomPoint(float radius)
            {
                var vector = containerPos + UnityEngine.Random.onUnitSphere * radius;

                vector.y = TerrainMeta.HeightMap.GetHeight(vector);

                return vector;
            }

            private HumanoidNPC SpawnNpc(bool isMurderer)
            {
                if (killed || IsUnloading)
                {
                    return null;
                }

                var positions = RandomWanderPositions(Radius * 0.9f);

                if (positions.Count == 0)
                {
                    return null;
                }

                var position = positions[0];

                if (!InstantiateEntity(position, isMurderer, out var brain, out var npc))
                {
                    return null;
                }

                ulong userid = Instance.BotIdCounter++;

                brain.isMurderer = isMurderer;
                npc.skinID = 14922525;
                npc.userID = userid;
                npc.UserIDString = userid.ToString();
                HumanoidBrains[brain.uid = npc.userID] = brain;

                List<string> names = isMurderer ? Options.NPC.Murderers.RandomNames : Options.NPC.Scientists.RandomNames;
                brain.displayName = names.Count > 0 ? names.GetRandom() : RandomUsernames.Get(npc.userID);

                npc.displayName = npc.DisplayNameOverride = brain.displayName;

                npc.loadouts = Array.Empty<PlayerInventoryProperties>();

                npcs.Add(npc);
                npc.Spawn();

                if (npc.IsKilled() || brain.isDisabled)
                {
                    npc.SafelyKill();
                    return null;
                }

                npc.CancelInvoke(npc.EquipTest);

                BasePlayer.bots.Remove(npc);

                SetupNpc(npc, brain, isMurderer, positions);

                return npc;
            }

            public class Loadout
            {
                public List<PlayerInventoryProperties.ItemAmountSkinned> belt = new();
                public List<PlayerInventoryProperties.ItemAmountSkinned> main = new();
                public List<PlayerInventoryProperties.ItemAmountSkinned> wear = new();
            }

            private PlayerInventoryProperties GetLoadout(HumanoidNPC npc, HumanoidBrain brain, bool isMurderer)
            {
                var loadout = CreateLoadout(npc, brain, isMurderer);
                var pip = ScriptableObject.CreateInstance<PlayerInventoryProperties>();

                if (pip.DeathIconPrefab == null)
                {
                    pip.DeathIconPrefab = new();
                    pip.DeathIconPrefab.guid = "6ff1ff9ea7408824ab5c8f6f3d9ab259";
                }

                pip.belt = loadout.belt;
                pip.main = loadout.main;
                pip.wear = loadout.wear;

                return pip;
            }

            private Loadout CreateLoadout(HumanoidNPC npc, HumanoidBrain brain, bool isMurderer)
            {
                var loadout = new Loadout();
                var items = isMurderer ? Options.NPC.Murderers.Items : Options.NPC.Scientists.Items;

                AddItemAmountSkinned(loadout.wear, items.Boots);
                AddItemAmountSkinned(loadout.wear, items.Gloves);
                AddItemAmountSkinned(loadout.wear, items.Helm);
                AddItemAmountSkinned(loadout.wear, items.Pants);
                AddItemAmountSkinned(loadout.wear, items.Shirt);
                AddItemAmountSkinned(loadout.wear, items.Torso);
                if (!items.Torso.Exists(v => v.Contains("suit")))
                {
                    AddItemAmountSkinned(loadout.wear, items.Kilts);
                }
                AddItemAmountSkinned(loadout.belt, items.Weapon);

                return loadout;
            }

            private void AddItemAmountSkinned(List<PlayerInventoryProperties.ItemAmountSkinned> source, List<string> shortnames)
            {
                if (shortnames.Count == 0)
                {
                    return;
                }

                string shortname = shortnames.GetRandom();

                if (string.IsNullOrEmpty(shortname))
                {
                    return;
                }

                ItemDefinition def = ItemManager.FindItemDefinition(shortname);

                if (def == null)
                {
                    Puts("Invalid shortname: {0}", shortname);
                    return;
                }

                ulong skin = 0uL;
                if (config.Skins.Npcs)
                {
                    skin = GetItemSkin(def, 0uL, config.Skins.UniqueNpcs);
                }

                source.Add(new()
                {
                    amount = 1,
                    itemDef = def,
                    skinOverride = skin,
                    startAmount = 1
                });
            }

            private void SetupNpc(HumanoidNPC npc, HumanoidBrain brain, bool isMurderer, List<Vector3> positions)
            {
                var alternate = isMurderer ? Options.NPC.Murderers.Alternate : Options.NPC.Scientists.Alternate;

                if (!alternate.None)
                {
                    if (alternate.Enabled && alternate.IDs.Count > 0)
                    {
                        var id = alternate.GetRandom();
                        var lootSpawnSlots = GameManager.server.FindPrefab(StringPool.Get(id))?.GetComponent<ScientistNPC>()?.LootSpawnSlots;

                        if (lootSpawnSlots != null)
                        {
                            npc.LootSpawnSlots = lootSpawnSlots;
                        }
                    }
                }
                else npc.LootSpawnSlots = Array.Empty<LootContainer.LootSpawnSlot>();

                npc.CancelInvoke(npc.PlayRadioChatter);
                npc.DeathEffects = Array.Empty<GameObjectRef>();
                npc.RadioChatterEffects = Array.Empty<GameObjectRef>();
                npc.radioChatterType = ScientistNPC.RadioChatterType.NONE;
                npc.startHealth = isMurderer ? Options.NPC.Murderers.Health : Options.NPC.Scientists.Health;
                npc.InitializeHealth(npc.startHealth, npc.startHealth);
                npc.Invoke(() => UpdateItems(npc, brain, isMurderer), 0.2f);
                npc.Invoke(() => SetupMovement(npc, brain, positions), 0.3f);
                npc.Invoke(() => GiveKit(npc, brain, isMurderer), 0.1f);
            }

            private void SetupMovement(HumanoidNPC npc, HumanoidBrain brain, List<Vector3> positions)
            {
                if (CannotContinue(npc, brain))
                    return;

                brain.SetupMovement(positions);
            }

            private void GiveKit(HumanoidNPC npc, HumanoidBrain brain, bool isMurderer)
            {
                if (CannotContinue(npc, brain))
                    return;

                brain.isMurderer = isMurderer;

                if (npcKits.TryGetValue(isMurderer ? "murderer" : "scientist", out var kits) && kits.Count > 0)
                {
                    string kit = kits.GetRandom();

                    if (Instance.Kits?.Call("GiveKit", npc, kit) is string val)
                    {
                        if (val.Contains("Couldn't find the player"))
                        {
                            val = "Npcs cannot use the CopyPasteFile field in Kits";
                        }
                        Puts("Invalid kit '{0}' ({1})", kit, val);
                    }

                    if (CannotContinue(npc, brain))
                        return;
                }

                using var itemList = npc.GetAllItems();

                bool isInventoryEmpty = itemList.Count == 0;

                if (isInventoryEmpty)
                {
                    var loadout = GetLoadout(npc, brain, isMurderer);

                    if (loadout.belt.Count > 0 || loadout.main.Count > 0 || loadout.wear.Count > 0)
                    {
                        npc.loadouts = new PlayerInventoryProperties[1];
                        npc.loadouts[0] = loadout;
                        npc.EquipLoadout(npc.loadouts);
                        isInventoryEmpty = false;
                    }
                }

                if (isInventoryEmpty)
                {
                    npc.inventory.GiveItem(ItemManager.CreateByName(isMurderer ? "halloween.surgeonsuit" : "hazmatsuit.spacesuit", 1, 0uL), npc.inventory.containerWear);
                    npc.inventory.GiveItem(ItemManager.CreateByName(isMurderer ? "knife.combat" : "pistol.python", 1, 0uL), npc.inventory.containerBelt);
                }
            }

            private bool CannotContinue(HumanoidNPC npc, HumanoidBrain brain)
            {
                return killed || IsUnloading || npc.IsDestroyed || npc.IsDead() || npc.Health() <= 0 || npc.inventory == null || brain == null || brain.isDisabled;
            }

            private void UpdateItems(HumanoidNPC npc, HumanoidBrain brain, bool isMurderer)
            {
                if (CannotContinue(npc, brain))
                    return;

                brain.Init();
                brain.isMurderer = isMurderer;

                EquipWeapon(npc, brain);

                if (!ToggleNpcMinerHat(npc, TOD_Sky.Instance?.IsNight == true))
                {
                    npc.inventory.ServerUpdate(0f);
                }
            }

            private bool ToggleNpcMinerHat(HumanoidNPC npc, bool state)
            {
                if (npc.IsNull() || npc.inventory == null || npc.IsDead())
                {
                    return false;
                }

                var slot = npc.inventory.FindItemByItemName("hat.miner");

                if (slot == null)
                {
                    return false;
                }

                if (state && slot.contents != null)
                {
                    slot.contents.AddItem(ItemManager.FindItemDefinition("lowgradefuel"), 50);
                }

                slot.SwitchOnOff(state);
                npc.inventory.ServerUpdate(0f);
                return true;
            }

            public void EquipWeapon(HumanoidNPC npc, HumanoidBrain brain)
            {
                bool isHoldingProjectileWeapon = false;

                using var itemList = npc.GetAllItems();

                foreach (Item item in itemList)
                {
                    if (item == null) continue;
                    if (item.GetHeldEntity() is HeldEntity e && e.IsValid())
                    {
                        if (item.skin != 0)
                        {
                            e.skinID = item.skin;
                            e.SendNetworkUpdate();
                        }

                        if (e.ShortPrefabName == "rocket_launcher.entity" || e.ShortPrefabName == "mgl.entity")
                        {
                            continue;
                        }

                        if (e is not AttackEntity attackEntity)
                        {
                            continue;
                        }

                        if (!isHoldingProjectileWeapon && attackEntity != null && attackEntity.hostileScore >= 2f && npc.inventory != null && item.GetRootContainer() == npc.inventory.containerBelt && brain._attackEntity.IsNull())
                        {
                            isHoldingProjectileWeapon = e is BaseProjectile;

                            brain.UpdateWeapon(attackEntity, item.uid);
                        }
                    }

                    item.MarkDirty();
                }

                brain.IdentifyWeapon();
            }

            void SetupNpcKits()
            {
                npcKits = new()
                {
                    { "murderer", Options.NPC.Murderers.Kits.Where(kit => IsKit(kit)).ToList() },
                    { "scientist", Options.NPC.Scientists.Kits.Where(kit => IsKit(kit)).ToList() }
                };
            }

            bool IsKit(string kit)
            {
                return Convert.ToBoolean(Instance.Kits?.Call("isKit", kit));
            }

            public void UpdateMarker()
            {
                if (!Options.Event.MarkerVending && !Options.Event.MarkerExplosion)
                {
                    CancelInvoke(UpdateMarker);
                }

                if (markerCreated)
                {
                    if (!explosionMarker.IsKilled())
                    {
                        explosionMarker.SendNetworkUpdate();
                    }

                    if (!genericMarker.IsKilled())
                    {
                        genericMarker.SendUpdate();
                    }

                    if (!vendingMarker.IsKilled())
                    {
                        vendingMarker.transform.position = containerPos;
                        vendingMarker.markerShopName = Options.Event.MarkerName;
                        vendingMarker.SendNetworkUpdate();
                    }

                    return;
                }

                if (Options.Event.MarkerManager && Instance.MarkerManager.CanCall())
                {
                    Interface.CallHook("API_CreateMarker", container as BaseEntity, "DangerousTreasures", 0, 10f, 0.25f, Options.Event.MarkerName, "FF0000", "00FFFFFF");
                    markerCreated = true;
                    return;
                }

                if (Instance.treasureChests.Sum(e => e.Value.HasRustMarker ? 1 : 0) > 10)
                {
                    return;
                }

                //explosionmarker cargomarker ch47marker cratemarker
                if (Options.Event.MarkerVending)
                {
                    vendingMarker = GameManager.server.CreateEntity(StringPool.Get(3459945130), containerPos) as VendingMachineMapMarker;

                    if (vendingMarker != null)
                    {
                        vendingMarker.enabled = false;
                        vendingMarker.markerShopName = Options.Event.MarkerName;
                        vendingMarker.Spawn();
                    }

                    CreateGenericMarker();
                }
                else if (Options.Event.MarkerExplosion)
                {
                    explosionMarker = GameManager.server.CreateEntity(StringPool.Get(4060989661), containerPos) as MapMarkerExplosion;

                    if (explosionMarker != null)
                    {
                        explosionMarker.Spawn();
                        explosionMarker.Invoke(() => explosionMarker.CancelInvoke(explosionMarker.DelayedDestroy), 1f);
                    }

                    CreateGenericMarker();
                }

                markerCreated = true;
            }

            private void CreateGenericMarker()
            {
                genericMarker = GameManager.server.CreateEntity(StringPool.Get(2849728229), containerPos) as MapMarkerGenericRadius;

                if (genericMarker != null)
                {
                    genericMarker.alpha = 0.75f;
                    genericMarker.color2 = __(Options.Event.MarkerColor);
                    genericMarker.radius = Mathf.Min(1f, World.Size <= 3600 ? Options.Event.MarkerRadiusSmall : Options.Event.MarkerRadius);
                    genericMarker.Spawn();
                    genericMarker.SendUpdate();
                }
            }

            private void KillNpc()
            {
                using var targets = Pool.Get<PooledList<HumanoidNPC>>();
                targets.AddRange(npcs);
                foreach (var npc in targets)
                {
                    npc.SafelyKill();
                }
                npcs.Clear();
            }

            public void RemoveMapMarkers()
            {
                if (!explosionMarker.IsKilled())
                {
                    explosionMarker.CancelInvoke(explosionMarker.DelayedDestroy);
                    explosionMarker.Kill(BaseNetworkable.DestroyMode.None);
                }

                genericMarker.SafelyKill();
                vendingMarker.SafelyKill();
            }

            private void OnDestroy()
            {
                DestroyMe();
            }

            public void DestroyMe()
            {
                Kill(IsUnloading);

                if (!IsUnloading && Instance.treasureChests.Count == 0)
                {
                    Instance.SubscribeHooks(false);
                }

                Free();
            }

            public void LaunchMissile()
            {
                if (!Options.MissileLauncher.Enabled)
                {
                    DestroyLauncher();
                    return;
                }

                var missilePos = missilePositions.GetRandom();
                float y = TerrainMeta.HeightMap.GetHeight(missilePos) + 15f;
                missilePos.y = Mathf.Max(200f, y);

                if (Physics.Raycast(missilePos, Vector3.down, out var hit, Mathf.Infinity, heightLayer, QueryTriggerInteraction.Ignore)) // don't want the missile to explode before it leaves its spawn location
                    missilePos.y = Mathf.Max(hit.point.y, y);

                var prefab = config.Rocket.FireRockets ? "assets/prefabs/ammo/rocket/rocket_fire.prefab" : "assets/prefabs/ammo/rocket/rocket_basic.prefab";
                var missile = GameManager.server.CreateEntity(prefab, missilePos) as TimedExplosive;

                if (missile == null)
                {
                    Options.MissileLauncher.Enabled = false;
                    DestroyLauncher();
                    return;
                }

                missiles.Add(missile);
                missiles.RemoveAll(x => x.IsKilled());

                var gs = missile.gameObject.AddComponent<GuidanceSystem>();

                gs.Options = Options;
                gs.Instance = Instance;
                gs.config = config;
                gs.Exclude(newmans);
                gs.SetTarget(container);
                gs.Launch(Options.MissileLauncher.TargettingTime);
            }

            void SpawnFire()
            {
                var firePos = firePositions.GetRandom();
                int retries = firePositions.Count;

                while (InRange2D(firePos, lastFirePos, Radius * 0.35f) && --retries > 0)
                {
                    firePos = firePositions.GetRandom();
                }

                SpawnFire(firePos);
                lastFirePos = firePos;
            }

            void SpawnFire(Vector3 firePos)
            {
                if (!Options.Fireballs.Enabled)
                    return;

                if (fireballs.Count >= 6) // limit fireballs
                {
                    foreach (var entry in fireballs)
                    {
                        entry.SafelyKill();
                        fireballs.Remove(entry);
                        break;
                    }
                }

                var fireball = GameManager.server.CreateEntity(StringPool.Get(3550347674), firePos) as FireBall;

                if (fireball == null)
                {
                    Puts(Instance._("Invalid Constant", null, 3550347674));
                    Options.Fireballs.Enabled = false;
                    CancelInvoke(SpawnFire);
                    firePositions.Clear();
                    return;
                }

                fireball.Spawn();
                fireball.damagePerSecond = Options.Fireballs.DamagePerSecond;
                fireball.generation = Options.Fireballs.Generation;
                fireball.lifeTimeMax = Options.Fireballs.LifeTimeMax;
                fireball.lifeTimeMin = Options.Fireballs.LifeTimeMin;
                fireball.radius = Options.Fireballs.Radius;
                fireball.tickRate = Options.Fireballs.TickRate;
                fireball.waterToExtinguish = Options.Fireballs.WaterToExtinguish;
                fireball.SendNetworkUpdate();
                fireball.Think();

                float lifeTime = UnityEngine.Random.Range(Options.Fireballs.LifeTimeMin, Options.Fireballs.LifeTimeMax);
                Instance.timer.Once(lifeTime, () => fireball?.Extinguish());

                fireballs.Add(fireball);
            }

            public void Destruct()
            {
                if (config.EventMessages.Destruct)
                {
                    var posStr = FormatGridReference(containerPos);

                    foreach (var target in BasePlayer.activePlayerList)
                        Message(target, "OnChestDespawned", posStr);
                }

                container.SafelyKill();
            }

            void Unclaimed()
            {
                if (!started)
                    return;

                float time = claimTime - Time.realtimeSinceStartup;

                if (time < 60f)
                    return;

                string eventPos = FormatGridReference(containerPos);

                foreach (var target in BasePlayer.activePlayerList)
                    Message(target, "DestroyingTreasure", eventPos, Instance.FormatTime(Options.Event.PlayerLimit, time, target.UserIDString), config.Settings.DistanceChatCommand);
            }

            public string GetUnlockTime(string userID = null)
            {
                return started ? null : Instance.FormatTime(Options.Event.PlayerLimit, _unlockTime - Time.realtimeSinceStartup, userID);
            }

            public void Unlock()
            {
                if (unlock != null && !unlock.Destroyed)
                {
                    unlock.Destroy();
                }

                if (!started)
                {
                    started = true;

                    if (Options.Event.DestroySphereOnStart)
                        DestroySphere();

                    if (Options.Event.DestroyFireOnStart)
                        DestroyFire();

                    if (Options.Event.DestroyLauncherOnStart)
                        DestroyLauncher();

                    SetDestructTime();

                    if (config.EventMessages.Started)
                    {
                        var posStr = FormatGridReference(containerPos);
                        foreach (var target in BasePlayer.activePlayerList)
                        {
                            Message(target, requireAllNpcsDie && npcSpawnedAmount > 0 ? "StartedNpcs" : "Started", posStr);
                        }
                        Puts(Instance._(requireAllNpcsDie && npcSpawnedAmount > 0 ? "StartedNpcs" : "Started", null, Instance.FormatGridReference(containerPos, true)));
                    }

                    if (config.UnlootedAnnouncements.Enabled)
                    {
                        claimTime = Time.realtimeSinceStartup + Options.Event.DestructTime;
                        announcement = Instance.timer.Repeat(config.UnlootedAnnouncements.Interval * 60f, 0, Unclaimed);
                    }
                }

                if (requireAllNpcsDie && npcSpawnedAmount > 0 && npcs != null)
                {
                    npcs.RemoveAll(npc => npc.IsKilled() || npc.IsDead());

                    if (npcs.Count > 0)
                    {
                        Invoke(Unlock, 1f);
                        return;
                    }
                }

                using (var update = container.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
                {
                    update.Set(BaseEntity.Flags.Locked, false);
                    update.Set(BaseEntity.Flags.OnFire, false);
                }
            }

            public void SetDestructTime()
            {
                if (Options.Event.DestructTime > 0f)
                {
                    if (destruct != null && !destruct.Destroyed)
                    {
                        destruct.Destroy();
                    }
                    destruct = Instance.timer.Once(Options.Event.DestructTime, Destruct);
                }
            }

            public void SetUnlockTime(float time)
            {
                countdownTime = Convert.ToInt32(time);
                _unlockTime = Convert.ToInt64(Time.realtimeSinceStartup + time);

                if (npcSpawnedAmount == 0 && Options.NPC.Murderers.SpawnAmount + Options.NPC.Scientists.SpawnAmount > 0 && Options.NPC.Enabled)
                {
                    if (requireAllNpcsDie || whenNpcsDie)
                    {
                        whenNpcsDie = false;
                        requireAllNpcsDie = false;
                    }
                }

                unlock = Instance.timer.Once(time, Unlock);

                if (config.Countdown.Enabled && !config.Countdown.Times.IsNullOrEmpty() && countdownTime > 0)
                {
                    if (times.Count == 0)
                        times.AddRange(config.Countdown.Times);

                    countdown = Instance.timer.Repeat(1f, 0, () =>
                    {
                        countdownTime--;

                        if (started || times.Count == 0)
                        {
                            countdown.Destroy();
                            return;
                        }

                        if (times.Remove(countdownTime))
                        {
                            string eventPos = FormatGridReference(containerPos);

                            foreach (var target in BasePlayer.activePlayerList)
                                Message(target, "Countdown", eventPos, Instance.FormatTime(Options.Event.PlayerLimit, countdownTime, target.UserIDString));
                        }
                    });
                }
            }

            public void TrySetOwner(HitInfo hitInfo)
            {
                if (hitInfo == null || userid.IsSteamId()) return;
                var attacker = hitInfo.Initiator as BasePlayer;
                if (attacker == null || !attacker.userID.IsSteamId()) return;
                userid = attacker.userID;
            }

            private void SafelyKill(BaseEntity e) => e.SafelyKill();

            public void DestroyLauncher()
            {
                if (missilePositions.Count > 0)
                {
                    CancelInvoke(LaunchMissile);
                    missilePositions.Clear();
                }

                if (missiles.Count > 0)
                {
                    missiles.ForEach(SafelyKill);
                    missiles.Clear();
                }
            }

            public void DestroySphere()
            {
                if (spheres.Count > 0)
                {
                    spheres.ForEach(SafelyKill);
                    spheres.Clear();
                }
            }

            public void DestroyFire()
            {
                CancelInvoke(SpawnFire);
                firePositions.Clear();

                if (fireballs.Count > 0)
                {
                    fireballs.ForEach(SafelyKill);
                    fireballs.Clear();
                }

                Instance.newmanProtections.RemoveAll(protects.Contains);
                traitors.Clear();
                newmans.Clear();
                protects.Clear();
            }
        }

        void OnNewSave(string filename) => wipeChestsSeed = true;

        void Init()
        {
            SubscribeHooks(false);
        }

        void OnServerInitialized(bool isStartup)
        {
            LoadData();
            TryWipeData();
            BlockZoneManagerZones(true);
            InitializeMonuments();
            InitializeSkins();
            timer.Repeat(Mathf.Clamp(config.EventMessages.Interval, 1f, 60f), 0, CheckNotifications);
            LoadOwnership();
            InitializeArmorSlots();
        }

        void Unload()
        {
            foreach (var chest in treasureChests.Values.ToList())
            {
                if (chest != null)
                {
                    Puts(_("Destroyed Treasure Chest", null, chest.containerPos));

                    chest.Kill(true);
                }
            }

            if (_cmc != null)
                ServerMgr.Instance.StopCoroutine(_cmc);
            if (_gridCo != null)
                ServerMgr.Instance.StopCoroutine(_gridCo);

        }

        object canTeleport(BasePlayer player)
        {
            return EventTerritory(player.transform.position) ? msg("CannotTeleport", player.UserIDString) : null;
        }

        object CanTeleport(BasePlayer player)
        {
            return EventTerritory(player.transform.position) ? msg("CannotTeleport", player.UserIDString) : null;
        }

        object CanBradleyApcTarget(BradleyAPC apc, HumanoidNPC npc)
        {
            return npc != null && HasNPC(npc.userID) ? (object)false : null;
        }

        object OnEntityEnter(TriggerBase trigger, BasePlayer player)
        {
            if (player.IsValid())
            {
                if (newmanProtections.Contains(player.userID) || HasNPC(player.userID))
                {
                    return true;
                }
            }

            return null;
        }

        private object OnNpcDuck(HumanoidNPC npc) => npc != null && HasNPC(npc.userID) ? true : (object)null;

        private object OnNpcDestinationSet(HumanoidNPC npc, Vector3 newDestination)
        {
            if (npc.IsNull() || npc.NavAgent == null || !npc.NavAgent.enabled || !npc.NavAgent.isOnNavMesh)
            {
                return true;
            }

            if (!HumanoidBrains.TryGetValue(npc.userID, out var brain) || brain.CanRoam(newDestination))
            {
                return null;
            }

            return true;
        }

        private object OnNpcResume(HumanoidNPC npc)
        {
            if (npc.IsNull())
            {
                return null;
            }

            if (!HumanoidBrains.TryGetValue(npc.userID, out var brain))
            {
                return null;
            }

            return true;
        }

        object OnNpcTarget(BasePlayer player, BasePlayer target)
        {
            if (player == null || target == null)
            {
                return null;
            }

            if (HasNPC(player.userID) && !target.userID.IsSteamId())
            {
                return true;
            }
            else if (HasNPC(target.userID) && !player.userID.IsSteamId())
            {
                return true;
            }
            else if (newmanProtections.Contains(target.userID))
            {
                return true;
            }

            return null;
        }

        object OnNpcTarget(BaseNpc npc, BasePlayer target)
        {
            if (npc == null || target == null)
            {
                return null;
            }

            if (HasNPC(target.userID) || newmanProtections.Contains(target.userID))
            {
                return true;
            }

            return null;
        }

        object OnNpcTarget(BasePlayer target, BaseNpc npc) => OnNpcTarget(npc, target);

        void OnEntitySpawned(BaseLock entity)
        {
            NextTick(() =>
            {
                if (!entity.IsKilled())
                {
                    foreach (var x in treasureChests.Values)
                    {
                        if (entity.HasParent() && entity.GetParentEntity() == x.container)
                        {
                            entity.SafelyKill();
                            break;
                        }
                    }
                }
            });
        }

        void OnEntitySpawned(DroppedItemContainer backpack)
        {
            var tc = Get(backpack);
            if (tc == null)
            {
                return;
            }

            if (backpack.ShortPrefabName == "item_drop_backpack")
            {
                if (!tc.Options.Event.PlayersLootable)
                    return;

                backpack.Invoke(() =>
                {
                    if (!backpack.IsKilled() && backpack.playerSteamID.IsSteamId())
                    {
                        backpack.playerSteamID = 0;
                    }
                }, 0.2f);
            }
            else if (backpack.ShortPrefabName == "item_drop" || backpack.ShortPrefabName == "item_drop_buoyant")
            {
                backpack.buryLeftoverItems = false;
            }
        }

        void OnEntitySpawned(PlayerCorpse corpse)
        {
            var tc = Get(corpse);
            if (tc == null)
            {
                return;
            }

            if (tc.Options.Event.PlayersLootable && !corpse.IsKilled() && EventTerritory(corpse.transform.position))
            {
                NextTick(() =>
                {
                    if (!corpse.IsKilled() && corpse.playerSteamID.IsSteamId())
                    {
                        corpse.playerSteamID = 0;
                    }
                });
            }
        }

        object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            var player = planner?.GetOwnerPlayer();

            if (player == null || player.IsAdmin) return null;

            var chest = Get(player);

            if (chest != null)
            {
                Message(player, "Building is blocked!");
                return false;
            }

            return null;
        }

        private bool IsAlly(ulong playerId, ulong targetId)
        {
            if (playerId == targetId)
            {
                return true;
            }

            if (RelationshipManager.ServerInstance.playerToTeam.TryGetValue(playerId, out var team) && team.members.Contains(targetId))
            {
                return true;
            }

            if (Clans.CanCall() && Convert.ToBoolean(Clans?.Call("IsMemberOrAlly", playerId, targetId)))
            {
                return true;
            }

            if (Friends.CanCall() && Convert.ToBoolean(Friends?.Call("AreFriends", playerId.ToString(), targetId.ToString())))
            {
                return true;
            }

            return false;
        }

        private object CanLootEntity(BasePlayer player, BoxStorage container)
        {
            if (player == null || !container.IsValid() || !treasureChests.ContainsKey(container.net.ID))
                return null;

            if (player.isMounted)
            {
                Message(player, "CannotBeMounted");
                return true;
            }
            else looters[container.net.ID] = player.UserIDString;

            var chest = treasureChests[container.net.ID];

            if (chest.userid.IsSteamId() && !IsAlly(chest.userid, player.userID))
            {
                Message(player, "CannotBeLooted");
                return true;
            }

            if (chest.opened || !config.EventMessages.FirstOpened)
            {
                return null;
            }

            chest.opened = true;
            var posStr = FormatGridReference(container.transform.position, config.Settings.ShowGrid);

            foreach (var target in BasePlayer.activePlayerList)
            {
                Message(target, "OnChestOpened", player.displayName, posStr);
            }

            return null;
        }

        private void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            if (container?.entityOwner == null || container.entityOwner.IsDestroyed || !container.entityOwner.Is(out StorageContainer box))
                return;

            box.Invoke(() =>
            {
                if (!box.IsValid() || box.IsDestroyed || !treasureChests.TryGetValue(box.net.ID, out var tc))
                    return;

                var looter = item.GetOwnerPlayer();

                if (looter != null)
                {
                    looters[box.net.ID] = looter.UserIDString;
                }

                if (box.inventory.itemList.Count == 0)
                {
                    if (looter == null && looters.ContainsKey(box.net.ID))
                        looter = BasePlayer.Find(looters[box.net.ID]);

                    if (looter != null)
                    {
                        if (config.RankedLadder.Enabled)
                        {
                            if (!data.Players.TryGetValue(looter.UserIDString, out var pi))
                                data.Players.Add(looter.UserIDString, pi = new());

                            pi.StolenChestsTotal++;
                            pi.StolenChestsSeed++;
                            SaveData();
                        }

                        Puts(_("Thief", null, FormatGridReference(looter.transform.position, true), looter.displayName));

                        if (config.EventMessages.Thief)
                        {
                            var posStr = FormatGridReference(looter.transform.position, config.Settings.ShowGrid);

                            foreach (var target in BasePlayer.activePlayerList)
                            {
                                Message(target, "Thief", posStr, looter.displayName);
                            }
                        }

                        looter.EndLooting();
                        var rewards = tc.Options.Rewards;
                        if (rewards.Economics && rewards.Money > 0 && Economics.CanCall())
                        {
                            Economics?.Call("Deposit", looter.UserIDString, rewards.Money);
                            Message(looter, "EconomicsDeposit", rewards.Money);
                        }

                        if (rewards.ServerRewards && rewards.Points > 0 && ServerRewards.CanCall())
                        {
                            if (Convert.ToBoolean(ServerRewards?.Call("AddPoints", (ulong)looter.userID, (int)rewards.Points)))
                            {
                                Message(looter, "ServerRewardPoints", (int)rewards.Points);
                            }
                        }

                        var boc = rewards.EventCommands;
                        if (boc.Any())
                        {
                            foreach (var target in tc.invaders)
                            {
                                ulong ownerid = 0uL;
                                if (boc.Owner) ownerid = tc.userid.IsSteamId() ? tc.userid : looter.userID;
                                if (!IsAlly(target, ownerid)) continue;
                                RunCommands(boc, target, ownerid);
                            }
                        }

                        Interface.CallHook("OnDangerousEventWon", looter, tc.invaders);
                    }

                    box.SafelyKill();

                    if (treasureChests.Count == 0)
                        SubscribeHooks(false);
                }
            }, 0.1f);
        }

        private void RunCommands(RewardRunCommands boc, ulong userid, ulong ownerid)
        {
            if (!boc.Enabled)
            {
                return;
            }
            foreach (var command in boc.Commands)
            {
                if (string.IsNullOrWhiteSpace(command)) continue;
                if (!CanAssignTo(userid, ownerid > 0 ? ownerid : userid, boc.Owner)) continue;
                ConsoleSystem.Run(ConsoleSystem.Option.Server, command.Replace("{userid}", userid.ToString()));
            }
        }

        private bool CanAssignTo(ulong userid, ulong ownerid, bool only)
        {
            return only == false || ownerid == 0uL || userid == ownerid;
        }

        private object CanEntityBeTargeted(BasePlayer player, BaseEntity target)
        {
            return !player.IsKilled() && player.IsHuman() && EventTerritory(player.transform.position) && !target.IsKilled() && IsTrueDamage(target) ? (object)true : null;
        }

        private object CanEntityTrapTrigger(BaseTrap trap, BasePlayer player)
        {
            return !player.IsKilled() && player.IsHuman() && EventTerritory(player.transform.position) ? (object)true : null;
        }

        private object CanEntityTakeDamage(BaseEntity entity, HitInfo hitInfo) // TruePVE!!!! <3 @ignignokt84
        {
            if (entity.IsKilled() || hitInfo == null || hitInfo.Initiator == null || entity.skinID == 14922524)
            {
                return null;
            }

            var attacker = hitInfo.Initiator as BasePlayer;

            if (attacker != null && attacker.skinID == 14922524)
            {
                return null;
            }

            if (Convert.ToBoolean(RaidableBases?.Call("EventTerritory", hitInfo.Initiator.ServerPosition)))
            {
                return null;
            }

            if (Convert.ToBoolean(RaidableBases?.Call("EventTerritory", entity.ServerPosition)))
            {
                return null;
            }

            if (entity is HumanoidNPC && entity.skinID == 14922525)
            {
                return true;
            }

            if (config.TruePVE.ServerWidePVP && treasureChests.Count > 0 && attacker != null && entity is BasePlayer) // 1.2.9 & 1.3.3 & 1.6.4
            {
                return true;
            }

            if (EventTerritory(entity.transform.position)) // 1.5.8 & 1.6.4
            {
                if (entity is NPCPlayerCorpse || IsTrueDamage(hitInfo.Initiator))
                {
                    return true;
                }

                if (config.TruePVE.AllowPVPAtEvents && entity is BasePlayer && attacker != null && EventTerritory(attacker.transform.position)) // 1.2.9
                {
                    return true;
                }

                if (config.TruePVE.AllowBuildingDamageAtEvents && entity.name.Contains("building") && attacker != null && EventTerritory(attacker.transform.position)) // 1.3.3
                {
                    return true;
                }
            }

            return null; // 1.6.4 rewrite
        }

        private void OnEntityTakeDamage(BasePlayer player, HitInfo hitInfo)
        {
            if (player == null || hitInfo == null)
            {
                return;
            }

            if (HasNPC(player.userID))
            {
                NpcDamageHelper(player, hitInfo);
                return;
            }

            if (newmanProtections.Contains(player.userID))
            {
                ProtectionDamageHelper(hitInfo, "Newman Protected");
                return;
            }

            var attacker = hitInfo.Initiator as BasePlayer;

            if (attacker == null)
            {
                return;
            }

            if (HumanoidBrains.TryGetValue(attacker.userID, out var brain) && brain != null && brain.AttackEntity != null && (brain.isMurderer && UnityEngine.Random.Range(0f, 100f) > brain.Settings.Murderers.Accuracy.Get(brain) || !brain.isMurderer && UnityEngine.Random.Range(0f, 100f) > brain.Settings.Scientists.Accuracy.Get(brain)))
            {
                hitInfo.damageTypes?.Clear();
                hitInfo.DidHit = false;
                hitInfo.DoHitEffects = false;
            }
        }

        private void OnEntityTakeDamage(BoxStorage box, HitInfo hitInfo)
        {
            if (hitInfo != null && box.IsValid() && treasureChests.ContainsKey(box.net.ID))
            {
                ProtectionDamageHelper(hitInfo, "Indestructible");
                hitInfo.damageTypes.ScaleAll(0f);
            }
        }

        private void ProtectionDamageHelper(HitInfo hitInfo, string key)
        {
            var attacker = hitInfo.Initiator as BasePlayer;

            if (attacker.IsValid() && attacker.IsHuman() && !indestructibleWarnings.Contains(attacker.userID))
            {
                ulong uid = attacker.userID;
                indestructibleWarnings.Add(uid);
                timer.Once(10f, () => indestructibleWarnings.Remove(uid));
                Message(attacker, key);
            }

            hitInfo.damageTypes.ScaleAll(0f);
        }

        private object CanPopulateLoot(BaseEntity entity, LootableCorpse corpse)
        {
            return corpse != null && corpse.skinID == 14922525 ? true : (object)null;
        }

        private object ShouldBLPopulate_NPC(ulong playerSteamID)
        {
            return playerSteamID >= 624922525 && playerSteamID <= BotIdCounter ? true : (object)null;
        }

        private object OnNpcKits(ulong targetId)
        {
            return HasNPC(targetId) ? true : (object)null;
        }

        private bool HasNPC(ulong userID)
        {
            return HumanoidBrains.ContainsKey(userID);
        }

        private TreasureChest Get(Vector3 target)
        {
            foreach (var x in treasureChests.Values)
            {
                if (InRange2D(x.containerPos, target, x.Radius))
                {
                    return x;
                }
            }
            return null;
        }

        private TreasureChest Get(BaseEntity entity)
        {
            if (entity.IsKilled())
            {
                return null;
            }
            return Get(entity.transform.position);
        }

        private bool IsTrueDamage(BaseEntity entity)
        {
            if (entity.IsNull())
            {
                return false;
            }

            return entity is AutoTurret || entity is BearTrap || entity is FlameTurret || entity is Landmine || entity is GunTrap || entity is ReactiveTarget || entity.name.Contains("spikes.floor") || entity is FireBall;
        }

        private bool EventTerritory(Vector3 target)
        {
            foreach (var x in treasureChests.Values)
            {
                if ((x.containerPos - target).sqrMagnitude <= x.Radius * x.Radius)
                {
                    return true;
                }
            }

            return false;
        }

        private void LoadData()
        {
            try { data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name); } catch { }
            data ??= new();
            data.Players ??= new();
            data.SecondsUntilEvent ??= new();
            sd_customPos = string.IsNullOrEmpty(data.CustomPosition) ? Vector3.zero : data.CustomPosition.ToVector3();
        }

        void TryWipeData()
        {
            if (wipeChestsSeed)
            {
                if (data.Players.Count > 0)
                {
                    var ladder = data.Players.Where(kvp => kvp.Value.StolenChestsSeed > 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value.StolenChestsSeed).ToList();

                    if (ladder.Count > 0 && AssignTreasureHunters(ladder))
                    {
                        foreach (var pi in data.Players.Values.ToList())
                        {
                            pi.StolenChestsSeed = 0;
                        }
                    }
                }

                data.CustomPosition = string.Empty;
                sd_customPos = Vector3.zero;
                wipeChestsSeed = false;
                SaveData();
            }
        }

        void BlockZoneManagerZones(bool show)
        {
            managedZones.Clear();

            if (!ZoneManager.CanCall())
            {
                return;
            }

            timer.Once(30f, () => BlockZoneManagerZones(false));

            var zoneIds = ZoneManager?.Call("GetZoneIDs") as string[];

            if (zoneIds == null)
            {
                return;
            }

            foreach (string zoneId in zoneIds)
            {
                var zoneLoc = ZoneManager.Call("GetZoneLocation", zoneId);

                if (zoneLoc is not Vector3 position || position == default)
                {
                    continue;
                }

                var zoneInfo = new ZoneInfo();
                var radius = ZoneManager.Call("GetZoneRadius", zoneId);

                if (radius is float r)
                {
                    zoneInfo.Distance = r;
                }

                var size = ZoneManager.Call("GetZoneSize", zoneId);

                if (size is Vector3 s)
                {
                    zoneInfo.Size = s;
                }

                zoneInfo.Position = position;
                zoneInfo.OBB = new OBB(zoneInfo.Position, zoneInfo.Size, Quaternion.identity);
                managedZones[position] = zoneInfo;
            }

            if (show && managedZones.Count > 0)
            {
                Puts("Blocked {0} zone manager zones", managedZones.Count);
            }
        }

        private class MonumentInfoEx
        {
            public MonumentInfo monument;
            public Vector3 position;
            public float radius;
            public string name;
            public string prefab;
            public MonumentInfoEx() { }
            public MonumentInfoEx(MonumentInfo monument, Vector3 position, float radius, string name, string prefab)
            {
                this.monument = monument;
                this.position = position;
                this.radius = radius;
                this.name = name;
                this.prefab = prefab;
            }
            public bool IsInBounds(Vector3 target)
            {
                if (InRange2D(target, position, radius))
                {
                    return true;
                }
                return monument != null && monument.transform.position.y < 0f && TerrainMeta.HeightMap.GetHeight(target) < 0f && monument.IsInBounds(target);
            }
        }

        private Coroutine _cmc;

        private void InitializeMonuments() => _cmc = ServerMgr.Instance.StartCoroutine(SetupMonuments());

        private IEnumerator SetupMonuments()
        {
            int checks = 0;
            foreach (var prefab in World.Serialization.world.prefabs)
            {
                if (prefab.id == 1724395471 && prefab.category != "IGNORE_MONUMENT")
                {
                    yield return CalculateMonumentSize(null, new(prefab.position.x, prefab.position.y, prefab.position.z), prefab.category, "monument_marker");
                }
                if (++checks >= 1000)
                {
                    yield return CoroutineEx.waitForSeconds(0.0025f);
                    checks = 0;
                }
            }
            foreach (var monument in UnityEngine.Object.FindObjectsByType<MonumentInfo>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            {
                if (monument.name.Contains("monument_marker"))
                {
                    foreach (var m in monuments)
                    {
                        if (m.monument == null && monument.transform.position == m.position)
                        {
                            m.monument = monument;
                            break;
                        }
                    }
                    continue;
                }
                var monPos = monument.transform.position;
                var name = monument.displayPhrase?.english?.TrimEnd() ?? null;
                if (string.IsNullOrEmpty(name))
                {
                    if (monument.name.Contains("cave"))
                    {
                        name = monument.name.Contains("cave_small") ? "Small Cave" : monument.name.Contains("cave_medium") ? "Medium Cave" : "Large Cave";
                    }
                    else name = monument.name;
                }
                if (name.Contains("/"))
                {
                    name = Utility.GetFileNameWithoutExtension(monument.name);
                }
                yield return CalculateMonumentSize(monument, monument.transform.position, name, monument.name);
            }
            SortMonuments();
            IsMonumentsReady = true;
            if (!config.Monuments.Only)
            {
                EnsureGridPositions();
                if (!IsGridReady)
                    yield return _gridCo;
            }
            _cmc = null;
            StartAutomation();
        }

        public IEnumerator CalculateMonumentSize(MonumentInfo monument, Vector3 from, string text, string prefab)
        {
            int checks = 0;
            float radius = 15f;
            while (radius < World.Size / 2f)
            {
                int pointsOfTopology = 0;
                foreach (var to in GetCircumferencePositions(from, radius, 30f))
                {
                    if (ContainsTopology(TerrainTopology.Enum.Building | TerrainTopology.Enum.Monument, to, 5f))
                    {
                        pointsOfTopology++;
                    }
                    if (++checks >= 25)
                    {
                        yield return CoroutineEx.waitForSeconds(0.0025f);
                        checks = 0;
                    }
                }
                if (pointsOfTopology < 4)
                {
                    break;
                }
                radius += 15f;
            }
            if (radius == 15f)
            {
                radius = 100f;
            }
            monuments.Add(new(monument, from, radius, text, prefab));
        }

        public bool ContainsTopology(TerrainTopology.Enum mask, Vector3 position, float radius)
        {
            return (TerrainMeta.TopologyMap.GetTopology(position, radius) & (int)mask) != 0;
        }

        public List<Vector3> GetCircumferencePositions(Vector3 center, float radius, float next)
        {
            float degree = 0f;
            float angleInRadians = 2f * Mathf.PI;
            List<Vector3> positions = new();

            while (degree < 360)
            {
                float radian = (angleInRadians / 360) * degree;
                float x = center.x + radius * Mathf.Cos(radian);
                float z = center.z + radius * Mathf.Sin(radian);
                Vector3 a = new(x, 0f, z);
                a.y = Mathf.Max(center.y, WaterSystem.OceanLevel, TerrainMeta.HeightMap.GetHeight(a));
                positions.Add(a);

                degree += next;
            }

            return positions;
        }

        private void SortMonuments()
        {
            int eventBlacklistCount = config.Monuments.EventBlacklist.Count;
            int npcBlacklistCount = config.Monuments.NPCBlacklist.Count;
            _allowedMonuments.Clear();

            foreach (var monument in monuments)
            {
                string name = monument.name;
                if (string.IsNullOrEmpty(name))
                {
                    continue;
                }

                if (!name.Contains("cave") && !name.Contains("power_sub") && !config.Monuments.NPCBlacklist.ContainsKey(name))
                {
                    config.Monuments.NPCBlacklist.Add(name, false);
                }

                if (!config.Monuments.EventBlacklist.TryGetValue(name, out bool disabled))
                {
                    config.Monuments.EventBlacklist.Add(name, disabled = false);
                }

                if (disabled)
                {
                    continue;
                }

                bool isUnderground = false;
                if (!config.Monuments.Underground)
                {
                    foreach (string value in underground)
                    {
                        if (name.Contains(value, CompareOptions.OrdinalIgnoreCase))
                        {
                            isUnderground = true;
                            break;
                        }
                    }
                }

                if (!isUnderground)
                {
                    _allowedMonuments.Add(monument);
                }
            }

            if (config.Monuments.EventBlacklist.Count != eventBlacklistCount || config.Monuments.NPCBlacklist.Count != npcBlacklistCount)
            {
                config.Monuments.EventBlacklist = System.Linq.Enumerable.OrderBy(config.Monuments.EventBlacklist, x => x.Key).ToDictionary(x => x.Key, x => x.Value);
                config.Monuments.NPCBlacklist = System.Linq.Enumerable.OrderBy(config.Monuments.NPCBlacklist, x => x.Key).ToDictionary(x => x.Key, x => x.Value);
                SaveConfig();
            }
        }

        private void InitializeSkins()
        {
            foreach (var def in ItemManager.GetItemDefinitions())
            {
                if (def.TryGetComponent<ItemModDeployable>(out var imd))
                {
                    _definitions[imd.entityPrefab.resourcePath] = def;
                }
            }
        }

        private void StartAutomation()
        {
            eventRetries.Clear();
            foreach (var option in config.Levels)
            {
                if (option.Event.Automated && data.SecondsUntilEvent.TryGetValue(option.Level, out double seconds))
                {
                    if (seconds != double.MinValue && seconds - Facepunch.Math.Epoch.Current > option.Event.IntervalMax) // Allows users to lower max event time
                    {
                        data.SecondsUntilEvent[option.Level] = double.MinValue;
                    }
                }
            }
            timer.Once(1f, CheckSecondsUntilEvent);
        }

        private static PooledList<T> FindEntitiesOfType<T>(Vector3 a, float n, int m = -1) where T : BaseEntity
        {
            PooledList<T> entities = Pool.Get<PooledList<T>>();
            Vis.Entities(a, n, entities, m, QueryTriggerInteraction.Collide);
            return entities;
        }

        void NpcDamageHelper(BasePlayer player, HitInfo hitInfo)
        {
            if (!HumanoidBrains.TryGetValue(player.userID, out var brain))
            {
                return;
            }

            if (brain.Settings.Range > 0f && hitInfo.ProjectileDistance > brain.Settings.Range || hitInfo.hasDamage && !(hitInfo.Initiator is BasePlayer) && !(hitInfo.Initiator is AutoTurret)) // immune to fire/explosions/other
            {
                hitInfo.damageTypes = new();
                hitInfo.DidHit = false;
                hitInfo.DoHitEffects = false;
            }
            else if (hitInfo.isHeadshot && (brain.isMurderer && brain.Settings.Murderers.Headshot || !brain.isMurderer && brain.Settings.Scientists.Headshot))
            {
                player.Die(hitInfo);
            }
            else if (hitInfo.Initiator is BasePlayer attacker)
            {
                var e = attacker.HasParent() ? attacker.GetParentEntity() : null;

                if (!(e == null) && (e is ScrapTransportHelicopter || e is HotAirBalloon || e is CH47Helicopter))
                {
                    hitInfo.damageTypes.ScaleAll(0f);
                    return;
                }

                if (brain.Options.Event.DestructTimeResetsWhenAttacked && attacker.userID.IsSteamId())
                {
                    brain.tc.SetDestructTime();
                }

                brain.SetTarget(attacker);
            }
        }

        private static bool InRange2D(Vector3 a, Vector3 b, float distance)
        {
            return (new Vector3(a.x, 0f, a.z) - new Vector3(b.x, 0f, b.z)).sqrMagnitude <= distance * distance;
        }

        private static bool InRange(Vector3 a, Vector3 b, float distance)
        {
            return (a - b).sqrMagnitude <= distance * distance;
        }

        private bool IsMelee(BasePlayer player)
        {
            var attackEntity = player.GetHeldEntity() as AttackEntity;

            if (attackEntity == null)
            {
                return false;
            }

            return attackEntity is BaseMelee;
        }

        private void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, data);

        protected new static void Puts(string format, params object[] args)
        {
            Interface.Oxide.LogInfo("[{0}] {1}", Name, (args.Length != 0) ? string.Format(format, args) : format);
        }

        void SubscribeHooks(bool flag)
        {
            if (flag)
            {
                if (config.Levels.Exists(x => x.NPC.Enabled))
                {
                    if (config.Settings.BlockAlphaLoot)
                    {
                        Subscribe(nameof(CanPopulateLoot));
                    }

                    if (config.Settings.BlockBetterLoot)
                    {
                        Subscribe(nameof(ShouldBLPopulate_NPC));
                    }

                    if (config.Settings.BlockNpcKits)
                    {
                        Subscribe(nameof(OnNpcKits));
                    }
                }

                Subscribe(nameof(CanEntityTakeDamage));
                Subscribe(nameof(OnNpcTarget));
                Subscribe(nameof(OnNpcResume));
                Subscribe(nameof(OnNpcDestinationSet));
                Subscribe(nameof(OnEntitySpawned));
                Subscribe(nameof(CanBradleyApcTarget));
                Subscribe(nameof(OnEntityTakeDamage));
                Subscribe(nameof(OnItemRemovedFromContainer));
                Subscribe(nameof(CanLootEntity));
                Subscribe(nameof(CanBuild));
                Subscribe(nameof(CanTeleport));
                Subscribe(nameof(canTeleport));
                Subscribe(nameof(OnEntityEnter));
            }
            else
            {
                Unsubscribe(nameof(CanPopulateLoot));
                Unsubscribe(nameof(ShouldBLPopulate_NPC));
                Unsubscribe(nameof(OnNpcKits));
                Unsubscribe(nameof(CanTeleport));
                Unsubscribe(nameof(canTeleport));
                Unsubscribe(nameof(OnEntityEnter));
                Unsubscribe(nameof(CanEntityTakeDamage));
                Unsubscribe(nameof(CanBradleyApcTarget));
                Unsubscribe(nameof(OnNpcTarget));
                Unsubscribe(nameof(OnNpcResume));
                Unsubscribe(nameof(OnNpcDestinationSet));
                Unsubscribe(nameof(OnEntitySpawned));
                Unsubscribe(nameof(OnEntityTakeDamage));
                Unsubscribe(nameof(OnItemRemovedFromContainer));
                Unsubscribe(nameof(CanLootEntity));
                Unsubscribe(nameof(CanBuild));
            }
        }

        private static List<Vector3> GetRandomPositions(Vector3 destination, float radius, int amount, float y)
        {
            var positions = new List<Vector3>();

            if (amount <= 0)
                return positions;

            int retries = 100;
            float space = (radius / amount); // space each rocket out from one another

            for (int i = 0; i < amount; i++)
            {
                var position = destination + UnityEngine.Random.insideUnitSphere * radius;

                position.y = y != 0f ? y : UnityEngine.Random.Range(100f, 200f);

                var match = Vector3.zero;

                foreach (var p in positions)
                {
                    if (InRange2D(p, position, space))
                    {
                        match = p;
                        break;
                    }
                }

                if (match != Vector3.zero)
                {
                    if (--retries < 0)
                        break;

                    i--;
                    continue;
                }

                retries = 100;
                positions.Add(position);
            }

            return positions;
        }

        private bool IsInsideBounds(OBB obb, Vector3 worldPos)
        {
            return obb.ClosestPoint(worldPos) == worldPos;
        }

        public Vector3 GetEventPosition(DifficultyLevel options)
        {
            if (sd_customPos != Vector3.zero)
            {
                return sd_customPos;
            }

            if (!IsMonumentsReady)
            {
                return Vector3.zero;
            }

            Vector3 eventPos = TryGetMonumentDropPosition(options);
            if (eventPos != Vector3.zero || config.Monuments.Only)
            {
                return eventPos;
            }

            EnsureGridPositions();
            if (!IsGridReady) return Vector3.zero;
            int attempts = Math.Min(500, _gridPositionsSrc.Count);
            if (_gridPositions.Count < attempts)
            {
                _gridPositions.Clear();
                _gridPositions.AddRange(_gridPositionsSrc);
            }

            while (attempts-- > 0 && _gridPositions.Count > 0)
            {
                Vector3 position = TakeRandom(_gridPositions);
                if (position == Vector3.zero || IsTooClose(position) || IsSafeZone(position))
                {
                    continue;
                }

                eventPos = GetSafeDropPosition(options, position);
                if (eventPos != Vector3.zero)
                {
                    return eventPos;
                }
            }

            return Vector3.zero;
        }

        public Vector3 TryGetMonumentDropPosition(DifficultyLevel options)
        {
            if (_allowedMonuments.Count == 0)
            {
                return Vector3.zero;
            }

            return config.Monuments.Only || config.Monuments.Chance > 0f && UnityEngine.Random.value <= config.Monuments.Chance ? GetMonumentDropPosition(options) : Vector3.zero;
        }

        private bool IsOtherEventPosition(Vector3 position)
        {
            if (Duelist.CanCall() && Convert.ToBoolean(Duelist.Call("DuelistTerritory", position))) return true;
            if (RaidableBases.CanCall() && Convert.ToBoolean(RaidableBases.Call("EventTerritory", position))) return true;
            return AbandonedBases.CanCall() && Convert.ToBoolean(AbandonedBases.Call("EventTerritory", position));
        }

        private bool IsTooClose(Vector3 vector, float multi = 2f)
        {
            foreach (var x in treasureChests.Values)
            {
                if (InRange2D(x.containerPos, vector, x.Radius * multi))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsZoneBlocked(Vector3 vector)
        {
            foreach (var zone in managedZones)
            {
                if (zone.Value.Size != Vector3.zero)
                {
                    if (IsInsideBounds(zone.Value.OBB, vector))
                    {
                        return true;
                    }
                }
                else if (InRange2D(zone.Key, vector, zone.Value.Distance))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsSafeZone(Vector3 a)
        {
            foreach (var zone in TriggerSafeZone.allSafeZones)
            {
                if (zone != null && InRange2D(zone.transform.position, a, 200f))
                    return true;
            }
            return false;
        }

        private Vector3 GetSafeDropPosition(DifficultyLevel options, Vector3 position)
        {
            float terrainHeight = TerrainMeta.HeightMap.GetHeight(position);
            float waterHeight = TerrainMeta.WaterMap.GetHeight(position);
            if (waterHeight - terrainHeight > 0.1f)
                return Vector3.zero;

            float y = position.y;
            if (!Physics.Raycast(position + Vector3.up * 200f, Vector3.down, out var hit, 1000f, heightLayer, QueryTriggerInteraction.Collide))
            {
                return Vector3.zero;
            }

            if (BlockedLayers.Contains(hit.collider.gameObject.layer))
            {
                return Vector3.zero;
            }

            string name = hit.collider.name;
            if (name.StartsWith("powerline_") || name.StartsWith("invisible_") || name.StartsWith("ice_sheet") || name.StartsWith("iceberg"))
            {
                return Vector3.zero;
            }

            position.y = Mathf.Max(hit.point.y, Mathf.Max(terrainHeight, waterHeight));
            if (position.y != y && IsMonumentPosition(position))
            {
                return Vector3.zero;
            }

            if (IsZoneBlocked(position) || IsOtherEventPosition(position))
            {
                return Vector3.zero;
            }

            if (IsLayerBlocked(position, options.Event.Radius + 10f, obstructionLayer))
            {
                return Vector3.zero;
            }

            return position;
        }

        private float GetSpawnHeight(Vector3 target, float terrainHeight, float waterHeight)
        {
            float rayHeight = TerrainMeta.HighestPoint.y + 250f;
            if (Physics.Raycast(target.WithY(rayHeight), Vector3.down, out var hit, rayHeight + 1f, TARGET_MASK, QueryTriggerInteraction.Ignore))
            {
                string name = hit.collider.name;
                if (name.StartsWith("ice_sheet") || name.StartsWith("iceberg"))
                {
                    return -1f;
                }
                foreach (string prefix in _blockedColliders)
                {
                    if (name.StartsWith(prefix))
                    {
                        return Mathf.Max(terrainHeight, waterHeight);
                    }
                }
                terrainHeight = Mathf.Max(terrainHeight, hit.point.y);
            }

            return Mathf.Max(terrainHeight, waterHeight);
        }

        private bool IsLayerBlocked(Vector3 position, float radius, int mask)
        {
            using var entities = FindEntitiesOfType<BaseEntity>(position, radius, mask);
            foreach (BaseEntity entity in entities)
            {
                if (entity != null && !entity.IsDestroyed && !entity.IsNpc && !entity.limitNetworking && (entity.OwnerID.IsSteamId() || entity is BasePlayer))
                {
                    return true;
                }
            }
            return false;
        }

        private Vector3 GetRandomMonumentDropPosition(DifficultyLevel options, MonumentInfoEx monument, ref int remainingAttempts)
        {
            int attempts = Math.Min(99, remainingAttempts);
            while (attempts-- > 0)
            {
                remainingAttempts--;
                Vector3 position = monument.position + UnityEngine.Random.insideUnitSphere * 75f;
                if (IsPositionBlocked(position) || IsTooClose(position, 1f) || IsSafeZone(position))
                {
                    continue;
                }

                position.y = 100f;
                if (!Physics.Raycast(position, Vector3.down, out var hit, 100.5f, Layers.Solid, QueryTriggerInteraction.Ignore))
                {
                    continue;
                }

                position = hit.point;
                if (position.y - TerrainMeta.HeightMap.GetHeight(position) > 3f)
                {
                    continue;
                }

                if (IsZoneBlocked(position) || IsOtherEventPosition(position))
                {
                    continue;
                }

                if (IsLayerBlocked(position, options.Event.Radius + 10f, obstructionLayer))
                {
                    continue;
                }

                return position;
            }

            return Vector3.zero;
        }

        private bool IsMonumentPosition(Vector3 target, bool horizontalOnly = false)
        {
            foreach (var monument in monuments)
            {
                if (horizontalOnly ? InRange2D(monument.position, target, monument.radius) : monument.IsInBounds(target))
                {
                    return true;
                }
            }

            return false;
        }

        private Vector3 GetMonumentDropPosition(DifficultyLevel options)
        {
            using var allowedMonuments = Pool.Get<PooledList<MonumentInfoEx>>();
            allowedMonuments.AddRange(_allowedMonuments);
            int remainingAttempts = 500;

            while (allowedMonuments.Count > 0)
            {
                MonumentInfoEx monument = TakeRandom(allowedMonuments);
                Vector3 center = monument.position;

                if (IsPositionBlocked(center) || IsTooClose(center, 1f) || IsZoneBlocked(center) || IsSafeZone(center))
                {
                    continue;
                }

                if (IsLayerBlocked(center, options.Event.Radius + 10f, obstructionLayer))
                {
                    continue;
                }

                using var entities = FindEntitiesOfType<BaseEntity>(center, options.Event.Radius);
                int count = entities.Count;

                for (int i = count - 1; i >= 0; i--)
                {
                    BaseEntity entity = entities[i];
                    if (IsMonumentEntity(entity))
                    {
                        Vector3 position = entity.transform.position;
                        if ((config.Monuments.Underground || position.y >= center.y) && !IsPositionBlocked(position))
                        {
                            continue;
                        }
                    }

                    entities[i] = entities[--count];
                }

                if (count < entities.Count)
                {
                    entities.RemoveRange(count, entities.Count - count);
                }

                if (entities.Count >= 2)
                {
                    while (entities.Count > 0)
                    {
                        BaseEntity entity = TakeRandom(entities);
                        Vector3 position = entity.transform.position;

                        if (IsTooClose(position, 1f) || IsZoneBlocked(position) || IsSafeZone(position) || IsOtherEventPosition(position))
                        {
                            continue;
                        }

                        if (IsLayerBlocked(position, options.Event.Radius + 10f, obstructionLayer))
                        {
                            continue;
                        }

                        entity.Invoke(entity.SafelyKill, 0.1f);
                        return position;
                    }
                }

                Vector3 fallback = GetRandomMonumentDropPosition(options, monument, ref remainingAttempts);
                if (fallback != Vector3.zero)
                {
                    return fallback;
                }
            }

            return Vector3.zero;
        }

        private static bool IsMonumentEntity(BaseEntity entity)
        {
            if (entity.IsKilled() || entity.OwnerID != 0 || entity.skinID != 0 || entity.HasParent()) return false;
            if (entity is NPCPlayer) return true;
            if (entity is not LootContainer) return false;
            return entity.ShortPrefabName.Contains("loot-barrel") || entity.ShortPrefabName.Contains("loot_barrel") || entity.ShortPrefabName.StartsWith("crate_");
        }

        private static T TakeRandom<T>(List<T> list)
        {
            int index = UnityEngine.Random.Range(0, list.Count);
            int last = list.Count - 1;
            T value = list[index];
            list[index] = list[last];
            list.RemoveAt(last);
            return value;
        }

        private IEnumerator SetupPositions()
        {
            IsGridReady = false;
            _gridPositions.Clear();
            _gridPositionsSrc.Clear();
            int minPos = (int)(World.Size / -2f);
            int maxPos = (int)(World.Size / 2f);
            long budget = Stopwatch.Frequency / 1000;
            long deadline = Stopwatch.GetTimestamp() + budget;

            for (float x = minPos; x < maxPos; x += 25f)
            {
                for (float z = minPos; z < maxPos; z += 25f)
                {
                    if (Stopwatch.GetTimestamp() >= deadline)
                    {
                        yield return null;
                        deadline = Stopwatch.GetTimestamp() + budget;
                    }

                    Vector3 position = new(x, 0f, z);
                    if (IsPositionBlocked(position) || IsMonumentPosition(position, true))
                    {
                        continue;
                    }

                    float terrainHeight = TerrainMeta.HeightMap.GetHeight(position);
                    float waterHeight = TerrainMeta.WaterMap.GetHeight(position);
                    if (waterHeight - terrainHeight > 0.1f)
                    {
                        continue;
                    }

                    position.y = GetSpawnHeight(position, terrainHeight, waterHeight);
                    if (position.y >= 0f && !IsMonumentPosition(position))
                    {
                        _gridPositionsSrc.Add(position);
                    }
                }
            }

            _gridPositions.AddRange(_gridPositionsSrc);
            IsGridReady = true;
            _gridCo = null;
        }

        private void EnsureGridPositions()
        {
            if (!IsGridReady)
            {
                if (IsMonumentsReady && _gridCo == null)
                {
                    _gridCo = ServerMgr.Instance.StartCoroutine(SetupPositions());
                }
                return;
            }

            if (_gridPositions.Count == 0)
            {
                _gridPositions.AddRange(_gridPositionsSrc);
            }
        }

        private bool IsPositionBlocked(Vector3 pos)
        {
            foreach (var blocked in config.Settings.BlockedPositions)
            {
                if (InRange2D(pos, blocked.position, blocked.radius))
                {
                    return true;
                }
            }
            if (config.Settings.BlockedGrids.Count == 0)
            {
                return false;
            }
            string grid = MapHelper.PositionToString(pos);
            foreach (string blocked in config.Settings.BlockedGrids)
            {
                if (grid.Equals(blocked, StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            return false;
        }

        public Vector3 RandomDropPosition()
        {
            EnsureGridPositions();
            return _gridPositions.Count > 0 ? TakeRandom(_gridPositions) : Vector3.zero;
        }

        TreasureChest TryOpenEvent(DifficultyLevel options, BasePlayer player = null)
        {
            var eventPos = Vector3.zero;

            if (!player.IsKilled())
            {
                if (!Physics.Raycast(player.eyes.HeadRay(), out var hit, Mathf.Infinity, -1, QueryTriggerInteraction.Ignore))
                {
                    return null;
                }

                eventPos = hit.point;
            }
            else
            {
                var randomPos = GetEventPosition(options);

                if (randomPos == Vector3.zero)
                {
                    return null;
                }

                eventPos = randomPos;
            }

            var container = GameManager.server.CreateEntity(StringPool.Get(2206646561), eventPos) as StorageContainer;

            if (container == null)
            {
                return null;
            }

            container.dropsLoot = false;
            container.enableSaving = false;
            container.Spawn();

            using (var update = container.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
            {
                update.Set(BaseEntity.Flags.Locked, true);
                update.Set(BaseEntity.Flags.OnFire, true);
            }

            var chest = container.gameObject.AddComponent<TreasureChest>();
            chest.SharedRockHits = sharedRockHits;
            chest.SharedRockColliders = sharedRockColliders;
            chest.go = chest.gameObject;
            chest.HumanoidBrains = HumanoidBrains;
            chest.Options = options;
            chest.Instance = this;
            chest.config = config;
            chest.Radius = chest.Options.Event.Radius;

            var chestLoot = new List<LootItem>();
            var lootList = ChestLoot(chest.Options);
            if (lootList != null) chestLoot.AddRange(lootList);
            if (config.BlockPaidContent)
            {
                chestLoot.RemoveAll(ti => RequiresOwnership(ti.definition, ti.skin));
            }
            chest.SpawnLoot(container, chestLoot);

            if (config.Skins.PresetSkin != 0uL)
            {
                container.skinID = config.Skins.PresetSkin;
            }
            else if (config.Skins.Custom.Count > 0)
            {
                container.skinID = config.Skins.Custom.GetRandom();
                container.SendNetworkUpdate();
            }
            else if (config.Skins.RandomSkins)
            {
                var skin = chest.GetItemSkin(ItemManager.FindItemDefinition("box.wooden.large"), 0, false);

                container.skinID = skin;
                container.SendNetworkUpdate();
            }

            var uid = container.net.ID;
            float unlockTime = UnityEngine.Random.Range(config.Unlock.MinTime, config.Unlock.MaxTime);

            SubscribeHooks(true);
            treasureChests[uid] = chest;

            var posStr = FormatGridReference(container.transform.position, config.Settings.ShowGrid);
            Puts("{0}: {1}", FormatGridReference(container.transform.position, true), string.Join(", ", container.inventory.itemList.Select(item => string.Format("{0} ({1})", item.info.displayName.translated, item.amount))));

            //if (!_config.Event.SpawnMax && treasureChests.Count > 1)
            //{
            //    AnnounceEventSpawn(container, unlockTime, posStr);
            //}

            foreach (var target in BasePlayer.activePlayerList)
            {
                double distance = Math.Round(target.transform.position.Distance(container.transform.position), 2);
                string unlockStr = FormatTime(options.Event.PlayerLimit, unlockTime, target.UserIDString);

                if (config.EventMessages.Opened)
                {
                    Message(target, "Opened", posStr, unlockStr, distance, config.Settings.DistanceChatCommand);
                }

                if (config.GUIAnnouncement.Enabled && GUIAnnouncements.CanCall() && distance <= config.GUIAnnouncement.Distance)
                {
                    string message = msg("Opened", target.UserIDString, posStr, unlockStr, distance, config.Settings.DistanceChatCommand);
                    GUIAnnouncements?.Call("CreateAnnouncement", message, config.GUIAnnouncement.TintColor, config.GUIAnnouncement.TextColor, target);
                }

                if (config.Rocket.Enabled && config.EventMessages.Barrage)
                {
                    Message(target, "Barrage", config.Rocket.Amount);
                }

                if (chest.Options.Event.DrawTreasureIfNearby && chest.Options.Event.AutoDrawDistance > 0f && distance <= chest.Options.Event.AutoDrawDistance)
                {
                    DrawText(target, container.transform.position, msg("Treasure Chest", target.UserIDString, distance), options.Event.DrawTime, options.Event.GrantDraw);
                }
            }

            var position = container.transform.position;
            data.TotalEvents++;
            SaveData();

            bool canSpawnNpcs = true;

            if (sd_customPos == Vector3.zero)
            {
                foreach (var x in monuments)
                {
                    if (x.IsInBounds(position))
                    {
                        foreach (var (monument, value) in config.Monuments.NPCBlacklist)
                        {
                            if (value && x.name.Trim() == monument.Trim())
                            {
                                canSpawnNpcs = false;
                                break;
                            }
                        }
                        break;
                    }
                }
            }

            if (options.NPC.Enabled && !Rust.Ai.AiManager.nav_disable && canSpawnNpcs) chest.Invoke(chest.SpawnNpcs, 1f);
            chest.Invoke(() => chest.SetUnlockTime(unlockTime), 2f);

            return chest;
        }

        private void AnnounceEventSpawn()
        {
            foreach (var target in BasePlayer.activePlayerList)
            {
                if (config.EventMessages.Opened) Player.Message(target, msg("OpenedX", target.UserIDString, config.Settings.DistanceChatCommand));
                foreach (var chest in treasureChests.Values)
                {
                    var options = chest.Options;
                    bool announce = config.GUIAnnouncement.Enabled && GUIAnnouncements.CanCall();
                    if (!announce && !options.Event.DrawTreasureIfNearby) continue;
                    double distance = Math.Round(target.transform.position.Distance(chest.containerPos), 2);
                    if (announce && distance <= config.GUIAnnouncement.Distance)
                    {
                        string unlockStr = FormatTime(options.Event.PlayerLimit, chest.countdownTime, target.UserIDString);
                        string posStr = FormatGridReference(chest.containerPos, config.Settings.ShowGrid);
                        string text = msg2("Opened", target.UserIDString, posStr, unlockStr, distance, config.Settings.DistanceChatCommand);
                        GUIAnnouncements.Call("CreateAnnouncement", text, config.GUIAnnouncement.TintColor, config.GUIAnnouncement.TextColor, target);
                    }
                    if (options.Event.DrawTreasureIfNearby && options.Event.AutoDrawDistance > 0f && distance <= options.Event.AutoDrawDistance)
                    {
                        DrawText(target, chest.containerPos, msg2("Treasure Chest", target.UserIDString, distance), options.Event.DrawTime, options.Event.GrantDraw);
                    }
                }
                if (config.Rocket.Enabled && config.EventMessages.Barrage) Message(target, "Barrage", config.Rocket.Amount);
            }
        }

        void API_SetContainer(StorageContainer container, float radius, bool spawnNpcs, int level = 0) // Expansion Mode for Raidable Bases plugin
        {
            if (!container.IsValid())
            {
                return;
            }

            var options = config.GetLevelOrHighest(level);
            if (options == null)
            {
                return;
            }

            using (var update = container.StartSetFlags(BaseEntity.FlagsUpdateMode.SendNetworkUpdate))
            {
                update.Set(BaseEntity.Flags.Locked, true);
                update.Set(BaseEntity.Flags.OnFire, true);
            }

            var chest = container.gameObject.AddComponent<TreasureChest>();
            chest.SharedRockHits = sharedRockHits;
            chest.SharedRockColliders = sharedRockColliders;
            chest.go = chest.gameObject;
            chest.HumanoidBrains = HumanoidBrains;
            chest.Options = options;
            chest.markerCreated = true;
            chest.Instance = this;
            chest.config = config;
            float unlockTime = UnityEngine.Random.Range(config.Unlock.MinTime, config.Unlock.MaxTime);

            chest.Radius = radius;
            treasureChests[container.net.ID] = chest;
            chest.Invoke(() => chest.SetUnlockTime(unlockTime), 2f);
            data.TotalEvents++;
            SaveData();

            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(OnItemRemovedFromContainer));
            Subscribe(nameof(CanLootEntity));

            if (spawnNpcs)
            {
                Subscribe(nameof(OnNpcTarget));
                Subscribe(nameof(OnNpcResume));
                Subscribe(nameof(OnNpcDestinationSet));
                Subscribe(nameof(OnEntityEnter));
                Subscribe(nameof(CanBradleyApcTarget));
                chest.Invoke(() => chest.SpawnNpcs(true), 1f);
            }
            else if (config.NewmanMode.Harm)
            {
                Subscribe(nameof(OnEntityEnter));
            }
        }

        int GetPlayerCount()
        {
            string name = config.Settings.PlayerLimitPermission;
            if (string.IsNullOrWhiteSpace(name)) return BasePlayer.activePlayerList.Count;
            return BasePlayer.activePlayerList.Count(x => name.Contains('.') ? !permission.UserHasPermission(x.UserIDString, name) : !permission.UserHasGroup(x.UserIDString, name));
        }

        private int GetEventCount(int level)
        {
            int count = 0;
            foreach (var chest in treasureChests.Values)
            {
                if (chest != null && !chest.killed && chest.Options.Level == level) count++;
            }
            return count;
        }

        private void ScheduleNextEvent(DifficultyLevel options, double stamp)
        {
            float interval = UnityEngine.Random.Range(options.Event.IntervalMin, options.Event.IntervalMax);
            data.SecondsUntilEvent[options.Level] = stamp + interval;
            eventRetries.Remove(options.Level);
            Puts(_("Next Automated Event", null, FormatTime(options.Event.PlayerLimit, interval), DateTime.Now.AddSeconds(interval).ToString()));
            SaveData();
        }

        private void CheckSecondsUntilEvent()
        {
            double stamp = Facepunch.Math.Epoch.Current;
            double now = Time.realtimeSinceStartupAsDouble;
            int playerCount = -1;
            foreach (var options in config.Levels)
            {
                if (!options.Event.Automated) continue;
                if (!data.SecondsUntilEvent.TryGetValue(options.Level, out double next) || next == double.MinValue)
                {
                    ScheduleNextEvent(options, stamp);
                    continue;
                }
                if (next > stamp || eventRetries.TryGetValue(options.Level, out double retry) && retry > now) continue;
                if (GetEventCount(options.Level) >= options.Event.Max) continue;
                if (playerCount < 0) playerCount = GetPlayerCount();
                if (playerCount < options.Event.PlayerLimit) continue;
                var chest = TryOpenEvent(options);
                int count = GetEventCount(options.Level);
                if (chest != null && (!options.Event.SpawnMax || count >= options.Event.Max))
                {
                    if (options.Event.SpawnMax && count > 1) AnnounceEventSpawn();
                    ScheduleNextEvent(options, stamp);
                }
                else eventRetries[options.Level] = now + Mathf.Max(1f, options.Event.Stagger);
            }
            timer.Once(1f, CheckSecondsUntilEvent);
        }

        public string FormatGridReference(Vector3 position, bool showGrid)
        {
            string monumentName = null;
            float distance = 10000f;

            foreach (var x in monuments) // request MrSmallZzy
            {
                float magnitude = x.position.Distance(position);

                if (magnitude <= x.radius && magnitude < distance)
                {
                    monumentName = x.name;
                    distance = magnitude;
                }
            }

            if (config.Settings.ShowXZ)
            {
                return string.IsNullOrEmpty(monumentName) ? $"{position.x:N2} {position.z:N2}" : $"{monumentName} ({position.x:N2} {position.z:N2})";
            }

            if (showGrid)
            {
                return string.IsNullOrEmpty(monumentName) ? MapHelper.PositionToString(position) : $"{monumentName} ({MapHelper.PositionToString(position)})";
            }

            return string.IsNullOrEmpty(monumentName) ? string.Empty : monumentName;
        }

        private string FormatTime(int limit, double seconds, string id = null)
        {
            if (seconds == 0)
            {
                return GetPlayerCount() < limit ? msg2("Not Enough Online", id, limit) : "0s";
            }

            var ts = TimeSpan.FromSeconds(seconds);

            return string.Format("{0:D2}h {1:D2}m {2:D2}s", ts.Hours, ts.Minutes, ts.Seconds);
        }

        bool AssignTreasureHunters(List<KeyValuePair<string, int>> ladder)
        {
            foreach (var target in covalence.Players.All)
            {
                if (target == null || string.IsNullOrEmpty(target.Id))
                {
                    continue;
                }

                if (target.HasPermission(config.RankedLadder.Permission))
                {
                    permission.RevokeUserPermission(target.Id, config.RankedLadder.Permission);
                }

                if (permission.UserHasGroup(target.Id, config.RankedLadder.Group))
                {
                    permission.RemoveUserGroup(target.Id, config.RankedLadder.Group);
                }
            }

            if (!config.RankedLadder.Enabled)
            {
                return true;
            }

            ladder.Sort((x, y) => y.Value.CompareTo(x.Value));

            foreach (var kvp in ladder.Take(config.RankedLadder.Amount))
            {
                var userid = kvp.Key;

                if (permission.UserHasPermission(userid, "dangeroustreasures.notitle"))
                {
                    continue;
                }

                var target = covalence.Players.FindPlayerById(userid);

                if (target != null && target.IsBanned)
                {
                    continue;
                }

                permission.GrantUserPermission(userid, config.RankedLadder.Permission, this);
                permission.AddUserGroup(userid, config.RankedLadder.Group);

                LogToFile("treasurehunters", DateTime.Now.ToString() + " : " + msg("Log Stolen", null, target?.Name ?? userid, userid, kvp.Value), this, true);
                Puts(_("Log Granted", null, target?.Name ?? userid, userid, config.RankedLadder.Permission, config.RankedLadder.Group));
            }

            string file = string.Format("{0}{1}{2}_{3}-{4}.txt", Interface.Oxide.LogDirectory, System.IO.Path.DirectorySeparatorChar, Name, "treasurehunters", DateTime.Now.ToString("yyyy-MM-dd"));
            Puts(_("Log Saved", null, file));

            return true;
        }

        private bool grantDrawError;
        void DrawText(BasePlayer player, Vector3 drawPos, string text, float drawTime, bool grantDraw)
        {
            if (grantDrawError || player == null || !player.IsConnected || drawPos == Vector3.zero || string.IsNullOrEmpty(text) || drawTime < 1f)
                return;

            bool isAdmin = player.IsAdmin;

            try
            {
                if (grantDraw && !player.IsAdmin)
                {
                    var uid = player.userID;

                    if (!drawGrants.Contains(uid))
                    {
                        drawGrants.Add(uid);
                        timer.Once(drawTime, () => drawGrants.Remove(uid));
                    }

                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                if (player.IsAdmin || drawGrants.Contains(player.userID))
                    player.SendConsoleCommand("ddraw.text", drawTime, Color.yellow, drawPos, text);
            }
            catch (Exception ex)
            {
                grantDrawError = true;
                Puts("DrawText Exception: {0}", ex);
                Puts("Disabled drawing for players!");
            }

            if (!isAdmin)
            {
                if (player.HasPlayerFlag(BasePlayer.PlayerFlags.IsAdmin))
                {
                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, false);
                    player.SendNetworkUpdateImmediate();
                }
            }
        }

        void AddItem(BasePlayer player, string[] args)
        {
            if (args.Length >= 2)
            {
                string shortname = args[0];
                var itemDef = ItemManager.FindItemDefinition(shortname);

                if (itemDef == null)
                {
                    Message(player, "InvalidItem", shortname, config.Settings.DistanceChatCommand);
                    return;
                }

                if (int.TryParse(args[1], out var amount))
                {
                    if (itemDef.stackable == 1 || (itemDef.condition.enabled && itemDef.condition.max > 0f) || amount < 1)
                        amount = 1;

                    ulong skin = 0uL;

                    if (args.Length >= 3)
                    {
                        if (ulong.TryParse(args[2], out var num)) skin = num;
                        else Message(player, "InvalidValue", args[2]);
                    }

                    int minAmount = amount;
                    if (args.Length >= 4)
                    {
                        if (int.TryParse(args[3], out var num))
                            minAmount = num;
                        else
                            Message(player, "InvalidValue", args[3]);
                    }

                    int level = 0;
                    if (args.Length >= 5)
                    {
                        if (int.TryParse(args[4], out var num2))
                        {
                            foreach (var options in config.Levels)
                            {
                                if (options.Level == num2)
                                {
                                    level = num2;
                                    break;
                                }
                            }
                        }
                        else
                            Message(player, "InvalidValue", args[3]);
                    }

                    var lootList = ChestLoot(level);
                    if (lootList == null)
                    {
                        Message(player, "InvalidValue", level);
                        return;
                    }

                    foreach (var loot in lootList)
                    {
                        if (loot.shortname == shortname)
                        {
                            loot.amount = amount;
                            loot.skin = skin;
                            loot.amountMin = minAmount;
                        }
                    }

                    SaveConfig();
                    Message(player, "AddedItem", shortname, amount, skin);
                }
                else
                    Message(player, "InvalidValue", args[2]);

                return;
            }

            Message(player, "InvalidItem", args.Length >= 1 ? args[0] : "?", config.Settings.DistanceChatCommand);
        }

        void cmdTreasureHunter(BasePlayer player, string command, string[] args)
        {
            if (drawGrants.Contains(player.userID))
                return;
            if (args.Contains("spm") && player.IsAdmin)
            {
                foreach (var mi in monuments)
                {
                    player.SendConsoleCommand("ddraw.sphere", 30f, Color.red, mi.position, mi.radius);
                    player.SendConsoleCommand("ddraw.text", 30f, Color.blue, mi.position, $"<size=22>{mi.name}</size>");
                }
                return;
            }
            if (!GetLevel(args, out var options))
            {
                return;
            }
            if (config.RankedLadder.Enabled)
            {
                if (args.Length >= 1 && (args[0].ToLower() == "ladder" || args[0].ToLower() == "lifetime"))
                {
                    if (data.Players.Count == 0)
                    {
                        Message(player, "Ladder Insufficient Players");
                        return;
                    }

                    if (args.Length == 2 && args[1] == "resetme")
                        if (data.Players.ContainsKey(player.UserIDString))
                            data.Players[player.UserIDString].StolenChestsSeed = 0;

                    int rank = 0;
                    var sb = new StringBuilder();
                    var ladder = data.Players.ToDictionary(k => k.Key, v => args[0].ToLower() == "ladder" ? v.Value.StolenChestsSeed : v.Value.StolenChestsTotal).Where(kvp => kvp.Value > 0).ToList();
                    ladder.Sort((x, y) => y.Value.CompareTo(x.Value));

                    var ranked = msg2(args[0].ToLower() == "ladder" ? "Ladder" : "Ladder Total", player.UserIDString);

                    if (!string.IsNullOrEmpty(ranked))
                    {
                        sb.AppendLine(ranked);
                    }

                    foreach (var kvp in ladder.Take(10))
                    {
                        string name = covalence.Players.FindPlayerById(kvp.Key)?.Name ?? kvp.Key;
                        string value = kvp.Value.ToString("N0");

                        sb.AppendLine(msg2("TreasureHunter", player.UserIDString, ++rank, name, value));
                    }

                    Message(player, sb.ToString());
                    return;
                }

                Message(player, "Wins", data.Players.ContainsKey(player.UserIDString) ? data.Players[player.UserIDString].StolenChestsSeed : 0, config.Settings.DistanceChatCommand);
            }

            if (args.Length >= 1 && player.IsAdmin)
            {
                if (args[0] == "wipe")
                {
                    Message(player, "Log Saved", "treasurehunters");
                    wipeChestsSeed = true;
                    TryWipeData();
                    return;
                }
                else if (args[0] == "resettime")
                {
                    data.SecondsUntilEvent[options.Level] = double.MinValue;
                    eventRetries.Remove(options.Level);
                    return;
                }
                else if (args[0] == "now")
                {
                    data.SecondsUntilEvent[options.Level] = Facepunch.Math.Epoch.Current;
                    eventRetries.Remove(options.Level);
                    return;
                }
                else if (args[0] == "tp" && treasureChests.Count > 0)
                {
                    int i = 0, k = 0;
                    if (args.Length == 2 && int.TryParse(args[1], out int idx))
                    {
                        idx = Math.Clamp(idx, 0, treasureChests.Count - 1);
                        foreach (var entry in treasureChests)
                        {
                            if (k++ == idx)
                            {
                                player.Teleport(entry.Value.containerPos);
                                return;
                            }
                        }
                    }

                    Vector3 pos = player.transform.position;
                    int currIdx = -1;
                    foreach (var entry in treasureChests)
                    {
                        if ((entry.Value.containerPos - pos).sqrMagnitude <= entry.Value.SqrRadius)
                        {
                            currIdx = i;
                            break;
                        }
                        i++;
                    }

                    int nextIdx = currIdx < 0 ? 0 : (currIdx + 1) % treasureChests.Count;
                    foreach (var entry in treasureChests)
                    {
                        if (k++ == nextIdx)
                        {
                            player.Teleport(entry.Value.containerPos);
                            break;
                        }
                    }
                }
                else if (args[0].Equals("additem", StringComparison.OrdinalIgnoreCase))
                {
                    AddItem(player, args.Skip(1));
                    return;
                }
                else if (args[0].Equals("showdebuggrid", StringComparison.OrdinalIgnoreCase))
                {
                    EnsureGridPositions();
                    foreach (Vector3 position in _gridPositionsSrc)
                    {
                        if (player.Distance(position) > 1000f) continue;
                        player.SendConsoleCommand("ddraw.text", 30f, Color.green, position, "X");
                    }
                    return;
                }
                else if (args[0].Equals("testblocked", StringComparison.OrdinalIgnoreCase))
                {
                    Player.Message(player, $"IsLayerBlocked: {IsLayerBlocked(player.transform.position, 25f, obstructionLayer)}");
                    Player.Message(player, $"SafeZone: {IsSafeZone(player.transform.position)}");

                    var entities = new List<BaseNetworkable>();

                    foreach (var e in BaseNetworkable.serverEntities.OfType<BaseEntity>())
                    {
                        if (!entities.Contains(e) && InRange2D(e.transform.position, player.transform.position, options.Event.Radius))
                        {
                            if (e.IsNpc || e is LootContainer)
                            {
                                entities.Add(e);
                                player.SendConsoleCommand("ddraw.text", 30f, Color.green, e.transform.position, e.ShortPrefabName);
                            }
                        }
                    }

                    return;
                }
            }

            if (treasureChests.Count == 0)
            {
                double time = Math.Max(0, data.SecondsUntilEvent.GetValueOrDefault(options.Level) - Facepunch.Math.Epoch.Current);
                Message(player, "Next", FormatTime(options.Event.PlayerLimit, time, player.UserIDString));
                return;
            }

            foreach (var chest in treasureChests.Values)
            {
                double distance = Math.Round(player.transform.position.Distance(chest.containerPos), 2);
                string posStr = FormatGridReference(chest.containerPos, config.Settings.ShowGrid);

                if (chest.GetUnlockTime() != null)
                {
                    Message(player, "Info", chest.GetUnlockTime(player.UserIDString), posStr, distance, config.Settings.DistanceChatCommand);
                }
                else Message(player, "Already", posStr, distance, config.Settings.DistanceChatCommand);

                if (config.Settings.AllowDrawText)
                {
                    DrawText(player, chest.containerPos, msg2("Treasure Chest", player.UserIDString, distance), chest.Options.Event.DrawTime, chest.Options.Event.GrantDraw);
                }
            }
        }

        private bool GetLevel(string[] args, out DifficultyLevel options)
        {
            options = config.GetLevel(0);
            foreach (var arg in args)
            {
                if (int.TryParse(arg, out var level))
                {
                    var x = config.GetLevel(level);
                    if (x != null)
                    {
                        options = x;
                        break;
                    }
                }
            }
            return options != null;
        }

        private string ParseEventArguments(string[] args, out DifficultyLevel options, out int amount, out string command, out ulong targetId)
        {
            options = config.GetLevel(0) ?? (config.Levels.Count > 0 ? config.Levels[0] : null);
            amount = 1;
            command = null;
            targetId = 0;

            bool difficultySpecified = false;
            bool amountSpecified = false;

            for (int i = 0; i < args.Length; i++)
            {
                string arg = args[i];

                if (i == args.Length - 1 && arg == "True")
                {
                    continue;
                }

                if (ulong.TryParse(arg, out ulong steamId) && steamId.IsSteamId())
                {
                    if (targetId != 0 || command is not (null or "tp"))
                    {
                        return "Event Command Usage";
                    }

                    targetId = steamId;
                    command = "tp";
                    continue;
                }

                if (int.TryParse(arg, out int value))
                {
                    if (!difficultySpecified)
                    {
                        options = config.GetLevel(value);
                        if (options == null)
                        {
                            return "Event Difficulty Invalid";
                        }

                        difficultySpecified = true;
                    }
                    else if (!amountSpecified && value > 0)
                    {
                        amount = value;
                        amountSpecified = true;
                    }
                    else
                    {
                        return "Event Command Usage";
                    }

                    continue;
                }

                if (command != null && !arg.Equals(command, StringComparison.OrdinalIgnoreCase))
                {
                    return "Event Command Usage";
                }

                command = arg.ToLower();

                if (command is not ("tp" or "me" or "help" or "custom" or "5sec"))
                {
                    return "Event Command Usage";
                }
            }

            return options == null ? "Event Difficulty Invalid" : null;
        }

        private void ccmdDangerousTreasures(ConsoleSystem.Arg arg)
        {
            BasePlayer player = arg.Player();
            bool isAdmin = arg.IsAdmin || player != null && player.IsAdmin;
            if (!isAdmin && (player == null || !permission.UserHasPermission(player.UserIDString, config.Settings.PermName)))
            {
                Message(arg, "No Permission");
                return;
            }
            string[] args = arg.HasArgs() ? arg.Args.ToStringArray() : Array.Empty<string>();
            string error = ParseEventArguments(args, out var options, out int amount, out string command, out ulong targetId);
            if (error != null)
            {
                Message(arg, error, config.Settings.EventConsoleCommand);
                return;
            }
            if (command == "help")
            {
                Message(arg, "Event Command Usage", config.Settings.EventConsoleCommand);
                Message(arg, "Event Levels", string.Join(", ", config.Levels.Select(x => $"{x.Level}: {x.Difficulty}")));
                if (player == null) { Puts("Monuments:"); foreach (var monument in monuments) Puts(monument.name); }
                return;
            }
            if (command != null && !isAdmin)
            {
                Message(arg, "No Permission");
                return;
            }
            if (command == "custom")
            {
                Message(arg, "Event Command Usage", config.Settings.EventConsoleCommand);
                return;
            }
            if (command == "5sec")
            {
                data.SecondsUntilEvent[options.Level] = (double)Facepunch.Math.Epoch.Current + 5d;
                eventRetries.Remove(options.Level);
                return;
            }
            BasePlayer target = targetId == 0 ? player : BasePlayer.FindByID(targetId);
            if ((command is "tp" or "me") && (target.IsKilled() || !target.IsConnected))
            {
                Message(arg, targetId == 0 ? "Event Player Required" : "Event Player Not Found");
                return;
            }
            Vector3 position = Vector3.zero;
            int opened = 0;
            for (int i = 0; i < amount; i++)
            {
                if (!isAdmin && GetEventCount(options.Level) >= options.Event.Max)
                {
                    Message(arg, "Max Manual Events", options.Event.Max);
                    if (opened == 0) return;
                    break;
                }
                var chest = TryOpenEvent(options, command == "me" ? target : null);
                if (chest == null) break;
                position = chest.containerPos;
                opened++;
            }
            if (opened == 0) Message(arg, "Manual Event Failed");
            else if (command == "tp" && !target.IsKilled() && target.IsConnected)
            {
                if (target.IsFlying) target.Teleport(position.WithY(Mathf.Max(position.y, target.transform.position.y)));
                else target.Teleport(position + new Vector3(0f, 0.2f, 0f));
            }
            if (opened > 0 && amount > 1) Message(arg, "OpenedEvents", opened, amount);
        }

        void cmdDangerousTreasures(BasePlayer player, string command, string[] args)
        {
            if (!player.IsAdmin && !permission.UserHasPermission(player.UserIDString, config.Settings.PermName))
            {
                Message(player, "No Permission");
                return;
            }
            string error = ParseEventArguments(args, out var options, out int amount, out string cmd, out ulong targetId);
            if (error != null)
            {
                Message(player, error, "/" + config.Settings.EventChatCommand);
                return;
            }
            if (cmd == "help")
            {
                Message(player, "Monuments: " + string.Join(", ", monuments.Select(m => m.name)));
                Message(player, "Event Command Usage", "/" + config.Settings.EventChatCommand);
                Message(player, "Event Levels", string.Join(", ", config.Levels.Select(x => $"{x.Level}: {x.Difficulty}")));
                return;
            }
            if (cmd != null && !player.IsAdmin)
            {
                Message(player, "No Permission");
                return;
            }
            if (cmd == "custom")
            {
                if (string.IsNullOrEmpty(data.CustomPosition))
                {
                    data.CustomPosition = player.transform.position.ToString();
                    sd_customPos = player.transform.position;
                    Message(player, "CustomPositionSet", data.CustomPosition);
                }
                else
                {
                    data.CustomPosition = string.Empty;
                    sd_customPos = Vector3.zero;
                    Message(player, "CustomPositionRemoved");
                }
                SaveData();
                return;
            }
            if (cmd == "5sec")
            {
                data.SecondsUntilEvent[options.Level] = (double)Facepunch.Math.Epoch.Current + 5d;
                eventRetries.Remove(options.Level);
                return;
            }
            BasePlayer target = targetId == 0 ? player : BasePlayer.FindByID(targetId);
            if ((cmd is "tp" or "me") && (target.IsKilled() || !target.IsConnected))
            {
                Message(player, "Event Player Not Found");
                return;
            }
            Vector3 position = Vector3.zero;
            int opened = 0;
            for (int i = 0; i < amount; i++)
            {
                if (!player.IsAdmin && GetEventCount(options.Level) >= options.Event.Max)
                {
                    Message(player, "Max Manual Events", options.Event.Max);
                    if (opened == 0) return;
                    break;
                }
                var chest = TryOpenEvent(options, cmd == "me" ? player : null);
                if (chest == null) break;
                position = chest.containerPos;
                opened++;
            }
            if (opened == 0) Message(player, "Manual Event Failed");
            else if (cmd == "tp" && !target.IsKilled() && target.IsConnected)
            {
                if (target.IsFlying) target.Teleport(position.WithY(Mathf.Max(position.y, target.transform.position.y)));
                else target.Teleport(position);
            }
            if (opened > 0 && amount > 1) Message(player, "OpenedEvents", opened, amount);
        }

        #region Facepunch TOS Compliance

        private readonly HashSet<int> _dlcItemIds = new();
        private readonly HashSet<ulong> _ownershipIds = new();
        private bool _ownershipReady;

        private void LoadOwnership()
        {
            if (!config.BlockPaidContent)
            {
                _ownershipReady = true;
                return;
            }

            if ((Steamworks.SteamInventory.Definitions?.Length ?? 0) == 0)
            {
                timer.In(3f, LoadOwnership);
                return;
            }

            foreach (var def in ItemManager.GetItemDefinitions())
            {
                if (RequiresOwnership(def))
                {
                    _dlcItemIds.Add(def.itemid);
                }

                if (def.skins != null)
                {
                    foreach (var sk in def.skins)
                    {
                        if (sk.id != 0) _ownershipIds.Add((ulong)sk.id);
                    }
                }

                if (def.skins2 != null)
                {
                    foreach (var sk2 in def.skins2)
                    {
                        if (sk2.WorkshopId != 0) _ownershipIds.Add(sk2.WorkshopId);
                    }
                }
            }

            _ownershipReady = true;
        }

        public bool RequiresOwnership(ItemDefinition def, ulong skin)
        {
            if (!config.BlockPaidContent) return false;
            if (skin != 0uL && !_ownershipReady) return true;
            if (skin != 0uL && _ownershipIds.Contains(skin)) return true;
            if (def != null && !_ownershipReady) return RequiresOwnership(def);
            return def != null && _dlcItemIds.Contains(def.itemid);
        }

        public bool RequiresOwnership(ItemDefinition def) => def switch
        {
            null => false,
            { steamItem: { id: not 0 } } => true,
            { steamDlc: { dlcAppID: not 0 } } => true,
            { Blueprint: { NeedsSteamDLC: true } } => true,
            { Parent: { Blueprint: { NeedsSteamDLC: true } } } => true,
            { isRedirectOf: { Blueprint: { NeedsSteamDLC: true } } } => true,
            { isRedirectOf: not null } => true,
            _ => false
        };

        public bool RemoveRequiresOwnership(ItemDefinition def, List<ulong> skins)
        {
            if (!config.BlockPaidContent) return true;
            return skins.RemoveAll(skin => RequiresOwnership(def, skin)) != 0;
        }

        #endregion Facepunch TOS Compliance

        #region Config

        Dictionary<string, string> GetMessages()
        {
            return new()
            {
                {"No Permission", "You do not have permission to use this command."},
                {"Building is blocked!", "<color=#FF0000>Building is blocked near treasure chests!</color>"},
                {"Max Manual Events", "Maximum number of manual events <color=#FF0000>{0}</color> has been reached!"},
                {"Dangerous Zone Protected", "<color=#FF0000>You have entered a dangerous zone protected by a fire aura! You must leave before you die!</color>"},
                {"Dangerous Zone Unprotected", "<color=#FF0000>You have entered a dangerous zone!</color>"},
                {"Manual Event Failed", "Event failed to start! Unable to obtain a valid position. Please try again."},
                {"Help", "{0} [difficulty] [amount] [tp|me|SteamID]"},
                {"Event Command Usage", "Use: {0} [difficulty] [amount] [tp|me|SteamID]"},
                {"Event Levels", "Difficulties: {0}"},
                {"Event Difficulty Invalid", "That difficulty is not configured. Use {0} help to list difficulties."},
                {"Event Player Required", "Invalid target, no steamid was specified."},
                {"Event Player Not Found", "Invalid target, that player is not connected."},
                {"Started", "<color=#C0C0C0>The event has started at <color=#FFFF00>{0}</color>! The protective fire aura has been obliterated!</color>"},
                {"StartedNpcs", "<color=#C0C0C0>The event has started at <color=#FFFF00>{0}</color>! The protective fire aura has been obliterated! Npcs must be killed before the treasure will become lootable.</color>"},
                {"Opened", "<color=#C0C0C0>An event has opened at <color=#FFFF00>{0}</color>! Event will start in <color=#FFFF00>{1}</color>. You are <color=#FFA500>{2}m</color> away. Use <color=#FFA500>/{3}</color> for help.</color>"},
                {"OpenedX", "<color=#C0C0C0><color=#FFFF00>Multiple events have opened! Use <color=#FFA500>/{0}</color> for help.</color>"},
                {"Barrage", "<color=#C0C0C0>A barrage of <color=#FFFF00>{0}</color> rockets can be heard at the location of the event!</color>"},
                {"Info", "<color=#C0C0C0>Event will start in <color=#FFFF00>{0}</color> at <color=#FFFF00>{1}</color>. You are <color=#FFA500>{2}m</color> away.</color>"},
                {"Already", "<color=#C0C0C0>The event has already started at <color=#FFFF00>{0}</color>! You are <color=#FFA500>{1}m</color> away.</color>"},
                {"Next", "<color=#C0C0C0>No events are open. Next event in <color=#FFFF00>{0}</color></color>"},
                {"Thief", "<color=#C0C0C0>The treasures at <color=#FFFF00>{0}</color> have been stolen by <color=#FFFF00>{1}</color>!</color>"},
                {"Wins", "<color=#C0C0C0>You have stolen <color=#FFFF00>{0}</color> treasure chests! View the ladder using <color=#FFA500>/{1} ladder</color> or <color=#FFA500>/{1} lifetime</color></color>"},
                {"Ladder", "<color=#FFFF00>[ Top 10 Treasure Hunters (This Wipe) ]</color>:"},
                {"Ladder Total", "<color=#FFFF00>[ Top 10 Treasure Hunters (Lifetime) ]</color>:"},
                {"Ladder Insufficient Players", "<color=#FFFF00>No players are on the ladder yet!</color>"},
                {"Event At", "Event at {0}"},
                {"Next Automated Event", "Next automated event in {0} at {1}"},
                {"Not Enough Online", "Not enough players online ({0} minimum)"},
                {"Treasure Chest", "Treasure Chest <color=#FFA500>{0}m</color>"},
                {"Invalid Constant", "Invalid constant {0} - please notify the author!"},
                {"Destroyed Treasure Chest", "Destroyed a left over treasure chest at {0}"},
                {"Indestructible", "<color=#FF0000>Treasure chests are indestructible!</color>"},
                {"Newman Enter", "<color=#FF0000>To walk with clothes is to set one-self on fire. Tread lightly.</color>"},
                {"Newman Traitor Burn", "<color=#FF0000>Tempted by the riches you have defiled these grounds. Vanish from these lands or PERISH!</color>"},
                {"Newman Traitor", "<color=#FF0000>Tempted by the riches you have defiled these grounds. Vanish from these lands!</color>"},
                {"Newman Protected", "<color=#FF0000>This newman is temporarily protected on these grounds!</color>"},
                {"Newman Protect", "<color=#FF0000>You are protected on these grounds. Do not defile them.</color>"},
                {"Newman Protect Fade", "<color=#FF0000>Your protection has faded.</color>"},
                {"Log Stolen", "{0} ({1}) chests stolen {2}"},
                {"Log Granted", "Granted {0} ({1}) permission {2} for group {3}"},
                {"Log Saved", "Treasure Hunters have been logged to: {0}"},
                {"MessagePrefix", "[ <color=#406B35>Dangerous Treasures</color> ] "},
                {"Countdown", "<color=#C0C0C0>Event at <color=#FFFF00>{0}</color> will start in <color=#FFFF00>{1}</color>!</color>"},
                {"RestartDetected", "Restart detected. Next event in {0} minutes."},
                {"DestroyingTreasure", "<color=#C0C0C0>The treasure at <color=#FFFF00>{0}</color> will be destroyed by fire in <color=#FFFF00>{1}</color> if not looted! Use <color=#FFA500>/{2}</color> to find this chest.</color>"},
                {"EconomicsDeposit", "You have received <color=#FFFF00>${0}</color> for stealing the treasure!"},
                {"ServerRewardPoints", "You have received <color=#FFFF00>{0} RP</color> for stealing the treasure!"},
                {"InvalidItem", "Invalid item shortname: {0}. Use /{1} additem <shortname> <amount> [skin]"},
                {"AddedItem", "Added item: {0} amount: {1}, skin: {2}"},
                {"CustomPositionSet", "Custom event spawn location set to: {0}"},
                {"CustomPositionRemoved", "Custom event spawn location removed."},
                {"OpenedEvents", "Opened {0}/{1} events."},
                {"OnFirstPlayerEntered", "<color=#FFFF00>{0}</color> is the first to enter the dangerous treasure event at <color=#FFFF00>{1}</color>"},
                {"OnChestOpened", "<color=#FFFF00>{0}</color> is the first to see the treasures at <color=#FFFF00>{1}</color>!</color>"},
                {"OnChestDespawned", "The treasures at <color=#FFFF00>{0}</color> have been lost forever! Better luck next time."},
                {"CannotBeMounted", "You cannot loot the treasure while mounted!"},
                {"CannotBeLooted", "This treasure does not belong to you!"},
                {"CannotTeleport", "You are not allowed to teleport from this event."},
                {"TreasureHunter", "<color=#ADD8E6>{0}</color>. <color=#C0C0C0>{1}</color> (<color=#FFFF00>{2}</color>)"},
                {"Timed Event", "<color=#FFFF00>You cannot loot until the fire aura expires! Tread lightly, the fire aura is very deadly!</color>)"},
                {"Timed Npc Event", "<color=#FFFF00>You cannot loot until you kill all of the npcs and wait for the fire aura to expire! Tread lightly, the fire aura is very deadly!</color>)"},
                {"Npc Event", "<color=#FFFF00>You cannot loot until you kill all of the npcs surrounding the fire aura! Tread lightly, the fire aura is very deadly!</color>)"},
            };
        }

        protected override void LoadDefaultMessages()
        {
            lang.RegisterMessages(GetMessages(), this);
        }

        private int GetPercentIncreasedAmount(DifficultyLevel options, int amount)
        {
            if (config.Treasure.UseDOWL && !config.Treasure.Increased && config.Treasure.PercentLoss > 0m)
            {
                return UnityEngine.Random.Range(Convert.ToInt32(amount - (amount * config.Treasure.PercentLoss)), amount + 1);
            }

            decimal percentIncrease = 0m;

            switch (DateTime.Now.DayOfWeek)
            {
                case DayOfWeek.Monday:
                    {
                        percentIncrease = config.Treasure.PercentIncreaseOnMonday;
                        break;
                    }
                case DayOfWeek.Tuesday:
                    {
                        percentIncrease = config.Treasure.PercentIncreaseOnTuesday;
                        break;
                    }
                case DayOfWeek.Wednesday:
                    {
                        percentIncrease = config.Treasure.PercentIncreaseOnWednesday;
                        break;
                    }
                case DayOfWeek.Thursday:
                    {
                        percentIncrease = config.Treasure.PercentIncreaseOnThursday;
                        break;
                    }
                case DayOfWeek.Friday:
                    {
                        percentIncrease = config.Treasure.PercentIncreaseOnFriday;
                        break;
                    }
                case DayOfWeek.Saturday:
                    {
                        percentIncrease = config.Treasure.PercentIncreaseOnSaturday;
                        break;
                    }
                case DayOfWeek.Sunday:
                    {
                        percentIncrease = config.Treasure.PercentIncreaseOnSunday;
                        break;
                    }
            }

            if (percentIncrease > 1m)
            {
                percentIncrease /= 100;
            }

            if (percentIncrease > 0m)
            {
                amount = Convert.ToInt32(amount + (amount * percentIncrease));

                if (config.Treasure.PercentLoss > 0m)
                {
                    amount = UnityEngine.Random.Range(Convert.ToInt32(amount - (amount * config.Treasure.PercentLoss)), amount + 1);
                }
            }

            return amount;
        }

        public static Color __(string hex)
        {
            return ColorUtility.TryParseHtmlString(hex.StartsWith("#") ? hex : $"#{hex}", out var color) ? color : Color.red;
        }

        private string _(string key, string id = null, params object[] args)
        {
            return RemoveFormatting(msg(key, id, args));
        }

        private Regex IndexRegex = new Regex(@"\{(\d+)\}", RegexOptions.Compiled);

        public string Format(string format, params object[] args)
        {
            return IndexRegex.Replace(format, match =>
            {
                if (int.TryParse(match.Groups[1].Value, out int index) && index < args.Length)
                {
                    return args[index] != null ? args[index].ToString() : string.Empty;
                }

                return match.Value;
            });
        }

        private string msg(string key, string id = null, params object[] args)
        {
            string message = config.EventMessages.Prefix && id != null && id != "server_console" ? lang.GetMessage("MessagePrefix", this, null) + lang.GetMessage(key, this, id) : lang.GetMessage(key, this, id);

            return args.Length > 0 ? Format(message, args) : message;
        }

        private string msg2(string key, string id, params object[] args)
        {
            string message = lang.GetMessage(key, this, id);

            return args.Length > 0 ? Format(message, args) : message;
        }

        private string RemoveFormatting(string source) => source.Contains(">") ? System.Text.RegularExpressions.Regex.Replace(source, "<.*?>", string.Empty) : source;

        private void Message(BasePlayer player, string key, params object[] args)
        {
            if (player == null)
            {
                return;
            }

            string message = msg(key, player.UserIDString, args);

            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (config.EventMessages.Message)
            {
                Player.Message(player, message, 0uL);
            }

            if (config.EventMessages.AA.Enabled || config.EventMessages.NotifyType != -1)
            {
                if (!_notifications.TryGetValue(player.userID, out var notifications))
                {
                    _notifications[player.userID] = notifications = new();
                }

                notifications.Add(new()
                {
                    player = player,
                    messageEx = message
                });
            }
        }

        private void Message(IPlayer user, string key, params object[] args)
        {
            if (user != null)
            {
                user.Reply(msg2(key, user.Id, args));
            }
        }

        private void Message(ConsoleSystem.Arg arg, string key, params object[] args)
        {
            if (arg != null)
            {
                arg.ReplyWith(msg2(key, arg.Player()?.UserIDString, args));
            }
        }

        public class Notification
        {
            public BasePlayer player;
            public string messageEx;
        }

        private Dictionary<ulong, List<Notification>> _notifications = new();

        private void CheckNotifications()
        {
            if (_notifications.Count > 0)
            {
                foreach (var entry in _notifications.ToList())
                {
                    var notification = entry.Value.ElementAt(0);

                    SendNotification(notification);

                    entry.Value.Remove(notification);

                    if (entry.Value.Count == 0)
                    {
                        _notifications.Remove(entry.Key);
                    }
                }
            }
        }

        private void SendNotification(Notification notification)
        {
            if (!notification.player.IsReallyConnected())
            {
                return;
            }

            if (config.EventMessages.AA.Enabled && AdvancedAlerts.CanCall())
            {
                AdvancedAlerts?.Call("SpawnAlert", notification.player, "hook", notification.messageEx, config.EventMessages.AA.AnchorMin, config.EventMessages.AA.AnchorMax, config.EventMessages.AA.Time);
            }

            if (config.EventMessages.NotifyType != -1 && Notify.CanCall())
            {
                Notify?.Call("SendNotify", notification.player, config.EventMessages.NotifyType, notification.messageEx);
            }
        }

        #endregion

        #region Configuration

        private Configuration config;

        private static List<LootItem> DefaultLoot
        {
            get
            {
                return new()
                {
                    new() { shortname = "ammo.pistol", amount = 40, skin = 0, amountMin = 40 },
                    new() { shortname = "ammo.pistol.fire", amount = 40, skin = 0, amountMin = 40 },
                    new() { shortname = "ammo.pistol.hv", amount = 40, skin = 0, amountMin = 40 },
                    new() { shortname = "ammo.rifle", amount = 60, skin = 0, amountMin = 60 },
                    new() { shortname = "ammo.rifle.explosive", amount = 60, skin = 0, amountMin = 60 },
                    new() { shortname = "ammo.rifle.hv", amount = 60, skin = 0, amountMin = 60 },
                    new() { shortname = "ammo.rifle.incendiary", amount = 60, skin = 0, amountMin = 60 },
                    new() { shortname = "ammo.shotgun", amount = 24, skin = 0, amountMin = 24 },
                    new() { shortname = "ammo.shotgun.slug", amount = 40, skin = 0, amountMin = 40 },
                    new() { shortname = "surveycharge", amount = 20, skin = 0, amountMin = 20 },
                    new() { shortname = "metal.refined", amount = 150, skin = 0, amountMin = 150 },
                    new() { shortname = "bucket.helmet", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "cctv.camera", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "coffeecan.helmet", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "explosive.timed", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "metal.facemask", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "metal.plate.torso", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "pistol.m92", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "rifle.ak", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "rifle.bolt", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "rifle.lr300", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "smg.2", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "smg.mp5", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "smg.thompson", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "supply.signal", amount = 1, skin = 0, amountMin = 1 },
                    new() { shortname = "targeting.computer", amount = 1, skin = 0, amountMin = 1 },
                };
            }
        }

        public class PluginSettings
        {
            [JsonProperty(PropertyName = "Permission Name")]
            public string PermName { get; set; } = "dangeroustreasures.use";

            [JsonProperty(PropertyName = "Permission To Ignore With Players Limit")]
            public string PlayerLimitPermission = "";

            [JsonProperty(PropertyName = "Event Chat Command")]
            public string EventChatCommand { get; set; } = "dtevent";

            [JsonProperty(PropertyName = "Distance Chat Command")]
            public string DistanceChatCommand { get; set; } = "dtd";

            [JsonProperty(PropertyName = "Draw Location On Screen With Distance Command")]
            public bool AllowDrawText { get; set; } = true;

            [JsonProperty(PropertyName = "Event Console Command")]
            public string EventConsoleCommand { get; set; } = "dtevent";

            [JsonProperty(PropertyName = "Show X Z Coordinates")]
            public bool ShowXZ { get; set; } = false;

            [JsonProperty(PropertyName = "Show Grid Coordinates")]
            public bool ShowGrid { get; set; } = true;

            [JsonProperty(PropertyName = "Grids To Block Spawns At", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> BlockedGrids = new();

            [JsonProperty(PropertyName = "Block Spawns At Positions", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ManagementSettingsLocations> BlockedPositions = new() { new(Vector3.zero, 1f) };

            [JsonProperty(PropertyName = "Block AlphaLoot Plugin (NPCs)")]
            public bool BlockAlphaLoot;

            [JsonProperty(PropertyName = "Block BetterLoot Plugin (NPCs)")]
            public bool BlockBetterLoot = true;

            [JsonProperty(PropertyName = "Block Npc Kits Plugin")]
            public bool BlockNpcKits { get; set; }

        }

        public class ManagementSettingsLocations
        {
            [JsonProperty(PropertyName = "position")]
            [JsonConverter(typeof(UnityVector3Converter))]
            public Vector3 position;
            public float radius;
            public ManagementSettingsLocations() { }
            public ManagementSettingsLocations(Vector3 position, float radius)
            {
                (this.position, this.radius) = (position, radius);
            }
        }

        private class UnityVector3Converter : JsonConverter
        {
            public override void WriteJson(JsonWriter writer, object value, JsonSerializer serializer)
            {
                var vector = (Vector3)value;
                writer.WriteValue($"{vector.x} {vector.y} {vector.z}");
            }

            public override object ReadJson(JsonReader reader, Type objectType, object existingValue, JsonSerializer serializer)
            {
                if (reader.TokenType == JsonToken.String)
                {
                    var values = reader.Value.ToString().Trim().Split(' ');
                    return new Vector3(Convert.ToSingle(values[0]), Convert.ToSingle(values[1]), Convert.ToSingle(values[2]));
                }
                var o = Newtonsoft.Json.Linq.JObject.Load(reader);
                return new Vector3(Convert.ToSingle(o["x"]), Convert.ToSingle(o["y"]), Convert.ToSingle(o["z"]));
            }

            public override bool CanConvert(Type objectType)
            {
                return objectType == typeof(Vector3);
            }
        }

        public class CountdownSettings
        {
            [JsonProperty(PropertyName = "Use Countdown Before Event Starts")]
            public bool Enabled { get; set; } = false;

            [JsonProperty(PropertyName = "Time In Seconds", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<int> Times { get; set; } = new() { 120, 60, 30, 15 };
        }

        public class EventSettings
        {
            public EventSettings Clone() => MemberwiseClone() as EventSettings;

            public EventSettings SelectiveClone(DifficultyLevel old)
            {
                EventSettings clone = Clone();
                clone.Automated = false;
                clone.IntervalMin = 3600f;
                clone.IntervalMax = 7200f;
                clone.TreasureAmount = old.Event.TreasureAmount;
                clone.MarkerName = $"Dangerous Treasures Event [{old.Difficulty}]";
                clone.MarkerColor = old.Event.MarkerColor;
                return clone;
            }

            [JsonProperty(PropertyName = "Allow Player Bags To Be Lootable At Events")]
            public bool PlayersLootable;

            [JsonProperty(PropertyName = "Automated")]
            public bool Automated { get; set; } = false;

            [JsonProperty(PropertyName = "Every Min Seconds")]
            public float IntervalMin { get; set; } = 3600f;

            [JsonProperty(PropertyName = "Every Max Seconds")]
            public float IntervalMax { get; set; } = 7200f;

            [JsonProperty(PropertyName = "Use Vending Map Marker")]
            public bool MarkerVending { get; set; } = true;

            [JsonProperty(PropertyName = "Use Marker Manager Plugin")]
            public bool MarkerManager { get; set; }

            [JsonProperty(PropertyName = "Use Explosion Map Marker")]
            public bool MarkerExplosion { get; set; } = false;

            [JsonProperty(PropertyName = "Marker Color")]
            public string MarkerColor { get; set; } = "#FF0000";

            [JsonProperty(PropertyName = "Marker Radius")]
            public float MarkerRadius { get; set; } = 0.25f;

            [JsonProperty(PropertyName = "Marker Radius (Smaller Maps)")]
            public float MarkerRadiusSmall { get; set; } = 0.5f;

            [JsonProperty(PropertyName = "Marker Event Name")]
            public string MarkerName { get; set; } = "Dangerous Treasures Event";

            [JsonProperty(PropertyName = "Max Manual Events")]
            public int Max { get; set; } = 1;

            [JsonProperty(PropertyName = "Always Spawn Max Manual Events")]
            public bool SpawnMax { get; set; }

            [JsonProperty(PropertyName = "Stagger Spawns Every X Seconds")]
            public float Stagger { get; set; } = 10f;

            [JsonProperty(PropertyName = "Amount Of Items To Spawn")]
            public int TreasureAmount { get; set; } = 6;

            [JsonProperty(PropertyName = "Use Spheres")]
            public bool Spheres { get; set; } = true;

            [JsonProperty(PropertyName = "Amount Of Spheres")]
            public int SphereAmount { get; set; } = 5;

            [JsonProperty(PropertyName = "Destroy Spheres When Event Starts")]
            public bool DestroySphereOnStart { get; set; } = true;

            [JsonProperty(PropertyName = "Destroy Fires When Event Starts")]
            public bool DestroyFireOnStart { get; set; } = true;

            [JsonProperty(PropertyName = "Destroy Launchers When Event Starts")]
            public bool DestroyLauncherOnStart { get; set; } = true;

            [JsonProperty(PropertyName = "Player Limit For Event")]
            public int PlayerLimit { get; set; } = 1;

            [JsonProperty(PropertyName = "Fire Aura Radius (Advanced Users Only)")]
            public float Radius { get; set; } = 25f;

            [JsonProperty(PropertyName = "Auto Draw On New Event For Nearby Players")]
            public bool DrawTreasureIfNearby { get; set; } = false;

            [JsonProperty(PropertyName = "Auto Draw Minimum Distance")]
            public float AutoDrawDistance { get; set; } = 300f;

            [JsonProperty(PropertyName = "Grant DDRAW temporarily to players")]
            public bool GrantDraw { get; set; } = true;

            [JsonProperty(PropertyName = "Grant Draw Time")]
            public float DrawTime { get; set; } = 15f;

            [JsonProperty(PropertyName = "Time To Loot")]
            public float DestructTime { get; set; } = 900f;

            [JsonProperty(PropertyName = "Despawn Timer Resets When Npc Is Attacked By Players")]
            public bool DestructTimeResetsWhenAttacked { get; set; }

            [JsonProperty(PropertyName = "Despawn Timer Resets When Npc Is Killed By Players")]
            public bool DestructTimeResetsWhenKilled { get; set; } = true;
        }

        public class UIAdvancedAlertSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Anchor Min")]
            public string AnchorMin { get; set; } = "0.35 0.85";

            [JsonProperty(PropertyName = "Anchor Max")]
            public string AnchorMax { get; set; } = "0.65 0.95";

            [JsonProperty(PropertyName = "Time Shown")]
            public float Time { get; set; } = 5f;
        }

        public class EventMessageSettings
        {
            [JsonProperty(PropertyName = "Advanced Alerts UI")]
            public UIAdvancedAlertSettings AA { get; set; } = new();

            [JsonProperty(PropertyName = "Notify Plugin - Type (-1 = disabled)")]
            public int NotifyType { get; set; } = -1;

            [JsonProperty(PropertyName = "UI Popup Interval")]
            public float Interval { get; set; } = 1f;

            [JsonProperty(PropertyName = "Show Noob Warning Message")]
            public bool NoobWarning { get; set; }

            [JsonProperty(PropertyName = "Show Barrage Message")]
            public bool Barrage { get; set; } = true;

            [JsonProperty(PropertyName = "Show Despawn Message")]
            public bool Destruct { get; set; } = true;

            [JsonProperty(PropertyName = "Show You Have Entered")]
            public bool Entered { get; set; } = true;

            [JsonProperty(PropertyName = "Show First Player Entered")]
            public bool FirstEntered { get; set; } = false;

            [JsonProperty(PropertyName = "Show First Player Opened")]
            public bool FirstOpened { get; set; } = false;

            [JsonProperty(PropertyName = "Show Opened Message")]
            public bool Opened { get; set; } = true;

            [JsonProperty(PropertyName = "Show Prefix")]
            public bool Prefix { get; set; } = true;

            [JsonProperty(PropertyName = "Show Started Message")]
            public bool Started { get; set; } = true;

            [JsonProperty(PropertyName = "Show Thief Message")]
            public bool Thief { get; set; } = true;

            [JsonProperty(PropertyName = "Send Messages To Player")]
            public bool Message { get; set; } = true;
        }

        public class FireballSettings
        {
            public FireballSettings Clone() => MemberwiseClone() as FireballSettings;

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Damage Per Second")]
            public float DamagePerSecond { get; set; } = 10f;

            [JsonProperty(PropertyName = "Lifetime Min")]
            public float LifeTimeMin { get; set; } = 7.5f;

            [JsonProperty(PropertyName = "Lifetime Max")]
            public float LifeTimeMax { get; set; } = 10f;

            [JsonProperty(PropertyName = "Radius")]
            public float Radius { get; set; } = 1f;

            [JsonProperty(PropertyName = "Tick Rate")]
            public float TickRate { get; set; } = 1f;

            [JsonProperty(PropertyName = "Generation")]
            public float Generation { get; set; } = 5f;

            [JsonProperty(PropertyName = "Water To Extinguish")]
            public int WaterToExtinguish { get; set; } = 25;

            [JsonProperty(PropertyName = "Spawn Every X Seconds")]
            public int SecondsBeforeTick { get; set; } = 5;
        }

        public class GUIAnnouncementSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = false;

            [JsonProperty(PropertyName = "Text Color")]
            public string TextColor { get; set; } = "White";

            [JsonProperty(PropertyName = "Banner Tint Color")]
            public string TintColor { get; set; } = "Grey";

            [JsonProperty(PropertyName = "Maximum Distance")]
            public float Distance { get; set; } = 300f;
        }

        public class MissileLauncherSettings
        {
            public MissileLauncherSettings Clone() => MemberwiseClone() as MissileLauncherSettings;

            [JsonProperty(PropertyName = "Acquire Time In Seconds")]
            public float TargettingTime { get; set; } = 10f;

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = false;

            [JsonProperty(PropertyName = "Damage Per Missile")]
            public float Damage { get; set; } = 0.0f;

            [JsonProperty(PropertyName = "Detection Distance")]
            public float Distance { get; set; } = 15f;

            [JsonProperty(PropertyName = "Life Time In Seconds")]
            public float Lifetime { get; set; } = 60f;

            [JsonProperty(PropertyName = "Ignore Flying Players")]
            public bool IgnoreFlying { get; set; } = true;

            [JsonProperty(PropertyName = "Spawn Every X Seconds")]
            public float Frequency { get; set; } = 15f;

            [JsonProperty(PropertyName = "Target Chest If No Player Target")]
            public bool TargetChest { get; set; } = false;
        }

        public class MonumentSettings
        {
            [JsonProperty(PropertyName = "NPC Blacklisted Monuments", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, bool> NPCBlacklist { get; set; } = new()
            {
                ["Bandit Camp"] = true,
                ["Barn"] = true,
                ["Fishing Village"] = true,
                ["Junkyard"] = true,
                ["Large Barn"] = true,
                ["Large Fishing Village"] = true,
                ["Outpost"] = true,
                ["Ranch"] = true,
                ["Train Tunnel"] = true,
                ["Underwater Lab"] = true,
            };

            [JsonProperty(PropertyName = "Event Blacklisted Monuments", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, bool> EventBlacklist { get; set; } = new()
            {
                ["Bandit Camp"] = true,
                ["Barn"] = true,
                ["Fishing Village"] = true,
                ["Junkyard"] = true,
                ["Large Barn"] = true,
                ["Large Fishing Village"] = true,
                ["Outpost"] = true,
                ["Ranch"] = true,
                ["Train Tunnel"] = true,
                ["Underwater Lab"] = true,
            };

            [JsonProperty(PropertyName = "Auto Spawn At Monuments Only")]
            public bool Only { get; set; } = false;

            [JsonProperty(PropertyName = "Chance To Spawn At Monuments Instead")]
            public float Chance { get; set; } = 0.0f;

            [JsonProperty(PropertyName = "Allow Treasure Loot Underground")]
            public bool Underground { get; set; } = false;
        }

        public class NewmanModeSettings
        {
            [JsonProperty(PropertyName = "Protect Nakeds From Fire Aura")]
            public bool Aura { get; set; } = false;

            [JsonProperty(PropertyName = "Protect Nakeds From Other Harm")]
            public bool Harm { get; set; } = false;
        }

        public class NpcKitSettings
        {
            public NpcKitSettings Clone()
            {
                return new()
                {
                    Helm = new(Helm),
                    Torso = new(Torso),
                    Pants = new(Pants),
                    Gloves = new(Gloves),
                    Boots = new(Boots),
                    Shirt = new(Shirt),
                    Kilts = new(Kilts),
                    Weapon = new(Weapon)
                };
            }

            [JsonProperty(PropertyName = "Helm", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Helm = new();

            [JsonProperty(PropertyName = "Torso", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Torso = new();

            [JsonProperty(PropertyName = "Pants", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Pants = new();

            [JsonProperty(PropertyName = "Gloves", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Gloves = new();

            [JsonProperty(PropertyName = "Boots", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Boots = new();

            [JsonProperty(PropertyName = "Shirt", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Shirt = new();

            [JsonProperty(PropertyName = "Kilts", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Kilts = new();

            [JsonProperty(PropertyName = "Weapon", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Weapon = new();
        }

        public class NpcLootSettings
        {
            public NpcLootSettings Clone()
            {
                var copy = (NpcLootSettings)MemberwiseClone();
                copy.IDs = new(IDs);
                return copy;
            }

            [JsonProperty(PropertyName = "Prefab ID List", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> IDs { get; set; } = new() { "cargo", "turret_any", "ch47_gunner", "excavator", "full_any", "heavy", "junkpile_pistol", "oilrig", "patrol", "peacekeeper", "roam", "roamtethered" };

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; }

            [JsonProperty(PropertyName = "Disable All Prefab Loot Spawns")]
            public bool None { get; set; }

            [JsonProperty(PropertyName = "Call OnCorpsePopulate Hook (some plugins require this)")]
            public bool CallHook { get; set; }

            public uint GetRandom()
            {
                if (IDs.Count > 0)
                {
                    switch (IDs.GetRandom())
                    {
                        case "cargo": return 3623670799;
                        case "turret_any": return 1639447304;
                        case "ch47_gunner": return 1017671955;
                        case "excavator": return 4293908444;
                        case "full_any": return 1539172658;
                        case "heavy": return 1536035819;
                        case "junkpile_pistol": return 2066159302;
                        case "cargo_turret": return 881071619;
                        case "oilrig": return 548379897;
                        case "patrol": return 4272904018;
                        case "peacekeeper": return 2390854225;
                        case "roam": return 4199494415;
                        case "roamtethered": return 529928930;
                    }
                }

                return 1536035819;
            }
        }

        public class NpcSettingsAccuracy
        {
            public NpcSettingsAccuracy Clone() => (NpcSettingsAccuracy)MemberwiseClone();

            [JsonProperty(PropertyName = "AK47")]
            public double AK47 { get; set; }

            [JsonProperty(PropertyName = "AK47 ICE")]
            public double AK47ICE { get; set; }

            [JsonProperty(PropertyName = "Bolt Rifle")]
            public double BOLT_RIFLE { get; set; }

            [JsonProperty(PropertyName = "Compound Bow")]
            public double COMPOUND_BOW { get; set; }

            [JsonProperty(PropertyName = "Crossbow")]
            public double CROSSBOW { get; set; }

            [JsonProperty(PropertyName = "Double Barrel Shotgun")]
            public double DOUBLE_SHOTGUN { get; set; }

            [JsonProperty(PropertyName = "Eoka")]
            public double EOKA { get; set; }

            [JsonProperty(PropertyName = "Glock")]
            public double GLOCK { get; set; }

            [JsonProperty(PropertyName = "HMLMG")]
            public double HMLMG { get; set; }

            [JsonProperty(PropertyName = "L96")]
            public double L96 { get; set; }

            [JsonProperty(PropertyName = "LR300")]
            public double LR300 { get; set; }

            [JsonProperty(PropertyName = "M249")]
            public double M249 { get; set; }

            [JsonProperty(PropertyName = "M39")]
            public double M39 { get; set; }

            [JsonProperty(PropertyName = "M92")]
            public double M92 { get; set; }

            [JsonProperty(PropertyName = "MP5")]
            public double MP5 { get; set; }

            [JsonProperty(PropertyName = "Nailgun")]
            public double NAILGUN { get; set; }

            [JsonProperty(PropertyName = "Pump Shotgun")]
            public double PUMP_SHOTGUN { get; set; }

            [JsonProperty(PropertyName = "Python")]
            public double PYTHON { get; set; }

            [JsonProperty(PropertyName = "Revolver")]
            public double REVOLVER { get; set; }

            [JsonProperty(PropertyName = "Semi Auto Pistol")]
            public double SEMI_AUTO_PISTOL { get; set; }

            [JsonProperty(PropertyName = "Semi Auto Rifle")]
            public double SEMI_AUTO_RIFLE { get; set; }

            [JsonProperty(PropertyName = "Spas12")]
            public double SPAS12 { get; set; }

            [JsonProperty(PropertyName = "Speargun")]
            public double SPEARGUN { get; set; }

            [JsonProperty(PropertyName = "SMG")]
            public double SMG { get; set; }

            [JsonProperty(PropertyName = "Snowball Gun")]
            public double SNOWBALL_GUN { get; set; }

            [JsonProperty(PropertyName = "Thompson")]
            public double THOMPSON { get; set; }

            [JsonProperty(PropertyName = "Waterpipe Shotgun")]
            public double WATERPIPE_SHOTGUN { get; set; }

            public NpcSettingsAccuracy(double guns)
            {
                Set(guns, 50);
            }

            public void Set(double guns, double bows)
            {
                AK47 = AK47ICE = BOLT_RIFLE = DOUBLE_SHOTGUN = EOKA = GLOCK = HMLMG = L96 = LR300 = M249 = M39 = M92 = MP5 = NAILGUN = PUMP_SHOTGUN = PYTHON = REVOLVER = SEMI_AUTO_PISTOL = SEMI_AUTO_RIFLE = SPAS12 = SPEARGUN = SMG = SNOWBALL_GUN = THOMPSON = WATERPIPE_SHOTGUN = guns;
                COMPOUND_BOW = CROSSBOW = bows;
            }

            public double Get(HumanoidBrain brain)
            {
                return brain.AttackEntity.ShortPrefabName switch
                {
                    "ak47u.entity" or "ak47u_med.entity" or "ak47u_diver.entity" or "sks.entity" => AK47,
                    "ak47u_ice.entity" => AK47ICE,
                    "bolt_rifle.entity" => BOLT_RIFLE,
                    "compound_bow.entity" or "legacybow.entity" => COMPOUND_BOW,
                    "crossbow.entity" or "bow_hunting.entity" or "mini_crossbow.entity" => CROSSBOW,
                    "double_shotgun.entity" => DOUBLE_SHOTGUN,
                    "glock.entity" or "hc_revolver.entity" => GLOCK,
                    "hmlmg.entity" or "mgl.entity" => HMLMG,
                    "l96.entity" => L96,
                    "lr300.entity" => LR300,
                    "m249.entity" or "minigun.entity" => M249,
                    "m39.entity" => M39,
                    "m92.entity" => M92,
                    "mp5.entity" => MP5,
                    "nailgun.entity" => NAILGUN,
                    "pistol_eoka.entity" => EOKA,
                    "pistol_revolver.entity" => REVOLVER,
                    "pistol_semiauto.entity" => SEMI_AUTO_PISTOL,
                    "python.entity" => PYTHON,
                    "semi_auto_rifle.entity" => SEMI_AUTO_RIFLE,
                    "shotgun_pump.entity" or "blunderbuss.entity" or "m4_shotgun.entity" => PUMP_SHOTGUN,
                    "shotgun_waterpipe.entity" => WATERPIPE_SHOTGUN,
                    "spas12.entity" => SPAS12,
                    "speargun.entity" or "blowpipe.entity" or "boomerang.entity" => SPEARGUN,
                    "smg.entity" or "t1_smg" => SMG,
                    "snowballgun.entity" => SNOWBALL_GUN,
                    "thompson.entity" or _ => THOMPSON,
                };
            }
        }

        public class NpcSettingsMurderer
        {
            public NpcSettingsMurderer Clone()
            {
                var copy = (NpcSettingsMurderer)MemberwiseClone();
                copy.RandomNames = new(RandomNames);
                copy.Kits = new(Kits);
                copy.Items = Items.Clone();
                copy.Alternate = Alternate.Clone();
                copy.Accuracy = Accuracy.Clone();
                return copy;
            }

            [JsonProperty(PropertyName = "Random Names", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> RandomNames { get; set; } = new();

            [JsonProperty(PropertyName = "Items)")]
            public NpcKitSettings Items { get; set; } = new()
            {
                Helm = { "metal.facemask" },
                Torso = { "metal.plate.torso" },
                Pants = { "pants" },
                Gloves = { "tactical.gloves" },
                Boots = { "boots.frog" },
                Shirt = { "tshirt" },
                Weapon = { "machete" }
            };

            [JsonProperty(PropertyName = "Kits", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Kits { get; set; } = new() { "murderer_kit_1", "murderer_kit_2" };

            [JsonProperty(PropertyName = "Spawn Alternate Loot")]
            public NpcLootSettings Alternate { get; set; } = new();

            [JsonProperty(PropertyName = "Weapon Accuracy (0 - 100)")]
            public NpcSettingsAccuracy Accuracy { get; set; } = new(100);

            [JsonProperty(PropertyName = "Aggression Range")]
            public float AggressionRange { get; set; } = 70f;

            [JsonProperty(PropertyName = "Despawn Inventory On Death")]
            public bool DespawnInventory { get; set; } = true;

            [JsonProperty(PropertyName = "Corpse Despawn Time When Despawn Inventory On Death")]
            public float DespawnInventoryTime { get; set; } = 30f;

            [JsonProperty(PropertyName = "Corpse Despawn Time Otherwise")]
            public float CorpseDespawnTime { get; set; } = 300f;

            [JsonProperty(PropertyName = "Die Instantly From Headshots")]
            public bool Headshot { get; set; }

            [JsonProperty(PropertyName = "Amount To Spawn (min)")]
            public int SpawnMinAmount { get; set; } = 2;

            [JsonProperty(PropertyName = "Amount To Spawn (max)")]
            public int SpawnAmount { get; set; } = 2;

            [JsonProperty(PropertyName = "Health")]
            public float Health { get; set; } = 150f;
        }

        public class NpcSettingsScientist
        {
            public NpcSettingsScientist Clone()
            {
                var copy = (NpcSettingsScientist)MemberwiseClone();
                copy.RandomNames = new(RandomNames);
                copy.Kits = new(Kits);
                copy.Items = Items.Clone();
                copy.Alternate = Alternate.Clone();
                copy.Accuracy = Accuracy.Clone();
                return copy;
            }

            [JsonProperty(PropertyName = "Random Names", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> RandomNames { get; set; } = new();

            [JsonProperty(PropertyName = "Items")]
            public NpcKitSettings Items { get; set; } = new()
            {
                Torso = { "hazmatsuit_scientist_peacekeeper" },
                Weapon = { "rifle.ak" }
            };

            [JsonProperty(PropertyName = "Kits", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Kits { get; set; } = new() { "scientist_kit_1", "scientist_kit_2" };

            [JsonProperty(PropertyName = "Spawn Alternate Loot")]
            public NpcLootSettings Alternate { get; set; } = new();

            [JsonProperty(PropertyName = "Weapon Accuracy (0 - 100)")]
            public NpcSettingsAccuracy Accuracy { get; set; } = new(20);

            [JsonProperty(PropertyName = "Aggression Range")]
            public float AggressionRange { get; set; } = 70f;

            [JsonProperty(PropertyName = "Despawn Inventory On Death")]
            public bool DespawnInventory { get; set; } = true;

            [JsonProperty(PropertyName = "Corpse Despawn Time When Despawn Inventory On Death")]
            public float DespawnInventoryTime { get; set; } = 30f;

            [JsonProperty(PropertyName = "Corpse Despawn Time Otherwise")]
            public float CorpseDespawnTime { get; set; } = 300f;

            [JsonProperty(PropertyName = "Die Instantly From Headshots")]
            public bool Headshot { get; set; }

            [JsonProperty(PropertyName = "Amount To Spawn (min)")]
            public int SpawnMinAmount { get; set; } = 2;

            [JsonProperty(PropertyName = "Amount To Spawn (max)")]
            public int SpawnAmount { get; set; } = 2;

            [JsonProperty(PropertyName = "Health (100 min, 5000 max)")]
            public float Health { get; set; } = 150f;
        }

        public class NpcSettings
        {
            [JsonProperty(PropertyName = "Murderers")]
            public NpcSettingsMurderer Murderers { get; set; } = new();

            [JsonProperty(PropertyName = "Scientists")]
            public NpcSettingsScientist Scientists { get; set; } = new();

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Allow Npcs To Leave Dome When Attacking")]
            public bool CanLeave { get; set; } = true;

            [JsonProperty(PropertyName = "Allow Npcs To Target Other Npcs")]
            public bool TargetNpcs { get; set; }

            [JsonProperty(PropertyName = "Block Damage From Players Beyond X Distance (0 = disabled)")]
            public float Range { get; set; } = 0f;

            [JsonProperty(PropertyName = "Kill Underwater Npcs")]
            public bool KillUnderwater { get; set; } = true;
        }

        public class PasteOption
        {
            [JsonProperty(PropertyName = "Option")]
            public string Key { get; set; }

            [JsonProperty(PropertyName = "Value")]
            public string Value { get; set; }
        }

        public class RankedLadderSettings
        {
            [JsonProperty(PropertyName = "Award Top X Players On Wipe")]
            public int Amount { get; set; } = 3;

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Group Name")]
            public string Group { get; set; } = "treasurehunter";

            [JsonProperty(PropertyName = "Permission Name")]
            public string Permission { get; set; } = "dangeroustreasures.th";
        }

        public class RewardSettings
        {
            public RewardSettings Clone()
            {
                var copy = (RewardSettings)MemberwiseClone();
                copy.EventCommands = EventCommands.Clone();
                return copy;
            }

            [JsonProperty(PropertyName = "Commands To Run When Box Is Looted")]
            public RewardRunCommands EventCommands = new();

            [JsonProperty(PropertyName = "Economics Money")]
            public double Money { get; set; } = 0;

            [JsonProperty(PropertyName = "ServerRewards Points")]
            public double Points { get; set; } = 0;

            [JsonProperty(PropertyName = "Use Economics")]
            public bool Economics { get; set; } = false;

            [JsonProperty(PropertyName = "Use ServerRewards")]
            public bool ServerRewards { get; set; } = false;
        }

        public class RocketOpenerSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Rockets")]
            public int Amount { get; set; } = 8;

            [JsonProperty(PropertyName = "Speed")]
            public float Speed { get; set; } = 5f;

            [JsonProperty(PropertyName = "Use Fire Rockets")]
            public bool FireRockets { get; set; } = false;
        }

        public class SkinSettings
        {
            [JsonProperty(PropertyName = "Custom Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> Custom { get; set; } = new();

            [JsonProperty(PropertyName = "Use Random Skin")]
            public bool RandomSkins { get; set; } = true;

            [JsonProperty(PropertyName = "Preset Skin")]
            public ulong PresetSkin { get; set; } = 0;

            [JsonProperty(PropertyName = "Include Workshop Skins")]
            public bool RandomWorkshopSkins { get; set; } = true;

            [JsonProperty(PropertyName = "Randomize Npc Item Skins")]
            public bool Npcs { get; set; } = true;

            [JsonProperty(PropertyName = "Use Identical Skins For All Npcs")]
            public bool UniqueNpcs { get; set; } = true;
        }

        public class LootItem
        {
            public class ArmorSlots
            {
                [JsonProperty(PropertyName = "min")]
                public int min;
                [JsonProperty(PropertyName = "max")]
                public int max;
                internal int amount => max > 0 ? UnityEngine.Random.Range(min, max + 1) : 0;
                public void TryAdd(Item item)
                {
                    if (item == null || item.info == null || !item.info.TryGetComponent(out ItemModContainerArmorSlot slot))
                    {
                        return;
                    }
                    int cap = amount;
                    if (cap > 0)
                    {
                        slot.CreateAtCapacity(cap, item);
                        slot.OnItemCreated(item);
                    }
                }
            }
            public string shortname { get; set; } = "";
            public string name { get; set; } = "";
            public string text { get; set; } = null;
            public ulong skin { get; set; }
            public int amount { get; set; }
            public int amountMin { get; set; }
            public float condition { get; set; } = 1f;
            public float probability { get; set; } = 1f;
            [JsonProperty(PropertyName = "Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> skins { get; set; } = new();
            [JsonProperty(PropertyName = "armor module slots", NullValueHandling = NullValueHandling.Ignore)]
            public ArmorSlots slots;

            internal ItemDefinition definition => _def ??= ItemManager.FindItemDefinition(shortname);
            internal ItemDefinition _def;

            internal bool InitializeArmorSlots()
            {
                if (slots != null || definition == null || !definition.TryGetComponent(out ItemModContainerArmorSlot slot))
                {
                    return false;
                }
                slots = new()
                {
                    min = slot.MinSlots,
                    max = slot.MaxSlots
                };
                return true;
            }

            public LootItem Clone()
            {
                var copy = (LootItem)MemberwiseClone();
                copy.skins = new(skins);
                if (slots != null) copy.slots = new() { min = slots.min, max = slots.max };
                return copy;
            }

            public LootItem() { }

            public LootItem(string shortname, int amountMin = 1, int amount = 1, ulong skin = 0, float condition = 1.0f, float probability = 1.0f, string name = "", string text = null, ArmorSlots slots = null)
            {
                (this.shortname, this.amountMin, this.amount, this.skin, this.condition, this.probability, this.name, this.text, this.slots) =
                    (shortname, amountMin, amount, skin, condition, probability, name, text, slots);
            }
        }

        public class TreasureSettings
        {
            [JsonProperty(PropertyName = "Loot", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> Loot { get; set; } = DefaultLoot;

            [JsonProperty(PropertyName = "Use Random Skins")]
            public bool RandomSkins { get; set; } = false;

            [JsonProperty(PropertyName = "Include Workshop Skins")]
            public bool RandomWorkshopSkins { get; set; } = false;

        }

        public class DayOfTheWeekSettings
        {
            [JsonProperty(PropertyName = "Day Of Week Loot Monday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Monday { get; set; } = new();

            [JsonProperty(PropertyName = "Day Of Week Loot Tuesday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Tuesday { get; set; } = new();

            [JsonProperty(PropertyName = "Day Of Week Loot Wednesday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Wednesday { get; set; } = new();

            [JsonProperty(PropertyName = "Day Of Week Loot Thursday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Thursday { get; set; } = new();

            [JsonProperty(PropertyName = "Day Of Week Loot Friday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Friday { get; set; } = new();

            [JsonProperty(PropertyName = "Day Of Week Loot Saturday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Saturday { get; set; } = new();

            [JsonProperty(PropertyName = "Day Of Week Loot Sunday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Sunday { get; set; } = new();

            [JsonProperty(PropertyName = "Use Day Of Week Loot")]
            public bool UseDOWL { get; set; } = false;

            [JsonProperty(PropertyName = "Percent Increase On Monday")]
            public decimal PercentIncreaseOnMonday { get; set; } = 0;

            [JsonProperty(PropertyName = "Percent Increase On Tuesday")]
            public decimal PercentIncreaseOnTuesday { get; set; } = 0;

            [JsonProperty(PropertyName = "Percent Increase On Wednesday")]
            public decimal PercentIncreaseOnWednesday { get; set; } = 0;

            [JsonProperty(PropertyName = "Percent Increase On Thursday")]
            public decimal PercentIncreaseOnThursday { get; set; } = 0;

            [JsonProperty(PropertyName = "Percent Increase On Friday")]
            public decimal PercentIncreaseOnFriday { get; set; } = 0;

            [JsonProperty(PropertyName = "Percent Increase On Saturday")]
            public decimal PercentIncreaseOnSaturday { get; set; } = 0;

            [JsonProperty(PropertyName = "Percent Increase On Sunday")]
            public decimal PercentIncreaseOnSunday { get; set; } = 0;

            [JsonProperty(PropertyName = "Minimum Percent Loss")]
            public decimal PercentLoss { get; set; } = 0;

            [JsonProperty(PropertyName = "Percent Increase When Using Day Of Week Loot")]
            public bool Increased { get; set; } = false;
        }

        public class TruePVESettings
        {
            [JsonProperty(PropertyName = "Allow Building Damage At Events")]
            public bool AllowBuildingDamageAtEvents { get; set; } = false;

            [JsonProperty(PropertyName = "Allow PVP At Events")]
            public bool AllowPVPAtEvents { get; set; } = true;

            [JsonProperty(PropertyName = "Allow PVP Server-Wide During Events")]
            public bool ServerWidePVP { get; set; } = false;
        }

        public class UnlockSettings
        {
            [JsonProperty(PropertyName = "Min Seconds")]
            public float MinTime { get; set; } = 300f;

            [JsonProperty(PropertyName = "Max Seconds")]
            public float MaxTime { get; set; } = 480f;

            [JsonProperty(PropertyName = "Unlock When Npcs Die")]
            public bool WhenNpcsDie { get; set; } = false;

            [JsonProperty(PropertyName = "Require All Npcs Die Before Unlocking")]
            public bool RequireAllNpcsDie { get; set; } = false;

            [JsonProperty(PropertyName = "Lock Event To Player On Npc Death")]
            public bool LockToPlayerOnNpcDeath { get; set; } = false;

            [JsonProperty(PropertyName = "Lock Event To Player On First Entered")]
            public bool LockToPlayerFirstEntered { get; set; } = false;
        }

        public class UnlootedAnnouncementSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = false;

            [JsonProperty(PropertyName = "Notify Every X Minutes (Minimum 1)")]
            public float Interval { get; set; } = 3f;
        }

        public class RewardRunCommands
        {
            public RewardRunCommands Clone()
            {
                var copy = (RewardRunCommands)MemberwiseClone();
                copy.Commands = new(Commands);
                return copy;
            }

            [JsonProperty(PropertyName = "Commands", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Commands = new();

            [JsonProperty(PropertyName = "Run Commands For Owner Only")]
            public bool Owner = true;

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled;

            public bool Any() => Enabled && Commands.Exists(x => !string.IsNullOrWhiteSpace(x));

            public RewardRunCommands()
            {
                Commands.Add("inventory.giveto {userid} apple 1");
                Commands.Add("o.usergroup add {userid} specialgroup");
            }
        }

        public class DifficultyLevel
        {
            [JsonProperty(PropertyName = "Difficulty Name")]
            public string Difficulty = "Easy";

            [JsonProperty(PropertyName = "Difficulty Level")]
            public int Level;

            [JsonProperty(PropertyName = "Events")]
            public EventSettings Event = new();

            [JsonProperty(PropertyName = "Fireballs")]
            public FireballSettings Fireballs = new();

            [JsonProperty(PropertyName = "NPCs")]
            public NpcSettings NPC = new();

            [JsonProperty(PropertyName = "Missile Launcher")]
            public MissileLauncherSettings MissileLauncher = new();

            [JsonProperty(PropertyName = "Rewards")]
            public RewardSettings Rewards = new();

            [JsonProperty(PropertyName = "Treasure")]
            public TreasureSettings Treasure = new();

            public DifficultyLevel() { }

            public DifficultyLevel(int level, string name, int multiplier)
            {
                Level = level;
                Difficulty = name;
                Event.TreasureAmount *= multiplier;
                Event.MarkerName = $"Dangerous Treasures Event [{name}]";
                Event.MarkerColor = level switch { 0 => "#FF0000", 1 => "#FF00FF", _ => "#6C244C" };
                SetNpcSettings(level);
                foreach (LootItem ti in Treasure.Loot)
                {
                    ti.amount *= multiplier;
                    ti.amountMin *= multiplier;
                }
            }

            private void SetNpcSettings(int level)
            {
                float guns = Mathf.Min(100, 20 + (level * 10));
                float bows = Mathf.Min(100, 50 + (level * 25));
                NPC.Scientists.Accuracy.Set(guns, bows);
                NPC.Murderers.Health *= level + 1;
                NPC.Scientists.Health *= level + 1;
            }
        }

        private void ChkLevels(List<DifficultyLevel> levels)
        {
            HashSet<int> reserved = new(levels.Select(x => x.Level));

            HashSet<int> assigned = new();

            foreach (var options in levels)
            {
                if (assigned.Add(options.Level))
                    continue;

                int replacement = FindReplacement(reserved);
                options.Level = replacement;
                assigned.Add(replacement);
                reserved.Add(replacement);
            }
        }
        public static bool Compare(Vector3 left, Vector3 right) => (left - right).sqrMagnitude < 0.001f;
        private static int FindReplacement(HashSet<int> reserved)
        {
            int candidate = 0;

            while (reserved.Contains(candidate))
            {
                candidate++;
            }

            return candidate;
        }

        public class Configuration
        {
            [JsonProperty(PropertyName = "Difficulty Levels", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<DifficultyLevel> Levels = new()
            {
                new(0, "Easy", 1),
                new(1, "Medium", 2),
                new(2, "Hard", 3),
            };

            [JsonProperty(PropertyName = "Settings")]
            public PluginSettings Settings = new();

            [JsonProperty(PropertyName = "Countdown")]
            public CountdownSettings Countdown = new();

            [JsonProperty(PropertyName = "Event Messages")]
            public EventMessageSettings EventMessages = new();

            [JsonProperty(PropertyName = "GUIAnnouncements")]
            public GUIAnnouncementSettings GUIAnnouncement = new();

            [JsonProperty(PropertyName = "Monuments")]
            public MonumentSettings Monuments = new();

            [JsonProperty(PropertyName = "Newman Mode")]
            public NewmanModeSettings NewmanMode = new();

            [JsonProperty(PropertyName = "Ranked Ladder")]
            public RankedLadderSettings RankedLadder = new();

            [JsonProperty(PropertyName = "Rocket Opener")]
            public RocketOpenerSettings Rocket = new();

            [JsonProperty(PropertyName = "Skins")]
            public SkinSettings Skins = new();

            [JsonProperty(PropertyName = "Treasure")]
            public DayOfTheWeekSettings Treasure = new();

            [JsonProperty(PropertyName = "TruePVE")]
            public TruePVESettings TruePVE = new();

            [JsonProperty(PropertyName = "Unlock Time")]
            public UnlockSettings Unlock = new();

            [JsonProperty(PropertyName = "Unlooted Announcements")]
            public UnlootedAnnouncementSettings UnlootedAnnouncements = new();

            [JsonProperty(PropertyName = "Block paid and restricted content to comply with Facepunch TOS")]
            public bool BlockPaidContent = true;

            public DifficultyLevel GetLevel(int level)
            {
                foreach (var options in Levels)
                {
                    if (options.Level == level)
                    {
                        return options;
                    }
                }
                return null;
            }

            public DifficultyLevel GetLevelOrHighest(int level)
            {
                int current = -1;
                DifficultyLevel value = null;
                foreach (var options in Levels)
                {
                    if (options.Level > current)
                    {
                        current = options.Level;
                        value = options;
                    }
                    if (options.Level == level)
                    {
                        return options;
                    }
                }
                return value;
            }
        }

        private bool? previousRandomSkins = null;
        private bool? previousRandomWorkshopSkins = null;
        private float? previousNpcRange = null;
        private bool? previousLeaveDome = null;
        private bool? previousTargetOther = null;
        private bool? previousNpcsEnabled = null;
        private bool? previousKillUnderwaterNpcs = null;
        private List<LootItem> previousLootItems;
        private NpcSettingsMurderer previousMurdererSettings;
        private NpcSettingsScientist previousScientistSettings;
        private Dictionary<string, bool> previousEventBlacklistedMonuments;
        private Dictionary<string, bool> previousNpcBlacklistedMonuments;
        private RewardSettings previousRewardSettings;
        private EventSettings previousEventSettings;
        private FireballSettings previousFireSettings;
        private MissileLauncherSettings previousMissileSettings;

        protected void TryImport()
        {
            TryImport<List<LootItem>>(value => previousLootItems = value, "Treasure", "Loot");
            TryImport<Dictionary<string, bool>>(value => previousEventBlacklistedMonuments = value, "Monuments", "Blacklisted Monuments");
            TryImport<Dictionary<string, bool>>(value => previousNpcBlacklistedMonuments = value, "NPCs", "Blacklisted Monuments");
            TryImport<NpcSettingsMurderer>(value => { if (value != null) previousMurdererSettings = value; }, "NPCs", "Murderers");
            TryImport<NpcSettingsScientist>(value => { if (value != null) previousScientistSettings = value; }, "NPCs", "Scientists");
            TryImport<RewardSettings>(value => { if (value != null) previousRewardSettings = value; }, "Rewards");
            TryImport<EventSettings>(value => { if (value != null) previousEventSettings = value; }, "Events");
            TryImport<FireballSettings>(value => { if (value != null) previousFireSettings = value; }, "Fireballs");
            TryImport<MissileLauncherSettings>(value => { if (value != null) previousMissileSettings = value; }, "Missile Launcher");
            TryImport<bool>(value => previousLeaveDome = value, "NPCs", "Allow Npcs To Leave Dome When Attacking");
            TryImport<bool>(value => previousTargetOther = value, "NPCs", "Allow Npcs To Target Other Npcs");
            TryImport<bool>(value => previousKillUnderwaterNpcs = value, "NPCs", "Kill Underwater Npcs");
            TryImport<bool>(value => previousNpcsEnabled = value, "NPCs", "Enabled");
            TryImport<bool>(value => previousRandomSkins = value, "Treasure", "Use Random Skins");
            TryImport<bool>(value => previousRandomWorkshopSkins = value, "Treasure", "Include Workshop Skins");
            TryImport<float>(value => previousNpcRange = value, "NPCs", "Block Damage From Players Beyond X Distance (0 = disabled)");
            TryImport<string>(value => config.Settings.PlayerLimitPermission = value, "Events", "Permission To Ignore With Players Limit");
            TryImport<bool>(value => config.Settings.BlockAlphaLoot = value, "NPCs", "Block AlphaLoot Plugin");
            TryImport<bool>(value => config.Settings.BlockBetterLoot = value, "NPCs", "Block BetterLoot Plugin");
            TryImport<bool>(value => config.Settings.BlockNpcKits = value, "NPCs", "Block Npc Kits Plugin");
        }

        protected void TryImport<T>(Action<T> apply, params string[] path)
        {
            object obj = Config.Get(path);
            if (obj == null)
                return;

            T value;

            try
            {
                value = JsonConvert.DeserializeObject<T>(JsonConvert.SerializeObject(obj));
            }
            catch
            {
                Puts("Unable to import {0}, you can do so manually from your backup config.", string.Join(" ", path));
                return;
            }

            apply(value);
        }

        private string BackupConfigFilePath() => Utility.CleanPath(Manager.ConfigPath + "/" + "DangerousTreasures.json.bak");

        protected override void LoadConfig()
        {
            base.LoadConfig();
            bool importing = Config.Get("Difficulty Levels") == null && Config.Get("Events") != null;
            if (importing)
            {
                Config.Save(BackupConfigFilePath());
                Puts("Created backup of config file: DangerousTreasures.json.bak");
            }
            canSaveConfig = false;
            try
            {
                config = Config.ReadObject<Configuration>();
                config ??= new();
                if (importing) TryImport();
                ValidateConfig();
                canSaveConfig = true;
                ChkLevels(config.Levels);
                SaveConfig();
            }
            catch (Exception ex)
            {
                Puts(ex.ToString());
                LoadDefaultConfig();
            }
        }

        private void ValidateConfig()
        {
            if (previousEventBlacklistedMonuments != null)
            {
                config.Monuments.EventBlacklist = previousEventBlacklistedMonuments;
                previousEventBlacklistedMonuments = null;
            }
            if (previousNpcBlacklistedMonuments != null)
            {
                config.Monuments.NPCBlacklist = previousNpcBlacklistedMonuments;
                previousNpcBlacklistedMonuments = null;
            }
            if (config.Rocket.Speed > 0.1f) config.Rocket.Speed = 0.1f;
            if (config.Monuments.Chance < 0) config.Monuments.Chance = 0f;
            if (config.Monuments.Chance > 1f) config.Monuments.Chance /= 100f;
            if (config.Treasure.PercentLoss > 0) config.Treasure.PercentLoss /= 100m;
            var imports = new HashSet<string>();
            foreach (var options in config.Levels)
            {
                if (previousLootItems != null)
                {
                    imports.Add("Importing settings from your old config at " + BackupConfigFilePath());
                    options.Treasure.Loot.Clear();
                    foreach (var obj in previousLootItems)
                    {
                        var ti = obj.Clone();
                        ti.amount *= options.Level + 1;
                        ti.amountMin *= options.Level + 1;
                        options.Treasure.Loot.Add(ti);
                    }
                    imports.Add("Successfully imported loot.");
                }
                if (previousEventSettings != null)
                {
                    if (options.Level == 0) options.Event = previousEventSettings.Clone();
                    else options.Event = previousEventSettings.SelectiveClone(options);
                    imports.Add("Successfully imported Event settings.");
                }
                if (previousMissileSettings != null)
                {
                    options.MissileLauncher = previousMissileSettings.Clone();
                    imports.Add("Successfully imported Missile Launcher settings.");
                }
                if (previousFireSettings != null)
                {
                    options.Fireballs = previousFireSettings.Clone();
                    imports.Add("Successfully imported Fireball settings.");
                }
                if (previousRewardSettings != null)
                {
                    options.Rewards = previousRewardSettings.Clone();
                    imports.Add("Successfully imported Reward settings.");
                }
                if (previousRandomSkins.HasValue) options.Treasure.RandomSkins = previousRandomSkins.Value;
                if (previousRandomWorkshopSkins.HasValue) options.Treasure.RandomWorkshopSkins = previousRandomWorkshopSkins.Value;
                if (previousNpcRange.HasValue) options.NPC.Range = previousNpcRange.Value;
                if (previousNpcsEnabled.HasValue) options.NPC.Enabled = previousNpcsEnabled.Value;
                if (previousLeaveDome.HasValue) options.NPC.CanLeave = previousLeaveDome.Value;
                if (previousTargetOther.HasValue) options.NPC.TargetNpcs = previousTargetOther.Value;
                if (previousKillUnderwaterNpcs.HasValue) options.NPC.KillUnderwater = previousKillUnderwaterNpcs.Value;
                if (previousMurdererSettings != null)
                {
                    options.NPC.Murderers = previousMurdererSettings.Clone();
                    imports.Add("Successfully imported Murderer settings.");
                }
                if (previousScientistSettings != null)
                {
                    options.NPC.Scientists = previousScientistSettings.Clone();
                    imports.Add("Successfully imported Scientist settings.");
                }
                if (options.Event.Radius < 10f) options.Event.Radius = 10f;
                if (options.Event.Radius > 150f) options.Event.Radius = 150f;
                if (options.MissileLauncher.Distance < 1f) options.MissileLauncher.Distance = 15f;
                if (options.MissileLauncher.Distance > options.Event.Radius * 15) options.MissileLauncher.Distance = options.Event.Radius * 2;
                if (options.NPC.Murderers.Accuracy.GLOCK == 0f) options.NPC.Murderers.Accuracy.AK47ICE = options.NPC.Murderers.Accuracy.GLOCK = options.NPC.Murderers.Accuracy.HMLMG = 100f;
                if (options.NPC.Scientists.Accuracy.GLOCK == 0f) options.NPC.Scientists.Accuracy.AK47ICE = options.NPC.Scientists.Accuracy.GLOCK = options.NPC.Scientists.Accuracy.HMLMG = 20f;
                if (options.Event.AutoDrawDistance < 0f) options.Event.AutoDrawDistance = 0f;
                if (options.Event.AutoDrawDistance > ConVar.Server.worldsize) options.Event.AutoDrawDistance = ConVar.Server.worldsize;
                if (options.NPC.Murderers.SpawnAmount + options.NPC.Scientists.SpawnAmount < 1) options.NPC.Enabled = false;
                if (options.NPC.Murderers.SpawnAmount > 25) options.NPC.Murderers.SpawnAmount = 25;
                if (options.NPC.Scientists.SpawnAmount > 25) options.NPC.Scientists.SpawnAmount = 25;
            }

            if (config.UnlootedAnnouncements.Interval < 1f) config.UnlootedAnnouncements.Interval = 1f;
            if (config.GUIAnnouncement.TintColor.ToLower() == "black") config.GUIAnnouncement.TintColor = "grey";
            if (!string.IsNullOrEmpty(config.Settings.PermName) && !permission.PermissionExists(config.Settings.PermName)) permission.RegisterPermission(config.Settings.PermName, this);
            if (!string.IsNullOrEmpty(config.Settings.EventChatCommand)) cmd.AddChatCommand(config.Settings.EventChatCommand, this, cmdDangerousTreasures);
            if (!string.IsNullOrEmpty(config.Settings.DistanceChatCommand)) cmd.AddChatCommand(config.Settings.DistanceChatCommand, this, cmdTreasureHunter);
            if (!string.IsNullOrEmpty(config.Settings.EventConsoleCommand)) cmd.AddConsoleCommand(config.Settings.EventConsoleCommand, this, nameof(ccmdDangerousTreasures));

            if (!string.IsNullOrEmpty(config.RankedLadder.Permission))
            {
                if (!permission.PermissionExists(config.RankedLadder.Permission))
                    permission.RegisterPermission(config.RankedLadder.Permission, this);

                if (!string.IsNullOrEmpty(config.RankedLadder.Group))
                {
                    permission.CreateGroup(config.RankedLadder.Group, config.RankedLadder.Group, 0);
                    permission.GrantGroupPermission(config.RankedLadder.Group, config.RankedLadder.Permission, this);
                }
            }

            permission.RegisterPermission("dangeroustreasures.notitle", this);
            previousLootItems = null;
            previousMurdererSettings = null;
            previousScientistSettings = null;
            previousEventSettings = null;
            previousMissileSettings = null;
            previousFireSettings = null;
            previousRewardSettings = null;
            previousRandomSkins = previousRandomWorkshopSkins = previousNpcsEnabled = previousLeaveDome = previousTargetOther = previousKillUnderwaterNpcs = null;
            previousNpcRange = null;

            if (imports.Count > 0) Puts("\n" + string.Join("\n", imports));
        }

        private List<LootItem> ChestLoot(int level)
        {
            var options = config.GetLevel(level);
            if (options == null)
            {
                return null;
            }

            return ChestLoot(options);
        }

        private List<LootItem> ChestLoot(DifficultyLevel options)
        {
            if (config.Treasure.UseDOWL)
            {
                switch (DateTime.Now.DayOfWeek)
                {
                    case DayOfWeek.Monday: return config.Treasure.DOWL_Monday;
                    case DayOfWeek.Tuesday: return config.Treasure.DOWL_Tuesday;
                    case DayOfWeek.Wednesday: return config.Treasure.DOWL_Wednesday;
                    case DayOfWeek.Thursday: return config.Treasure.DOWL_Thursday;
                    case DayOfWeek.Friday: return config.Treasure.DOWL_Friday;
                    case DayOfWeek.Saturday: return config.Treasure.DOWL_Saturday;
                    case DayOfWeek.Sunday: return config.Treasure.DOWL_Sunday;
                }
            }

            return options.Treasure.Loot;
        }

        protected void InitializeArmorSlots()
        {
            bool ret = false;
            foreach (var options in config.Levels)
            {
                ret |= InitializeArmorSlots(options.Treasure.Loot);
            }

            ret |= InitializeArmorSlots(config.Treasure.DOWL_Monday);
            ret |= InitializeArmorSlots(config.Treasure.DOWL_Tuesday);
            ret |= InitializeArmorSlots(config.Treasure.DOWL_Wednesday);
            ret |= InitializeArmorSlots(config.Treasure.DOWL_Thursday);
            ret |= InitializeArmorSlots(config.Treasure.DOWL_Friday);
            ret |= InitializeArmorSlots(config.Treasure.DOWL_Saturday);
            ret |= InitializeArmorSlots(config.Treasure.DOWL_Sunday);

            if (ret)
            {
                SaveConfig();
            }
        }

        protected bool InitializeArmorSlots(List<LootItem> items)
        {
            if (items == null)
                return false;
            bool ret = false;
            foreach (var ti in items)
            {
                ret |= ti.InitializeArmorSlots();
            }
            return ret;
        }

        private bool canSaveConfig = true;

        protected override void SaveConfig()
        {
            if (canSaveConfig)
            {
                Config.WriteObject(config);
            }
        }

        protected override void LoadDefaultConfig() => config = new();

        #endregion
    }
}

namespace Oxide.Plugins.DangerousTreasuresExtensionMethods
{
    public static class ExtensionMethods
    {
        public static string[] ToStringArray(this string[] args) => args;
        public static string[] ToStringArray(this StringView[] args) { if (args == null || args.Length == 0) return Array.Empty<string>(); string[] array = new string[args.Length]; for (int i = 0; i < args.Length; i++) array[i] = args[i].ToString(); return array; }
        public static PooledList<Item> GetAllItems(this BasePlayer a) { var b = Facepunch.Pool.Get<PooledList<Item>>(); if (a != null && a.inventory != null) { a.inventory.GetAllItems(b); } return b; }
        public static void SafelyRemove(this ItemContainer inv, string shortname) { if (inv == null) return; Item item = inv.FindItemByItemName(shortname); if (item == null) return; item.RemoveFromContainer(); item.Remove(); }
        public static void SafelyStrip(this PlayerInventory inv) { if (inv == null) return; inv.containerMain?.Clear(); inv.containerWear?.Clear(); inv.containerBelt?.Clear(); ItemManager.DoRemoves(); }
        public static bool All<T>(this IEnumerable<T> a, Func<T, bool> b) { foreach (T c in a) { if (!b(c)) { return false; } } return true; }
        public static T ElementAt<T>(this IEnumerable<T> a, int b) { using (var c = a.GetEnumerator()) { while (c.MoveNext()) { if (b == 0) { return c.Current; } b--; } } return default(T); }
        public static bool Exists<T>(this IEnumerable<T> a, Func<T, bool> b = null) { using (var c = a.GetEnumerator()) { while (c.MoveNext()) { if (b == null || b(c.Current)) { return true; } } } return false; }
        public static T FirstOrDefault<T>(this IEnumerable<T> a, Func<T, bool> b = null) { using (var c = a.GetEnumerator()) { while (c.MoveNext()) { if (b == null || b(c.Current)) { return c.Current; } } } return default; }
        public static IEnumerable<V> Select<T, V>(this IEnumerable<T> a, Func<T, V> b) { var c = new List<V>(); using (var d = a.GetEnumerator()) { while (d.MoveNext()) { c.Add(b(d.Current)); } } return c; }
        public static string[] Skip(this string[] a, int b) { if (a.Length == 0) { return Array.Empty<string>(); } string[] c = new string[a.Length - b]; int n = 0; for (int i = 0; i < a.Length; i++) { if (i < b) continue; c[n] = a[i]; n++; } return c; }
        public static List<T> Take<T>(this IList<T> a, int b) { var c = new List<T>(); for (int i = 0; i < a.Count; i++) { if (c.Count == b) { break; } c.Add(a[i]); } return c; }
        public static Dictionary<T, V> ToDictionary<S, T, V>(this IEnumerable<S> a, Func<S, T> b, Func<S, V> c) { var d = new Dictionary<T, V>(); using (var e = a.GetEnumerator()) { while (e.MoveNext()) { d[b(e.Current)] = c(e.Current); } } return d; }
        public static List<T> ToList<T>(this IEnumerable<T> a) { var b = new List<T>(); if (a == null) { return b; } using (var c = a.GetEnumerator()) { while (c.MoveNext()) { b.Add(c.Current); } } return b; }
        public static List<T> Where<T>(this IEnumerable<T> a, Func<T, bool> b) { var c = new List<T>(); using (var d = a.GetEnumerator()) { while (d.MoveNext()) { if (b(d.Current)) { c.Add(d.Current); } } } return c; }
        public static List<T> OfType<T>(this IEnumerable<BaseNetworkable> a) where T : BaseEntity { var b = new List<T>(); using (var c = a.GetEnumerator()) { while (c.MoveNext()) { if (c.Current is T) { b.Add(c.Current as T); } } } return b; }
        public static int Count<T>(this IEnumerable<T> a, Func<T, bool> b = null) { int c = 0; foreach (T d in a) { if (b == null || b(d)) { c++; } } return c; }
        public static int Sum<T>(this IEnumerable<T> a, Func<T, int> b) { int c = 0; foreach (T d in a) { c = checked(c + b(d)); } return c; }
        public static string ObjectName(this Collider collider) { try { return collider.name ?? string.Empty; } catch { return string.Empty; } }
        public static bool IsReallyConnected(this BasePlayer a) { return a.IsReallyValid() && a.net.connection != null; }
        public static bool IsKilled(this BaseNetworkable a) => a == null || a.IsDestroyed || !a.IsFullySpawned();
        public static bool IsNull<T>(this T a) where T : class { return a == null; }
        public static bool IsNull(this BasePlayer a) => a == null || a.IsDestroyed;
        public static bool IsNullOrEmpty<T>(this IReadOnlyCollection<T> c) => c == null || c.Count == 0;
        public static bool IsReallyValid(this BaseNetworkable a) { return !(a == null || a.IsDestroyed || !a.IsFullySpawned() || a.net == null); }
        public static void SafelyKill(this BaseNetworkable a) { if (a.IsKilled()) { return; } a.Kill(BaseNetworkable.DestroyMode.None); }
        public static bool CanCall(this Plugin o) { return o != null && o.IsLoaded; }
        public static bool IsHuman(this BasePlayer a) { return !(a.IsNpc || !a.userID.IsSteamId()); }
        public static float Distance(this Vector3 a, Vector3 b) => (a - b).magnitude;
    }
}
