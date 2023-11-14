using System;
using System.Collections.Generic;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Oxide.Plugins.DangerousTreasuresExtensionMethods;
using Rust;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using System.Text;
using Rust.Ai;
using Facepunch;
using System.Globalization;
using Oxide.Core.Libraries.Covalence;

namespace Oxide.Plugins
{
    [Info("Dangerous Treasures", "nivex", "2.3.2")]
    [Description("Event with treasure chests.")]
    class DangerousTreasures : RustPlugin
    {
        [PluginReference] Plugin LustyMap, ZoneManager, Economics, ServerRewards, Map, GUIAnnouncements, MarkerManager, Kits, Duelist, RaidableBases, AbandonedBases, Notify, AdvancedAlerts;

        private new const string Name = "Dangerous Treasures";
        private static DangerousTreasures Instance;
        private bool wipeChestsSeed;
        private StoredData data = new StoredData(); 
        private List<int> BlockedLayers = new List<int> { (int)Layer.Water, (int)Layer.Construction, (int)Layer.Trigger, (int)Layer.Prevent_Building, (int)Layer.Deployed, (int)Layer.Tree, (int)Layer.Clutter };
        private Dictionary<ulong, HumanoidBrain> HumanoidBrains = new Dictionary<ulong, HumanoidBrain>();
        private Dictionary<string, MonInfo> allowedMonuments = new Dictionary<string, MonInfo>();
        private Dictionary<string, MonInfo> monuments = new Dictionary<string, MonInfo>();
        private Dictionary<Vector3, ZoneInfo> managedZones = new Dictionary<Vector3, ZoneInfo>();
        private Dictionary<uint, MapInfo> mapMarkers = new Dictionary<uint, MapInfo>();
        private Dictionary<uint, string> lustyMarkers = new Dictionary<uint, string>();
        private Dictionary<uint, TreasureChest> treasureChests = new Dictionary<uint, TreasureChest>();
        private Dictionary<uint, string> looters = new Dictionary<uint, string>();
        private Dictionary<string, ItemDefinition> _definitions = new Dictionary<string, ItemDefinition>();
        private Dictionary<string, SkinInfo> Skins = new Dictionary<string, SkinInfo>(); 
        private List<ulong> newmanProtections = new List<ulong>();
        private List<ulong> indestructibleWarnings = new List<ulong>(); // indestructible messages limited to once every 10 seconds
        private List<ulong> drawGrants = new List<ulong>(); // limit draw to once every 15 seconds by default
        private List<int> obstructionLayers = new List<int> { Layers.Mask.Player_Server, Layers.Mask.Construction, Layers.Mask.Deployed };
        private List<string> _blockedColliders = new List<string> { "powerline_", "invisible_", "TopCol", "train", "swamp_", "floating_" };
        private List<string> underground = new List<string> { "Cave", "Sewer Branch", "Military Tunnel", "Underwater Lab", "Train Tunnel" };
        private List<Vector3> _gridPositions = new List<Vector3>();
        private const int TARGET_MASK = 8454145; 
        private const int targetMask = Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Default;
        private const int visibleMask = Layers.Mask.Deployed | Layers.Mask.Construction | targetMask;
        private const int obstructionLayer = Layers.Mask.Player_Server | Layers.Mask.Construction | Layers.Mask.Deployed;
        private const int heightLayer = TARGET_MASK | Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.Clutter;
        private StringBuilder _sb = new StringBuilder();
        private Vector3 sd_customPos;

        public class ZoneInfo
        {
            public Vector3 Position;
            public Vector3 Size;
            public float Distance;
            public OBB OBB;
        }

        private class MonInfo
        {
            public Vector3 Position;
            public float Radius;
            public MonInfo (Vector3 pos, float radius)
            {
                Position = pos;
                Radius = radius;
            }
        }
        
        public class SkinInfo
        {
            public List<ulong> skins = new List<ulong>();
            public List<ulong> workshopSkins = new List<ulong>();
            public List<ulong> importedSkins = new List<ulong>();
            public List<ulong> allSkins = new List<ulong>();
        }

        private class MapInfo
        {
            public string Url;
            public string IconName;
            public Vector3 Position;

            public MapInfo() { }
        }

        private class PlayerInfo
        {
            public int StolenChestsTotal;
            public int StolenChestsSeed;
            public PlayerInfo() { }
        }

        private class StoredData
        {            
            public readonly Dictionary<string, PlayerInfo> Players = new Dictionary<string, PlayerInfo>();
            public List<uint> TemporaryUID = new List<uint>();
            public List<uint> Markers = new List<uint>();
            public double SecondsUntilEvent = double.MinValue;
            public string CustomPosition; 
            public int TotalEvents = 0;
            public StoredData() { }            
        }

        public class HumanoidBrain : ScientistBrain
        {
            internal enum AttackType
            {
                BaseProjectile,
                FlameThrower,
                Melee,
                Water,
                None
            }

            internal ScientistNPC npc;
            private AttackEntity _attackEntity;
            private FlameThrower flameThrower;
            private LiquidWeapon liquidWeapon;
            private BaseMelee baseMelee;
            private BasePlayer AttackTarget;
            internal NpcSettings Settings;
            internal TreasureChest tc;
            private List<Vector3> positions;
            internal Vector3 DestinationOverride;
            internal bool isMurderer;
            internal ulong uid;
            private float lastWarpTime;
            internal float softLimitSenseRange;
            private float nextAttackTime;
            private float attackRange;
            private float attackCooldown;
            internal AttackType attackType = AttackType.None;
            private BaseNavigator.NavigationSpeed CurrentSpeed = BaseNavigator.NavigationSpeed.Normal;

            internal Vector3 AttackPosition => AttackTarget.ServerPosition;

            internal Vector3 ServerPosition => npc.ServerPosition;

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

                switch (_attackEntity.ShortPrefabName)
                {
                    case "double_shotgun.entity":
                    case "shotgun_pump.entity":
                    case "shotgun_waterpipe.entity":
                    case "spas12.entity":
                        SetAttackRestrictions(AttackType.BaseProjectile, 30f, 0f, 30f);
                        break;
                    case "ak47u.entity":
                    case "bolt_rifle.entity":
                    case "l96.entity":
                    case "lr300.entity":
                    case "m249.entity":
                    case "m39.entity":
                    case "m92.entity":
                    case "mp5.entity":
                    case "nailgun.entity":
                    case "pistol_eoka.entity":
                    case "pistol_revolver.entity":
                    case "pistol_semiauto.entity":
                    case "python.entity":
                    case "semi_auto_rifle.entity":
                    case "thompson.entity":
                    case "smg.entity":
                        SetAttackRestrictions(AttackType.BaseProjectile, 300f, 0f, 150f);
                        break;
                    case "snowballgun.entity":
                        SetAttackRestrictions(AttackType.BaseProjectile, 15f, 0.1f, 15f);
                        break;
                    case "chainsaw.entity":
                    case "jackhammer.entity":
                        baseMelee = _attackEntity as BaseMelee;
                        SetAttackRestrictions(AttackType.Melee, 2.5f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 2f);
                        break;
                    case "axe_salvaged.entity":
                    case "bone_club.entity":
                    case "butcherknife.entity":
                    case "candy_cane.entity":
                    case "hammer_salvaged.entity":
                    case "hatchet.entity":
                    case "icepick_salvaged.entity":
                    case "knife.combat.entity":
                    case "knife_bone.entity":
                    case "longsword.entity":
                    case "mace.entity":
                    case "machete.weapon":
                    case "pickaxe.entity":
                    case "pitchfork.entity":
                    case "salvaged_cleaver.entity":
                    case "salvaged_sword.entity":
                    case "sickle.entity":
                    case "spear_stone.entity":
                    case "spear_wooden.entity":
                    case "stone_pickaxe.entity":
                    case "stonehatchet.entity":
                        baseMelee = _attackEntity as BaseMelee;
                        SetAttackRestrictions(AttackType.Melee, 2.5f, _attackEntity.animationDelay + _attackEntity.deployDelay);
                        break;
                    case "flamethrower.entity":
                        flameThrower = _attackEntity as FlameThrower;
                        SetAttackRestrictions(AttackType.FlameThrower, 10f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 2f);
                        break;
                    case "compound_bow.entity":
                    case "crossbow.entity":
                    case "speargun.entity":
                        SetAttackRestrictions(AttackType.BaseProjectile, 200f, (_attackEntity.animationDelay + _attackEntity.deployDelay) * 1.25f, 150f);
                        break;
                    case "watergun.entity":
                    case "waterpistol.entity":
                        liquidWeapon = _attackEntity as LiquidWeapon;
                        liquidWeapon.AutoPump = true;
                        SetAttackRestrictions(AttackType.Water, 10f, 2f);
                        break;
                    default: _attackEntity = null; break;
                }
            }

            private void SetAttackRestrictions(AttackType attackType, float attackRange, float attackCooldown, float effectiveRange = 0f)
            {
                if (effectiveRange != 0f)
                {
                    _attackEntity.effectiveRange = effectiveRange;
                }

                this.attackType = attackType;
                this.attackRange = attackRange;
                this.attackCooldown = attackCooldown;
            }

            public bool ValidTarget
            {
                get
                {
                    if (AttackTarget.IsKilled() || ShouldForgetTarget(AttackTarget))
                    {
                        return false;
                    }

                    return true;
                }
            }

            public override void OnDestroy()
            {
                if (!Rust.Application.isQuitting)
                {
                    BaseEntity.Query.Server.RemoveBrain(GetEntity());
                    Instance?.HumanoidBrains?.Remove(uid);
                    LeaveGroup();
                }

                CancelInvoke();
            }

            public override void InitializeAI()
            {
                base.InitializeAI();
                base.ForceSetAge(0f);

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

                CanUseHealingItems = true;
            }

            public override void AddStates()
            {
                base.AddStates();

                states[AIState.Attack] = new AttackState(this);
            }

            public class AttackState : BaseAttackState
            {
                private new HumanoidBrain brain;
                private global::HumanNPC npc;

                private IAIAttack attack => brain.Senses.ownerAttack;

                public AttackState(HumanoidBrain humanoidBrain)
                {
                    base.brain = brain = humanoidBrain;
                    base.AgrresiveState = true;
                    npc = brain.GetBrainBaseEntity() as global::HumanNPC;
                }

                public override void StateEnter(BaseAIBrain _brain, BaseEntity _entity)
                {
                    if (brain.ValidTarget)
                    {
                        if (InAttackRange())
                        {
                            StartAttacking();
                        }
                        else
                        {
                            StopAttacking();
                        }
                        if (brain.Navigator.CanUseNavMesh)
                        {
                            brain.Navigator.SetDestination(brain.DestinationOverride, BaseNavigator.NavigationSpeed.Fast, 0f, 0f);
                        }
                    }
                }

                public override void StateLeave(BaseAIBrain _brain, BaseEntity _entity)
                {
                    StopAttacking();
                }

                private void StopAttacking()
                {
                    if (attack != null)
                    {
                        attack.StopAttacking();
                        brain.Navigator.ClearFacingDirectionOverride();
                    }
                }

                public override StateStatus StateThink(float delta, BaseAIBrain _brain, BaseEntity _entity)
                {
                    if (attack == null)
                    {
                        return StateStatus.Error;
                    }
                    if (!brain.ValidTarget)
                    {
                        StopAttacking();

                        return StateStatus.Finished;
                    }
                    if (brain.Senses.ignoreSafeZonePlayers && brain.AttackTarget.InSafeZone())
                    {
                        return StateStatus.Error;
                    }
                    if (brain.Navigator.CanUseNavMesh && !brain.Navigator.SetDestination(brain.DestinationOverride, BaseNavigator.NavigationSpeed.Fast, 0f, 0f))
                    {
                        return StateStatus.Error;
                    }
                    if (!brain.CanLeave(brain.AttackPosition) || !brain.CanShoot())
                    {
                        brain.Forget();

                        StopAttacking();

                        return StateStatus.Finished;
                    }
                    if (InAttackRange())
                    {
                        StartAttacking();
                    }
                    else
                    {
                        StopAttacking();
                    }
                    return StateStatus.Running;
                }

                private bool InAttackRange()
                {
                    return attack.CanAttack(brain.AttackTarget) && brain.IsInAttackRange() && brain.CanSeeTarget(brain.AttackTarget);
                }

                private void StartAttacking()
                {
                    brain.SetAimDirection();

                    if (!brain.CanShoot() || brain.IsAttackOnCooldown())
                    {
                        return;
                    }

                    if (brain.attackType == AttackType.BaseProjectile)
                    {
                        npc.ShotTest(brain.AttackPosition.Distance(brain.ServerPosition));
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
            }

            private bool init;

            public void Init()
            {
                if (init) return;
                init = true;
                lastWarpTime = Time.time;
                npc.spawnPos = tc.containerPos;
                npc.AdditionalLosBlockingLayer = visibleMask;

                IdentifyWeapon();

                SetupNavigator(GetEntity(), GetComponent<BaseNavigator>(), tc.Radius);
            }

            private void Converge()
            {
                foreach (var brain in Instance.HumanoidBrains.Values)
                {
                    if (brain != this && brain.attackType == attackType && brain.CanConverge(npc) && CanLeave(AttackPosition))
                    {
                        brain.SetTarget(AttackTarget, false);
                        brain.TryToAttack(AttackTarget);
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
                TargetLostRange = SenseRange * 1.25f;
                AttackTarget = null;

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
                navigator.Agent.agentTypeID = -1372625422;
                navigator.MaxWaterDepth = 3f;

                if (navigator.CanUseNavMesh)
                {
                    navigator.Init(owner, navigator.Agent);
                }
            }

            private void SetAimDirection()
            {
                Navigator.SetFacingDirectionEntity(AttackTarget);
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

                if (Navigator.Agent == null || !Navigator.CanUseNavMesh || !Navigator.SetDestination(destination, CurrentSpeed))
                {
                    Navigator.Destination = destination;
                    npc.finalDestination = destination;
                }
            }

            public void SetTarget(BasePlayer player, bool converge = true)
            {
                if (npc.IsKilled())
                {
                    Destroy(this);
                    return;
                }

                if (AttackTarget == player || player.IsKilled())
                {
                    return;
                }

                Senses.Memory.SetKnown(player, npc, null);
                npc.lastAttacker = player;
                AttackTarget = player;

                if (!IsInSenseRange(player.transform.position))
                {
                    SenseRange = ListenRange = (isMurderer ? Settings.Murderers.AggressionRange : Settings.Scientists.AggressionRange) + player.transform.position.Distance(ServerPosition);
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
                if (attacker.IsNull())
                {
                    attacker = GetBestTarget();
                }

                if (attacker.IsNull())
                {
                    return;
                }

                if (ShouldForgetTarget(attacker))
                {
                    Forget();

                    return;
                }

                SetTarget(attacker);

                if (!CanSeeTarget(attacker))
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
                this.positions = positions;

                InvokeRepeating(TryToRoam, 0f, 7.5f);
                InvokeRepeating(TryToAttack, 1f, 1f);
            }

            private void TryToRoam()
            {
                if (Settings.KillUnderwater && npc.IsSwimming())
                {
                    npc.SafelyKill();
                    Destroy(this);
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
                RaycastHit hit;
                Physics.Raycast(npc.eyes.BodyRay(), out hit, 10f, 1218652417);
                List<DamageTypeEntry> damage = new List<DamageTypeEntry>();
                WaterBall.DoSplash(hit.point, 2f, ItemManager.FindItemDefinition("water"), 10);
                DamageUtil.RadiusDamage(npc, liquidWeapon.LookupPrefab(), hit.point, 0.15f, 0.15f, damage, 131072, true);
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
                HitInfo hitInfo = new HitInfo
                {
                    damageTypes = new DamageTypeList(),
                    DidHit = true,
                    Initiator = npc,
                    HitEntity = AttackTarget,
                    HitPositionWorld = position,
                    HitPositionLocal = AttackTarget.transform.InverseTransformPoint(position),
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
                if (ValidTarget && !ShouldForgetTarget(AttackTarget)) return false;
                if (other.IsKilled() || other.IsDead()) return false;
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

                if (InRange(ServerPosition, target.ServerPosition, 10f) || Senses.Memory.IsLOS(target))
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
                    if (player.IsNull() || player.health <= 0f || player.IsDead() || player.limitNetworking) continue;
                    if (!player.IsHuman() && !config.NPC.TargetNpcs) continue;
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

            private bool IsInThrowRange()
            {
                return InRange(ServerPosition, AttackPosition, attackRange);
            }

            private bool ShouldForgetTarget(BasePlayer target)
            {
                return target.health <= 0f || target.IsDead() || target.limitNetworking || !IsInTargetRange(target.transform.position);
            }
        }

        private class GuidanceSystem : FacepunchBehaviour
        {
            private TimedExplosive missile;
            private ServerProjectile projectile;
            private BaseEntity target;
            private Vector3 launchPos; 
            private List<ulong> newmans = new List<ulong>();

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
                missile.timerAmountMin = config.MissileLauncher.Lifetime;
                missile.timerAmountMax = config.MissileLauncher.Lifetime;

                missile.damageTypes = new List<DamageTypeEntry>(); // no damage
            }

            public void SetTarget(BaseEntity target)
            {
                this.target = target;
            }

            public void Launch(float targettingTime)
            {
                missile.Spawn();

                Instance.timer.Once(targettingTime, () =>
                {
                    if (missile.IsKilled())
                        return;

                    var list = new List<BasePlayer>();
                    var players = FindEntitiesOfType<BasePlayer>(launchPos, config.Event.Radius + config.MissileLauncher.Distance, Layers.Mask.Player_Server);

                    for (int i = 0; i < players.Count; i++)
                    {
                        var player = players[i];

                        if (player.IsKilled() || !player.IsHuman() || !player.CanInteract())
                            continue;

                        if (config.MissileLauncher.IgnoreFlying && player.IsFlying)
                            continue;

                        if (newmans.Contains(player.userID) || Instance.newmanProtections.Contains(player.userID))
                            continue;

                        list.Add(player); // acquire a player target 
                    }

                    if (list.Count > 0)
                    {
                        target = list.GetRandom(); // pick a random player
                    }
                    else if (!config.MissileLauncher.TargetChest)
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
                CancelInvoke();
                Destroy(this);
            }
        }

        public class TreasureChest : FacepunchBehaviour
        {
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
            internal uint uid;

            private Dictionary<string, List<string>> npcKits = Pool.Get<Dictionary<string, List<string>>>();
            private Dictionary<ulong, float> fireticks = Pool.Get<Dictionary<ulong, float>>();
            private List<FireBall> fireballs = Pool.GetList<FireBall>();
            private List<ulong> newmans = Pool.GetList<ulong>();
            private List<ulong> traitors = Pool.GetList<ulong>();
            private List<ulong> protects = Pool.GetList<ulong>();
            private List<ulong> players = Pool.GetList<ulong>();
            private List<TimedExplosive> missiles = Pool.GetList<TimedExplosive>();
            private List<int> times = Pool.GetList<int>();
            private List<SphereEntity> spheres = Pool.GetList<SphereEntity>();
            private List<Vector3> missilePositions = Pool.GetList<Vector3>();
            private List<Vector3> firePositions = Pool.GetList<Vector3>();
            public List<ScientistNPC> npcs = Pool.GetList<ScientistNPC>();
            private Timer destruct, unlock, countdown, announcement;
            private MapMarkerExplosion explosionMarker;
            private MapMarkerGenericRadius genericMarker;
            private VendingMachineMapMarker vendingMarker;

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

            private void Free()
            {
                ResetToPool(fireballs);
                ResetToPool(newmans);
                ResetToPool(traitors);
                ResetToPool(protects);
                ResetToPool(missiles);
                ResetToPool(times);
                ResetToPool(spheres);
                ResetToPool(missilePositions);
                ResetToPool(firePositions);
                ResetToPool(npcKits);

                destruct?.Destroy();
                unlock?.Destroy();
                countdown?.Destroy();
                announcement?.Destroy();
            }

            private void ResetToPool<T>(ICollection<T> collection)
            {
                collection.Clear();

                Pool.Free(ref collection);
            }

            private class NewmanTracker : FacepunchBehaviour
            {
                BasePlayer player;
                TreasureChest chest;

                void Awake()
                {
                    player = GetComponent<BasePlayer>();
                }

                public void Assign(TreasureChest chest)
                {
                    this.chest = chest;
                    InvokeRepeating(Track, 1f, 0.1f);
                }

                void Track()
                {
                    if (!chest || chest.started || player.IsKilled() || !player.IsConnected || !chest.players.Contains(player.userID))
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
                        if (player.inventory.AllItems().Sum(item => player.IsHostileItem(item) ? 1 : 0) == 0)
                        {
                            if (config.NewmanMode.Aura && !chest.newmans.Contains(player.userID) && !chest.traitors.Contains(player.userID))
                            {
                                Message(player, "Newman Enter");
                                chest.newmans.Add(player.userID);
                            }

                            if (config.NewmanMode.Harm && !Instance.newmanProtections.Contains(player.userID) && !chest.protects.Contains(player.net.ID) && !chest.traitors.Contains(player.userID))
                            {
                                Message(player, "Newman Protect");
                                Instance.newmanProtections.Add(player.userID);
                                chest.protects.Add(player.net.ID);
                            }

                            if (!chest.traitors.Contains(player.userID))
                            {
                                return;
                            }
                        }

                        if (chest.newmans.Remove(player.userID))
                        {
                            Message(player, config.Fireballs.Enabled ? "Newman Traitor Burn" : "Newman Traitor");

                            if (!chest.traitors.Contains(player.userID))
                                chest.traitors.Add(player.userID);

                            Instance.newmanProtections.Remove(player.userID);
                            chest.protects.Remove(player.net.ID);
                        }
                    }

                    if (!config.Fireballs.Enabled || player.IsFlying)
                    {
                        return;
                    }

                    var stamp = Time.realtimeSinceStartup;

                    if (!chest.fireticks.ContainsKey(player.userID))
                    {
                        chest.fireticks[player.userID] = stamp + config.Fireballs.SecondsBeforeTick;
                    }

                    if (chest.fireticks[player.userID] - stamp <= 0)
                    {
                        chest.fireticks[player.userID] = stamp + config.Fireballs.SecondsBeforeTick;
                        chest.SpawnFire(player.transform.position);
                    }
                }

                void OnDestroy()
                {
                    CancelInvoke(Track);
                    Destroy(this);
                }
            }

            public void Kill(bool isUnloading)
            {
                IsUnloading = isUnloading;
                if (killed) return;

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
                killed = true;
                Interface.CallHook("OnDangerousEventEnded", containerPos);
            }

            public bool HasRustMarker
            {
                get
                {
                    return explosionMarker != null || vendingMarker != null;
                }
            }

            public static bool HasNPC(ulong userID)
            {
                return Instance.HumanoidBrains.ContainsKey(userID);
            }

            public static TreasureChest Get(Vector3 target)
            {
                foreach (var x in Instance.treasureChests.Values)
                {
                    if (InRange2D(x.containerPos, target, x.Radius))
                    {
                        return x;
                    }
                }

                return null;
            }

            public void Awaken()
            {
                var collider = gameObject.GetComponent<SphereCollider>() ?? gameObject.AddComponent<SphereCollider>();
                collider.center = Vector3.zero;
                collider.radius = Radius;
                collider.isTrigger = true;
                collider.enabled = true;

                requireAllNpcsDie = config.Unlock.RequireAllNpcsDie;
                whenNpcsDie = config.Unlock.WhenNpcsDie;

                if (config.Event.Spheres && config.Event.SphereAmount > 0)
                {
                    for (int i = 0; i < config.Event.SphereAmount; i++)
                    {
                        var sphere = GameManager.server.CreateEntity(StringPool.Get(3211242734), containerPos) as SphereEntity;

                        if (sphere == null)
                        {
                            Puts(_("Invalid Constant", null, 3211242734));
                            config.Event.Spheres = false;
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

                        gs.SetTarget(container);
                        gs.Launch(0.1f);
                    }
                }

                if (config.Fireballs.Enabled)
                {
                    firePositions = GetRandomPositions(containerPos, Radius, 25, containerPos.y + 25f);

                    if (firePositions.Count > 0)
                        InvokeRepeating(SpawnFire, 0.1f, config.Fireballs.SecondsBeforeTick);
                }

                if (config.MissileLauncher.Enabled)
                {
                    missilePositions = GetRandomPositions(containerPos, Radius, 25, 1);

                    if (missilePositions.Count > 0)
                    {
                        InvokeRepeating(LaunchMissile, 0.1f, config.MissileLauncher.Frequency);
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
                SetupNpcKits();
            }

            public void SpawnLoot(StorageContainer container, List<LootItem> treasure)
            {
                if (container.IsKilled() || treasure == null || treasure.Count == 0)
                {
                    return;
                }

                var loot = treasure.ToList();
                int j = 0;

                container.inventory.Clear();
                container.inventory.capacity = Math.Min(config.Event.TreasureAmount, loot.Count);

                while (j++ < container.inventory.capacity && loot.Count > 0)
                {
                    var lootItem = loot.GetRandom();
                    var definition = ItemManager.FindItemDefinition(lootItem.shortname);

                    loot.Remove(lootItem);

                    if (UnityEngine.Random.value > lootItem.probability)
                    {
                        continue;
                    }

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

                    if (definition.stackable == 1 || (definition.condition.enabled && definition.condition.max > 0f))
                    {
                        amount = 1;
                    }

                    ulong skin = lootItem.skins.Count > 0 ? lootItem.skins.GetRandom() : lootItem.skin;
                    Item item = ItemManager.CreateByName(definition.shortname, amount, skin);

                    if (item.info.stackable > 1 && !item.hasCondition)
                    {
                        item.amount = GetPercentIncreasedAmount(amount);
                    }

                    if (config.Treasure.RandomSkins && skin == 0)
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

                    item.MarkDirty();

                    if (!item.MoveToContainer(container.inventory, -1, true))
                    {
                        item.Remove(0.1f);
                    }
                }
            }

            private Dictionary<string, ulong> skinIds { get; set; } = new Dictionary<string, ulong>();

            private static bool IsBlacklistedSkin(ItemDefinition def, int num)
            {
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

                        if ((def.shortname == "box.wooden.large" && config.Skins.RandomWorkshopSkins) || (def.shortname != "box.wooden.large" && config.Treasure.RandomWorkshopSkins))
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

            public static SkinInfo GetItemSkins(ItemDefinition def)
            {
                SkinInfo si;
                if (!Instance.Skins.TryGetValue(def.shortname, out si))
                {
                    Instance.Skins[def.shortname] = si = new SkinInfo();

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

            void OnTriggerEnter(Collider col)
            {
                if (started)
                    return;

                var player = col.ToBaseEntity() as BasePlayer;

                if (!player || !player.IsHuman())
                    return;

                Interface.CallHook("OnPlayerEnteredDangerousEvent", player, containerPos, config.TruePVE.AllowPVPAtEvents);

                if (players.Contains(player.userID))
                    return;

                if (config.EventMessages.FirstEntered && !firstEntered && !player.IsFlying)
                {
                    firstEntered = true;
                    foreach (var target in BasePlayer.activePlayerList)
                    {
                        Message(target, "OnFirstPlayerEntered", player.displayName, FormatGridReference(containerPos));
                    }
                }

                string key;
                if (config.EventMessages.NoobWarning)
                {
                    Message(player, whenNpcsDie && npcSpawnedAmount > 0 ? "Npc Event" : requireAllNpcsDie && npcSpawnedAmount > 0 ? "Timed Npc Event" : "Timed Event");
                }
                else if (config.EventMessages.Entered)
                {
                    Message(player, config.Fireballs.Enabled ? "Dangerous Zone Protected" : "Dangerous Zone Unprotected");
                }

                var tracker = player.gameObject.GetComponent<NewmanTracker>() ?? player.gameObject.AddComponent<NewmanTracker>();

                tracker.Assign(this);

                players.Add(player.userID);
            }

            void OnTriggerExit(Collider col)
            {
                var player = col.ToBaseEntity() as BasePlayer;

                if (!player.IsValid())
                    return;

                if (player.IsHuman())
                    Interface.CallHook("OnPlayerExitedDangerousEvent", player, containerPos, config.TruePVE.AllowPVPAtEvents);
                else if (player is ScientistNPC)
                {
                    var npc = player as ScientistNPC;

                    if (npcs.Contains(npc))
                    {
                        if (npc.NavAgent == null || !npc.NavAgent.isOnNavMesh)
                            npc.finalDestination = containerPos;
                        else npc.NavAgent.SetDestination(containerPos);

                        npc.finalDestination = containerPos;
                    }
                }

                if (config.NewmanMode.Harm)
                {
                    if (protects.Remove(player.net.ID))
                    {
                        Instance.newmanProtections.Remove(player.userID);
                        Message(player, "Newman Protect Fade");
                    }

                    newmans.Remove(player.userID);
                }
            }

            public void SpawnNpcs()
            {
                container.SendNetworkUpdate();

                npcMaxAmountMurderers = config.NPC.Murderers.SpawnAmount > 0 ? UnityEngine.Random.Range(config.NPC.Murderers.SpawnMinAmount, config.NPC.Murderers.SpawnAmount + 1) : config.NPC.Murderers.SpawnAmount;
                npcMaxAmountScientists = config.NPC.Scientists.SpawnAmount > 0 ? UnityEngine.Random.Range(config.NPC.Scientists.SpawnMinAmount, config.NPC.Scientists.SpawnAmount + 1) : config.NPC.Scientists.SpawnAmount;

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

            private NavMeshHit _navHit;

            private Vector3 FindPointOnNavmesh(Vector3 target, float radius)
            {
                int tries = 0;

                while (++tries < 100)
                {
                    if (NavMesh.SamplePosition(target, out _navHit, radius, NavMesh.AllAreas))
                    {
                        float y = TerrainMeta.HeightMap.GetHeight(_navHit.position);

                        if (_navHit.position.y < y || !IsAcceptableWaterDepth(_navHit.position))
                        {
                            continue;
                        }

                        if (!InRange2D(_navHit.position, containerPos, Radius - 2.5f))
                        {
                            continue;
                        }

                        if (TestInsideRock(_navHit.position) || TestInsideObject(_navHit.position))
                        {
                            continue;
                        }

                        return _navHit.position;
                    }
                }

                return Vector3.zero;
            }

            private RaycastHit _hit;

            private bool IsAcceptableWaterDepth(Vector3 position)
            {
                return WaterLevel.GetOverallWaterDepth(position, true, null, false) <= 0.75f;
            }

            private bool TestInsideObject(Vector3 position)
            {
                return GamePhysics.CheckSphere(position, 0.5f, Layers.Mask.Player_Server | Layers.Server.Deployed, QueryTriggerInteraction.Ignore);
            }

            private bool TestInsideRock(Vector3 a)
            {
                Physics.queriesHitBackfaces = true;

                bool flag = IsRockFaceUpwards(a);

                Physics.queriesHitBackfaces = false;

                return flag || IsRockFaceDownwards(a);
            }

            private bool IsRockFaceDownwards(Vector3 a)
            {
                Vector3 b = a + new Vector3(0f, 20f, 0f);
                Vector3 d = a - b;
                RaycastHit[] hits = Physics.RaycastAll(b, d, d.magnitude, TARGET_MASK);
                return hits.Exists(hit => IsRock(hit.collider.name));
            }

            private bool IsRockFaceUpwards(Vector3 point)
            {
                if (!Physics.Raycast(point, Vector3.up, out _hit, 20f, TARGET_MASK)) return false;
                return IsRock(_hit.collider.gameObject.name);
            }

            private bool IsRock(string name) => _prefabs.Exists(value => name.Contains(value, CompareOptions.OrdinalIgnoreCase));

            private List<string> _prefabs = new List<string> { "rock", "formation", "junk", "cliff", "invisible" };

            private ScientistNPC InstantiateEntity(Vector3 position, bool isMurderer, out HumanoidBrain humanoidBrain)
            {
                var prefabName = StringPool.Get(1536035819);
                var prefab = GameManager.server.FindPrefab(prefabName);
                var go = Facepunch.Instantiate.GameObject(prefab, position, Quaternion.identity);

                go.SetActive(false);

                go.name = prefabName;

                ScientistBrain scientistBrain = go.GetComponent<ScientistBrain>();
                ScientistNPC npc = go.GetComponent<ScientistNPC>();

                humanoidBrain = go.AddComponent<HumanoidBrain>();
                humanoidBrain.DestinationOverride = position;
                humanoidBrain.CheckLOS = humanoidBrain.RefreshKnownLOS = true;
                humanoidBrain.SenseRange = isMurderer ? config.NPC.Murderers.AggressionRange : config.NPC.Scientists.AggressionRange;
                humanoidBrain.softLimitSenseRange = humanoidBrain.SenseRange + (humanoidBrain.SenseRange * 0.25f);
                humanoidBrain.TargetLostRange = humanoidBrain.SenseRange * 1.25f;
                humanoidBrain.Settings = config.NPC;
                humanoidBrain.UseAIDesign = false;
                humanoidBrain._baseEntity = npc;
                humanoidBrain.tc = this;
                humanoidBrain.npc = npc;

                DestroyImmediate(scientistBrain, true);

                SceneManager.MoveGameObjectToScene(go, Rust.Server.EntityScene);

                go.SetActive(true); //go.AwakeFromInstantiate();

                return npc;
            }

            private Vector3 RandomPosition(float radius)
            {
                return RandomWanderPositions(Radius * 0.9f).FirstOrDefault();
            }

            private List<Vector3> RandomWanderPositions(float radius)
            {
                var list = new List<Vector3>();

                for (int i = 0; i < 10; i++)
                {
                    var target = GetRandomPoint(radius);
                    var vector = FindPointOnNavmesh(target, radius);

                    if (vector != Vector3.zero)
                    {
                        list.Add(vector);
                    }
                }

                return list;
            }

            private Vector3 GetRandomPoint(float radius)
            {
                var vector = containerPos + UnityEngine.Random.onUnitSphere * radius;

                vector.y = TerrainMeta.HeightMap.GetHeight(vector);

                return vector;
            }

            private ScientistNPC SpawnNpc(bool isMurderer)
            {
                var positions = RandomWanderPositions(Radius * 0.9f);

                if (positions.Count == 0)
                {
                    return null;
                }

                var position = RandomPosition(Radius * 0.9f);

                if (position == Vector3.zero)
                {
                    return null;
                }

                HumanoidBrain brain;
                ScientistNPC npc = InstantiateEntity(position, isMurderer, out brain);

                if (npc == null)
                {
                    return null;
                }
                
                npc.userID = (ulong)UnityEngine.Random.Range(0, 10000000);
                npc.UserIDString = npc.userID.ToString();
                if (isMurderer)
                {
                    npc.displayName = config.NPC.Murderers.RandomNames.Count > 0 ? config.NPC.Murderers.RandomNames.GetRandom() : RandomUsernames.Get(npc.userID);
                }
                else npc.displayName = config.NPC.Scientists.RandomNames.Count > 0 ? config.NPC.Scientists.RandomNames.GetRandom() : RandomUsernames.Get(npc.userID);

                var loadout = GetLoadout(npc, brain, isMurderer);

                if (loadout.belt.Count > 0 || loadout.main.Count > 0 || loadout.wear.Count > 0)
                {
                    npc.loadouts = new PlayerInventoryProperties[1];
                    npc.loadouts[0] = loadout;
                }
                else npc.loadouts = new PlayerInventoryProperties[0];

                BasePlayer.bots.Add(npc);

                Instance.HumanoidBrains[brain.uid = npc.userID] = brain;
                brain.isMurderer = isMurderer;

                npc.Spawn();
                npc.CancelInvoke(npc.EquipTest);

                npcs.Add(npc);

                SetupNpc(npc, brain, isMurderer, positions);

                return npc;
            }

            public class Loadout
            {
                public List<PlayerInventoryProperties.ItemAmountSkinned> belt = new List<PlayerInventoryProperties.ItemAmountSkinned>();
                public List<PlayerInventoryProperties.ItemAmountSkinned> main = new List<PlayerInventoryProperties.ItemAmountSkinned>();
                public List<PlayerInventoryProperties.ItemAmountSkinned> wear = new List<PlayerInventoryProperties.ItemAmountSkinned>();
            }

            private PlayerInventoryProperties GetLoadout(ScientistNPC npc, HumanoidBrain brain, bool isMurderer)
            {
                var loadout = CreateLoadout(npc, brain, isMurderer);
                var pip = ScriptableObject.CreateInstance<PlayerInventoryProperties>();

                pip.belt = loadout.belt;
                pip.main = loadout.main;
                pip.wear = loadout.wear;

                return pip;
            }

            private Loadout CreateLoadout(ScientistNPC npc, HumanoidBrain brain, bool isMurderer)
            {
                var loadout = new Loadout();

                switch (isMurderer)
                {
                    case true:
                        AddItemAmountSkinned(loadout.wear, config.NPC.Murderers.Items.Boots);
                        AddItemAmountSkinned(loadout.wear, config.NPC.Murderers.Items.Gloves);
                        AddItemAmountSkinned(loadout.wear, config.NPC.Murderers.Items.Helm);
                        AddItemAmountSkinned(loadout.wear, config.NPC.Murderers.Items.Pants);
                        AddItemAmountSkinned(loadout.wear, config.NPC.Murderers.Items.Shirt);
                        AddItemAmountSkinned(loadout.wear, config.NPC.Murderers.Items.Torso);
                        if (!config.NPC.Murderers.Items.Torso.Exists(v => v.Contains("suit")))
                        {
                            AddItemAmountSkinned(loadout.wear, config.NPC.Murderers.Items.Kilts);
                        }
                        AddItemAmountSkinned(loadout.belt, config.NPC.Murderers.Items.Weapon);
                        break;
                    case false:
                        AddItemAmountSkinned(loadout.wear, config.NPC.Scientists.Items.Boots);
                        AddItemAmountSkinned(loadout.wear, config.NPC.Scientists.Items.Gloves);
                        AddItemAmountSkinned(loadout.wear, config.NPC.Scientists.Items.Helm);
                        AddItemAmountSkinned(loadout.wear, config.NPC.Scientists.Items.Pants);
                        AddItemAmountSkinned(loadout.wear, config.NPC.Scientists.Items.Shirt);
                        AddItemAmountSkinned(loadout.wear, config.NPC.Scientists.Items.Torso);
                        if (!config.NPC.Scientists.Items.Torso.Exists(v => v.Contains("suit")))
                        {
                            AddItemAmountSkinned(loadout.wear, config.NPC.Scientists.Items.Kilts);
                        }
                        AddItemAmountSkinned(loadout.belt, config.NPC.Scientists.Items.Weapon);
                        break;
                }

                return loadout;
            }

            private void AddItemAmountSkinned(List<PlayerInventoryProperties.ItemAmountSkinned> source, List<string> shortnames)
            {
                if (shortnames.Count == 0)
                {
                    return;
                }

                string shortname = shortnames.GetRandom();

                if (shortname == "bow.hunting")
                {
                    shortname = "bow.compound";
                }

                ItemDefinition def = ItemManager.FindItemDefinition(shortname);

                if (def == null)
                {
                    Puts("Invalid shortname for npc: {0}", shortname);
                    return;
                }

                ulong skin = 0uL;
                if (config.Skins.Npcs)
                {
                    skin = GetItemSkin(def, 0uL, config.Skins.UniqueNpcs);
                }

                source.Add(new PlayerInventoryProperties.ItemAmountSkinned
                {
                    amount = 1,
                    itemDef = def,
                    skinOverride = skin,
                    startAmount = 1
                });
            }

            private void SetupNpc(ScientistNPC npc, HumanoidBrain brain, bool isMurderer, List<Vector3> positions)
            {
                if (isMurderer && config.NPC.Murderers.DespawnInventory || !isMurderer && config.NPC.Scientists.DespawnInventory)
                {
                    npc.LootSpawnSlots = new LootContainer.LootSpawnSlot[0];
                }

                var alternate = isMurderer ? config.NPC.Murderers.Alternate : config.NPC.Scientists.Alternate;

                if (alternate.Enabled && alternate.IDs.Count > 0)
                {
                    var id = alternate.GetRandom();
                    var lootSpawnSlots = GameManager.server.FindPrefab(StringPool.Get(id))?.GetComponent<ScientistNPC>()?.LootSpawnSlots;

                    if (lootSpawnSlots != null)
                    {
                        npc.LootSpawnSlots = lootSpawnSlots;
                    }
                }

                npc.CancelInvoke(npc.PlayRadioChatter);
                npc.DeathEffects = new GameObjectRef[0];
                npc.RadioChatterEffects = new GameObjectRef[0];
                npc.radioChatterType = ScientistNPC.RadioChatterType.NONE;
                npc.startHealth = isMurderer ? config.NPC.Murderers.Health : config.NPC.Scientists.Health;
                npc.InitializeHealth(npc.startHealth, npc.startHealth);
                npc.Invoke(() => UpdateItems(npc, brain, isMurderer), 0.2f);
                npc.Invoke(() => brain.SetupMovement(positions), 0.3f);
            }

            private void UpdateItems(ScientistNPC npc, HumanoidBrain brain, bool isMurderer)
            {
                brain.isMurderer = isMurderer;

                List<string> kits;
                if (npcKits.TryGetValue(isMurderer ? "murderer" : "scientist", out kits) && kits.Count > 0)
                {
                    string kit = kits.GetRandom();

                    npc.inventory.Strip();

                    Instance.Kits?.Call("GiveKit", npc, kit);
                }

                EquipWeapon(npc, brain);

                if (!ToggleNpcMinerHat(npc, TOD_Sky.Instance?.IsNight == true))
                {
                    npc.inventory.ServerUpdate(0f);
                }
            }

            private bool ToggleNpcMinerHat(ScientistNPC npc, bool state)
            {
                if (npc.IsNull() || npc.inventory == null || npc.IsDead())
                {
                    return false;
                }

                var slot = npc.inventory.FindItemID("hat.miner");

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

            public void EquipWeapon(ScientistNPC npc, HumanoidBrain brain)
            {
                foreach (Item item in npc.inventory.AllItems())
                {
                    var e = item.GetHeldEntity() as HeldEntity;

                    if (e.IsReallyValid())
                    {
                        Instance.data.TemporaryUID.Add(e.net.ID);

                        if (item.skin != 0)
                        {
                            e.skinID = item.skin;
                            e.SendNetworkUpdate();
                        }

                        if (!brain.AttackEntity.IsNull() || item.info.shortname == "syringe.medical")
                        {
                            continue;
                        }

                        var weapon = e as BaseProjectile;

                        if (weapon.IsReallyValid())
                        {
                            weapon.primaryMagazine.contents = weapon.primaryMagazine.capacity;
                            weapon.SendNetworkUpdateImmediate();
                        }

                        if (e is AttackEntity && item.GetRootContainer() == npc.inventory.containerBelt)
                        {
                            var attackEntity = e as AttackEntity;

                            if (attackEntity.hostile || item.info.shortname == "syringe.medical")
                            {
                                UpdateWeapon(npc, brain, attackEntity, item);
                            }
                        }
                    }

                    item.MarkDirty();
                }
            }

            private void UpdateWeapon(ScientistNPC npc, HumanoidBrain brain, AttackEntity attackEntity, Item item)
            {
                npc.UpdateActiveItem(item.uid);

                if (attackEntity is Chainsaw)
                {
                    (attackEntity as Chainsaw).ServerNPCStart();
                }

                npc.damageScale = 1f;

                attackEntity.TopUpAmmo();
                attackEntity.SetHeld(true);
                brain.Init();
            }

            void SetupNpcKits()
            {
                npcKits = new Dictionary<string, List<string>>
                {
                    { "murderer", config.NPC.Murderers.Kits.Where(kit => IsKit(kit)).ToList() },
                    { "scientist", config.NPC.Scientists.Kits.Where(kit => IsKit(kit)).ToList() }
                };
            }

            bool IsKit(string kit)
            {
                return Convert.ToBoolean(Instance.Kits?.Call("isKit", kit));
            }

            public void UpdateMarker()
            {
                if (!config.Event.MarkerVending && !config.Event.MarkerExplosion)
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
                        vendingMarker.SendNetworkUpdate();
                    }

                    return;
                }

                if (Instance.MarkerManager.CanCall())
                {
                    Interface.CallHook("API_CreateMarker", container as BaseEntity, "DangerousTreasures", 0, 10f, 0.25f, config.Event.MarkerName, "FF0000", "00FFFFFF");
                    markerCreated = true;
                    return;
                }

                if (Instance.treasureChests.Sum(e => e.Value.HasRustMarker ? 1 : 0) > 10)
                {
                    return;
                }

                //explosionmarker cargomarker ch47marker cratemarker
                if (config.Event.MarkerVending)
                {
                    vendingMarker = GameManager.server.CreateEntity(StringPool.Get(3459945130), containerPos) as VendingMachineMapMarker;

                    if (vendingMarker != null)
                    {
                        vendingMarker.enabled = false;
                        vendingMarker.markerShopName = config.Event.MarkerName;
                        vendingMarker.Spawn();
                    }
                }
                else if (config.Event.MarkerExplosion)
                {
                    explosionMarker = GameManager.server.CreateEntity(StringPool.Get(4060989661), containerPos) as MapMarkerExplosion;

                    if (explosionMarker != null)
                    {
                        explosionMarker.Spawn();
                        explosionMarker.Invoke(() => explosionMarker.CancelInvoke(explosionMarker.DelayedDestroy), 1f);
                    }
                }

                genericMarker = GameManager.server.CreateEntity(StringPool.Get(2849728229), containerPos) as MapMarkerGenericRadius;

                if (genericMarker != null)
                {
                    genericMarker.alpha = 0.75f;
                    genericMarker.color2 = __(config.Event.MarkerColor);
                    genericMarker.radius = Mathf.Clamp(config.Event.MarkerRadius, 0.1f, 1f);
                    genericMarker.Spawn();
                    genericMarker.SendUpdate();
                }

                markerCreated = true;
            }

            void KillNpc()
            {
                npcs.ForEach(npc => npc.SafelyKill());
            }

            public void RemoveMapMarkers()
            {
                Instance.RemoveLustyMarker(uid);
                Instance.RemoveMapMarker(uid);

                if (!explosionMarker.IsKilled())
                {
                    explosionMarker.CancelInvoke(explosionMarker.DelayedDestroy);
                    explosionMarker.Kill(BaseNetworkable.DestroyMode.None);
                }

                genericMarker.SafelyKill();
                vendingMarker.SafelyKill();
            }

            void OnDestroy()
            {
                Kill(IsUnloading);

                if (Instance != null && !IsUnloading && Instance.treasureChests.Remove(uid) && Instance.treasureChests.Count == 0)
                {
                    Instance.SubscribeHooks(false);
                }

                Free();
            }

            public void LaunchMissile()
            {
                if (!config.MissileLauncher.Enabled)
                {
                    DestroyLauncher();
                    return;
                }

                var missilePos = missilePositions.GetRandom();
                float y = TerrainMeta.HeightMap.GetHeight(missilePos) + 15f;
                missilePos.y = 200f;

                RaycastHit hit;
                if (Physics.Raycast(missilePos, Vector3.down, out hit, heightLayer)) // don't want the missile to explode before it leaves its spawn location
                    missilePos.y = Mathf.Max(hit.point.y, y);

                var prefab = config.Rocket.FireRockets ? "assets/prefabs/ammo/rocket/rocket_fire.prefab" : "assets/prefabs/ammo/rocket/rocket_basic.prefab";
                var missile = GameManager.server.CreateEntity(prefab, missilePos) as TimedExplosive;

                if (!missile)
                {
                    config.MissileLauncher.Enabled = false;
                    DestroyLauncher();
                    return;
                }

                missiles.Add(missile);
                missiles.RemoveAll(x => x.IsKilled());

                var gs = missile.gameObject.AddComponent<GuidanceSystem>();

                gs.Exclude(newmans);
                gs.SetTarget(container);
                gs.Launch(config.MissileLauncher.TargettingTime);
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
                if (!config.Fireballs.Enabled)
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
                    Puts(_("Invalid Constant", null, 3550347674));
                    config.Fireballs.Enabled = false;
                    CancelInvoke(SpawnFire);
                    firePositions.Clear();
                    return;
                }

                fireball.Spawn();
                fireball.damagePerSecond = config.Fireballs.DamagePerSecond;
                fireball.generation = config.Fireballs.Generation;
                fireball.lifeTimeMax = config.Fireballs.LifeTimeMax;
                fireball.lifeTimeMin = config.Fireballs.LifeTimeMin;
                fireball.radius = config.Fireballs.Radius;
                fireball.tickRate = config.Fireballs.TickRate;
                fireball.waterToExtinguish = config.Fireballs.WaterToExtinguish;
                fireball.SendNetworkUpdate();
                fireball.Think();

                float lifeTime = UnityEngine.Random.Range(config.Fireballs.LifeTimeMin, config.Fireballs.LifeTimeMax);
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
                    Message(target, "DestroyingTreasure", eventPos, Instance.FormatTime(time, target.UserIDString), config.Settings.DistanceChatCommand);
            }

            public string GetUnlockTime(string userID = null)
            {
                return started ? null : Instance.FormatTime(_unlockTime - Time.realtimeSinceStartup, userID);
            }

            public void Unlock()
            {
                if (unlock != null && !unlock.Destroyed)
                {
                    unlock.Destroy();
                }

                if (!started)
                {
                    DestroyFire();
                    DestroySphere();
                    DestroyLauncher();

                    if (config.Event.DestructTime > 0f && destruct == null)
                        destruct = Instance.timer.Once(config.Event.DestructTime, Destruct);

                    if (config.EventMessages.Started)
                    {
                        foreach (var target in BasePlayer.activePlayerList)
                        {
                            Message(target, requireAllNpcsDie && npcSpawnedAmount > 0 ? "StartedNpcs" : "Started", FormatGridReference(containerPos));
                        }
                    }

                    if (config.UnlootedAnnouncements.Enabled)
                    {
                        claimTime = Time.realtimeSinceStartup + config.Event.DestructTime;
                        announcement = Instance.timer.Repeat(config.UnlootedAnnouncements.Interval * 60f, 0, Unclaimed);
                    }

                    started = true;
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

                container.SetFlag(BaseEntity.Flags.Locked, false);
                container.SetFlag(BaseEntity.Flags.OnFire, false);
            }

            public void SetUnlockTime(float time)
            {
                countdownTime = Convert.ToInt32(time);
                _unlockTime = Convert.ToInt64(Time.realtimeSinceStartup + time);

                if (npcSpawnedAmount == 0 && config.NPC.Murderers.SpawnAmount + config.NPC.Scientists.SpawnAmount > 0 && config.NPC.Enabled)
                {
                    if (requireAllNpcsDie || whenNpcsDie)
                    {
                        whenNpcsDie = false;
                        requireAllNpcsDie = false;
                    }
                }
                
                unlock = Instance.timer.Once(time, Unlock);

                if (config.Countdown.Enabled && config.Countdown.Times?.Count > 0 && countdownTime > 0)
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

                        if (times.Contains(countdownTime))
                        {
                            string eventPos = FormatGridReference(containerPos);

                            foreach (var target in BasePlayer.activePlayerList)
                                Message(target, "Countdown", eventPos, Instance.FormatTime(countdownTime, target.UserIDString));

                            times.Remove(countdownTime);
                        }
                    });
                }
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
            Instance = this;
            SubscribeHooks(false);
            Unsubscribe(nameof(Unload));
        }

        void OnServerInitialized(bool isStartup)
        {
            LoadData();
            TryWipeData();
            BlockZoneManagerZones();
            LoadMonuments();                        
            RemoveAllTemporaryMarkers();
            InitializeSkins();
            StartAutomation();
            timer.Repeat(Mathf.Clamp(config.EventMessages.Interval, 1f, 60f), 0, CheckNotifications);
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

            lustyMarkers.Keys.ToList().ForEach(RemoveLustyMarker);
            mapMarkers.Keys.ToList().ForEach(RemoveMapMarker);
            RemoveAllTemporaryMarkers();
            config = null;
            Instance = null;
            DangerousTreasuresExtensionMethods.ExtensionMethods.p = null;
        }

        object canTeleport(BasePlayer player)
        {
            return EventTerritory(player.transform.position) ? msg("CannotTeleport", player.UserIDString) : null;
        }

        object CanTeleport(BasePlayer player)
        {
            return EventTerritory(player.transform.position) ? msg("CannotTeleport", player.UserIDString) : null;
        }

        object CanBradleyApcTarget(BradleyAPC apc, ScientistNPC npc)
        {
            return npc != null && TreasureChest.HasNPC(npc.userID) ? (object)false : null;
        }

        object OnEntityEnter(TriggerBase trigger, BasePlayer player)
        {
            if (player.IsValid())
            {
                if (newmanProtections.Contains(player.userID) || TreasureChest.HasNPC(player.userID))
                {
                    return true;
                }
            }

            return null;
        }

        private object OnNpcDuck(ScientistNPC npc) => npc != null && TreasureChest.HasNPC(npc.userID) ? true : (object)null;

        private object OnNpcDestinationSet(ScientistNPC npc, Vector3 newDestination)
        {
            if (npc.IsNull() || npc.NavAgent == null || !npc.NavAgent.enabled || !npc.NavAgent.isOnNavMesh)
            {
                return true;
            }

            HumanoidBrain brain;
            if (!HumanoidBrains.TryGetValue(npc.userID, out brain) || brain.CanRoam(newDestination))
            {
                return null;
            }

            return true;
        }

        private object OnNpcResume(ScientistNPC npc)
        {
            if (npc.IsNull())
            {
                return null;
            }

            HumanoidBrain brain;
            if (!HumanoidBrains.TryGetValue(npc.userID, out brain))
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

            if (TreasureChest.HasNPC(player.userID) && !target.userID.IsSteamId())
            {
                return true;
            }
            else if (TreasureChest.HasNPC(target.userID) && !player.userID.IsSteamId())
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

            if (TreasureChest.HasNPC(target.userID) || newmanProtections.Contains(target.userID))
            {
                return true;
            }

            return null;
        }

        object OnNpcTarget(BasePlayer target, BaseNpc npc) => OnNpcTarget(npc, target);

        void OnPlayerDeath(ScientistNPC npc)
        {
            if (npc == null)
            {
                return;
            }

            HumanoidBrain brain;
            if (!HumanoidBrains.TryGetValue(npc.userID, out brain) || brain.tc == null)
            {
                return;
            }

            if (brain.isMurderer && config.NPC.Murderers.DespawnInventory || !brain.isMurderer && config.NPC.Scientists.DespawnInventory)
            {
                npc.inventory.Strip();
            }

            npc.svActiveItemID = 0;
            npc.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);

            brain.tc.npcs.Remove(npc);

            if (brain.tc.whenNpcsDie && brain.tc.npcs.Count == 0)
            {
                brain.tc.Unlock();
            }
        }

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

        void OnEntitySpawned(NPCPlayerCorpse corpse)
        {
            if (!config.NPC.Enabled || corpse == null)
            {
                return;
            }

            HumanoidBrain brain;
            if (!HumanoidBrains.TryGetValue(corpse.playerSteamID, out brain) || brain.tc == null)
            {
                return;
            }

            brain.tc.npcs.RemoveAll(npc => npc.IsKilled() || npc.userID == corpse.playerSteamID);

            if (brain.isMurderer && config.NPC.Murderers.DespawnInventory || !brain.isMurderer && config.NPC.Scientists.DespawnInventory)
            {
                corpse.Invoke(corpse.SafelyKill, 30f);
            }

            UnityEngine.Object.Destroy(brain);

            if (!treasureChests.Values.Exists(x => x.npcs.Count > 0))
            {
                Unsubscribe(nameof(OnNpcTarget));
                Unsubscribe(nameof(OnNpcResume));
                Unsubscribe(nameof(OnNpcDestinationSet));
                Unsubscribe(nameof(CanBradleyApcTarget));
                Unsubscribe(nameof(OnPlayerDeath));
            }
        }

        object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            var player = planner?.GetOwnerPlayer();

            if (!player || player.IsAdmin) return null;

            var chest = TreasureChest.Get(player.transform.position);

            if (chest != null)
            {
                Message(player, "Building is blocked!");
                return false;
            }

            return null;
        }

        void OnLootEntity(BasePlayer player, BoxStorage container)
        {
            if (player == null || !container.IsValid() || !treasureChests.ContainsKey(container.net.ID))
                return;

            if (player.isMounted)
            {
                Message(player, "CannotBeMounted");
                player.Invoke(player.EndLooting, 0.1f);
                return;
            }
            else looters[container.net.ID] = player.UserIDString;

            if (!config.EventMessages.FirstOpened)
            {
                return;
            }

            var chest = treasureChests[container.net.ID];

            if (chest.opened)
            {
                return;
            }

            chest.opened = true;
            var posStr = FormatGridReference(container.transform.position);

            foreach (var target in BasePlayer.activePlayerList)
            {
                Message(target, "OnChestOpened", player.displayName, posStr);
            }
        }

        void OnItemRemovedFromContainer(ItemContainer container, Item item)
        {
            if (container?.entityOwner == null || !(container.entityOwner is StorageContainer))
                return;

            NextTick(() =>
            {
                var box = container?.entityOwner as StorageContainer;

                if (!box.IsValid() || !treasureChests.ContainsKey(box.net.ID))
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
                            if (!data.Players.ContainsKey(looter.UserIDString))
                                data.Players.Add(looter.UserIDString, new PlayerInfo());

                            data.Players[looter.UserIDString].StolenChestsTotal++;
                            data.Players[looter.UserIDString].StolenChestsSeed++;
                            SaveData();
                        }

                        var posStr = FormatGridReference(looter.transform.position);

                        Puts(_("Thief", null, posStr, looter.displayName));

                        if (config.EventMessages.Thief)
                        {
                            foreach (var target in BasePlayer.activePlayerList)
                                Message(target, "Thief", posStr, looter.displayName);
                        }

                        looter.EndLooting();

                        if (config.Rewards.Economics && config.Rewards.Money > 0 && Economics.CanCall())
                        {
                            Economics?.Call("Deposit", looter.UserIDString, config.Rewards.Money);
                            Message(looter, "EconomicsDeposit", config.Rewards.Money);
                        }

                        if (config.Rewards.ServerRewards && config.Rewards.Points > 0 && ServerRewards.CanCall())
                        {
                            if (Convert.ToBoolean(ServerRewards?.Call("AddPoints", looter.userID, (int)config.Rewards.Points)))
                            {
                                Message(looter, "ServerRewardPoints", (int)config.Rewards.Points);
                            }
                        }
                    }

                    RemoveLustyMarker(box.net.ID);
                    RemoveMapMarker(box.net.ID);
                    box.SafelyKill();

                    if (treasureChests.Count == 0)
                        SubscribeHooks(false);
                }
            });
        }

        object CanEntityBeTargeted(BasePlayer player, BaseEntity target)
        {
            return !player.IsKilled() && player.IsHuman() && EventTerritory(player.transform.position) && !target.IsKilled() && IsTrueDamage(target) ? (object)true : null;
        }

        object CanEntityTrapTrigger(BaseTrap trap, BasePlayer player)
        {
            return !player.IsKilled() && player.IsHuman() && EventTerritory(player.transform.position) ? (object)true : null;
        }

        object CanEntityTakeDamage(BaseEntity entity, HitInfo hitInfo) // TruePVE!!!! <3 @ignignokt84
        {
            if (entity.IsKilled() || hitInfo == null || hitInfo.Initiator == null)
            {
                return null;
            }
            
            if (entity is ScientistNPC && TreasureChest.HasNPC(entity.ToPlayer().userID))
            {
                return true;
            }

            if (config.TruePVE.ServerWidePVP && treasureChests.Count > 0 && hitInfo.Initiator is BasePlayer && entity is BasePlayer) // 1.2.9 & 1.3.3 & 1.6.4
            {
                return true;
            }

            if (EventTerritory(entity.transform.position)) // 1.5.8 & 1.6.4
            {
                if (entity is NPCPlayerCorpse || IsTrueDamage(hitInfo.Initiator))
                {
                    return true;
                }

                if (config.TruePVE.AllowPVPAtEvents && entity is BasePlayer && hitInfo.Initiator is BasePlayer && EventTerritory(hitInfo.Initiator.transform.position)) // 1.2.9
                {
                    return true;
                }

                if (config.TruePVE.AllowBuildingDamageAtEvents && entity.name.Contains("building") && hitInfo.Initiator is BasePlayer && EventTerritory(hitInfo.Initiator.transform.position)) // 1.3.3
                {
                    return true;
                }
            }

            return null; // 1.6.4 rewrite
        }
                
        void OnEntityTakeDamage(BasePlayer player, HitInfo hitInfo)
        {
            if (player == null || hitInfo == null)
            {
                return;
            }

            if (TreasureChest.HasNPC(player.userID))
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

            HumanoidBrain brain;
            if (HumanoidBrains.TryGetValue(attacker.userID, out brain) && brain != null && (brain.isMurderer && UnityEngine.Random.Range(0f, 100f) > config.NPC.Murderers.Accuracy.Get(brain) || !brain.isMurderer && UnityEngine.Random.Range(0f, 100f) > config.NPC.Scientists.Accuracy.Get(brain)))
            {
                hitInfo.damageTypes = new DamageTypeList();
                hitInfo.DidHit = false;
                hitInfo.DoHitEffects = false;
            }
        }

        void OnEntityTakeDamage(BoxStorage box, HitInfo hitInfo)
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

        object OnNpcKits(ulong targetId)
        {
            return TreasureChest.HasNPC(targetId) ? true : (object)null;
        }

        bool IsTrueDamage(BaseEntity entity)
        {
            if (entity.IsNull())
            {
                return false;
            }

            return entity is AutoTurret || entity is BearTrap || entity is FlameTurret || entity is Landmine || entity is GunTrap || entity is ReactiveTarget || entity.name.Contains("spikes.floor") || entity is FireBall;
        }

        bool EventTerritory(Vector3 target)
        {
            foreach (var x in treasureChests.Values)
            {
                if (Vector3Ex.Distance2D(x.containerPos, target) <= x.Radius)
                {
                    return true;
                }
            }

            return false;
        }

        bool IsOnSameTeam(ulong playerId, ulong targetId)
        {
            RelationshipManager.PlayerTeam team;
            if (RelationshipManager.ServerInstance.playerToTeam.TryGetValue(playerId, out team))
            {
                return team.members.Contains(targetId);
            }

            return false;
        }

        void LoadData()
        {
            try { data = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name); } catch { }

            sd_customPos = string.IsNullOrEmpty(data.CustomPosition) ? Vector3.zero : data.CustomPosition.ToVector3();

            if (data == null || data.Players == null || data.TemporaryUID == null)
            {
                data = new StoredData();
            }
            
            if (data.TemporaryUID.Count > 0)
            {
                foreach (uint uid in data.TemporaryUID.ToList())
                {
                    data.TemporaryUID.Remove(uid);
                    BaseNetworkable.serverEntities.Find(uid).SafelyKill();
                }
            }
        }

        private bool IsEmptyMap()
        {
            foreach (var b in BuildingManager.server.buildingDictionary)
            {
                if (b.Value.HasDecayEntities() && b.Value.decayEntities.Exists(de => de != null && de.OwnerID.IsSteamId()))
                {
                    return false;
                }
            }
            return true;
        }

        void TryWipeData()
        {
            if (wipeChestsSeed || IsEmptyMap())
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

        void BlockZoneManagerZones()
        {
            if (!ZoneManager.CanCall())
            {
                return;
            }

            var zoneIds = ZoneManager?.Call("GetZoneIDs") as string[];

            if (zoneIds == null)
            {
                return;
            }

            managedZones.Clear();

            foreach (string zoneId in zoneIds)
            {
                var zoneLoc = ZoneManager.Call("GetZoneLocation", zoneId);

                if (!(zoneLoc is Vector3))
                {
                    continue;
                }

                var position = (Vector3)zoneLoc;

                if (position == Vector3.zero)
                {
                    continue;
                }

                var zoneInfo = new ZoneInfo();
                var radius = ZoneManager.Call("GetZoneRadius", zoneId);

                if (radius is float)
                {
                    zoneInfo.Distance = (float)radius;
                }

                var size = ZoneManager.Call("GetZoneSize", zoneId);

                if (size is Vector3)
                {
                    zoneInfo.Size = (Vector3)size;
                }

                zoneInfo.Position = position;
                zoneInfo.OBB = new OBB(zoneInfo.Position, zoneInfo.Size, Quaternion.identity);
                managedZones[position] = zoneInfo;
            }

            if (managedZones.Count > 0)
            {
                Puts("Blocked {0} zone manager zones", managedZones.Count);
            }
        }

        void LoadMonuments()
        {
            monuments.Clear();
            allowedMonuments.Clear();

            int num1 = config.Monuments.Blacklist.Count;
            int num2 = config.NPC.BlacklistedMonuments.Count;

            foreach (var monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                if (monument.IsSafeZone || monument.name.Contains("cave") || monument.name.Contains("power_sub"))
                {
                    continue;
                }
                string name = monument.displayPhrase.english;
                if (monuments.ContainsKey(name)) name += ":" + monument.transform.position.GetHashCode();
                monuments[name] = new MonInfo(monument.transform.position, GetMonumentFloat(name));
                name = monument.displayPhrase.english.Trim();
                if (!string.IsNullOrEmpty(name) && !config.NPC.BlacklistedMonuments.ContainsKey(name))
                {
                    config.NPC.BlacklistedMonuments.Add(name, false);
                }
            }

            if (monuments.Count > 0)
            {
                allowedMonuments = monuments.ToDictionary(k => k.Key, k => k.Value);
            }

            foreach (var key in allowedMonuments.Keys.ToList())
            {
                string value = (key.Contains(":") ? key.Substring(0, key.LastIndexOf(":")) : key.TrimEnd()).Trim();

                if (string.IsNullOrEmpty(value))
                {
                    continue;
                }
                if (!config.Monuments.Blacklist.ContainsKey(value))
                {
                    config.Monuments.Blacklist.Add(value, false);
                }
                else if (config.Monuments.Blacklist[value])
                {
                    allowedMonuments.Remove(key);
                }
                if (!config.Monuments.Underground && underground.Exists(x => x.Contains(value, CompareOptions.OrdinalIgnoreCase)))
                {
                    allowedMonuments.Remove(key);
                }
            }

            if (config.Monuments.Blacklist.Count != num1 || config.NPC.BlacklistedMonuments.Count != num2)
            {
                config.Monuments.Blacklist = System.Linq.Enumerable.OrderBy(config.Monuments.Blacklist, x => x.Key).ToDictionary(x => x.Key, x => x.Value);
                config.NPC.BlacklistedMonuments = System.Linq.Enumerable.OrderBy(config.NPC.BlacklistedMonuments, x => x.Key).ToDictionary(x => x.Key, x => x.Value);
                SaveConfig();
            }
        }

        void InitializeSkins()
        {
            foreach (var def in ItemManager.GetItemDefinitions())
            {
                ItemModDeployable imd;
                if (def.TryGetComponent(out imd))
                {
                    _definitions[imd.entityPrefab.resourcePath] = def;
                }
            }
        }

        void StartAutomation()
        {
            if (config.Event.Automated)
            {
                if (data.SecondsUntilEvent != double.MinValue)
                    if (data.SecondsUntilEvent - Facepunch.Math.Epoch.Current > config.Event.IntervalMax) // Allows users to lower max event time
                        data.SecondsUntilEvent = double.MinValue;

                timer.Once(1f, CheckSecondsUntilEvent);
            }
        }

        private static List<T> FindEntitiesOfType<T>(Vector3 a, float n, int m = -1) where T : BaseEntity
        {
            int hits = Physics.OverlapSphereNonAlloc(a, n, Vis.colBuffer, m, QueryTriggerInteraction.Collide);
            List<T> entities = new List<T>();
            for (int i = 0; i < hits; i++)
            {
                var entity = Vis.colBuffer[i]?.ToBaseEntity();
                if (entity is T) entities.Add(entity as T);
                Vis.colBuffer[i] = null;
            }

            return entities;
        }

        void NpcDamageHelper(BasePlayer player, HitInfo hitInfo)
        {
            HumanoidBrain brain;
            if (!HumanoidBrains.TryGetValue(player.userID, out brain))
            {
                return;
            }

            if (config.NPC.Range > 0f && hitInfo.ProjectileDistance > config.NPC.Range || hitInfo.hasDamage && !(hitInfo.Initiator is BasePlayer) && !(hitInfo.Initiator is AutoTurret)) // immune to fire/explosions/other
            {
                hitInfo.damageTypes = new DamageTypeList();
                hitInfo.DidHit = false;
                hitInfo.DoHitEffects = false;
            }
            else if (hitInfo.isHeadshot && (brain.isMurderer && config.NPC.Murderers.Headshot || !brain.isMurderer && config.NPC.Scientists.Headshot))
            {
                player.Die(hitInfo);
            }
            else if (hitInfo.Initiator is BasePlayer)
            {
                var attacker = hitInfo.Initiator as BasePlayer;
                var e = attacker.HasParent() ? attacker.GetParentEntity() : null;

                if (!(e == null) && (e is ScrapTransportHelicopter || e is HotAirBalloon || e is CH47Helicopter))
                {
                    hitInfo.damageTypes.ScaleAll(0f);
                    return;
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

        bool IsMelee(BasePlayer player)
        {
            var attackEntity = player.GetHeldEntity() as AttackEntity;

            if (attackEntity == null)
            {
                return false;
            }

            return attackEntity is BaseMelee;
        }

        void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, data);

        protected new static void Puts(string format, params object[] args)
        {
            Interface.Oxide.LogInfo("[{0}] {1}", Name, (args.Length != 0) ? string.Format(format, args) : format);
        }

        void SubscribeHooks(bool flag)
        {
            if (flag)
            {
                if (config.NPC.Enabled)
                {
                    if (config.NPC.BlockNpcKits)
                    {
                        Subscribe(nameof(OnNpcKits));
                    }

                    Subscribe(nameof(OnPlayerDeath));
                }

                Subscribe(nameof(CanEntityTakeDamage));
                Subscribe(nameof(OnNpcTarget));
                Subscribe(nameof(OnNpcResume));
                Subscribe(nameof(OnNpcDestinationSet));
                Subscribe(nameof(OnEntitySpawned));
                Subscribe(nameof(CanBradleyApcTarget));
                Subscribe(nameof(OnEntityTakeDamage));
                Subscribe(nameof(OnItemRemovedFromContainer));
                Subscribe(nameof(OnLootEntity));
                Subscribe(nameof(CanBuild));
                Subscribe(nameof(CanTeleport));
                Subscribe(nameof(canTeleport));
                Subscribe(nameof(OnEntityEnter));
            }
            else
            {
                Unsubscribe(nameof(OnNpcKits));
                Unsubscribe(nameof(CanTeleport));
                Unsubscribe(nameof(canTeleport));
                Unsubscribe(nameof(OnEntityEnter));
                Unsubscribe(nameof(CanEntityTakeDamage));
                Unsubscribe(nameof(CanBradleyApcTarget));
                Unsubscribe(nameof(OnNpcTarget));
                Unsubscribe(nameof(OnNpcResume));
                Unsubscribe(nameof(OnEntitySpawned));
                Unsubscribe(nameof(OnEntityTakeDamage));
                Unsubscribe(nameof(OnItemRemovedFromContainer));
                Unsubscribe(nameof(OnLootEntity));
                Unsubscribe(nameof(CanBuild));
                Unsubscribe(nameof(OnPlayerDeath));
            }
        }
        
        static List<Vector3> GetRandomPositions(Vector3 destination, float radius, int amount, float y)
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

        public Vector3 GetEventPosition()
        {
            if (sd_customPos != Vector3.zero)
            {
                return sd_customPos;
            }

            var maxRetries = 500;
            var eventPos = TryGetMonumentDropPosition();

            if (eventPos != Vector3.zero)
            {
                return eventPos;
            }

            bool isDuelist = Duelist.CanCall();
            bool isRaidable = RaidableBases.CanCall();
            bool isAbandoned = AbandonedBases.CanCall();

            do
            {
                var r = RandomDropPosition();

                eventPos = GetSafeDropPosition(r);

                if (eventPos == Vector3.zero)
                {
                    _gridPositions.Remove(r);
                    continue;
                }

                if (IsTooClose(eventPos))
                {
                    eventPos = Vector3.zero;
                }
                else if (IsZoneBlocked(eventPos))
                {
                    eventPos = Vector3.zero;
                }
                else if (IsMonumentPosition(eventPos))
                {
                    eventPos = Vector3.zero;
                }
                else if (isDuelist && Convert.ToBoolean(Duelist?.Call("DuelistTerritory", eventPos)))
                {
                    eventPos = Vector3.zero;
                }
                else if (isRaidable && Convert.ToBoolean(RaidableBases?.Call("EventTerritory", eventPos)))
                {
                    eventPos = Vector3.zero;
                }
                else if (isAbandoned && Convert.ToBoolean(AbandonedBases?.Call("EventTerritory", eventPos)))
                {
                    eventPos = Vector3.zero;
                }
            } while (eventPos == Vector3.zero && --maxRetries > 0);

            return eventPos;
        }

        Vector3 TryGetMonumentDropPosition()
        {
            if (allowedMonuments.Count == 0)
            {
                return Vector3.zero;
            }

            if (config.Monuments.Only)
            {
                return GetMonumentDropPosition();
            }

            if (config.Monuments.Chance > 0f)
            {
                var value = UnityEngine.Random.value;

                if (value <= config.Monuments.Chance)
                {
                    return GetMonumentDropPosition();
                }
            }

            return Vector3.zero;
        }

        bool IsTooClose(Vector3 vector, float multi = 2f)
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

        bool IsZoneBlocked(Vector3 vector)
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

        bool IsSafeZone(Vector3 a)
        {
            return TriggerSafeZone.allSafeZones.Exists(triggerSafeZone => InRange2D(triggerSafeZone.transform.position, a, 200f));
        }

        Vector3 GetSafeDropPosition(Vector3 position)
        {
            RaycastHit hit;
            position.y += 200f;

            if (!Physics.Raycast(position, Vector3.down, out hit, 1000f, heightLayer, QueryTriggerInteraction.Collide))
            {
                return Vector3.zero;
            }

            if (BlockedLayers.Contains(hit.collider.gameObject.layer))
            {
                return Vector3.zero;
            }

            if (IsSafeZone(hit.point))
            {
                return Vector3.zero;
            }

            if (hit.collider.name.StartsWith("powerline_") || hit.collider.name.StartsWith("invisible_"))
            {
                return Vector3.zero;
            }

            float h = TerrainMeta.HeightMap.GetHeight(position);

            position.y = Mathf.Max(hit.point.y, GetSpawnHeight(position));

            if (TerrainMeta.WaterMap.GetHeight(position) - h > 0.1f)
            {
                return Vector3.zero;
            }

            if (IsLayerBlocked(position, config.Event.Radius + 10f, obstructionLayer))
            {
                return Vector3.zero;
            }

            return position;
        }

        float GetSpawnHeight(Vector3 target, bool flag = true, bool draw = false)
        {
            float y = TerrainMeta.HeightMap.GetHeight(target);
            float w = TerrainMeta.WaterMap.GetHeight(target);
            float p = TerrainMeta.HighestPoint.y + 250f;
            RaycastHit hit;

            if (Physics.Raycast(target.WithY(p), Vector3.down, out hit, ++p, TARGET_MASK, QueryTriggerInteraction.Ignore))
            {
                if (!_blockedColliders.Exists(hit.collider.name.StartsWith))
                {
                    y = Mathf.Max(y, hit.point.y);
                }
            }

            return flag ? Mathf.Max(y, w) : y;
        }

        bool IsLayerBlocked(Vector3 position, float radius, int mask)
        {
            var entities = FindEntitiesOfType<BaseEntity>(position, radius, mask);
            entities.RemoveAll(entity => entity.IsNpc || !entity.OwnerID.IsSteamId());
            bool blocked = entities.Count > 0;
            return blocked;
        }

        Vector3 GetRandomMonumentDropPosition(Vector3 position)
        {
            foreach (var monument in allowedMonuments)
            {
                if (!InRange2D(monument.Value.Position, position, 75f))
                {
                    continue;
                }

                int attempts = 100;

                while (--attempts > 0)
                {
                    var randomPoint = monument.Value.Position + UnityEngine.Random.insideUnitSphere * 75f;
                    randomPoint.y = 100f;

                    RaycastHit hit;
                    if (!Physics.Raycast(randomPoint, Vector3.down, out hit, 100.5f, Layers.Solid, QueryTriggerInteraction.Ignore))
                    {
                        continue;
                    }

                    if (IsSafeZone(hit.point))
                    {
                        continue;
                    }

                    if (hit.point.y - TerrainMeta.HeightMap.GetHeight(hit.point) > 3f)
                    {
                        continue;
                    }

                    if (IsLayerBlocked(hit.point, config.Event.Radius + 10f, obstructionLayer))
                    {
                        continue;
                    }

                    return hit.point;
                }
            }

            return Vector3.zero;
        }

        bool IsMonumentPosition(Vector3 target)
        {
            foreach (var monument in monuments)
            {
                if (Vector3Ex.Distance2D(monument.Value.Position, target) < monument.Value.Radius)
                {
                    return true;
                }
            }

            return false;
        }

        Vector3 GetMonumentDropPosition()
        {
            var list = allowedMonuments.ToList();
            var position = Vector3.zero;

            while (position == Vector3.zero && list.Count > 0)
            {
                var mon = list.GetRandom();
                var pos = mon.Value.Position;

                if (!IsTooClose(pos, 1f) && !IsZoneBlocked(pos) && !IsLayerBlocked(pos, config.Event.Radius + 10f, obstructionLayer))
                {
                    position = pos;
                    break;
                }

                list.Remove(mon);
            }

            if (position == Vector3.zero)
            {
                return Vector3.zero;
            }

            var entities = Pool.GetList<BaseEntity>();

            foreach (var e in BaseNetworkable.serverEntities.OfType<BaseEntity>())
            {
                if (!entities.Contains(e) && !e.IsKilled() && InRange2D(e.transform.position, position, config.Event.Radius))
                {
                    if (e.IsNpc || e is LootContainer)
                    {
                        entities.Add(e);
                    }
                }
            }

            if (!config.Monuments.Underground)
            {
                entities.RemoveAll(e => e.transform.position.y < position.y);
            }

            if (entities.Count < 2)
            {
                position = GetRandomMonumentDropPosition(position);
                
                Pool.FreeList(ref entities);

                return position == Vector3.zero ? GetMonumentDropPosition() : position;
            }

            var entity = entities.GetRandom();

            entity.Invoke(entity.SafelyKill, 0.1f);

            Pool.FreeList(ref entities);

            return entity.transform.position;
        }

        void SetupPositions()
        {
            int minPos = (int)(World.Size / -2f);
            int maxPos = (int)(World.Size / 2f);

            for (float x = minPos; x < maxPos; x += 25f)
            {
                for (float z = minPos; z < maxPos; z += 25f)
                {
                    var pos = new Vector3(x, 0f, z);

                    pos.y = GetSpawnHeight(pos);

                    _gridPositions.Add(pos);
                }
            }
        }

        public Vector3 RandomDropPosition()
        {
            if (_gridPositions.Count < 5000)
            {
                SetupPositions();
            }

            return _gridPositions.GetRandom();
        }

        TreasureChest TryOpenEvent(BasePlayer player = null)
        {
            var eventPos = Vector3.zero;

            if (!player.IsKilled())
            {
                RaycastHit hit;

                if (!Physics.Raycast(player.eyes.HeadRay(), out hit, Mathf.Infinity, -1, QueryTriggerInteraction.Ignore))
                {
                    return null;
                }
                
                eventPos = hit.point;
            }
            else
            {
                var randomPos = GetEventPosition();

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
            container.SetFlag(BaseEntity.Flags.Locked, true);
            container.SetFlag(BaseEntity.Flags.OnFire, true);
            container.Spawn();

            var chest = container.gameObject.AddComponent<TreasureChest>();
            chest.Radius = config.Event.Radius;
            
            chest.SpawnLoot(container, ChestLoot);

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

            if (container.enableSaving)
            {
                container.enableSaving = false;
                BaseEntity.saveList.Remove(container);
            }

            uint uid = container.net.ID;
            float unlockTime = UnityEngine.Random.Range(config.Unlock.MinTime, config.Unlock.MaxTime);

            SubscribeHooks(true);
            treasureChests.Add(uid, chest);
            
            var posStr = FormatGridReference(container.transform.position);
            Puts("{0}: {1}", posStr, string.Join(", ", container.inventory.itemList.Select(item => string.Format("{0} ({1})", item.info.displayName.translated, item.amount))));

            //if (!_config.Event.SpawnMax && treasureChests.Count > 1)
            //{
            //    AnnounceEventSpawn(container, unlockTime, posStr);
            //}

            foreach (var target in BasePlayer.activePlayerList)
            {
                double distance = Math.Round(target.transform.position.Distance(container.transform.position), 2);
                string unlockStr = FormatTime(unlockTime, target.UserIDString);

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

                if (config.Event.DrawTreasureIfNearby && config.Event.AutoDrawDistance > 0f && distance <= config.Event.AutoDrawDistance)
                {
                    DrawText(target, container.transform.position, msg("Treasure Chest", target.UserIDString, distance));
                }
            }

            var position = container.transform.position;
            data.TotalEvents++;
            SaveData();

            if (config.LustyMap.Enabled)
                AddLustyMarker(position, uid);

            if (Map.CanCall())
                AddMapMarker(position, uid);

            bool canSpawnNpcs = true;

            foreach (var x in monuments)
            {
                if (InRange2D(x.Value.Position, position, x.Value.Radius))
                {
                    foreach (var value in config.NPC.BlacklistedMonuments)
                    {
                        if (!value.Value && x.Key.Trim() == value.Key.Trim())
                        {
                            canSpawnNpcs = false;
                            break;
                        }
                    }
                    break;
                }
            }

            if (!AiManager.nav_disable && canSpawnNpcs) chest.Invoke(chest.SpawnNpcs, 1f);
            chest.Invoke(() => chest.SetUnlockTime(unlockTime), 2f);

            return chest;
        }

        void AnnounceEventSpawn()
        {
            foreach (var target in BasePlayer.activePlayerList)
            {
                string message = msg("OpenedX", target.UserIDString, config.Settings.DistanceChatCommand);

                if (config.EventMessages.Opened)
                {
                    Player.Message(target, message);
                }

                if (config.GUIAnnouncement.Enabled && GUIAnnouncements.CanCall())
                {
                    foreach (var chest in treasureChests)
                    {
                        double distance = Math.Round(target.transform.position.Distance(chest.Value.containerPos), 2);
                        string unlockStr = FormatTime(chest.Value.countdownTime, target.UserIDString);
                        var posStr = FormatGridReference(chest.Value.containerPos);
                        string text = msg2("Opened", target.UserIDString, posStr, unlockStr, distance, config.Settings.DistanceChatCommand);

                        if (distance <= config.GUIAnnouncement.Distance)
                        {
                            GUIAnnouncements?.Call("CreateAnnouncement", text, config.GUIAnnouncement.TintColor, config.GUIAnnouncement.TextColor, target);
                        }

                        if (config.Event.DrawTreasureIfNearby && config.Event.AutoDrawDistance > 0f && distance <= config.Event.AutoDrawDistance)
                        {
                            DrawText(target, chest.Value.containerPos, msg2("Treasure Chest", target.UserIDString, distance));
                        }
                    }
                }

                if (config.Rocket.Enabled && config.EventMessages.Barrage)
                    Message(target, "Barrage", config.Rocket.Amount);
            }
        }

        void AnnounceEventSpawn(StorageContainer container, float unlockTime, string posStr)
        {
            foreach (var target in BasePlayer.activePlayerList)
            {
                double distance = Math.Round(target.transform.position.Distance(container.transform.position), 2);
                string unlockStr = FormatTime(unlockTime, target.UserIDString);
                string message = msg("Opened", target.UserIDString, posStr, unlockStr, distance, config.Settings.DistanceChatCommand);

                if (config.EventMessages.Opened)
                {
                    Player.Message(target, message);
                }

                if (config.GUIAnnouncement.Enabled && GUIAnnouncements.CanCall() && distance <= config.GUIAnnouncement.Distance)
                {
                    GUIAnnouncements?.Call("CreateAnnouncement", message, config.GUIAnnouncement.TintColor, config.GUIAnnouncement.TextColor, target);
                }

                if (config.Rocket.Enabled && config.EventMessages.Barrage)
                {
                    Message(target, "Barrage", config.Rocket.Amount);
                }

                if (config.Event.DrawTreasureIfNearby && config.Event.AutoDrawDistance > 0f && distance <= config.Event.AutoDrawDistance)
                {
                    DrawText(target, container.transform.position, msg2("Treasure Chest", target.UserIDString, distance));
                }
            }
        }

        void API_SetContainer(StorageContainer container, float radius, bool spawnNpcs) // Expansion Mode for Raidable Bases plugin
        {
            if (!container.IsValid())
            {
                return;
            }

            container.SetFlag(BaseEntity.Flags.Locked, true);
            container.SetFlag(BaseEntity.Flags.OnFire, true);

            var chest = container.gameObject.AddComponent<TreasureChest>();
            float unlockTime = UnityEngine.Random.Range(config.Unlock.MinTime, config.Unlock.MaxTime);

            chest.Radius = radius;
            treasureChests.Add(container.net.ID, chest);
            chest.Invoke(() => chest.SetUnlockTime(unlockTime), 2f);
            data.TotalEvents++;
            SaveData();

            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(OnItemRemovedFromContainer));
            Subscribe(nameof(OnLootEntity));

            if (spawnNpcs)
            {
                Subscribe(nameof(OnNpcTarget));
                Subscribe(nameof(OnNpcResume));
                Subscribe(nameof(OnNpcDestinationSet));
                Subscribe(nameof(OnEntityEnter));
                Subscribe(nameof(OnEntitySpawned));
                Subscribe(nameof(OnPlayerDeath));
                Subscribe(nameof(CanBradleyApcTarget));
                chest.Invoke(chest.SpawnNpcs, 1f);
            }
            else if (config.NewmanMode.Harm)
            {
                Subscribe(nameof(OnEntityEnter));
            }
        }

        float GetMonumentFloat(string monumentName)
        {
            string name = (monumentName.Contains(":") ? monumentName.Substring(0, monumentName.LastIndexOf(":")) : monumentName).TrimEnd();

            switch (name)
            {
                case "Abandoned Cabins":
                    return 54f;
                case "Abandoned Supermarket":
                    return 50f;
                case "Airfield":
                    return 200f;
                case "Bandit Camp":
                    return 125f;
                case "Barn":
                case "Large Barn":
                    return 75f;
                case "Fishing Village":
                case "Large Fishing Village":
                    return 50f;
                case "Junkyard":
                    return 125f;
                case "Giant Excavator Pit":
                    return 225f;
                case "Harbor":
                    return 150f;
                case "HQM Quarry":
                    return 37.5f;
                case "Ice Lake":
                    return 50f;
                case "Large Oil Rig":
                    return 200f;
                case "Launch Site":
                case "Arctic Research Base":
                    return 300f;
                case "Lighthouse":
                    return 48f;
                case "Military Tunnel":
                    return 100f;
                case "Mining Outpost":
                    return 45f;
                case "Oil Rig":
                    return 100f;
                case "Outpost":
                    return 250f;
                case "Oxum's Gas Station":
                    return 65f;
                case "Power Plant":
                    return 140f;
                case "power_sub_small_1":
                case "power_sub_small_2":
                case "power_sub_big_1":
                case "power_sub_big_2":
                    return 30f;
                case "Ranch":
                    return 75f;
                case "Satellite Dish":
                    return 90f;
                case "Sewer Branch":
                    return 100f;
                case "Stone Quarry":
                    return 27.5f;
                case "Sulfur Quarry":
                    return 27.5f;
                case "The Dome":
                    return 70f;
                case "Train Yard":
                    return 150f;
                case "Underwater Lab":
                    return 65f;
                case "Water Treatment Plant":
                    return 185f;
                case "Water Well":
                    return 24f;
                case "Wild Swamp":
                    return 24f;
            }

            return 200f;
        }

        void CheckSecondsUntilEvent()
        {
            var eventInterval = UnityEngine.Random.Range(config.Event.IntervalMin, config.Event.IntervalMax);
            float stamp = Facepunch.Math.Epoch.Current;

            if (data.SecondsUntilEvent == double.MinValue) // first time users
            {
                data.SecondsUntilEvent = stamp + eventInterval;
                Puts(_("Next Automated Event", null, FormatTime(eventInterval), DateTime.Now.AddSeconds(eventInterval).ToString()));
                //Puts(_("View Config"));
                SaveData();
            }

            float time = 1f;

            if (config.Event.Automated && data.SecondsUntilEvent - stamp <= 0 && treasureChests.Count < config.Event.Max && BasePlayer.activePlayerList.Count >= config.Event.PlayerLimit)
            {
                bool save = false;

                if (config.Event.SpawnMax)
                {
                    save = TryOpenEvent() != null && treasureChests.Count >= config.Event.Max;
                }
                else save = TryOpenEvent() != null;

                if (save)
                {
                    if (config.Event.SpawnMax && treasureChests.Count > 1)
                    {
                        AnnounceEventSpawn();
                    }

                    data.SecondsUntilEvent = stamp + eventInterval;
                    Puts(_("Next Automated Event", null, FormatTime(eventInterval), DateTime.Now.AddSeconds(eventInterval).ToString()));
                    SaveData();
                }
                else time = config.Event.Stagger;
            }

            timer.Once(time, CheckSecondsUntilEvent);
        }

        public static string FormatGridReference(Vector3 position)
        {
            string monumentName = null;
            float distance = 10000f;

            foreach (var x in Instance.monuments) // request MrSmallZzy
            {
                float magnitude = x.Value.Position.Distance(position);
                
                if (magnitude <= x.Value.Radius && magnitude < distance)
                {
                    monumentName = (x.Key.Contains(":") ? x.Key.Substring(0, x.Key.LastIndexOf(":")) : x.Key).TrimEnd();
                    distance = magnitude;
                }
            }

            if (config.Settings.ShowXZ)
            {
                string pos = string.Format("{0} {1}", position.x.ToString("N2"), position.z.ToString("N2"));
                return string.IsNullOrEmpty(monumentName) ? pos : $"{monumentName} ({pos})";
            }

            return string.IsNullOrEmpty(monumentName) ? PhoneController.PositionToGridCoord(position) : $"{monumentName} ({PhoneController.PositionToGridCoord(position)})";
        }

        string FormatTime(double seconds, string id = null)
        {
            if (seconds == 0)
            {
                return BasePlayer.activePlayerList.Count < config.Event.PlayerLimit ? msg2("Not Enough Online", id, config.Event.PlayerLimit) : "0s";
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

                if (target.UserHasGroup(config.RankedLadder.Group))
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

        void AddMapMarker(Vector3 position, uint uid)
        {
            mapMarkers[uid] = new MapInfo { IconName = config.LustyMap.IconName, Position = position, Url = config.LustyMap.IconFile };
            Map?.Call("ApiAddPointUrl", config.LustyMap.IconFile, config.LustyMap.IconName, position);
            data.Markers.Add(uid);
        }

        void RemoveMapMarker(uint uid)
        {
            if (!mapMarkers.ContainsKey(uid))
                return;

            var mapInfo = mapMarkers[uid];

            Map?.Call("ApiRemovePointUrl", mapInfo.Url, mapInfo.IconName, mapInfo.Position);
            mapMarkers.Remove(uid);
            data.Markers.Remove(uid);
        }

        void AddLustyMarker(Vector3 pos, uint uid)
        {
            string name = string.Format("{0}_{1}", config.LustyMap.IconName, data.TotalEvents).ToLower();

            LustyMap?.Call("AddTemporaryMarker", pos.x, pos.z, name, config.LustyMap.IconFile, config.LustyMap.IconRotation);
            lustyMarkers[uid] = name;
            data.Markers.Add(uid);
        }

        void RemoveLustyMarker(uint uid)
        {
            if (!lustyMarkers.ContainsKey(uid))
                return;

            LustyMap?.Call("RemoveTemporaryMarker", lustyMarkers[uid]);
            lustyMarkers.Remove(uid);
            data.Markers.Remove(uid);
        }

        void RemoveAllTemporaryMarkers()
        {
            if (data.Markers.Count == 0)
                return;

            if (LustyMap.CanCall())
            {
                foreach (uint marker in data.Markers)
                {
                    LustyMap.Call("RemoveMarker", marker.ToString());
                }
            }

            if (Map.CanCall())
            {
                foreach (uint marker in data.Markers.ToList())
                {
                    RemoveMapMarker(marker);
                }
            }

            data.Markers.Clear();
            SaveData();
        }

        void RemoveAllMarkers()
        {
            int removed = 0;

            for (int i = 0; i < data.TotalEvents + 1; i++)
            {
                string name = string.Format("{0}_{1}", config.LustyMap.IconName, i).ToLower();

                if (Convert.ToBoolean(LustyMap?.Call("RemoveMarker", name)))
                {
                    removed++;
                }
            }

            data.Markers.Clear();

            if (removed > 0)
            {
                Puts("Removed {0} existing markers", removed);
            }
            else Puts("No markers found");
        }

        void DrawText(BasePlayer player, Vector3 drawPos, string text)
        {
            if (!player || !player.IsConnected || drawPos == Vector3.zero || string.IsNullOrEmpty(text) || config.Event.DrawTime < 1f)
                return;

            bool isAdmin = player.IsAdmin;

            try
            {
                if (config.Event.GrantDraw && !player.IsAdmin)
                {
                    var uid = player.userID;

                    if (!drawGrants.Contains(uid))
                    {
                        drawGrants.Add(uid);
                        timer.Once(config.Event.DrawTime, () => drawGrants.Remove(uid));
                    }

                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                if (player.IsAdmin || drawGrants.Contains(player.userID))
                    player.SendConsoleCommand("ddraw.text", config.Event.DrawTime, Color.yellow, drawPos, text);
            }
            catch (Exception ex)
            {
                config.Event.GrantDraw = false;
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

                int amount;
                if (int.TryParse(args[1], out amount))
                {
                    if (itemDef.stackable == 1 || (itemDef.condition.enabled && itemDef.condition.max > 0f) || amount < 1)
                        amount = 1;

                    ulong skin = 0uL;

                    if (args.Length >= 3)
                    {
                        ulong num;
                        if (ulong.TryParse(args[2], out num)) skin = num;
                        else Message(player, "InvalidValue", args[2]);
                    }

                    int minAmount = amount;
                    if (args.Length >= 4)
                    {
                        int num;
                        if (int.TryParse(args[3], out num))
                            minAmount = num;
                        else
                            Message(player, "InvalidValue", args[3]);
                    }

                    foreach (var loot in ChestLoot)
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
                    var ladder = data.Players.ToDictionary(k => k.Key, v => args[0].ToLower() == "ladder" ? v.Value.StolenChestsSeed : v.Value.StolenChestsTotal).Where(kvp => kvp.Value > 0).ToList();
                    ladder.Sort((x, y) => y.Value.CompareTo(x.Value));

                    Message(player, args[0].ToLower() == "ladder" ? "Ladder" : "Ladder Total");

                    foreach (var kvp in ladder.Take(10))
                    {
                        string name = covalence.Players.FindPlayerById(kvp.Key)?.Name ?? kvp.Key;
                        string value = kvp.Value.ToString("N0");

                        Message(player, "TreasureHunter", ++rank, name, value);
                    }

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
                else if (args[0] == "markers")
                {
                    RemoveAllMarkers();
                    return;
                }
                else if (args[0] == "resettime")
                {
                    data.SecondsUntilEvent = double.MinValue;
                    return;
                }
                else if (args[0] == "now")
                {
                    data.SecondsUntilEvent = Facepunch.Math.Epoch.Current;
                    return;
                }
                else if (args[0] == "tp" && treasureChests.Count > 0)
                {
                    float dist = float.MaxValue;
                    var position = Vector3.zero;

                    foreach (var entry in treasureChests)
                    {
                        var v3 = entry.Value.containerPos.Distance(player.transform.position);

                        if (treasureChests.Count > 1 && v3 < 25f) // 0.2.0 fix - move admin to the next nearest chest
                            continue;

                        if (v3 < dist)
                        {
                            dist = v3;
                            position = entry.Value.containerPos;
                        }
                    }

                    if (position != Vector3.zero) 
                    {
                        if (player.IsFlying) 
                        {
                            player.Teleport(position.y > player.transform.position.y ? position : position.WithY(player.transform.position.y)); 
                        }
                        else player.Teleport(position);
                    }
                }
                else if (args[0].ToLower() == "additem")
                {
                    AddItem(player, args.Skip(1));
                    return;
                }
                else if (args[0].ToLower() == "showdebuggrid")
                {
                    if (_gridPositions.Count < 5000) SetupPositions();
                    _gridPositions.ForEach(pos =>
                    {
                        if (player.Distance(pos) > 1000f) return;
                        player.SendConsoleCommand("ddraw.text", 30f, Color.green, pos, "X");
                    });
                    return;
                }
                else if (args[0].ToLower() == "testblocked")
                {
                    Player.Message(player, $"IsLayerBlocked: {IsLayerBlocked(player.transform.position, 25f, obstructionLayer)}");
                    Player.Message(player, $"SafeZone: {IsSafeZone(player.transform.position)}");

                    var entities = new List<BaseNetworkable>();

                    foreach (var e in BaseNetworkable.serverEntities.OfType<BaseEntity>())
                    {
                        if (!entities.Contains(e) && InRange2D(e.transform.position, player.transform.position, config.Event.Radius))
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
                double time = Math.Max(0, data.SecondsUntilEvent - Facepunch.Math.Epoch.Current);
                Message(player, "Next", FormatTime(time, player.UserIDString));
                return;
            }

            foreach (var chest in treasureChests)
            {
                double distance = Math.Round(player.transform.position.Distance(chest.Value.containerPos), 2);
                string posStr = FormatGridReference(chest.Value.containerPos);

                if (chest.Value.GetUnlockTime() != null)
                {
                    Message(player, "Info", chest.Value.GetUnlockTime(player.UserIDString), posStr, distance, config.Settings.DistanceChatCommand);
                }
                else Message(player, "Already", posStr, distance, config.Settings.DistanceChatCommand);

                if (config.Settings.AllowDrawText)
                {
                    DrawText(player, chest.Value.containerPos, msg2("Treasure Chest", player.UserIDString, distance));
                }
            }
        }

        private void ccmdDangerousTreasures(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();
            var args = arg.Args ?? new string[0];

            if (!arg.IsAdmin)
            {
                if (player == null || !permission.UserHasPermission(player.UserIDString, config.Settings.PermName))
                {
                    Message(arg, "No Permission");
                    return;
                }
            }

            if (args.Length == 1)
            {
                if (args[0].ToLower() == "help")
                {
                    if (player == null)
                    {
                        Puts("Monuments:");
                        foreach (var m in monuments) Puts(m.Key);
                    }

                    Message(arg, "Help", config.Settings.EventChatCommand);
                }
                else if (args[0].ToLower() == "5sec") data.SecondsUntilEvent = Facepunch.Math.Epoch.Current + 5f;

                return;
            }

            var position = Vector3.zero;
            bool isTeleport = false;
            int num = 0, amount = 0;

            for (int i = 0; i < args.Length; i++)
            {
                int num2;
                if (int.TryParse(args[i], out num2))
                {
                    amount = num2;
                }
                else if (args[i].Equals("tp", StringComparison.OrdinalIgnoreCase))
                {
                    isTeleport = true;
                }
            }

            if (amount < 1)
            {
                amount = 1;
            }

            for (int i = 0; i < amount; i++)
            {
                if (treasureChests.Count >= config.Event.Max && !arg.IsAdmin)
                {
                    Message(arg, "Max Manual Events", config.Event.Max);
                    break;
                }

                var chest = TryOpenEvent();

                if (chest != null)
                {
                    position = chest.containerPos;
                    num++;
                }
            }

            if (position != Vector3.zero)
            {
                if (args.Length > 0 && isTeleport && !player.IsKilled() && player.IsAdmin)
                {
                    if (player.IsFlying)
                    {
                        player.Teleport(position.y > player.transform.position.y ? position : position.WithY(player.transform.position.y));
                    }
                    else player.Teleport(position);
                }
            }
            else Message(arg, "Manual Event Failed");

            if (num > 1)
            {
                Message(arg, "OpenedEvents", num, amount);
            }
        }

        void cmdDangerousTreasures(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, config.Settings.PermName) && !player.IsAdmin)
            {
                Message(player, "No Permission");
                return;
            }

            if (args.Length == 1)
            {
                var arg = args[0].ToLower();

                if (arg == "help")
                {
                    Message(player, "Monuments: " + string.Join(", ", monuments.Select(m => m.Key)));
                    Message(player, "Help", config.Settings.EventChatCommand);
                    return;
                }
                else if (player.IsAdmin)
                {
                    if (arg == "custom")
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
                }
            }

            if (treasureChests.Count >= config.Event.Max && player.net.connection.authLevel < 2)
            {
                Message(player, "Max Manual Events", config.Event.Max);
                return;
            }

            var chest = TryOpenEvent(args.Length == 1 && args[0] == "me" && player.IsAdmin ? player : null);
            
            if (chest != null)
            {
                if (args.Length == 1 && args[0].ToLower() == "tp" && player.IsAdmin)
                {
                    if (player.IsFlying)
                    {
                        player.Teleport(chest.containerPos.y > player.transform.position.y ? chest.containerPos : chest.containerPos.WithY(player.transform.position.y));
                    }
                    else player.Teleport(chest.containerPos);
                }
            }
            else
            {
                Message(player, "Manual Event Failed");
            }
        }

        #region Config

        Dictionary<string, string> GetMessages()
        {
            return new Dictionary<string, string>
            {
                {"No Permission", "You do not have permission to use this command."},
                {"Building is blocked!", "<color=#FF0000>Building is blocked near treasure chests!</color>"},
                {"Max Manual Events", "Maximum number of manual events <color=#FF0000>{0}</color> has been reached!"},
                {"Dangerous Zone Protected", "<color=#FF0000>You have entered a dangerous zone protected by a fire aura! You must leave before you die!</color>"},
                {"Dangerous Zone Unprotected", "<color=#FF0000>You have entered a dangerous zone!</color>"},
                {"Manual Event Failed", "Event failed to start! Unable to obtain a valid position. Please try again."},
                {"Help", "/{0} <tp> - start a manual event, and teleport to the position if TP argument is specified and you are an admin."},
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
                {"View Config", "Please view the config if you haven't already."},
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

        static int GetPercentIncreasedAmount(int amount)
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
            Color color;
            if (!ColorUtility.TryParseHtmlString(hex.StartsWith("#") ? hex : $"#{hex}", out color))
            {
                color = Color.red;
            }

            return color;
        }

        static string _(string key, string id = null, params object[] args)
        {
            return Instance.RemoveFormatting(Instance.msg(key, id, args));
        }

        string msg(string key, string id = null, params object[] args)
        {
            string message = config.EventMessages.Prefix && id != null && id != "server_console" ? lang.GetMessage("MessagePrefix", this, null) + lang.GetMessage(key, this, id) : lang.GetMessage(key, this, id);

            return message.Contains("{0}") ? string.Format(message, args) : message;
        }

        string msg2(string key, string id, params object[] args)
        {
            string message = lang.GetMessage(key, this, id);

            return message.Contains("{0}") ? string.Format(message, args) : message;
        }

        string RemoveFormatting(string source) => source.Contains(">") ? System.Text.RegularExpressions.Regex.Replace(source, "<.*?>", string.Empty) : source;

        static void Message(BasePlayer player, string key, params object[] args)
        {
            if (player == null) return;
            string message = Instance.msg(key, player.UserIDString, args);

            if (string.IsNullOrEmpty(message))
            {
                return;
            }

            if (config.EventMessages.Message)
            {
                Instance.Player.Message(player, message, 0uL);
            }

            if (config.EventMessages.AA.Enabled || config.EventMessages.NotifyType != -1)
            {
                List<Notification> notifications;
                if (!Instance._notifications.TryGetValue(player.userID, out notifications))
                {
                    Instance._notifications[player.userID] = notifications = new List<Notification>();
                }

                notifications.Add(new Notification
                {
                    player = player,
                    messageEx = message
                });
            }
        }

        static void Message(IPlayer user, string key, params object[] args)
        {
            if (user != null)
            {
                user.Reply(Instance.msg2(key, user.Id, args));
            }
        }

        static void Message(ConsoleSystem.Arg arg, string key, params object[] args)
        {
            if (arg != null)
            {
                arg.ReplyWith(Instance.msg2(key, arg.Player()?.UserIDString, args));
            }
        }

        public class Notification
        {
            public BasePlayer player;
            public string messageEx;
        }

        private Dictionary<ulong, List<Notification>> _notifications = new Dictionary<ulong, List<Notification>>();

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

        private static void SendNotification(Notification notification)
        {
            if (!notification.player.IsReallyConnected())
            {
                return;
            }

            if (config.EventMessages.AA.Enabled && Instance.AdvancedAlerts.CanCall())
            {
                Instance.AdvancedAlerts?.Call("SpawnAlert", notification.player, "hook", notification.messageEx, config.EventMessages.AA.AnchorMin, config.EventMessages.AA.AnchorMax, config.EventMessages.AA.Time);
            }

            if (config.EventMessages.NotifyType != -1 && Instance.Notify.CanCall())
            {
                Instance.Notify?.Call("SendNotify", notification.player, config.EventMessages.NotifyType, notification.messageEx);
            }
        }

        #endregion

        #region Configuration

        private static Configuration config;

        private static List<LootItem> DefaultLoot
        {
            get
            {
                return new List<LootItem>
                {
                    new LootItem { shortname = "ammo.pistol", amount = 40, skin = 0, amountMin = 40 },
                    new LootItem { shortname = "ammo.pistol.fire", amount = 40, skin = 0, amountMin = 40 },
                    new LootItem { shortname = "ammo.pistol.hv", amount = 40, skin = 0, amountMin = 40 },
                    new LootItem { shortname = "ammo.rifle", amount = 60, skin = 0, amountMin = 60 },
                    new LootItem { shortname = "ammo.rifle.explosive", amount = 60, skin = 0, amountMin = 60 },
                    new LootItem { shortname = "ammo.rifle.hv", amount = 60, skin = 0, amountMin = 60 },
                    new LootItem { shortname = "ammo.rifle.incendiary", amount = 60, skin = 0, amountMin = 60 },
                    new LootItem { shortname = "ammo.shotgun", amount = 24, skin = 0, amountMin = 24 },
                    new LootItem { shortname = "ammo.shotgun.slug", amount = 40, skin = 0, amountMin = 40 },
                    new LootItem { shortname = "surveycharge", amount = 20, skin = 0, amountMin = 20 },
                    new LootItem { shortname = "metal.refined", amount = 150, skin = 0, amountMin = 150 },
                    new LootItem { shortname = "bucket.helmet", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "cctv.camera", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "coffeecan.helmet", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "explosive.timed", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "metal.facemask", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "metal.plate.torso", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "mining.quarry", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "pistol.m92", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "rifle.ak", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "rifle.bolt", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "rifle.lr300", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "smg.2", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "smg.mp5", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "smg.thompson", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "supply.signal", amount = 1, skin = 0, amountMin = 1 },
                    new LootItem { shortname = "targeting.computer", amount = 1, skin = 0, amountMin = 1 },
                };
            }
        }

        public class PluginSettings
        {
            [JsonProperty(PropertyName = "Permission Name")]
            public string PermName { get; set; } = "dangeroustreasures.use";

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
        }

        public class CountdownSettings
        {
            [JsonProperty(PropertyName = "Use Countdown Before Event Starts")]
            public bool Enabled { get; set; } = false;

            [JsonProperty(PropertyName = "Time In Seconds", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<int> Times { get; set; } = new List<int> { 120, 60, 30, 15 };
        }

        public class EventSettings
        {
            [JsonProperty(PropertyName = "Automated")]
            public bool Automated { get; set; } = false;

            [JsonProperty(PropertyName = "Every Min Seconds")]
            public float IntervalMin { get; set; } = 3600f;

            [JsonProperty(PropertyName = "Every Max Seconds")]
            public float IntervalMax { get; set; } = 7200f;

            [JsonProperty(PropertyName = "Use Vending Map Marker")]
            public bool MarkerVending { get; set; } = true;

            [JsonProperty(PropertyName = "Use Explosion Map Marker")]
            public bool MarkerExplosion { get; set; } = false;

            [JsonProperty(PropertyName = "Marker Color")]
            public string MarkerColor { get; set; } = "#FF0000";

            [JsonProperty(PropertyName = "Marker Radius")]
            public float MarkerRadius { get; set; } = 0.25f;

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
            public UIAdvancedAlertSettings AA { get; set; } = new UIAdvancedAlertSettings();

            [JsonProperty(PropertyName = "Notify Plugin - Type (-1 = disabled)")]
            public int NotifyType { get; set; }

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

        public class LustyMapSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Icon Name")]
            public string IconName { get; set; } = "dtchest";

            [JsonProperty(PropertyName = "Icon File")]
            public string IconFile { get; set; } = "http://i.imgur.com/XoEMTJj.png";

            [JsonProperty(PropertyName = "Icon Rotation")]
            public float IconRotation { get; set; } = 0f;
        }

        public class MissileLauncherSettings
        {
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
            [JsonProperty(PropertyName = "Blacklisted Monuments", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, bool> Blacklist { get; set; } = new Dictionary<string, bool>
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


        public class MurdererKitSettings
        {
            [JsonProperty(PropertyName = "Helm", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Helm { get; set; } = new List<string> { "metal.facemask" };

            [JsonProperty(PropertyName = "Torso", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Torso { get; set; } = new List<string> { "metal.plate.torso" };

            [JsonProperty(PropertyName = "Pants", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Pants { get; set; } = new List<string> { "pants" };

            [JsonProperty(PropertyName = "Gloves", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Gloves { get; set; } = new List<string> { "tactical.gloves" };

            [JsonProperty(PropertyName = "Boots", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Boots { get; set; } = new List<string> { "boots.frog" };

            [JsonProperty(PropertyName = "Shirt", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Shirt { get; set; } = new List<string> { "tshirt" };

            [JsonProperty(PropertyName = "Kilts", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Kilts { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Weapon", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Weapon { get; set; } = new List<string> { "machete" };
        }

        public class ScientistKitSettings
        {
            [JsonProperty(PropertyName = "Helm", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Helm { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Torso", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Torso { get; set; } = new List<string> { "hazmatsuit_scientist_peacekeeper" };

            [JsonProperty(PropertyName = "Pants", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Pants { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Gloves", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Gloves { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Boots", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Boots { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Shirt", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Shirt { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Kilts", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Kilts { get; set; } = new List<string>();

            [JsonProperty(PropertyName = "Weapon", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Weapon { get; set; } = new List<string> { "rifle.ak" };
        }

        public class NpcLootSettings
        {
            [JsonProperty(PropertyName = "Prefab ID List", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> IDs { get; set; } = new List<string> { "cargo", "turret_any", "ch47_gunner", "excavator", "full_any", "heavy", "junkpile_pistol", "oilrig", "patrol", "peacekeeper", "roam", "roamtethered" };

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; }

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
            [JsonProperty(PropertyName = "AK47")]
            public double AK47 { get; set; }

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

            public NpcSettingsAccuracy(double accuracy)
            {
                AK47 = BOLT_RIFLE = COMPOUND_BOW = CROSSBOW = DOUBLE_SHOTGUN = EOKA = L96 = LR300 = M249 = M39 = M92 = MP5 = NAILGUN = PUMP_SHOTGUN = PYTHON = REVOLVER = SEMI_AUTO_PISTOL = SEMI_AUTO_RIFLE = SPAS12 = SPEARGUN = SMG = SNOWBALL_GUN = THOMPSON = WATERPIPE_SHOTGUN = accuracy;
            }

            public double Get(HumanoidBrain brain)
            {
                switch (brain.AttackEntity?.ShortPrefabName)
                {
                    case "ak47u.entity": return AK47;
                    case "bolt_rifle.entity": return BOLT_RIFLE;
                    case "compound_bow.entity": return COMPOUND_BOW;
                    case "crossbow.entity": return CROSSBOW;
                    case "double_shotgun.entity": return DOUBLE_SHOTGUN;
                    case "l96.entity": return L96;
                    case "lr300.entity": return LR300;
                    case "m249.entity": return M249;
                    case "m39.entity": return M39;
                    case "m92.entity": return M92;
                    case "mp5.entity": return MP5;
                    case "nailgun.entity": return NAILGUN;
                    case "pistol_eoka.entity": return EOKA;
                    case "pistol_revolver.entity": return REVOLVER;
                    case "pistol_semiauto.entity": return SEMI_AUTO_PISTOL;
                    case "python.entity": return PYTHON;
                    case "semi_auto_rifle.entity": return SEMI_AUTO_RIFLE;
                    case "shotgun_pump.entity": return PUMP_SHOTGUN;
                    case "shotgun_waterpipe.entity": return WATERPIPE_SHOTGUN;
                    case "spas12.entity": return SPAS12;
                    case "speargun.entity": return SPEARGUN;
                    case "smg.entity": return SMG;
                    case "snowballgun.entity": return SNOWBALL_GUN;
                    case "thompson.entity": default: return THOMPSON;
                }
            }
        }

        public class NpcSettingsMurderer
        {
            [JsonProperty(PropertyName = "Random Names", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> RandomNames { get; set; } = new List<string>(); 
            
            [JsonProperty(PropertyName = "Items)")]
            public MurdererKitSettings Items { get; set; } = new MurdererKitSettings();

            [JsonProperty(PropertyName = "Kits", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Kits { get; set; } = new List<string> { "murderer_kit_1", "murderer_kit_2" };

            [JsonProperty(PropertyName = "Spawn Alternate Loot")]
            public NpcLootSettings Alternate { get; set; } = new NpcLootSettings();

            [JsonProperty(PropertyName = "Weapon Accuracy (0 - 100)")]
            public NpcSettingsAccuracy Accuracy { get; set; } = new NpcSettingsAccuracy(100);

            [JsonProperty(PropertyName = "Aggression Range")]
            public float AggressionRange { get; set; } = 70f;

            [JsonProperty(PropertyName = "Despawn Inventory On Death")]
            public bool DespawnInventory { get; set; } = true;

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
            [JsonProperty(PropertyName = "Random Names", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> RandomNames { get; set; } = new List<string>(); 
            
            [JsonProperty(PropertyName = "Items")]
            public ScientistKitSettings Items { get; set; } = new ScientistKitSettings();

            [JsonProperty(PropertyName = "Kits", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Kits { get; set; } = new List<string> { "scientist_kit_1", "scientist_kit_2" };

            [JsonProperty(PropertyName = "Spawn Alternate Loot")]
            public NpcLootSettings Alternate { get; set; } = new NpcLootSettings();

            [JsonProperty(PropertyName = "Weapon Accuracy (0 - 100)")]
            public NpcSettingsAccuracy Accuracy { get; set; } = new NpcSettingsAccuracy(20);
            
            [JsonProperty(PropertyName = "Aggression Range")]
            public float AggressionRange { get; set; } = 70f;

            [JsonProperty(PropertyName = "Despawn Inventory On Death")]
            public bool DespawnInventory { get; set; } = true;

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
            [JsonProperty(PropertyName = "Blacklisted Monuments", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public Dictionary<string, bool> BlacklistedMonuments { get; set; } = new Dictionary<string, bool>
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

            [JsonProperty(PropertyName = "Murderers")]
            public NpcSettingsMurderer Murderers { get; set; } = new NpcSettingsMurderer();

            [JsonProperty(PropertyName = "Scientists")]
            public NpcSettingsScientist Scientists { get; set; } = new NpcSettingsScientist();

            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Allow Npcs To Leave Dome When Attacking")]
            public bool CanLeave { get; set; } = true;

            [JsonProperty(PropertyName = "Allow Npcs To Target Other Npcs")]
            public bool TargetNpcs { get; set; }

            [JsonProperty(PropertyName = "Block Damage From Players Beyond X Distance (0 = disabled)")]
            public float Range { get; set; } = 0f;

            [JsonProperty(PropertyName = "Block Npc Kits Plugin")]
            public bool BlockNpcKits { get; set; }

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
            public List<ulong> Custom { get; set; } = new List<ulong>();

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
            public string shortname { get; set; } = "";
            public string name { get; set; } = "";
            public int amount { get; set; }
            public ulong skin { get; set; }
            public int amountMin { get; set; }
            public float probability { get; set; } = 1f;
            [JsonProperty(PropertyName = "Skins", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<ulong> skins { get; set; } = new List<ulong>();
        }

        public class TreasureSettings
        {
            [JsonProperty(PropertyName = "Loot", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> Loot { get; set; } = DefaultLoot;

            [JsonProperty(PropertyName = "Minimum Percent Loss")]
            public decimal PercentLoss { get; set; } = 0;

            [JsonProperty(PropertyName = "Percent Increase When Using Day Of Week Loot")]
            public bool Increased { get; set; } = false;

            [JsonProperty(PropertyName = "Use Random Skins")]
            public bool RandomSkins { get; set; } = false;

            [JsonProperty(PropertyName = "Include Workshop Skins")]
            public bool RandomWorkshopSkins { get; set; } = false;

            [JsonProperty(PropertyName = "Day Of Week Loot Monday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Monday { get; set; } = new List<LootItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Tuesday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Tuesday { get; set; } = new List<LootItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Wednesday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Wednesday { get; set; } = new List<LootItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Thursday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Thursday { get; set; } = new List<LootItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Friday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Friday { get; set; } = new List<LootItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Saturday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Saturday { get; set; } = new List<LootItem>();

            [JsonProperty(PropertyName = "Day Of Week Loot Sunday", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<LootItem> DOWL_Sunday { get; set; } = new List<LootItem>();

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
        }

        public class UnlootedAnnouncementSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = false;

            [JsonProperty(PropertyName = "Notify Every X Minutes (Minimum 1)")]
            public float Interval { get; set; } = 3f;
        }

        public class Configuration
        {
            [JsonProperty(PropertyName = "Settings")]
            public PluginSettings Settings = new PluginSettings();

            [JsonProperty(PropertyName = "Countdown")]
            public CountdownSettings Countdown = new CountdownSettings();

            [JsonProperty(PropertyName = "Events")]
            public EventSettings Event = new EventSettings();

            [JsonProperty(PropertyName = "Event Messages")]
            public EventMessageSettings EventMessages = new EventMessageSettings();

            [JsonProperty(PropertyName = "Fireballs")]
            public FireballSettings Fireballs = new FireballSettings();

            [JsonProperty(PropertyName = "GUIAnnouncements")]
            public GUIAnnouncementSettings GUIAnnouncement = new GUIAnnouncementSettings();

            [JsonProperty(PropertyName = "Lusty Map")]
            public LustyMapSettings LustyMap = new LustyMapSettings();

            [JsonProperty(PropertyName = "Monuments")]
            public MonumentSettings Monuments = new MonumentSettings();

            [JsonProperty(PropertyName = "Newman Mode")]
            public NewmanModeSettings NewmanMode = new NewmanModeSettings();

            [JsonProperty(PropertyName = "NPCs")]
            public NpcSettings NPC = new NpcSettings();

            [JsonProperty(PropertyName = "Missile Launcher")]
            public MissileLauncherSettings MissileLauncher = new MissileLauncherSettings();

            [JsonProperty(PropertyName = "Ranked Ladder")]
            public RankedLadderSettings RankedLadder = new RankedLadderSettings();

            [JsonProperty(PropertyName = "Rewards")]
            public RewardSettings Rewards = new RewardSettings();

            [JsonProperty(PropertyName = "Rocket Opener")]
            public RocketOpenerSettings Rocket = new RocketOpenerSettings();

            [JsonProperty(PropertyName = "Skins")]
            public SkinSettings Skins = new SkinSettings();

            [JsonProperty(PropertyName = "Treasure")]
            public TreasureSettings Treasure = new TreasureSettings();

            [JsonProperty(PropertyName = "TruePVE")]
            public TruePVESettings TruePVE = new TruePVESettings();

            [JsonProperty(PropertyName = "Unlock Time")]
            public UnlockSettings Unlock = new UnlockSettings();

            [JsonProperty(PropertyName = "Unlooted Announcements")]
            public UnlootedAnnouncementSettings UnlootedAnnouncements = new UnlootedAnnouncementSettings();
        }

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                config = Config.ReadObject<Configuration>();
                if (config == null) throw new Exception("config is null");
                ValidateConfig();
                SaveConfig();
            }
            catch (Exception ex)
            {
                Debug.LogException(ex);
                LoadDefaultConfig();
            }
        }

        private void ValidateConfig()
        {
            if (config.Rocket.Speed > 0.1f) config.Rocket.Speed = 0.1f;
            if (config.Treasure.PercentLoss > 0) config.Treasure.PercentLoss /= 100m;
            if (config.Monuments.Chance < 0) config.Monuments.Chance = 0f;
            if (config.Monuments.Chance > 1f) config.Monuments.Chance /= 100f;
            if (config.Event.Radius < 10f) config.Event.Radius = 10f;
            if (config.Event.Radius > 150f) config.Event.Radius = 150f;
            if (config.MissileLauncher.Distance < 1f) config.MissileLauncher.Distance = 15f;
            if (config.MissileLauncher.Distance > config.Event.Radius * 15) config.MissileLauncher.Distance = config.Event.Radius * 2;
            if (config.LustyMap.IconFile == "special") config.LustyMap.IconFile = "http://i.imgur.com/XoEMTJj.png";

            if (!string.IsNullOrEmpty(config.Settings.PermName) && !permission.PermissionExists(config.Settings.PermName)) permission.RegisterPermission(config.Settings.PermName, this);
            if (!string.IsNullOrEmpty(config.Settings.EventChatCommand)) cmd.AddChatCommand(config.Settings.EventChatCommand, this, cmdDangerousTreasures);
            if (!string.IsNullOrEmpty(config.Settings.DistanceChatCommand)) cmd.AddChatCommand(config.Settings.DistanceChatCommand, this, cmdTreasureHunter);
            if (!string.IsNullOrEmpty(config.Settings.EventConsoleCommand)) cmd.AddConsoleCommand(config.Settings.EventConsoleCommand, this, nameof(ccmdDangerousTreasures));
            if (string.IsNullOrEmpty(config.RankedLadder.Permission)) config.RankedLadder.Permission = "dangeroustreasures.th";
            if (string.IsNullOrEmpty(config.RankedLadder.Group)) config.RankedLadder.Group = "treasurehunter";
            if (string.IsNullOrEmpty(config.LustyMap.IconFile) || string.IsNullOrEmpty(config.LustyMap.IconName)) config.LustyMap.Enabled = false;

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

            if (config.UnlootedAnnouncements.Interval < 1f) config.UnlootedAnnouncements.Interval = 1f;
            if (config.Event.AutoDrawDistance < 0f) config.Event.AutoDrawDistance = 0f;
            if (config.Event.AutoDrawDistance > ConVar.Server.worldsize) config.Event.AutoDrawDistance = ConVar.Server.worldsize;
            if (config.GUIAnnouncement.TintColor.ToLower() == "black") config.GUIAnnouncement.TintColor = "grey";
            if (config.NPC.Murderers.SpawnAmount + config.NPC.Scientists.SpawnAmount < 1) config.NPC.Enabled = false;
            if (config.NPC.Murderers.SpawnAmount > 25) config.NPC.Murderers.SpawnAmount = 25;
            if (config.NPC.Scientists.SpawnAmount > 25) config.NPC.Scientists.SpawnAmount = 25;
        }

        List<LootItem> ChestLoot
        {
            get
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

                return config.Treasure.Loot;
            }
        }

        protected override void SaveConfig() => Config.WriteObject(config);

        protected override void LoadDefaultConfig() => config = new Configuration();

        #endregion
    }
}

namespace Oxide.Plugins.DangerousTreasuresExtensionMethods
{
    public static class ExtensionMethods
    {
        internal static Core.Libraries.Permission p;
        public static bool All<T>(this IEnumerable<T> a, Func<T, bool> b) { foreach (T c in a) { if (!b(c)) { return false; } } return true; }
        public static T ElementAt<T>(this IEnumerable<T> a, int b) { using (var c = a.GetEnumerator()) { while (c.MoveNext()) { if (b == 0) { return c.Current; } b--; } } return default(T); }
        public static bool Exists<T>(this IEnumerable<T> a, Func<T, bool> b = null) { using (var c = a.GetEnumerator()) { while (c.MoveNext()) { if (b == null || b(c.Current)) { return true; } } } return false; }
        public static T FirstOrDefault<T>(this IEnumerable<T> a, Func<T, bool> b = null) { using (var c = a.GetEnumerator()) { while (c.MoveNext()) { if (b == null || b(c.Current)) { return c.Current; } } } return default(T); }
        public static int RemoveAll<TKey, TValue>(this IDictionary<TKey, TValue> c, Func<TKey, TValue, bool> d) { int a = 0; foreach (var b in c.ToList()) { if (d(b.Key, b.Value)) { c.Remove(b.Key); a++; } } return a; }
        public static IEnumerable<V> Select<T, V>(this IEnumerable<T> a, Func<T, V> b) { var c = new List<V>(); using (var d = a.GetEnumerator()) { while (d.MoveNext()) { c.Add(b(d.Current)); } } return c; }
        public static string[] Skip(this string[] a, int b) { if (a.Length == 0) { return Array.Empty<string>(); } string[] c = new string[a.Length - b]; int n = 0; for (int i = 0; i < a.Length; i++) { if (i < b) continue; c[n] = a[i]; n++; } return c; }
        public static List<T> Take<T>(this IList<T> a, int b) { var c = new List<T>(); for (int i = 0; i < a.Count; i++) { if (c.Count == b) { break; } c.Add(a[i]); } return c; }
        public static Dictionary<T, V> ToDictionary<S, T, V>(this IEnumerable<S> a, Func<S, T> b, Func<S, V> c) { var d = new Dictionary<T, V>(); using (var e = a.GetEnumerator()) { while (e.MoveNext()) { d[b(e.Current)] = c(e.Current); } } return d; }
        public static List<T> ToList<T>(this IEnumerable<T> a) { var b = new List<T>(); if (a == null) { return b; } using (var c = a.GetEnumerator()) { while (c.MoveNext()) { b.Add(c.Current); } } return b; }
        public static List<T> Where<T>(this IEnumerable<T> a, Func<T, bool> b) { var c = new List<T>(); using (var d = a.GetEnumerator()) { while (d.MoveNext()) { if (b(d.Current)) { c.Add(d.Current); } } } return c; }
        public static List<T> OfType<T>(this IEnumerable<BaseNetworkable> a) where T : BaseEntity { var b = new List<T>(); using (var c = a.GetEnumerator()) { while (c.MoveNext()) { if (c.Current is T) { b.Add(c.Current as T); } } } return b; }
        public static int Sum<T>(this IEnumerable<T> a, Func<T, int> b) { int c = 0; foreach (T d in a) { c = checked(c + b(d)); } return c; }
        public static bool HasPermission(this string a, string b) { if (p == null) { p = Interface.Oxide.GetLibrary<Core.Libraries.Permission>(null); } return !string.IsNullOrEmpty(a) && p.UserHasPermission(a, b); }
        public static bool HasPermission(this BasePlayer a, string b) { return a.UserIDString.HasPermission(b); }
        public static bool HasPermission(this ulong a, string b) { return a.ToString().HasPermission(b); }
        public static bool UserHasGroup(this string a, string b) { if (string.IsNullOrEmpty(a)) return false; if (p == null) { p = Interface.Oxide.GetLibrary<Core.Libraries.Permission>(null); } return p.UserHasGroup(a, b); }
        public static bool UserHasGroup(this IPlayer a, string b) { return !(a == null) && a.Id.UserHasGroup(b); }
        public static bool IsReallyConnected(this BasePlayer a) { return a.IsReallyValid() && a.net.connection != null; }
        public static bool IsKilled(this BaseNetworkable a) { return (object)a == null || a.IsDestroyed; }
        public static bool IsNull<T>(this T a) where T : class { return a == null; }
        public static bool IsNull(this BasePlayer a) { return (object)a == null; }
        public static bool IsReallyValid(this BaseNetworkable a) { return !((object)a == null || a.IsDestroyed || (object)a.net == null); }
        public static void SafelyKill(this BaseNetworkable a) { if (a.IsKilled()) { return; } a.Kill(BaseNetworkable.DestroyMode.None); }
        public static bool CanCall(this Plugin o) { return (object)o != null && o.IsLoaded; }
        public static bool IsInBounds(this OBB o, Vector3 a) { return o.ClosestPoint(a) == a; }
        public static bool IsHuman(this BasePlayer a) { return !(a.IsNpc || !a.userID.IsSteamId()); }
        public static bool IsCheating(this BasePlayer a) { return a._limitedNetworking || a.IsFlying || a.UsedAdminCheat(30f) || a.IsGod() || a.metabolism?.calories?.min == 500; }
        public static void SetAiming(this BasePlayer a, bool f) { a.modelState.aiming = f; a.SendNetworkUpdate(); }
        public static BasePlayer ToPlayer(this IPlayer user) { return user.Object as BasePlayer; }
        public static string ObjectName(this Collider collider) { try { return collider.gameObject?.name ?? string.Empty; } catch { return string.Empty; } }
        public static Vector3 GetPosition(this Collider collider) { try { return collider.transform?.position ?? Vector3.zero; } catch { return Vector3.zero; } }
        public static string ObjectName(this BaseEntity entity) { try { return entity.name ?? string.Empty; } catch { return string.Empty; } }
        public static T GetRandom<T>(this HashSet<T> h) { if (h == null || h.Count == 0) { return default(T); } return h.ElementAt(UnityEngine.Random.Range(0, h.Count)); }
        public static float Distance(this Vector3 a, Vector3 b) => (a - b).magnitude;
    }
}
