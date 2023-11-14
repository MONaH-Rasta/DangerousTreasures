using System;
using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Oxide.Core;
using Oxide.Core.Plugins;
using Rust;
using UnityEngine;
using UnityEngine.AI;
using UnityEngine.SceneManagement;
using System.Text;
using Rust.Ai;
using Facepunch;
using System.Globalization;

namespace Oxide.Plugins
{
    [Info("Dangerous Treasures", "nivex", "2.2.5")]
    [Description("Event with treasure chests.")]
    class DangerousTreasures : RustPlugin
    {
        [PluginReference] Plugin LustyMap, ZoneManager, Economics, ServerRewards, Map, GUIAnnouncements, MarkerManager, CopyPaste, Clans, Friends, Kits, NPCKits, Duelist, RaidableBases, AbandonedBases;

        private static DangerousTreasures Instance;
        private bool IsUnloading = false;
        private bool wipeChestsSeed;

        public Dictionary<ulong, TreasureChest> Npcs { get; set; } = new Dictionary<ulong, TreasureChest>();
        public Dictionary<ulong, FinalDestination> Destinations { get; set; } = new Dictionary<ulong, FinalDestination>();
        private List<int> BlockedLayers = new List<int> { (int)Layer.Water, (int)Layer.Construction, (int)Layer.Trigger, (int)Layer.Prevent_Building, (int)Layer.Deployed, (int)Layer.Tree, (int)Layer.Clutter };
        private Dictionary<string, MonInfo> allowedMonuments = new Dictionary<string, MonInfo>();
        private Dictionary<string, MonInfo> monuments = new Dictionary<string, MonInfo>();
        private StoredData storedData = new StoredData();
        private SpawnFilter filter = new SpawnFilter();
        private string rocketResourcePath;
        private ItemDefinition rocketDef;
        private ItemDefinition boxDef;
        private Vector3 sd_customPos;
        private Timer eventTimer;
        private bool useMissileLauncher;
        private bool useRandomSkins;
        private bool useFireballs;
        private bool useRockets;
        private bool useSpheres;
        private bool init;

        private List<int> obstructionLayers = new List<int> { Layers.Mask.Player_Server, Layers.Mask.Construction, Layers.Mask.Deployed };
        private const int obstructionLayer = Layers.Mask.Player_Server | Layers.Mask.Construction | Layers.Mask.Deployed;
        private const int heightLayer = Layers.Mask.Terrain | Layers.Mask.World | Layers.Mask.Default | Layers.Mask.Construction | Layers.Mask.Deployed | Layers.Mask.Clutter;
        private const string basicRocketShortname = "ammo.rocket.basic";
        private const string fireRocketShortname = "ammo.rocket.fire";
        private const uint boxPrefabID = 2206646561;
        private const uint boxShortnameID = 2735448871;
        private const uint fireballPrefabID = 3550347674;
        private const uint explosionPrefabID = 4060989661;
        private const uint radiusMarkerID = 2849728229;
        private const uint sphereID = 3211242734;
        private const uint vendingPrefabID = 3459945130;
        public const uint SCARECROW = 3473349223;
        public const uint SCARECROW_CORPSE = 2400390439;
        public const uint SCIENTIST_HEAVY = 1536035819;
        public const uint SCIENTIST_CORPSE = 1236143239;

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
        }

        private List<ulong> newmanProtections = new List<ulong>();
        private List<ulong> indestructibleWarnings = new List<ulong>(); // indestructible messages limited to once every 10 seconds
        private List<ulong> drawGrants = new List<ulong>(); // limit draw to once every 15 seconds by default
        private Dictionary<Vector3, ZoneInfo> managedZones = new Dictionary<Vector3, ZoneInfo>();
        private Dictionary<uint, MapInfo> mapMarkers = new Dictionary<uint, MapInfo>();
        private Dictionary<uint, string> lustyMarkers = new Dictionary<uint, string>();
        private Dictionary<string, List<ulong>> skinsCache = new Dictionary<string, List<ulong>>();
        private Dictionary<string, List<ulong>> workshopskinsCache = new Dictionary<string, List<ulong>>();
        private Dictionary<uint, TreasureChest> treasureChests = new Dictionary<uint, TreasureChest>();
        private Dictionary<uint, string> looters = new Dictionary<uint, string>();

        private class MapInfo
        {
            public string Url;
            public string IconName;
            public Vector3 Position;

            public MapInfo() { }
        }

        private class PlayerInfo
        {
            public int StolenChestsTotal { get; set; } = 0;
            public int StolenChestsSeed { get; set; } = 0;
            public PlayerInfo() { }
        }

        private class StoredData
        {
            public double SecondsUntilEvent = double.MinValue;
            public int TotalEvents = 0;
            public readonly Dictionary<string, PlayerInfo> Players = new Dictionary<string, PlayerInfo>();
            public List<uint> Markers = new List<uint>();
            public string CustomPosition;
            public StoredData() { }
        }

        public class FinalDestination : FacepunchBehaviour
        {
            internal NPCPlayer npc;
            internal ScarecrowNPC scarecrow;
            internal ScientistNPC scientist;
            internal BaseNavigator navigator;
            internal List<Vector3> positions;
            internal BaseEntity target;
            internal Vector3 home;
            internal Vector3 destination;
            internal ulong userID;
            internal bool isRanged;
            internal bool isMounted;
            internal bool isStationary;
            internal bool isScarecrow;
            internal bool isWallhack;
            internal float attackRange = 30f;
            internal float targetLostRange;
            internal float attackRangeMultiplier;
            internal float baseAttackDamage;

            private void OnDestroy()
            {
                Instance?.Destinations?.Remove(userID);
                CancelInvoke();
                Destroy(this);
            }

            public static void SetupNavigator(BaseCombatEntity owner, BaseNavigator navigator, float distance)
            {
                navigator.MaxRoamDistanceFromHome = navigator.BestMovementPointMaxDistance = navigator.BestRoamPointMaxDistance = distance * 0.85f;
                navigator.DefaultArea = "Walkable";
                navigator.Agent.agentTypeID = -1372625422;
                navigator.MaxWaterDepth = 0.5f;
                navigator.CanUseNavMesh = true;
                navigator.CanUseAStar = true;
                navigator.Init(owner, navigator.Agent);
                navigator.PlaceOnNavMesh();
            }

            public static void SetupBrain<T>(BaseAIBrain<T> brain, BaseEntity owner, Vector3 position, float senseRange, bool useWallhack) where T : BaseEntity
            {
                brain.Invoke(() =>
                {
                    brain.ForceSetAge(0);
                    brain.Pet = false;
                    brain.UseAIDesign = true;
                    brain.AllowedToSleep = false;
                    brain._baseEntity = (T)owner;
                    brain.HostileTargetsOnly = false;
                    brain.MaxGroupSize = 0;
                    if (owner is ScarecrowNPC) brain.AttackRangeMultiplier = 1f;
                    brain.Senses.Init(owner: owner, memoryDuration: 5f, range: senseRange, targetLostRange: senseRange * 2f, visionCone: -1f, checkVision: false,
                        checkLOS: !useWallhack, ignoreNonVisionSneakers: true, listenRange: 15f, hostileTargetsOnly: false, senseFriendlies: false,
                        ignoreSafeZonePlayers: false, senseTypes: EntityType.Player, refreshKnownLOS: !useWallhack);
                    brain.Events.Memory.Position.Set(position, 4);
                }, 0.1f);
            }

            public static void Forget<T>(BaseAIBrain<T> brain, FinalDestination fd) where T : BaseEntity
            {
                brain.Senses.Players.Clear();
                brain.Senses.Memory.All.Clear();
                brain.Senses.Memory.Threats.Clear();
                brain.Senses.Memory.Targets.Clear();
                brain.Senses.Memory.Players.Clear();
                brain.Senses.Memory.LOS.Clear();
                brain.SenseRange = brain.ListenRange = _config.NPC.AggressionRange;
                brain.TargetLostRange = fd.targetLostRange = brain.SenseRange * 2f;
            }

            public void Set(NPCPlayer npc, List<Vector3> positions, TreasureChest tc)
            {
                navigator = npc.GetComponent<BaseNavigator>();

                this.npc = npc;
                this.positions = positions;

                home = tc.containerPos;
                Instance.Destinations[userID = npc.userID] = this;
                isMounted = npc.GetMounted() != null;
                isScarecrow = npc is ScarecrowNPC;
                isWallhack = _config.NPC.Wallhack;

                if (isScarecrow)
                {
                    scarecrow = npc as ScarecrowNPC;
                    baseAttackDamage = scarecrow.BaseAttackDamge;
                    targetLostRange = scarecrow.Brain.TargetLostRange;
                    attackRangeMultiplier = scarecrow.Brain.AttackRangeMultiplier;
                }
                else
                {
                    scientist = npc as ScientistNPC;
                    targetLostRange = scientist.Brain.TargetLostRange;
                    attackRangeMultiplier = scientist.Brain.AttackRangeMultiplier;
                }

                AttackEntity attackEntity = npc.GetAttackEntity();

                if (attackEntity.IsValid())
                {
                    if (attackEntity is BaseProjectile)
                    {
                        attackEntity.effectiveRange = 350f;
                    }

                    attackRange = attackEntity.effectiveRange * (attackEntity.aiOnlyInRange ? 1f : 2f) * attackRangeMultiplier;
                }

                if (!(isStationary = positions == null))
                {
                    InvokeRepeating(TryToRoam, 0f, 7.5f);
                }

                if (isStationary)
                {
                    InvokeRepeating(StationaryAttack, 1f, 1f);
                }
                else InvokeRepeating(TryToAttack, 1f, 1f);
            }

            public void SetTarget(BasePlayer player, bool converge = true)
            {
                if (target == player)
                {
                    return;
                }

                npc.lastAttacker = target = player;

                if (isScarecrow)
                {
                    SetIncreasedRange(scarecrow.Brain);
                }
                else SetIncreasedRange(scientist.Brain);

                if (converge)
                {
                    Converge(player);
                }
            }

            private void TryToAttack() => TryToAttack(null);

            private void TryToAttack(BasePlayer attacker)
            {
                if (attacker == null)
                {
                    attacker = GetBestTarget();
                }

                if (attacker == null)
                {
                    return;
                }

                SetTarget(attacker);

                if (ShouldForgetTarget(attacker))
                {
                    if (isScarecrow)
                    {
                        Forget(scarecrow.Brain, this);
                        target = null;
                    }
                    else
                    {
                        Forget(scientist.Brain, this);
                        target = null;
                    }

                    return;
                }

                npc.Resume();

                if (isScarecrow && scarecrow.CanAttack(attacker) && InRange(scarecrow.transform.position, attacker.transform.position, attackRange))
                {
                    Vector3 vector = attacker.ServerPosition - scarecrow.ServerPosition;

                    if (vector.magnitude > 0.001f)
                    {
                        scarecrow.SetAimDirection(vector.normalized);
                    }

                    scarecrow.BaseAttackDamge = UnityEngine.Random.Range(baseAttackDamage, baseAttackDamage * 2.5f);
                    scarecrow.StartAttacking(attacker);
                    SetDestination(attacker.transform.position, false);
                }
                else if (!isScarecrow)
                {
                    SetDestination(positions.GetRandom(), false);
                }
            }

            private void StationaryAttack()
            {
                scientist.SetAimDirection(new Vector3(scientist.transform.forward.x, 0f, scientist.transform.forward.z));

                var attacker = GetBestTarget();

                if (attacker == null)
                {
                    return;
                }

                if (ShouldForgetTarget(attacker))
                {
                    Forget(scientist.Brain, this);

                    target = null;

                    return;
                }

                if (isWallhack || attacker.IsVisible(scientist.eyes.position, attacker.eyes.position))
                {
                    var normalized = (scientist.transform.position - attacker.transform.position).normalized;

                    scientist.SetAimDirection(normalized);
                    scientist.SetStationaryAimPoint(normalized);
                }
            }

            public void Warp()
            {
                var position = positions.GetRandom();

                navigator.Pause();
                destination = position;
                npc.ServerPosition = position;
                navigator.Warp(position);
                navigator.stuckTimer = 0f;
                navigator.Resume();
            }

            private void TryToRoam()
            {
                if (isMounted)
                {
                    return;
                }

                if (npc.IsSwimming())
                {
                    npc.Kill();
                    Destroy(this);
                    return;
                }

                if (navigator.stuckTimer > 0f) // || navigator.Agent.enabled && navigator.CurrentNavigationType == BaseNavigator.NavigationType.None)
                {
                    Warp();
                }

                if (GetBestTarget().IsValid())
                {
                    return;
                }

                SetDestination(positions.GetRandom(), true);
            }

            private void SetIncreasedRange<T>(BaseAIBrain<T> brain) where T : BaseEntity
            {
                if (brain.SenseRange < 400f)
                {
                    brain.SenseRange += 400f;
                    brain.ListenRange += 400f;
                    brain.TargetLostRange += 400f;
                    targetLostRange = brain.TargetLostRange;
                }
            }

            private void SetDestination(Vector3 destination, bool isNormal)
            {
                if (navigator.CurrentNavigationType == BaseNavigator.NavigationType.None && !Rust.Ai.AiManager.ai_dormant && !Rust.Ai.AiManager.nav_disable)
                {
                    navigator.SetNavMeshEnabled(true);
                }

                this.destination = destination;

                if (navigator.Agent == null || !navigator.Agent.enabled || !navigator.Agent.isOnNavMesh)
                {
                    navigator.Destination = destination;
                    npc.finalDestination = destination;
                }
                else
                {
                    var speed = isNormal ? BaseNavigator.NavigationSpeed.Normal : BaseNavigator.NavigationSpeed.Fast;

                    if (!navigator.SetDestination(destination, speed, 0f, 0f))
                    {
                        navigator.Destination = destination;
                        npc.finalDestination = destination;
                    }
                }
            }

            private void Converge(BasePlayer player)
            {
                foreach (var fd in Instance.Destinations.Values)
                {
                    if (fd != this && fd.isScarecrow == isScarecrow && fd.CanConverge(npc))
                    {
                        fd.SetTarget(player, false);
                        fd.TryToAttack(player);
                    }
                }
            }

            private bool ShouldForgetTarget(BasePlayer attacker)
            {
                return attacker.IsDead() || attacker.limitNetworking || !InRange(attacker.transform.position, npc.transform.position, targetLostRange);
            }

            private bool CanConverge(NPCPlayer other)
            {
                if (other == null || other.IsDestroyed || other.IsDead()) return false;
                if (GetBestTarget().IsValid()) return false;
                return true;
            }

            private BasePlayer GetBestTarget()
            {
                if (isScarecrow && scarecrow.Brain.Senses.Players.Count > 0)
                {
                    return GetBestScarecrowTarget() as BasePlayer;
                }

                if (!isScarecrow && scientist.Brain.Senses.Players.Count > 0)
                {
                    return scientist.GetBestTarget() as BasePlayer;
                }

                return null;
            }

            public BaseEntity GetBestScarecrowTarget()
            {
                float delta = -1f;
                BaseEntity target = null;
                Vector3 bodyForward = scarecrow.eyes.BodyForward();
                foreach (var player in scarecrow.Brain.Senses.Players)
                {
                    if (player == null || player.Health() <= 0f)
                    {
                        continue;
                    }
                    float dist = Vector3.Distance(player.transform.position, scarecrow.transform.position);
                    float dot = Vector3.Dot((player.transform.position - scarecrow.eyes.position).normalized, bodyForward);
                    float rangeDelta = 1f - Mathf.InverseLerp(1f, scarecrow.Brain.SenseRange, dist);
                    rangeDelta += Mathf.InverseLerp(scarecrow.Brain.VisionCone, 1f, dot) / 2f;
                    rangeDelta += scarecrow.Brain.Senses.Memory.IsLOS(player) ? 2f : 0f;
                    if (delta > rangeDelta)
                    {
                        continue;
                    }
                    target = player;
                    delta = rangeDelta;
                }
                return target;
            }

            public bool NpcCanRoam(Vector3 destination) => destination == this.destination && InRange(home, destination, _config.NPC.AggressionRange);
        }

        private class GuidanceSystem : FacepunchBehaviour
        {
            private TimedExplosive missile;
            private ServerProjectile projectile;
            private BaseEntity target;
            private float rocketSpeedMulti = 2f;
            private Timer launch;
            private Vector3 launchPos;
            private List<ulong> exclude = new List<ulong>();
            public bool targetChest;

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
                missile.timerAmountMin = _config.MissileLauncher.Lifetime;
                missile.timerAmountMax = _config.MissileLauncher.Lifetime;

                missile.damageTypes = new List<DamageTypeEntry>(); // no damage
            }

            public void SetTarget(BaseEntity target, bool targetChest)
            {
                this.target = target;
                this.targetChest = targetChest;
            }

            public void Launch(float targettingTime)
            {
                missile.Spawn();

                launch = Instance.timer.Once(targettingTime, () =>
                {
                    if (!missile || missile.IsDestroyed)
                        return;

                    var list = new List<BasePlayer>();
                    var entities = new List<BaseEntity>();
                    Vis.Entities(launchPos, _config.Event.Radius + _config.MissileLauncher.Distance, entities, Layers.Mask.Player_Server, QueryTriggerInteraction.Ignore);

                    foreach (var entity in entities)
                    {
                        var player = entity as BasePlayer;

                        if (!player || player is NPCPlayer || !player.CanInteract())
                            continue;

                        if (_config.MissileLauncher.IgnoreFlying && player.IsFlying)
                            continue;

                        if (exclude.Contains(player.userID) || Instance.newmanProtections.Contains(player.userID))
                            continue;

                        list.Add(player); // acquire a player target 
                    }

                    entities.Clear();
                    entities = null;

                    if (list.Count > 0)
                    {
                        target = list.GetRandom(); // pick a random player
                        list.Clear();
                        list = null;
                    }
                    else if (!_config.MissileLauncher.TargetChest)
                    {
                        missile.Kill();
                        return;
                    }

                    projectile.speed = _config.Rocket.Speed * rocketSpeedMulti;
                    InvokeRepeating(GuideMissile, 0.1f, 0.1f);
                });
            }

            public void Exclude(List<ulong> list)
            {
                if (list != null && list.Count > 0)
                {
                    exclude.Clear();
                    exclude.AddRange(list);
                }
            }

            private void GuideMissile()
            {
                if (target == null)
                    return;

                if (target.IsDestroyed)
                {
                    if (missile != null && !missile.IsDestroyed)
                    {
                        missile.Kill();
                    }

                    return;
                }

                if (missile == null || missile.IsDestroyed || projectile == null)
                {
                    Destroy(this);
                    return;
                }

                if (Vector3.Distance(target.transform.position, missile.transform.position) <= 1f)
                {
                    missile.Explode();
                    return;
                }

                var direction = (target.transform.position - missile.transform.position) + Vector3.down; // direction to guide the missile
                projectile.InitializeVelocity(direction); // guide the missile to the target's position
            }

            private void OnDestroy()
            {
                exclude.Clear();
                launch?.Destroy();
                CancelInvoke(GuideMissile);
                Destroy(this);
            }
        }

        public class TreasureChest : FacepunchBehaviour
        {
            public StorageContainer container;
            public bool started;
            public bool opened;
            private bool firstEntered;
            private bool markerCreated;
            private bool npcsSpawned;
            private bool killed;
            private bool requireAllNpcsDie;
            public bool whenNpcsDie;
            private float posMulti = 3f;
            private float sphereMulti = 2f;
            private float claimTime;
            private float _radius;
            private long _unlockTime;
            public Vector3 containerPos;
            private Vector3 lastFirePos;
            private string zoneName;
            private int npcSpawnedAmount;
            public int countdownTime;
            public uint uid;

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
            public List<NPCPlayer> npcs = Pool.GetList<NPCPlayer>();
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
                    if (!chest || chest.started || !player || !player.IsConnected || !chest.players.Contains(player.userID))
                    {
                        Destroy(this);
                        return;
                    }

                    if (!InRange(player.transform.position, chest.containerPos, chest.Radius))
                    {
                        return;
                    }

                    if (_config.NewmanMode.Aura || _config.NewmanMode.Harm)
                    {
                        int count = player.inventory.AllItems().Where(item => item.info.shortname != "torch" && item.info.shortname != "rock" && player.IsHostileItem(item))?.Count() ?? 0;

                        if (count == 0)
                        {
                            if (_config.NewmanMode.Aura && !chest.newmans.Contains(player.userID) && !chest.traitors.Contains(player.userID))
                            {
                                player.ChatMessage(Instance.msg("Newman Enter", player.UserIDString));
                                chest.newmans.Add(player.userID);
                            }

                            if (_config.NewmanMode.Harm && !Instance.newmanProtections.Contains(player.userID) && !chest.protects.Contains(player.net.ID) && !chest.traitors.Contains(player.userID))
                            {
                                player.ChatMessage(Instance.msg("Newman Protect", player.UserIDString));
                                Instance.newmanProtections.Add(player.userID);
                                chest.protects.Add(player.net.ID);
                            }

                            if (!chest.traitors.Contains(player.userID))
                            {
                                return;
                            }
                        }

                        if (chest.newmans.Contains(player.userID))
                        {
                            player.ChatMessage(Instance.msg(Instance.useFireballs ? "Newman Traitor Burn" : "Newman Traitor", player.UserIDString));
                            chest.newmans.Remove(player.userID);

                            if (!chest.traitors.Contains(player.userID))
                                chest.traitors.Add(player.userID);

                            Instance.newmanProtections.Remove(player.userID);
                            chest.protects.Remove(player.net.ID);
                        }
                    }

                    if (!Instance.useFireballs || player.IsFlying)
                    {
                        return;
                    }

                    var stamp = Time.realtimeSinceStartup;

                    if (!chest.fireticks.ContainsKey(player.userID))
                    {
                        chest.fireticks[player.userID] = stamp + _config.Fireballs.SecondsBeforeTick;
                    }

                    if (chest.fireticks[player.userID] - stamp <= 0)
                    {
                        chest.fireticks[player.userID] = stamp + _config.Fireballs.SecondsBeforeTick;
                        chest.SpawnFire(player.transform.position);
                    }
                }

                void OnDestroy()
                {
                    CancelInvoke(Track);
                    Destroy(this);
                }
            }

            public void Kill()
            {
                if (killed) return;

                KillChest();
                RemoveMapMarkers();
                KillNpc();
                CancelInvoke();
                DestroyLauncher();
                DestroySphere();
                DestroyFire();
                killed = true;
                Interface.CallHook("OnDangerousEventEnded", containerPos);
            }

            private void KillChest()
            {
                if (container.IsValid())
                {
                    container.dropsLoot = false;

                    if (!container.IsDestroyed)
                    {
                        container.Kill();
                    }
                }
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
                return Instance.Npcs.ContainsKey(userID);
            }

            public static TreasureChest GetNPC(ulong userID)
            {
                TreasureChest tc;
                if (Instance.Npcs.TryGetValue(userID, out tc))
                {
                    return tc;
                }

                return null;
            }

            public static TreasureChest Get(Vector3 target)
            {
                foreach (var x in Instance.treasureChests.Values)
                {
                    if (Vector3Ex.Distance2D(x.containerPos, target) <= x.Radius)
                    {
                        return x;
                    }
                }

                return null;
            }

            public static bool HasNPC(ulong userID1, ulong userID2)
            {
                bool flag1 = false;
                bool flag2 = false;
                
                foreach (var chest in Instance.treasureChests.Values)
                {
                    foreach (var npc in chest.npcs)
                    {
                        if (npc.userID == userID1)
                        {
                            flag1 = true;
                        }
                        else if (npc.userID == userID2)
                        {
                            flag2 = true;
                        }
                    }
                }

                return flag1 && flag2;
            }

            public void Awaken()
            {
                var collider = gameObject.GetComponent<SphereCollider>() ?? gameObject.AddComponent<SphereCollider>();
                collider.center = Vector3.zero;
                collider.radius = Radius;
                collider.isTrigger = true;
                collider.enabled = true;

                requireAllNpcsDie = _config.Unlock.RequireAllNpcsDie;
                whenNpcsDie = _config.Unlock.WhenNpcsDie;

                if (_config.Event.Spheres && _config.Event.SphereAmount > 0)
                {
                    for (int i = 0; i < _config.Event.SphereAmount; i++)
                    {
                        var sphere = GameManager.server.CreateEntity(StringPool.Get(sphereID), containerPos) as SphereEntity;

                        if (sphere != null)
                        {
                            sphere.currentRadius = 1f;
                            sphere.Spawn();
                            sphere.LerpRadiusTo(Radius * sphereMulti, 5f);
                            spheres.Add(sphere);
                        }
                        else
                        {
                            Instance.Puts(_("Invalid Constant", null, sphereID));
                            Instance.useSpheres = false;
                            break;
                        }
                    }
                }

                if (Instance.useRockets)
                {
                    var positions = GetRandomPositions(containerPos, Radius * posMulti, _config.Rocket.Amount, 0f);

                    foreach (var position in positions)
                    {
                        var missile = GameManager.server.CreateEntity(Instance.rocketResourcePath, position, new Quaternion(), true) as TimedExplosive;
                        var gs = missile.gameObject.AddComponent<GuidanceSystem>();

                        gs.SetTarget(container, true);
                        gs.Launch(0.1f);
                    }

                    positions.Clear();
                }

                if (Instance.useFireballs)
                {
                    firePositions = GetRandomPositions(containerPos, Radius, 25, containerPos.y + 25f);

                    if (firePositions.Count > 0)
                        InvokeRepeating(SpawnFire, 0.1f, _config.Fireballs.SecondsBeforeTick);
                }

                if (Instance.useMissileLauncher)
                {
                    missilePositions = GetRandomPositions(containerPos, Radius, 25, 1);

                    if (missilePositions.Count > 0)
                    {
                        InvokeRepeating(LaunchMissile, 0.1f, _config.MissileLauncher.Frequency);
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
                containerPos = container.transform.position;
                uid = container.net.ID;
                container.inventory.SetFlag(ItemContainer.Flag.NoItemInput, true);
                SetupNpcKits();
            }

            public static void SpawnLoot(StorageContainer container, List<LootItem> treasure)
            {
                if (container == null || container.IsDestroyed || treasure == null || treasure.Count == 0)
                {
                    return;
                }

                var loot = treasure.ToList();
                int j = 0;

                container.inventory.Clear();
                container.inventory.capacity = Math.Min(_config.Event.TreasureAmount, loot.Count);

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

                    ulong skin = lootItem.skin;
                    Item item = ItemManager.CreateByName(definition.shortname, amount, skin);

                    if (item.info.stackable > 1 && !item.hasCondition)
                    {
                        item.amount = GetPercentIncreasedAmount(amount);
                    }

                    if (_config.Treasure.RandomSkins && skin == 0)
                    {
                        var skins = Instance.GetItemSkins(item.info);

                        if (skins.Count > 0)
                        {
                            skin = skins.GetRandom();
                            item.skin = skin;
                        }
                    }

                    if (skin != 0 && item.GetHeldEntity())
                    {
                        item.GetHeldEntity().skinID = skin;
                    }

                    item.MarkDirty();

                    if (!item.MoveToContainer(container.inventory, -1, true))
                    {
                        item.Remove(0.1f);
                    }
                }
            }

            void OnTriggerEnter(Collider col)
            {
                if (started)
                    return;

                var player = col.ToBaseEntity() as BasePlayer;

                if (!player || player.IsNpc)
                    return;

                Interface.CallHook("OnPlayerEnteredDangerousEvent", player, containerPos, _config.TruePVE.AllowPVPAtEvents);

                if (players.Contains(player.userID))
                    return;

                if (_config.EventMessages.FirstEntered && !firstEntered && !player.IsFlying)
                {
                    firstEntered = true;
                    Instance.PrintToChat(Instance.msg("OnFirstPlayerEntered", null, player.displayName, FormatGridReference(containerPos)));
                }

                string key;
                if (_config.EventMessages.NoobWarning)
                {
                    key = whenNpcsDie && npcsSpawned ? "Npc Event" : requireAllNpcsDie && npcsSpawned ? "Timed Npc Event" : "Timed Event";
                }
                else key = Instance.useFireballs ? "Dangerous Zone Protected" : "Dangerous Zone Unprotected";

                Message(player, Instance.msg(key, player.UserIDString));

                var tracker = player.gameObject.GetComponent<NewmanTracker>() ?? player.gameObject.AddComponent<NewmanTracker>();

                tracker.Assign(this);

                players.Add(player.userID);
            }

            void OnTriggerExit(Collider col)
            {
                var player = col.ToBaseEntity() as BasePlayer;

                if (!player)
                    return;

                if (!player.IsNpc)
                    Interface.CallHook("OnPlayerExitedDangerousEvent", player, containerPos, _config.TruePVE.AllowPVPAtEvents);

                if (player.IsNpc && npcs.Contains(player))
                {
                    var npc = player as NPCPlayer;

                    if (npc.NavAgent == null || !npc.NavAgent.isOnNavMesh)
                        npc.finalDestination = containerPos;
                    else npc.NavAgent.SetDestination(containerPos);

                    npc.finalDestination = containerPos;
                }

                if (!_config.NewmanMode.Harm)
                    return;

                if (protects.Contains(player.net.ID))
                {
                    Instance.newmanProtections.Remove(player.userID);
                    protects.Remove(player.net.ID);
                    Message(player, Instance.msg("Newman Protect Fade", player.UserIDString));
                }

                newmans.Remove(player.userID);
            }

            public void SpawnNpcs()
            {
                container.SendNetworkUpdate();

                if (_config.NPC.SpawnAmount < 1 || !_config.NPC.Enabled)
                    return;

                int amount = _config.NPC.SpawnRandomAmount && _config.NPC.SpawnAmount > 1 ? UnityEngine.Random.Range(_config.NPC.SpawnMinAmount, _config.NPC.SpawnAmount + 1) : _config.NPC.SpawnAmount;

                for (int i = 0; i < amount; i++)
                {
                    SpawnNpc(!_config.NPC.SpawnScientistsOnly && (_config.NPC.SpawnBoth ? UnityEngine.Random.value > 0.5f : _config.NPC.SpawnMurderers));
                }

                npcSpawnedAmount = npcs.Count;
                npcsSpawned = npcSpawnedAmount > 0;
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

                        if (!InRange(_navHit.position, containerPos, Radius - 2.5f))
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

            private bool TestInsideRock(Vector3 position)
            {
                Physics.queriesHitBackfaces = true;

                bool flag = IsInside(position);

                Physics.queriesHitBackfaces = false;

                return flag;
            }

            private bool IsInside(Vector3 point) => Physics.Raycast(point, Vector3.up, out _hit, 50f, Layers.Solid, QueryTriggerInteraction.Ignore) && IsRock(_hit.collider.gameObject.name);

            private bool IsRock(string name) => _prefabs.Exists(value => name.Contains(value, CompareOptions.OrdinalIgnoreCase));

            private List<string> _prefabs = new List<string> { "rock", "formation", "junk", "cliff", "invisible" };

            BaseEntity InstantiateEntity(Vector3 position, bool isScarecrow)
            {
                var prefabName = isScarecrow ? StringPool.Get(SCARECROW) : StringPool.Get(SCIENTIST_HEAVY);
                var prefab = GameManager.server.FindPrefab(prefabName);
                var go = Facepunch.Instantiate.GameObject(prefab, position, default(Quaternion));

                go.name = prefabName;
                SceneManager.MoveGameObjectToScene(go, Rust.Server.EntityScene);

                Spawnable spawnable;
                if (go.TryGetComponent(out spawnable))
                {
                    Destroy(spawnable);
                }

                if (!go.activeSelf)
                {
                    go.SetActive(true);
                }

                return go.GetComponent<BaseEntity>();
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

            private NPCPlayer SpawnNpc(bool isScarecrow)
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

                var npc = InstantiateEntity(position, isScarecrow) as NPCPlayer;

                if (npc == null)
                {
                    return null;
                }

                npc.enableSaving = false;
                npc.Spawn();

                SetupNpc(npc, positions);

                return npc;
            }

            private void SetupNpc(NPCPlayer npc, List<Vector3> positions)
            {
                npcs.Add(npc);

                var isScarecrow = npc is ScarecrowNPC;
                var navigator = npc.GetComponent<BaseNavigator>();

                FinalDestination.SetupNavigator(npc, navigator, Radius);

                if (isScarecrow)
                {
                    var scarecrow = npc as ScarecrowNPC;

                    if (_config.NPC.DespawnInventory)
                    {
                        scarecrow.LootSpawnSlots = new LootContainer.LootSpawnSlot[0];
                    }

                    FinalDestination.SetupBrain(scarecrow.Brain, npc, containerPos, _config.NPC.AggressionRange, _config.NPC.Wallhack);
                }
                else
                {
                    var scientist = npc as ScientistNPC;

                    if (_config.NPC.DespawnInventory)
                    {
                        scientist.LootSpawnSlots = new LootContainer.LootSpawnSlot[0];
                    }

                    scientist.DeathEffects = new GameObjectRef[0];
                    scientist.radioChatterType = ScientistNPC.RadioChatterType.NONE;
                    scientist.RadioChatterEffects = new GameObjectRef[0];

                    FinalDestination.SetupBrain(scientist.Brain, npc, containerPos, _config.NPC.AggressionRange, _config.NPC.Wallhack);
                }

                npc.displayName = _config.NPC.RandomNames.Count > 0 ? _config.NPC.RandomNames.GetRandom() : Facepunch.RandomUsernames.Get(npc.userID);
                npc.startHealth = isScarecrow ? _config.NPC.MurdererHealth : _config.NPC.ScientistHealth;
                npc.InitializeHealth(npc.startHealth, npc.startHealth);
                npc.Invoke(() => GiveEquipment(npc, isScarecrow), 1f);
                npc.Invoke(() => EquipWeapon(npc), 2f);
                npc.Invoke(() => SetupAI(npc, positions), 3f);

                Instance.Npcs[npc.userID] = this;
            }

            private void SetupAI(NPCPlayer npc, List<Vector3> list)
            {
                if (npc.IsDestroyed)
                {
                    npcs.Remove(npc);
                    return;
                }

                npc.gameObject.AddComponent<FinalDestination>().Set(npc, list, this);
            }

            private void EquipWeapon(NPCPlayer npc)
            {
                if (npc.IsDestroyed)
                {
                    npcs.Remove(npc);
                    return;
                }

                npc.EquipWeapon();

                var heldEntity = npc.GetHeldEntity();

                if (heldEntity == null)
                {
                    return;
                }

                if (heldEntity is Chainsaw)
                {
                    (heldEntity as Chainsaw).ServerNPCStart();
                }

                heldEntity.SetHeld(true);
                npc.SendNetworkUpdate();
                npc.inventory.UpdatedVisibleHolsteredItems();
            }

            void SetupNpcKits()
            {
                npcKits = new Dictionary<string, List<string>>
                {
                    { "murderer", _config.NPC.MurdererKits.Where(kit => IsKit(kit)).ToList() },
                    { "scientist", _config.NPC.ScientistKits.Where(kit => IsKit(kit)).ToList() }
                };
            }

            bool IsKit(string kit)
            {
                return Instance.Kits != null && Convert.ToBoolean(Instance.Kits?.Call("isKit", kit));
            }

            void GiveEquipment(NPCPlayer npc, bool murd)
            {
                if (npc.IsDestroyed)
                {
                    return;
                }

                List<string> kits;
                if (npcKits.TryGetValue(murd ? "murderer" : "scientist", out kits) && kits.Count > 0)
                {
                    npc.inventory.Strip();

                    object success = Instance.Kits.Call("GiveKit", npc, kits.GetRandom());

                    if (success is bool && (bool)success)
                    {
                        return;
                    }
                }

                var items = new List<string>();

                if (murd)
                {
                    if (_config.NPC.MurdererItems.Boots.Count > 0) items.Add(_config.NPC.MurdererItems.Boots.GetRandom());
                    if (_config.NPC.MurdererItems.Gloves.Count > 0) items.Add(_config.NPC.MurdererItems.Gloves.GetRandom());
                    if (_config.NPC.MurdererItems.Helm.Count > 0) items.Add(_config.NPC.MurdererItems.Helm.GetRandom());
                    if (_config.NPC.MurdererItems.Pants.Count > 0) items.Add(_config.NPC.MurdererItems.Pants.GetRandom());
                    if (_config.NPC.MurdererItems.Shirt.Count > 0) items.Add(_config.NPC.MurdererItems.Shirt.GetRandom());
                    if (_config.NPC.MurdererItems.Torso.Count > 0) items.Add(_config.NPC.MurdererItems.Torso.GetRandom());
                    if (_config.NPC.MurdererItems.Weapon.Count > 0) items.Add(_config.NPC.MurdererItems.Weapon.GetRandom());
                }
                else
                {
                    if (_config.NPC.ScientistItems.Boots.Count > 0) items.Add(_config.NPC.ScientistItems.Boots.GetRandom());
                    if (_config.NPC.ScientistItems.Gloves.Count > 0) items.Add(_config.NPC.ScientistItems.Gloves.GetRandom());
                    if (_config.NPC.ScientistItems.Helm.Count > 0) items.Add(_config.NPC.ScientistItems.Helm.GetRandom());
                    if (_config.NPC.ScientistItems.Pants.Count > 0) items.Add(_config.NPC.ScientistItems.Pants.GetRandom());
                    if (_config.NPC.ScientistItems.Shirt.Count > 0) items.Add(_config.NPC.ScientistItems.Shirt.GetRandom());
                    if (_config.NPC.ScientistItems.Torso.Count > 0) items.Add(_config.NPC.ScientistItems.Torso.GetRandom());
                    if (_config.NPC.ScientistItems.Weapon.Count > 0) items.Add(_config.NPC.ScientistItems.Weapon.GetRandom());
                }

                if (items.Count == 0)
                {
                    return;
                }

                npc.inventory.Strip();

                SpawnItems(npc, items);
            }

            void SpawnItems(BasePlayer player, List<string> items)
            {
                foreach (string shortname in items)
                {
                    var def = ItemManager.FindItemDefinition(shortname);

                    if (def == null)
                    {
                        continue;
                    }

                    Item item = ItemManager.Create(def, 1, 0);
                    var skins = Instance.GetItemSkins(item.info);

                    if (skins.Count > 0)
                    {
                        ulong skin = skins.GetRandom();
                        item.skin = skin;
                    }

                    if (item.skin != 0 && item.GetHeldEntity())
                    {
                        item.GetHeldEntity().skinID = item.skin;
                    }

                    item.MarkDirty();

                    if (!item.MoveToContainer(player.inventory.containerWear, -1, false) && !item.MoveToContainer(player.inventory.containerBelt, 0, false))
                    {
                        item.Remove(0f);
                    }
                }
            }

            public void UpdateMarker()
            {
                if (!_config.Event.MarkerVending && !_config.Event.MarkerExplosion)
                {
                    CancelInvoke(UpdateMarker);
                }

                if (markerCreated)
                {
                    if (explosionMarker != null && !explosionMarker.IsDestroyed)
                    {
                        explosionMarker.SendNetworkUpdate();
                    }

                    if (genericMarker != null && !genericMarker.IsDestroyed)
                    {
                        genericMarker.SendUpdate();
                    }

                    if (vendingMarker != null && !vendingMarker.IsDestroyed)
                    {
                        vendingMarker.SendNetworkUpdate();
                    }

                    return;
                }

                if (Instance.MarkerManager != null)
                {
                    Interface.CallHook("API_CreateMarker", container as BaseEntity, "DangerousTreasures", 0, 10f, 0.25f, _config.Event.MarkerName, "FF0000", "00FFFFFF");
                    markerCreated = true;
                    return;
                }

                if (Instance.treasureChests.Values.Count(e => e.HasRustMarker) > 10)
                {
                    return;
                }

                //explosionmarker cargomarker ch47marker cratemarker
                if (_config.Event.MarkerVending)
                {
                    vendingMarker = GameManager.server.CreateEntity(StringPool.Get(vendingPrefabID), containerPos) as VendingMachineMapMarker;

                    if (vendingMarker != null)
                    {
                        vendingMarker.enabled = false;
                        vendingMarker.markerShopName = _config.Event.MarkerName;
                        vendingMarker.Spawn();
                    }
                }
                else if (_config.Event.MarkerExplosion)
                {
                    explosionMarker = GameManager.server.CreateEntity(StringPool.Get(explosionPrefabID), containerPos) as MapMarkerExplosion;

                    if (explosionMarker != null)
                    {
                        explosionMarker.Spawn();
                        explosionMarker.Invoke(() => explosionMarker.CancelInvoke(explosionMarker.DelayedDestroy), 1f);
                    }
                }

                genericMarker = GameManager.server.CreateEntity(StringPool.Get(radiusMarkerID), containerPos) as MapMarkerGenericRadius;

                if (genericMarker != null)
                {
                    genericMarker.alpha = 0.75f;
                    genericMarker.color2 = __(_config.Event.MarkerColor);
                    genericMarker.radius = Mathf.Clamp(_config.Event.MarkerRadius, 0.1f, 1f);
                    genericMarker.Spawn();
                    genericMarker.SendUpdate();
                }

                markerCreated = true;
            }

            void KillNpc()
            {
                npcs.ForEach(npc =>
                {
                    if (npc == null || npc.IsDestroyed)
                        return;

                    npc.Kill();
                });
            }

            public void RemoveMapMarkers()
            {
                Instance.RemoveLustyMarker(uid);
                Instance.RemoveMapMarker(uid);

                if (explosionMarker != null && !explosionMarker.IsDestroyed)
                {
                    explosionMarker.CancelInvoke(explosionMarker.DelayedDestroy);
                    explosionMarker.Kill(BaseNetworkable.DestroyMode.None);
                }

                if (genericMarker != null && !genericMarker.IsDestroyed)
                {
                    genericMarker.Kill();
                }

                if (vendingMarker != null && !vendingMarker.IsDestroyed)
                {
                    vendingMarker.Kill();
                }
            }

            void OnDestroy()
            {
                Kill();

                if (Instance != null && Instance.treasureChests.ContainsKey(uid))
                {
                    Instance.treasureChests.Remove(uid);

                    if (!Instance.IsUnloading && Instance.treasureChests.Count == 0) // 0.1.21 - nre fix
                    {
                        Instance.SubscribeHooks(false);
                    }
                }

                Free();
            }

            public void LaunchMissile()
            {
                if (string.IsNullOrEmpty(Instance.rocketResourcePath))
                    Instance.useMissileLauncher = false;

                if (!Instance.useMissileLauncher)
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

                var missile = GameManager.server.CreateEntity(Instance.rocketResourcePath, missilePos) as TimedExplosive;

                if (!missile)
                {
                    Instance.useMissileLauncher = false;
                    DestroyLauncher();
                    return;
                }

                missiles.Add(missile);
                missiles.RemoveAll(x => x == null || x.IsDestroyed);

                var gs = missile.gameObject.AddComponent<GuidanceSystem>();

                gs.Exclude(newmans);
                gs.SetTarget(container, _config.MissileLauncher.TargetChest);
                gs.Launch(_config.MissileLauncher.TargettingTime);
            }

            void SpawnFire()
            {
                var firePos = firePositions.GetRandom();
                int retries = firePositions.Count;

                while (Vector3.Distance(firePos, lastFirePos) < Radius * 0.35f && --retries > 0)
                {
                    firePos = firePositions.GetRandom();
                }

                SpawnFire(firePos);
                lastFirePos = firePos;
            }

            void SpawnFire(Vector3 firePos)
            {
                if (!Instance.useFireballs)
                    return;

                if (fireballs.Count >= 6) // limit fireballs
                {
                    foreach (var entry in fireballs)
                    {
                        if (entry != null && !entry.IsDestroyed)
                            entry.Kill();

                        fireballs.Remove(entry);
                        break;
                    }
                }

                var fireball = GameManager.server.CreateEntity(StringPool.Get(fireballPrefabID), firePos) as FireBall;

                if (fireball == null)
                {
                    Instance.Puts(_("Invalid Constant", null, fireballPrefabID));
                    Instance.useFireballs = false;
                    CancelInvoke(SpawnFire);
                    firePositions.Clear();
                    return;
                }

                fireball.Spawn();
                fireball.damagePerSecond = _config.Fireballs.DamagePerSecond;
                fireball.generation = _config.Fireballs.Generation;
                fireball.lifeTimeMax = _config.Fireballs.LifeTimeMax;
                fireball.lifeTimeMin = _config.Fireballs.LifeTimeMin;
                fireball.radius = _config.Fireballs.Radius;
                fireball.tickRate = _config.Fireballs.TickRate;
                fireball.waterToExtinguish = _config.Fireballs.WaterToExtinguish;
                fireball.SendNetworkUpdate();
                fireball.Think();

                float lifeTime = UnityEngine.Random.Range(_config.Fireballs.LifeTimeMin, _config.Fireballs.LifeTimeMax);
                Instance.timer.Once(lifeTime, () => fireball?.Extinguish());

                fireballs.Add(fireball);
            }

            public void Destruct()
            {
                if (_config.EventMessages.Destruct)
                {
                    var posStr = FormatGridReference(containerPos);

                    foreach (var target in BasePlayer.activePlayerList)
                        Message(target, Instance.msg("OnChestDespawned", target.UserIDString, posStr));
                }

                if (container != null && !container.IsDestroyed)
                {
                    container.inventory.Clear();
                    container.Kill();
                }
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
                    Message(target, Instance.msg("DestroyingTreasure", target.UserIDString, eventPos, Instance.FormatTime(time, target.UserIDString), _config.Settings.DistanceChatCommand));
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

                    if (_config.Event.DestructTime > 0f && destruct == null)
                        destruct = Instance.timer.Once(_config.Event.DestructTime, () => Destruct());

                    if (_config.EventMessages.Started)
                        Instance.PrintToChat(Instance.msg(requireAllNpcsDie && npcsSpawned ? "StartedNpcs" : "Started", null, FormatGridReference(containerPos)));

                    if (_config.UnlootedAnnouncements.Enabled)
                    {
                        claimTime = Time.realtimeSinceStartup + _config.Event.DestructTime;
                        announcement = Instance.timer.Repeat(_config.UnlootedAnnouncements.Interval * 60f, 0, () => Unclaimed());
                    }

                    started = true;
                }

                if (requireAllNpcsDie && npcsSpawned)
                {
                    if (npcs != null)
                    {
                        npcs.RemoveAll(npc => npc == null || npc.IsDestroyed || npc.IsDead());

                        if (npcs.Count > 0)
                        {
                            Invoke(Unlock, 1f);
                            return;
                        }
                    }
                }

                if (container.HasFlag(BaseEntity.Flags.Locked))
                    container.SetFlag(BaseEntity.Flags.Locked, false);
                if (container.HasFlag(BaseEntity.Flags.OnFire))
                    container.SetFlag(BaseEntity.Flags.OnFire, false);
            }

            public void SetUnlockTime(float time)
            {
                countdownTime = Convert.ToInt32(time);
                _unlockTime = Convert.ToInt64(Time.realtimeSinceStartup + time);

                if (_config.NPC.SpawnAmount > 0 && _config.NPC.Enabled && !AiManager.nav_disable)
                {
                    if (requireAllNpcsDie && !npcsSpawned || whenNpcsDie && !npcsSpawned)
                    {
                        whenNpcsDie = false;
                        requireAllNpcsDie = false;
                    }
                }
                
                unlock = Instance.timer.Once(time, () => Unlock());

                if (_config.Countdown.Enabled && _config.Countdown.Times?.Count > 0 && countdownTime > 0)
                {
                    if (times.Count == 0)
                        times.AddRange(_config.Countdown.Times);

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
                                Message(target, Instance.msg("Countdown", target.UserIDString, eventPos, Instance.FormatTime(countdownTime, target.UserIDString)));

                            times.Remove(countdownTime);
                        }
                    });
                }
            }

            public void DestroyLauncher()
            {
                if (missilePositions.Count > 0)
                {
                    CancelInvoke(LaunchMissile);
                    missilePositions.Clear();
                }

                if (missiles.Count > 0)
                {
                    foreach (var entry in missiles)
                        if (entry != null && !entry.IsDestroyed)
                            entry.Kill();

                    missiles.Clear();
                }
            }

            public void DestroySphere()
            {
                if (spheres.Count > 0)
                {
                    foreach (var sphere in spheres)
                    {
                        if (sphere != null && !sphere.IsDestroyed)
                        {
                            sphere.Kill();
                        }
                    }

                    spheres.Clear();
                }
            }

            public void DestroyFire()
            {
                CancelInvoke(SpawnFire);
                firePositions.Clear();

                if (fireballs.Count > 0)
                {
                    foreach (var fireball in fireballs)
                    {
                        if (fireball != null && !fireball.IsDestroyed)
                        {
                            fireball.Kill();
                        }
                    }

                    fireballs.Clear();
                }

                Instance.newmanProtections.RemoveAll(x => protects.Contains(x));
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

        void OnServerInitialized()
        {
            try
            {
                storedData = Interface.Oxide.DataFileSystem.ReadObject<StoredData>(Name);
            }
            catch { }

            if (storedData?.Players == null)
            {
                storedData = new StoredData();
            }

            if (_config.Event.Automated)
            {
                if (storedData.SecondsUntilEvent != double.MinValue)
                    if (storedData.SecondsUntilEvent - Facepunch.Math.Epoch.Current > _config.Event.IntervalMax) // Allows users to lower max event time
                        storedData.SecondsUntilEvent = double.MinValue;

                eventTimer = timer.Once(1f, () => CheckSecondsUntilEvent());
            }

            if (!string.IsNullOrEmpty(storedData.CustomPosition))
            {
                sd_customPos = storedData.CustomPosition.ToVector3();
            }
            else sd_customPos = Vector3.zero;

            TryWipeData();

            useMissileLauncher = _config.MissileLauncher.Enabled;
            useSpheres = _config.Event.Spheres;
            useFireballs = _config.Fireballs.Enabled;
            useRockets = _config.Rocket.Enabled;
            rocketDef = ItemManager.FindItemDefinition(_config.Rocket.FireRockets ? fireRocketShortname : basicRocketShortname);

            if (!rocketDef)
            {
                Instance.Puts(_("Invalid Constant", null, _config.Rocket.FireRockets ? fireRocketShortname : basicRocketShortname));
                useRockets = false;
            }
            else rocketResourcePath = rocketDef.GetComponent<ItemModProjectile>().projectileObject.resourcePath;

            boxDef = ItemManager.FindItemDefinition(StringPool.Get(boxShortnameID));
            useRandomSkins = _config.Skins.RandomSkins;

            BlockZoneManagerZones();
            monuments.Clear();
            allowedMonuments.Clear();

            string name = null;
            int BlacklistedMonumentsCount = _config.NPC.BlacklistedMonuments.Count;
            foreach (var monument in UnityEngine.Object.FindObjectsOfType<MonumentInfo>())
            {
                name = monument.displayPhrase.english;
                if (monument.name.Contains("cave")) continue; //name = "Cave:" + RandomString(5, 5);
                else if (monument.name.Contains("power_sub")) continue; //name = monument.name.Substring(monument.name.LastIndexOf("/") + 1).Replace(".prefab", "") + ":" + RandomString(5, 5);
                else if (monuments.ContainsKey(name)) name += ":" + RandomString(5, 5);
                float radius = GetMonumentFloat(name);
                monuments[name] = new MonInfo() { Position = monument.transform.position, Radius = radius };
                                
                if (string.IsNullOrEmpty(monument.displayPhrase.english.Trim())) continue;

                if (!_config.NPC.BlacklistedMonuments.ContainsKey(monument.displayPhrase.english.Trim()))
                {
                    _config.NPC.BlacklistedMonuments.Add(monument.displayPhrase.english.Trim(), false);
                }
            }

            if (monuments.Count > 0) allowedMonuments = monuments.ToDictionary(k => k.Key, k => k.Value);

            List<string> underground = new List<string>
            {
                "Cave",
                "Sewer Branch",
                "Military Tunnel",
                "Underwater Lab",
                "Train Tunnel"
            };

            int BlacklistCount = _config.Monuments.Blacklist.Count;

            foreach (var mon in allowedMonuments.ToList())
            {
                name = mon.Key.Contains(":") ? mon.Key.Substring(0, mon.Key.LastIndexOf(":")) : mon.Key.TrimEnd();

                string value = name.Trim();

                if (string.IsNullOrEmpty(value)) continue;

                if (!_config.Monuments.Blacklist.ContainsKey(value))
                {
                    _config.Monuments.Blacklist.Add(value, false);
                }
                else if (_config.Monuments.Blacklist[value])
                {
                    allowedMonuments.Remove(mon.Key);
                }

                if (!_config.Monuments.Underground && underground.Exists(x => x.Contains(value, CompareOptions.OrdinalIgnoreCase)))
                {
                    allowedMonuments.Remove(name);
                }
            }

            init = true;
            Subscribe(nameof(Unload));
            RemoveAllTemporaryMarkers();

            if (_config.Monuments.Blacklist.Count != BlacklistCount || _config.NPC.BlacklistedMonuments.Count != BlacklistedMonumentsCount)
            {
                var blacklist = _config.Monuments.Blacklist.ToList();
                
                blacklist.RemoveAll(x => string.IsNullOrEmpty(x.Key));
                blacklist.Sort((x, y) => x.Key.CompareTo(y.Key));

                _config.Monuments.Blacklist = blacklist.ToDictionary(x => x.Key, x => x.Value);

                var npcblacklist = _config.NPC.BlacklistedMonuments.ToList();

                npcblacklist.RemoveAll(x => string.IsNullOrEmpty(x.Key));
                npcblacklist.Sort((x, y) => x.Key.CompareTo(y.Key));

                _config.NPC.BlacklistedMonuments = npcblacklist.ToDictionary(x => x.Key, x => x.Value);

                SaveConfig();
            }
            if (_config.Skins.RandomWorkshopSkins || _config.Treasure.RandomWorkshopSkins) SetWorkshopIDs(); // webrequest.Enqueue("http://s3.amazonaws.com/s3.playrust.com/icons/inventory/rust/schema.json", null, GetWorkshopIDs, this, Core.Libraries.RequestMethod.GET);
        }

        private void TryWipeData()
        {
            if (!wipeChestsSeed && BuildingManager.server.buildingDictionary.Count == 0)
            {
                wipeChestsSeed = true;
            }

            if (wipeChestsSeed)
            {
                if (storedData.Players.Count > 0)
                {
                    var ladder = storedData.Players.Where(kvp => kvp.Value.StolenChestsSeed > 0).ToDictionary(kvp => kvp.Key, kvp => kvp.Value.StolenChestsSeed).ToList();

                    if (ladder.Count > 0 && AssignTreasureHunters(ladder))
                    {
                        foreach (string key in storedData.Players.Keys)
                        {
                            storedData.Players[key].StolenChestsSeed = 0;
                        }                       
                    }
                }

                storedData.CustomPosition = string.Empty;
                sd_customPos = Vector3.zero;
                wipeChestsSeed = false;
                SaveData();
            }
        }

        private void BlockZoneManagerZones()
        {
            if (ZoneManager == null || !ZoneManager.IsLoaded)
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

        void Unload()
        {
            IsUnloading = true;

            foreach (var chest in treasureChests.Values.ToList())
            {
                if (chest != null)
                {
                    Puts(_("Destroyed Treasure Chest", null, chest.containerPos));

                    chest.Kill();
                }
            }

            lustyMarkers.ToList().ForEach(entry => RemoveLustyMarker(entry.Key));
            mapMarkers.ToList().ForEach(entry => RemoveMapMarker(entry.Key));
            RemoveAllTemporaryMarkers();
            _config = null;
            Instance = null;
        }

        object canTeleport(BasePlayer player)
        {
            return EventTerritory(player.transform.position) ? msg("CannotTeleport", player.UserIDString) : null;
        }

        object CanTeleport(BasePlayer player)
        {
            return EventTerritory(player.transform.position) ? msg("CannotTeleport", player.UserIDString) : null;
        }

        object CanBradleyApcTarget(BradleyAPC apc, NPCPlayer npc)
        {
            return npc != null && TreasureChest.HasNPC(npc.userID) ? (object)false : null;
        }

        object CanBeTargeted(BasePlayer player, MonoBehaviour behaviour)
        {
            if (player.IsValid())
            {
                if (newmanProtections.Contains(player.userID) || TreasureChest.HasNPC(player.userID))
                {
                    return false;
                }
            }

            return null;
        }

        private object OnNpcDestinationSet(NPCPlayer npc, Vector3 newDestination)
        {
            if (npc == null || npc.NavAgent == null || !npc.NavAgent.enabled || !npc.NavAgent.isOnNavMesh)
            {
                return true;
            }

            FinalDestination fd;
            if (!Destinations.TryGetValue(npc.userID, out fd) || fd.NpcCanRoam(newDestination))
            {
                return null;
            }

            return true;
        }

        private object OnNpcResume(NPCPlayer npc)
        {
            if (npc == null)
            {
                return null;
            }

            FinalDestination fd;
            if (!Instance.Destinations.TryGetValue(npc.userID, out fd) || !fd.isStationary)
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

        void OnPlayerDeath(NPCPlayer player)
        {
            if (player == null)
                return;

            var chest = TreasureChest.GetNPC(player.userID);

            if (chest == null)
            {
                return;
            }

            player.svActiveItemID = 0;
            player.SendNetworkUpdate(BasePlayer.NetworkQueue.Update);

            if (_config.NPC.DespawnInventory)
            {
                player.inventory.Strip();
            }

            if (chest.whenNpcsDie && chest.npcs.Count <= 1)
            {
                NextTick(() =>
                {
                    if (chest != null)
                    {
                        chest.Unlock();
                    }
                });
            }
        }

        void OnEntitySpawned(BaseNetworkable entity)
        {
            if (entity == null || entity.transform == null)
                return;

            if (entity is BaseLock)
            {
                NextTick(() =>
                {
                    if (entity != null && !entity.IsDestroyed)
                    {
                        foreach (var x in treasureChests.Values)
                        {
                            if (entity.HasParent() && entity.GetParentEntity() == x.container)
                            {
                                entity.KillMessage();
                                break;
                            }
                        }
                    }
                });
            }

            if (!_config.NPC.Enabled)
            {
                return;
            }

            var corpse = entity as NPCPlayerCorpse;

            if (corpse == null)
            {
                return;
            }

            var chest = TreasureChest.GetNPC(corpse.playerSteamID);

            if (chest == null)
            {
                return;
            }

            if (_config.NPC.DespawnInventory) corpse.Invoke(corpse.KillMessage, 30f);
            chest.npcs.RemoveAll(npc => npc.userID == corpse.playerSteamID);
            Npcs.Remove(corpse.playerSteamID);

            foreach (var x in treasureChests.Values)
            {
                if (x.npcs.Count > 0)
                {
                    return;
                }
            }

            Unsubscribe(nameof(CanBeTargeted));
            Unsubscribe(nameof(OnNpcTarget));
            Unsubscribe(nameof(OnNpcResume));
            Unsubscribe(nameof(OnNpcDestinationSet));
            Unsubscribe(nameof(CanBradleyApcTarget));
            Unsubscribe(nameof(OnPlayerDeath));
        }

        object CanBuild(Planner planner, Construction prefab, Construction.Target target)
        {
            var player = planner?.GetOwnerPlayer();

            if (!player || player.IsAdmin) return null;

            var chest = TreasureChest.Get(player.transform.position);

            if (chest != null)
            {
                Message(player, msg("Building is blocked!", player.UserIDString));
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
                Message(player, msg("CannotBeMounted", player.UserIDString));
                player.Invoke(player.EndLooting, 0.1f);
                return;
            }
            else looters[container.net.ID] = player.UserIDString;

            if (!_config.EventMessages.FirstOpened)
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
                Message(target, msg("OnChestOpened", target.UserIDString, player.displayName, posStr));
            }
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
                        if (_config.RankedLadder.Enabled)
                        {
                            if (!storedData.Players.ContainsKey(looter.UserIDString))
                                storedData.Players.Add(looter.UserIDString, new PlayerInfo());

                            storedData.Players[looter.UserIDString].StolenChestsTotal++;
                            storedData.Players[looter.UserIDString].StolenChestsSeed++;
                            SaveData();
                        }

                        var posStr = FormatGridReference(looter.transform.position);

                        Puts(_("Thief", null, posStr, looter.displayName));

                        if (_config.EventMessages.Thief)
                        {
                            foreach (var target in BasePlayer.activePlayerList)
                                Message(target, msg("Thief", target.UserIDString, posStr, looter.displayName));
                        }

                        looter.EndLooting();

                        if (_config.Rewards.Economics && _config.Rewards.Money > 0)
                        {
                            if (Economics != null)
                            {
                                Economics?.Call("Deposit", looter.UserIDString, _config.Rewards.Money);
                                Message(looter, msg("EconomicsDeposit", looter.UserIDString, _config.Rewards.Money));
                            }
                        }

                        if (_config.Rewards.ServerRewards && _config.Rewards.Points > 0)
                        {
                            if (ServerRewards != null)
                            {
                                var success = ServerRewards?.Call("AddPoints", looter.userID, (int)_config.Rewards.Points);

                                if (success != null && success is bool && (bool)success)
                                    Message(looter, msg("ServerRewardPoints", looter.UserIDString, (int)_config.Rewards.Points));
                            }
                        }
                    }

                    RemoveLustyMarker(box.net.ID);
                    RemoveMapMarker(box.net.ID);

                    if (!box.IsDestroyed)
                        box.Kill();

                    if (treasureChests.Count == 0)
                        SubscribeHooks(false);
                }
            });
        }

        object CanEntityBeTargeted(BasePlayer player, BaseEntity target)
        {
            return player.IsValid() && !player.IsNpc && target.IsValid() && EventTerritory(player.transform.position) && IsTrueDamage(target) ? (object)true : null;
        }

        object CanEntityTrapTrigger(BaseTrap trap, BasePlayer player)
        {
            return player.IsValid() && !player.IsNpc && trap.IsValid() && EventTerritory(player.transform.position) ? (object)true : null;
        }

        object CanEntityTakeDamage(BaseEntity entity, HitInfo hitInfo) // TruePVE!!!! <3 @ignignokt84
        {
            if (!entity.IsValid() || hitInfo == null || hitInfo.Initiator == null)
            {
                return null;
            }
            
            if (entity is NPCPlayer)
            {
                var npc = entity as NPCPlayer;

                if (TreasureChest.HasNPC(npc.userID))
                {
                    return true;
                }
            }
            
            var attacker = hitInfo.Initiator as BasePlayer;

            if (_config.TruePVE.ServerWidePVP && attacker.IsValid() && entity is BasePlayer && treasureChests.Count > 0) // 1.2.9 & 1.3.3 & 1.6.4
            {
                return true;
            }

            if (EventTerritory(entity.transform.position)) // 1.5.8 & 1.6.4
            {
                if (entity is NPCPlayerCorpse)
                {
                    return true;
                }

                if (attacker.IsValid())
                {
                    if (EventTerritory(attacker.transform.position) && _config.TruePVE.AllowPVPAtEvents && entity is BasePlayer) // 1.2.9
                    {
                        return true;
                    }

                    if (EventTerritory(attacker.transform.position) && _config.TruePVE.AllowBuildingDamageAtEvents && entity.name.Contains("building")) // 1.3.3
                    {
                        return true;
                    }
                }

                if (IsTrueDamage(hitInfo.Initiator))
                {
                    return true;
                }
            }

            return null; // 1.6.4 rewrite
        }

        bool IsTrueDamage(BaseEntity entity)
        {
            if (!entity.IsValid())
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

        void OnEntityTakeDamage(BasePlayer player, HitInfo hitInfo)
        {
            if (player == null || hitInfo == null)
            {
                return;
            }

            var attacker = hitInfo.Initiator as BasePlayer;

            if (newmanProtections.Contains(player.userID) && attacker.IsValid())
            {
                ProtectionDamageHelper(attacker, hitInfo, true);
            }
            else if (_config.NPC.Accuracy < UnityEngine.Random.Range(0f, 100f) && attacker.IsValid() && TreasureChest.HasNPC(attacker.userID))
            {
                hitInfo.damageTypes = new DamageTypeList(); // shot missed
                hitInfo.DidHit = false;
                hitInfo.DoHitEffects = false;
            }
            else if (TreasureChest.HasNPC(player.userID))
            {
                NpcDamageHelper(player as NPCPlayer, hitInfo, attacker);
            }
        }

        void OnEntityTakeDamage(BoxStorage box, HitInfo hitInfo)
        {
            if (box == null || hitInfo == null || !treasureChests.ContainsKey(box.net.ID) || !(hitInfo.Initiator is BasePlayer))
            {
                return;
            }

            var attacker = hitInfo.Initiator as BasePlayer;

            ProtectionDamageHelper(attacker, hitInfo, true);
        }

        void NpcDamageHelper(NPCPlayer npc, HitInfo hitInfo, BasePlayer attacker)
        {
            if (_config.NPC.Range > 0f && hitInfo.ProjectileDistance > _config.NPC.Range)
            {
                hitInfo.damageTypes = new DamageTypeList();
                hitInfo.DidHit = false;
                hitInfo.DoHitEffects = false;
            }
            else if (hitInfo.hasDamage && !(hitInfo.Initiator is BasePlayer) && !(hitInfo.Initiator is AutoTurret)) // immune to fire/explosions/other
            {
                hitInfo.damageTypes = new DamageTypeList();
                hitInfo.DidHit = false;
                hitInfo.DoHitEffects = false;
            }
            else if (_config.NPC.MurdererHeadshot && hitInfo.isHeadshot && npc is ScarecrowNPC)
            {
                npc.Die(hitInfo);
            }
            else if (_config.NPC.ScientistHeadshot && hitInfo.isHeadshot && npc is ScientistNPC)
            {
                npc.Die(hitInfo);
            }
            else if (attacker.IsValid())
            {
                FinalDestination fd;
                if (!Instance.Destinations.TryGetValue(npc.userID, out fd))
                {
                    return;
                }

                var e = attacker.HasParent() ? attacker.GetParentEntity() : null;

                if (!(e == null) && (e is ScrapTransportHelicopter || e is HotAirBalloon || e is CH47Helicopter))
                {
                    hitInfo.damageTypes.ScaleAll(0f);
                    return;
                }

                fd.SetTarget(attacker);
            }
        }

        private void ProtectionDamageHelper(BasePlayer attacker, HitInfo hitInfo, bool isChest)
        {
            if (hitInfo.hasDamage)
            {
                hitInfo.damageTypes.ScaleAll(0f);
            }

            if (attacker.IsNpc)
            {
                return;
            }

            if (!indestructibleWarnings.Contains(attacker.userID))
            {
                ulong uid = attacker.userID;
                indestructibleWarnings.Add(uid);
                timer.Once(10f, () => indestructibleWarnings.Remove(uid));
                Message(attacker, msg(isChest ? "Indestructible" : "Newman Protected", attacker.UserIDString));
            }
        }

        private static bool InRange(Vector3 a, Vector3 b, float distance)
        {
            
            return (new Vector3(a.x, 0f, a.z) - new Vector3(b.x, 0f, b.z)).sqrMagnitude <= distance * distance;
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

        void SaveData() => Interface.Oxide.DataFileSystem.WriteObject(Name, storedData);

        static void Message(BasePlayer target, string message)
        {
            if (target == null)
                return;

            Instance.Player.Message(target, message, 0uL);
        }

        void SubscribeHooks(bool flag)
        {
            if (flag && init)
            {
                if (_config.NPC.Enabled)
                {
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
                Subscribe(nameof(CanBeTargeted));
            }
            else
            {
                Unsubscribe(nameof(CanTeleport));
                Unsubscribe(nameof(canTeleport));
                Unsubscribe(nameof(CanBeTargeted));
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

        void SetWorkshopIDs()
        {
            try
            {
                foreach (var def in ItemManager.GetItemDefinitions())
                {
                    var skins = Rust.Workshop.Approved.All.Values.Where(skin => !string.IsNullOrEmpty(skin.Skinnable.ItemName) && skin.Skinnable.ItemName == def.shortname).Select(skin => System.Convert.ToUInt64(skin.WorkshopdId)).ToList();

                    if (skins != null && skins.Count > 0)
                    {
                        if (!workshopskinsCache.ContainsKey(def.shortname))
                        {
                            workshopskinsCache.Add(def.shortname, new List<ulong>());
                        }

                        foreach (ulong skin in skins)
                        {
                            if (!workshopskinsCache[def.shortname].Contains(skin))
                            {
                                workshopskinsCache[def.shortname].Add(skin);
                            }
                        }

                        skins.Clear();
                        skins = null;
                    }
                }
            }
            catch { }
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
                    if (Vector3.Distance(p, position) < space)
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

            bool isDuelist = Duelist != null;
            bool isRaidable = RaidableBases != null;
            bool isAbandoned = AbandonedBases != null;

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

        private Vector3 TryGetMonumentDropPosition()
        {
            if (allowedMonuments.Count == 0)
            {
                return Vector3.zero;
            }

            if (_config.Monuments.Only)
            {
                return GetMonumentDropPosition();
            }

            if (_config.Monuments.Chance > 0f)
            {
                var value = UnityEngine.Random.value;

                if (value <= _config.Monuments.Chance)
                {
                    return GetMonumentDropPosition();
                }
            }

            return Vector3.zero;
        }

        private bool IsTooClose(Vector3 vector, float multi = 2f)
        {
            foreach (var x in treasureChests.Values)
            {
                if (InRange(x.containerPos, vector, x.Radius * multi))
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
                else if (InRange(zone.Key, vector, zone.Value.Distance))
                {
                    return true;
                }
            }

            return false;
        }

        private bool IsSafeZone(Vector3 position)
        {
            var colliders = Pool.GetList<Collider>();
            Vis.Colliders(position, 200f, colliders);
            bool isSafeZone = colliders.Any(collider => collider.name.Contains("SafeZone"));
            Pool.FreeList(ref colliders);
            return isSafeZone;
        }

        public Vector3 GetSafeDropPosition(Vector3 position)
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

            if (IsLayerBlocked(position, _config.Event.Radius + 10f, obstructionLayer))
            {
                return Vector3.zero;
            }

            return position;
        }

        public float GetSpawnHeight(Vector3 target, bool flag = true, bool draw = false)
        {
            float y = TerrainMeta.HeightMap.GetHeight(target);
            float w = TerrainMeta.WaterMap.GetHeight(target);
            float p = TerrainMeta.HighestPoint.y + 250f;
            RaycastHit hit;

            if (Physics.Raycast(target.WithY(p), Vector3.down, out hit, ++p, Layers.Mask.World | Layers.Mask.Terrain | Layers.Mask.Default, QueryTriggerInteraction.Ignore))
            {
                if (!_blockedColliders.Exists(hit.collider.name.StartsWith))
                {
                    y = Mathf.Max(y, hit.point.y);
                }
            }

            return flag ? Mathf.Max(y, w) : y;
        }

        private List<string> _blockedColliders = new List<string>
        {
            "powerline_",
            "invisible_",
            "TopCol",
        };

        public bool IsLayerBlocked(Vector3 position, float radius, int mask)
        {
            var entities = Pool.GetList<BaseEntity>();
            Vis.Entities(position, radius, entities, mask, QueryTriggerInteraction.Ignore);
            entities.RemoveAll(entity => entity.IsNpc || !entity.OwnerID.IsSteamId());
            bool blocked = entities.Count > 0;
            Pool.FreeList(ref entities);
            return blocked;
        }

        public Vector3 GetRandomMonumentDropPosition(Vector3 position)
        {
            foreach (var monument in allowedMonuments)
            {
                if (Vector3.Distance(monument.Value.Position, position) > 75f)
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

                    if (IsLayerBlocked(hit.point, _config.Event.Radius + 10f, obstructionLayer))
                    {
                        continue;
                    }

                    return hit.point;
                }
            }

            return Vector3.zero;
        }

        public bool IsMonumentPosition(Vector3 target)
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

        public Vector3 GetMonumentDropPosition()
        {
            var list = allowedMonuments.ToList();
            var position = Vector3.zero;

            while (position == Vector3.zero && list.Count > 0)
            {
                var mon = list.GetRandom();
                var pos = mon.Value.Position;

                if (!IsTooClose(pos, 1f) && !IsZoneBlocked(pos) && !IsLayerBlocked(pos, _config.Event.Radius + 10f, obstructionLayer))
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
                if (!entities.Contains(e) && InRange(e.transform.position, position, _config.Event.Radius))
                {
                    if (e.IsNpc || e is LootContainer)
                    {
                        entities.Add(e);
                    }
                }
            }

            if (!_config.Monuments.Underground)
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

            entity.Invoke(entity.KillMessage, 0.1f);

            Pool.FreeList(ref entities);

            return entity.transform.position;
        }

        private List<Vector3> _gridPositions = new List<Vector3>();

        internal void SetupPositions()
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

        List<ulong> GetItemSkins(ItemDefinition def)
        {
            if (!skinsCache.ContainsKey(def.shortname))
            {
                var skins = def.skins.Select(skin => Convert.ToUInt64(skin.id)).ToList();

                if ((def.shortname == boxDef.shortname && _config.Skins.RandomWorkshopSkins) || (def.shortname != boxDef.shortname && _config.Treasure.RandomWorkshopSkins))
                {
                    if (workshopskinsCache.ContainsKey(def.shortname))
                    {
                        foreach (ulong skin in workshopskinsCache[def.shortname])
                        {
                            if (!skins.Contains(skin))
                            {
                                skins.Add(skin);
                            }
                        }

                        workshopskinsCache.Remove(def.shortname);
                    }
                }

                skinsCache.Add(def.shortname, new List<ulong>());

                foreach (ulong skin in skins)
                {
                    if (!skinsCache[def.shortname].Contains(skin))
                    {
                        skinsCache[def.shortname].Add(skin);
                    }
                }
            }

            return skinsCache[def.shortname];
        }

        TreasureChest TryOpenEvent(BasePlayer player = null)
        {
            var eventPos = Vector3.zero;

            if (player.IsValid())
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

            var container = GameManager.server.CreateEntity(StringPool.Get(boxPrefabID), eventPos, new Quaternion(), true) as StorageContainer;

            if (container == null)
            {
                return null;
            }

            if (_config.Skins.PresetSkin != 0uL)
                container.skinID = _config.Skins.PresetSkin;
            else if (useRandomSkins && boxDef != null)
            {
                var skins = GetItemSkins(boxDef);

                if (skins.Count > 0)
                {
                    container.skinID = skins.GetRandom();
                    container.SendNetworkUpdate();
                }
            }

            container.SetFlag(BaseEntity.Flags.Locked, true);
            container.SetFlag(BaseEntity.Flags.OnFire, true);
            if (!container.isSpawned) container.Spawn();

            TreasureChest.SpawnLoot(container, ChestLoot);

            var chest = container.gameObject.AddComponent<TreasureChest>();
            chest.Radius = _config.Event.Radius;

            if (container.enableSaving)
            {
                container.enableSaving = false;
                BaseEntity.saveList.Remove(container);
            }

            uint uid = container.net.ID;
            float unlockTime = UnityEngine.Random.Range(_config.Unlock.MinTime, _config.Unlock.MaxTime);

            SubscribeHooks(true);
            treasureChests.Add(uid, chest);
            
            var posStr = FormatGridReference(container.transform.position);
            Instance.Puts("{0}: {1}", posStr, string.Join(", ", container.inventory.itemList.Select(item => string.Format("{0} ({1})", item.info.displayName.translated, item.amount)).ToArray()));

            //if (!_config.Event.SpawnMax && treasureChests.Count > 1)
            //{
            //    AnnounceEventSpawn(container, unlockTime, posStr);
            //}

            foreach (var target in BasePlayer.activePlayerList)
            {
                double distance = Math.Round(Vector3.Distance(target.transform.position, container.transform.position), 2);
                string unlockStr = FormatTime(unlockTime, target.UserIDString);
                string message = msg("Opened", target.UserIDString, posStr, unlockStr, distance, _config.Settings.DistanceChatCommand);

                if (_config.EventMessages.Opened)
                {
                    Message(target, message);
                }

                if (_config.GUIAnnouncement.Enabled && GUIAnnouncements != null && distance <= _config.GUIAnnouncement.Distance)
                {
                    GUIAnnouncements?.Call("CreateAnnouncement", message, _config.GUIAnnouncement.TintColor, _config.GUIAnnouncement.TextColor, target);
                }

                if (useRockets && _config.EventMessages.Barrage)
                    Message(target, msg("Barrage", target.UserIDString, _config.Rocket.Amount));

                if (_config.Event.DrawTreasureIfNearby && _config.Event.AutoDrawDistance > 0f && distance <= _config.Event.AutoDrawDistance)
                    DrawText(target, container.transform.position, msg("Treasure Chest", target.UserIDString, distance));
            }

            var position = container.transform.position;
            storedData.TotalEvents++;
            SaveData();

            if (_config.LustyMap.Enabled)
                AddLustyMarker(position, uid);

            if (Map)
                AddMapMarker(position, uid);

            bool canSpawnNpcs = true;

            foreach (var x in monuments)
            {
                if (Vector3.Distance(x.Value.Position, position) <= x.Value.Radius)
                {
                    foreach (var value in _config.NPC.BlacklistedMonuments)
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

        private void AnnounceEventSpawn()
        {
            foreach (var target in BasePlayer.activePlayerList)
            {
                string message = msg("OpenedX", target.UserIDString, _config.Settings.DistanceChatCommand);

                if (_config.EventMessages.Opened)
                {
                    Message(target, message);
                }

                if (_config.GUIAnnouncement.Enabled && GUIAnnouncements != null)
                {
                    foreach (var chest in treasureChests)
                    {
                        double distance = Math.Round(Vector3.Distance(target.transform.position, chest.Value.containerPos), 2);
                        string unlockStr = FormatTime(chest.Value.countdownTime, target.UserIDString);
                        var posStr = FormatGridReference(chest.Value.containerPos);
                        string text = msg2("Opened", target.UserIDString, posStr, unlockStr, distance, _config.Settings.DistanceChatCommand);

                        if (distance <= _config.GUIAnnouncement.Distance)
                        {
                            GUIAnnouncements?.Call("CreateAnnouncement", text, _config.GUIAnnouncement.TintColor, _config.GUIAnnouncement.TextColor, target);
                        }

                        if (_config.Event.DrawTreasureIfNearby && _config.Event.AutoDrawDistance > 0f && distance <= _config.Event.AutoDrawDistance)
                        {
                            DrawText(target, chest.Value.containerPos, msg2("Treasure Chest", target.UserIDString, distance));
                        }
                    }
                }

                if (useRockets && _config.EventMessages.Barrage)
                    Message(target, msg("Barrage", target.UserIDString, _config.Rocket.Amount));
            }
        }

        private void AnnounceEventSpawn(StorageContainer container, float unlockTime, string posStr)
        {
            foreach (var target in BasePlayer.activePlayerList)
            {
                double distance = Math.Round(Vector3.Distance(target.transform.position, container.transform.position), 2);
                string unlockStr = FormatTime(unlockTime, target.UserIDString);
                string message = msg("Opened", target.UserIDString, posStr, unlockStr, distance, _config.Settings.DistanceChatCommand);

                if (_config.EventMessages.Opened)
                {
                    Message(target, message);
                }

                if (_config.GUIAnnouncement.Enabled && GUIAnnouncements != null && distance <= _config.GUIAnnouncement.Distance)
                {
                    GUIAnnouncements?.Call("CreateAnnouncement", message, _config.GUIAnnouncement.TintColor, _config.GUIAnnouncement.TextColor, target);
                }

                if (useRockets && _config.EventMessages.Barrage)
                {
                    Message(target, msg("Barrage", target.UserIDString, _config.Rocket.Amount));
                }

                if (_config.Event.DrawTreasureIfNearby && _config.Event.AutoDrawDistance > 0f && distance <= _config.Event.AutoDrawDistance)
                {
                    DrawText(target, container.transform.position, msg2("Treasure Chest", target.UserIDString, distance));
                }
            }
        }

        private void API_SetContainer(StorageContainer container, float radius, bool spawnNpcs) // Expansion Mode for Raidable Bases plugin
        {
            if (!container.IsValid())
            {
                return;
            }

            container.SetFlag(BaseEntity.Flags.Locked, true);
            container.SetFlag(BaseEntity.Flags.OnFire, true);

            var chest = container.gameObject.AddComponent<TreasureChest>();
            float unlockTime = UnityEngine.Random.Range(_config.Unlock.MinTime, _config.Unlock.MaxTime);

            chest.Radius = radius;
            treasureChests.Add(container.net.ID, chest);
            chest.Invoke(() => chest.SetUnlockTime(unlockTime), 2f);
            storedData.TotalEvents++;
            SaveData();

            Subscribe(nameof(OnEntityTakeDamage));
            Subscribe(nameof(OnItemRemovedFromContainer));
            Subscribe(nameof(OnLootEntity));

            if (spawnNpcs)
            {
                Subscribe(nameof(OnNpcTarget));
                Subscribe(nameof(OnNpcResume));
                Subscribe(nameof(OnNpcDestinationSet));
                Subscribe(nameof(CanBeTargeted));
                Subscribe(nameof(OnEntitySpawned));
                Subscribe(nameof(OnPlayerDeath));
                Subscribe(nameof(CanBradleyApcTarget));
                chest.Invoke(chest.SpawnNpcs, 1f);
            }
        }

        char[] chars = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz".ToCharArray();
        readonly StringBuilder _sb = new StringBuilder();

        string RandomString(int min = 5, int max = 10)
        {
            _sb.Length = 0;

            for (int i = 0; i <= UnityEngine.Random.Range(min, max + 1); i++)
                _sb.Append(chars[UnityEngine.Random.Range(0, chars.Length)]);

            return _sb.ToString();
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
            var eventInterval = UnityEngine.Random.Range(_config.Event.IntervalMin, _config.Event.IntervalMax);
            float stamp = Facepunch.Math.Epoch.Current;

            if (storedData.SecondsUntilEvent == double.MinValue) // first time users
            {
                storedData.SecondsUntilEvent = stamp + eventInterval;
                Puts(_("Next Automated Event", null, FormatTime(eventInterval), DateTime.Now.AddSeconds(eventInterval).ToString()));
                //Puts(_("View Config"));
                SaveData();
            }

            float time = 1f;

            if (_config.Event.Automated && storedData.SecondsUntilEvent - stamp <= 0 && treasureChests.Count < _config.Event.Max && BasePlayer.activePlayerList.Count >= _config.Event.PlayerLimit)
            {
                bool save = false;

                if (_config.Event.SpawnMax)
                {
                    save = TryOpenEvent() != null && treasureChests.Count >= _config.Event.Max;
                }
                else save = TryOpenEvent() != null;

                if (save)
                {
                    if (_config.Event.SpawnMax && treasureChests.Count > 1)
                    {
                        AnnounceEventSpawn();
                    }

                    storedData.SecondsUntilEvent = stamp + eventInterval;
                    Puts(_("Next Automated Event", null, FormatTime(eventInterval), DateTime.Now.AddSeconds(eventInterval).ToString()));
                    SaveData();
                }
                else time = _config.Event.Stagger;
            }

            eventTimer = timer.Once(time, () => CheckSecondsUntilEvent());
        }

        public static string FormatGridReference(Vector3 position)
        {
            string monumentName = null;
            float distance = 10000f;

            foreach (var x in Instance.monuments) // request MrSmallZzy
            {
                float magnitude = (x.Value.Position - position).magnitude;
                
                if (magnitude <= x.Value.Radius && magnitude < distance)
                {
                    monumentName = (x.Key.Contains(":") ? x.Key.Substring(0, x.Key.LastIndexOf(":")) : x.Key).TrimEnd();
                    distance = magnitude;
                }
            }

            if (_config.Settings.ShowXZ)
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
                if (BasePlayer.activePlayerList.Count < _config.Event.PlayerLimit)
                    return msg("Not Enough Online", null, _config.Event.PlayerLimit);
                else
                    return "0s";
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

                if (permission.UserHasPermission(target.Id, _config.RankedLadder.Permission))
                {
                    permission.RevokeUserPermission(target.Id, _config.RankedLadder.Permission);
                }

                if (permission.UserHasGroup(target.Id, _config.RankedLadder.Group))
                {
                    permission.RemoveUserGroup(target.Id, _config.RankedLadder.Group);
                }
            }

            if (!_config.RankedLadder.Enabled)
            {
                return true;
            }

            ladder.Sort((x, y) => y.Value.CompareTo(x.Value));

            foreach (var kvp in ladder.Take(_config.RankedLadder.Amount))
            {
                var userid = kvp.Key;

                if (permission.UserHasPermission(userid, notitlePermission))
                {
                    continue;
                }

                var target = covalence.Players.FindPlayerById(userid);

                if (target != null && target.IsBanned)
                {
                    continue;
                }

                permission.GrantUserPermission(userid, _config.RankedLadder.Permission, this);
                permission.AddUserGroup(userid, _config.RankedLadder.Group);

                LogToFile("treasurehunters", DateTime.Now.ToString() + " : " + msg("Log Stolen", null, target?.Name ?? userid, userid, kvp.Value), this, true);
                Puts(_("Log Granted", null, target?.Name ?? userid, userid, _config.RankedLadder.Permission, _config.RankedLadder.Group));
            }

            string file = string.Format("{0}{1}{2}_{3}-{4}.txt", Interface.Oxide.LogDirectory, System.IO.Path.DirectorySeparatorChar, Name, "treasurehunters", DateTime.Now.ToString("yyyy-MM-dd"));
            Puts(_("Log Saved", null, file));

            return true;
        }

        void AddMapMarker(Vector3 position, uint uid)
        {
            mapMarkers[uid] = new MapInfo { IconName = _config.LustyMap.IconName, Position = position, Url = _config.LustyMap.IconFile };
            Map?.Call("ApiAddPointUrl", _config.LustyMap.IconFile, _config.LustyMap.IconName, position);
            storedData.Markers.Add(uid);
        }

        void RemoveMapMarker(uint uid)
        {
            if (!mapMarkers.ContainsKey(uid))
                return;

            var mapInfo = mapMarkers[uid];

            Map?.Call("ApiRemovePointUrl", mapInfo.Url, mapInfo.IconName, mapInfo.Position);
            mapMarkers.Remove(uid);
            storedData.Markers.Remove(uid);
        }

        void AddLustyMarker(Vector3 pos, uint uid)
        {
            string name = string.Format("{0}_{1}", _config.LustyMap.IconName, storedData.TotalEvents).ToLower();

            LustyMap?.Call("AddTemporaryMarker", pos.x, pos.z, name, _config.LustyMap.IconFile, _config.LustyMap.IconRotation);
            lustyMarkers[uid] = name;
            storedData.Markers.Add(uid);
        }

        void RemoveLustyMarker(uint uid)
        {
            if (!lustyMarkers.ContainsKey(uid))
                return;

            LustyMap?.Call("RemoveTemporaryMarker", lustyMarkers[uid]);
            lustyMarkers.Remove(uid);
            storedData.Markers.Remove(uid);
        }

        void RemoveAllTemporaryMarkers()
        {
            if (storedData.Markers.Count == 0)
                return;

            if (LustyMap)
            {
                foreach (uint marker in storedData.Markers)
                {
                    LustyMap.Call("RemoveMarker", marker.ToString());
                }
            }

            if (Map)
            {
                foreach (uint marker in storedData.Markers.ToList())
                {
                    RemoveMapMarker(marker);
                }
            }

            storedData.Markers.Clear();
            SaveData();
        }

        void RemoveAllMarkers()
        {
            int removed = 0;

            for (int i = 0; i < storedData.TotalEvents + 1; i++)
            {
                string name = string.Format("{0}_{1}", _config.LustyMap.IconName, i).ToLower();

                if ((bool)(LustyMap?.Call("RemoveMarker", name) ?? false))
                {
                    removed++;
                }
            }

            storedData.Markers.Clear();

            if (removed > 0)
            {
                Puts("Removed {0} existing markers", removed);
            }
            else
                Puts("No markers found");
        }

        void DrawText(BasePlayer player, Vector3 drawPos, string text)
        {
            if (!player || !player.IsConnected || drawPos == Vector3.zero || string.IsNullOrEmpty(text) || _config.Event.DrawTime < 1f)
                return;

            bool isAdmin = player.IsAdmin;

            try
            {
                if (_config.Event.GrantDraw && !player.IsAdmin)
                {
                    var uid = player.userID;

                    if (!drawGrants.Contains(uid))
                    {
                        drawGrants.Add(uid);
                        timer.Once(_config.Event.DrawTime, () => drawGrants.Remove(uid));
                    }

                    player.SetPlayerFlag(BasePlayer.PlayerFlags.IsAdmin, true);
                    player.SendNetworkUpdateImmediate();
                }

                if (player.IsAdmin || drawGrants.Contains(player.userID))
                    player.SendConsoleCommand("ddraw.text", _config.Event.DrawTime, Color.yellow, drawPos, text);
            }
            catch (Exception ex)
            {
                _config.Event.GrantDraw = false;
                Puts("DrawText Exception: {0} --- {1}", ex.Message, ex.StackTrace);
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
                    Message(player, msg("InvalidItem", player.UserIDString, shortname, _config.Settings.DistanceChatCommand));
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
                        if (ulong.TryParse(args[2], out num))
                            skin = num;
                        else
                            Message(player, msg("InvalidValue", player.UserIDString, args[2]));
                    }

                    int minAmount = amount;
                    if (args.Length >= 4)
                    {
                        int num;
                        if (int.TryParse(args[3], out num))
                            minAmount = num;
                        else
                            Message(player, msg("InvalidValue", player.UserIDString, args[3]));
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
                    Message(player, msg("AddedItem", player.UserIDString, shortname, amount, skin));
                }
                else
                    Message(player, msg("InvalidValue", player.UserIDString, args[2]));

                return;
            }

            Message(player, msg("InvalidItem", player.UserIDString, args.Length >= 1 ? args[0] : "?", _config.Settings.DistanceChatCommand));
        }

        void cmdTreasureHunter(BasePlayer player, string command, string[] args)
        {
            if (drawGrants.Contains(player.userID))
                return;

            if (_config.RankedLadder.Enabled)
            {
                if (args.Length >= 1 && (args[0].ToLower() == "ladder" || args[0].ToLower() == "lifetime"))
                {
                    if (storedData.Players.Count == 0)
                    {
                        Message(player, msg("Ladder Insufficient Players", player.UserIDString));
                        return;
                    }

                    if (args.Length == 2 && args[1] == "resetme")
                        if (storedData.Players.ContainsKey(player.UserIDString))
                            storedData.Players[player.UserIDString].StolenChestsSeed = 0;

                    int rank = 0;
                    var ladder = storedData.Players.ToDictionary(k => k.Key, v => args[0].ToLower() == "ladder" ? v.Value.StolenChestsSeed : v.Value.StolenChestsTotal).Where(kvp => kvp.Value > 0).ToList();
                    ladder.Sort((x, y) => y.Value.CompareTo(x.Value));

                    Message(player, msg(args[0].ToLower() == "ladder" ? "Ladder" : "Ladder Total", player.UserIDString));

                    foreach (var kvp in ladder.Take(10))
                    {
                        string name = covalence.Players.FindPlayerById(kvp.Key)?.Name ?? kvp.Key;
                        string value = kvp.Value.ToString("N0");

                        Message(player, msg("TreasureHunter", player.UserIDString, ++rank, name, value));
                    }

                    return;
                }

                Message(player, msg("Wins", player.UserIDString, storedData.Players.ContainsKey(player.UserIDString) ? storedData.Players[player.UserIDString].StolenChestsSeed : 0, _config.Settings.DistanceChatCommand));
            }

            if (args.Length >= 1 && player.IsAdmin)
            {
                if (args[0] == "wipe")
                {
                    Message(player, msg("Log Saved", player.UserIDString, "treasurehunters"));
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
                    storedData.SecondsUntilEvent = double.MinValue;
                    return;
                }
                else if (args[0] == "now")
                {
                    storedData.SecondsUntilEvent = Facepunch.Math.Epoch.Current;
                    return;
                }
                else if (args[0] == "tp" && treasureChests.Count > 0)
                {
                    float dist = float.MaxValue;
                    var position = Vector3.zero;

                    foreach (var entry in treasureChests)
                    {
                        var v3 = Vector3.Distance(entry.Value.containerPos, player.transform.position);

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
                    AddItem(player, args.Skip(1).ToArray());
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
                    player.ChatMessage($"IsLayerBlocked: {IsLayerBlocked(player.transform.position, 25f, obstructionLayer)}");
                    player.ChatMessage($"SafeZone: {IsSafeZone(player.transform.position)}");

                    var entities = new List<BaseNetworkable>();

                    foreach (var e in BaseNetworkable.serverEntities.OfType<BaseEntity>())
                    {
                        if (!entities.Contains(e) && InRange(e.transform.position, player.transform.position, _config.Event.Radius))
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
                double time = Math.Max(0, storedData.SecondsUntilEvent - Facepunch.Math.Epoch.Current);
                Message(player, msg("Next", player.UserIDString, FormatTime(time, player.UserIDString)));
                return;
            }

            foreach (var chest in treasureChests)
            {
                double distance = Math.Round(Vector3.Distance(player.transform.position, chest.Value.containerPos), 2);
                string posStr = FormatGridReference(chest.Value.containerPos);

                Message(player, chest.Value.GetUnlockTime() != null ? msg("Info", player.UserIDString, chest.Value.GetUnlockTime(player.UserIDString), posStr, distance, _config.Settings.DistanceChatCommand) : msg("Already", player.UserIDString, posStr, distance, _config.Settings.DistanceChatCommand));
                if (_config.Settings.AllowDrawText) DrawText(player, chest.Value.containerPos, msg("Treasure Chest", player.UserIDString, distance));
            }
        }

        void ccmdDangerousTreasures(ConsoleSystem.Arg arg)
        {
            var player = arg.Player();

            if (!arg.IsAdmin)
            {
                if (player == null || !permission.UserHasPermission(player.UserIDString, _config.Settings.PermName))
                {
                    arg.ReplyWith(msg("No Permission", player?.UserIDString));
                    return;
                }
            }

            if (arg.HasArgs() && arg.Args.Length == 1)
            {
                if (arg.Args[0].ToLower() == "help")
                {
                    if (arg.IsRcon || arg.IsServerside)
                    {
                        Puts("Monuments:");
                        foreach (var m in monuments) Puts(m.Key);
                    }

                    arg.ReplyWith(msg("Help", player?.UserIDString, _config.Settings.EventChatCommand));
                }
                else if (arg.Args[0].ToLower() == "5") storedData.SecondsUntilEvent = Facepunch.Math.Epoch.Current + 5f;

                return;
            }

            var position = Vector3.zero;
            bool isDigit = arg.HasArgs() && arg.Args.Any(x => x.All(char.IsDigit));
            bool isTeleport = arg.HasArgs() && arg.Args.Any(x => x.ToLower() == "tp");

            int num = 0, amount = isDigit ? Convert.ToInt32(arg.Args.First(x => x.All(char.IsDigit))) : 1;

            if (amount < 1)
                amount = 1;

            if (treasureChests.Count >= _config.Event.Max && !arg.IsServerside)
            {
                arg.ReplyWith(RemoveFormatting(msg("Max Manual Events", player?.UserIDString, _config.Event.Max)));
                return;
            }

            for (int i = 0; i < amount; i++)
            {
                if (treasureChests.Count >= _config.Event.Max)
                {
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
                if (arg.HasArgs() && isTeleport && (player?.IsAdmin ?? false))
                {
                    if (player.IsFlying)
                    {
                        player.Teleport(position.y > player.transform.position.y ? position : position.WithY(player.transform.position.y));
                    }
                    else player.Teleport(position);
                }
            }
            else
            {
                if (position == Vector3.zero)
                    arg.ReplyWith(msg("Manual Event Failed", player?.UserIDString));
                else
                    Instance.Puts(_("Invalid Constant", null, boxPrefabID + " : Command()"));
            }

            if (num > 1)
            {
                arg.ReplyWith(msg("OpenedEvents", player?.UserIDString, num, amount));
            }
        }

        void cmdDangerousTreasures(BasePlayer player, string command, string[] args)
        {
            if (!permission.UserHasPermission(player.UserIDString, _config.Settings.PermName) && !player.IsAdmin)
            {
                Message(player, msg("No Permission", player.UserIDString));
                return;
            }

            if (args.Length == 1)
            {
                var arg = args[0].ToLower();

                if (arg == "help")
                {
                    Message(player, "Monuments: " + string.Join(", ", monuments.Select(m => m.Key)));
                    Message(player, msg("Help", player.UserIDString, _config.Settings.EventChatCommand));
                    return;
                }
                else if (player.IsAdmin)
                {
                    if (arg == "custom")
                    {
                        if (string.IsNullOrEmpty(storedData.CustomPosition))
                        {
                            storedData.CustomPosition = player.transform.position.ToString();
                            sd_customPos = player.transform.position;
                            Message(player, msg("CustomPositionSet", player.UserIDString, storedData.CustomPosition));
                        }
                        else
                        {
                            storedData.CustomPosition = "";
                            sd_customPos = Vector3.zero;
                            Message(player, msg("CustomPositionRemoved", player.UserIDString));
                        }
                        SaveData();
                        return;
                    }
                }
            }

            if (treasureChests.Count >= _config.Event.Max && player.net.connection.authLevel < 2)
            {
                Message(player, msg("Max Manual Events", player.UserIDString, _config.Event.Max));
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
                Message(player, msg("Manual Event Failed", player.UserIDString));
            }
        }

        #region Config

        Dictionary<string, Dictionary<string, string>> GetMessages()
        {
            return new Dictionary<string, Dictionary<string, string>>
            {
                {"No Permission", new Dictionary<string, string>() {
                    {"en", "You do not have permission to use this command."},
                }},
                {"Building is blocked!", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>Building is blocked near treasure chests!</color>"},
                }},
                {"Max Manual Events", new Dictionary<string, string>() {
                    {"en", "Maximum number of manual events <color=#FF0000>{0}</color> has been reached!"},
                }},
                {"Dangerous Zone Protected", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>You have entered a dangerous zone protected by a fire aura! You must leave before you die!</color>"},
                }},
                {"Dangerous Zone Unprotected", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>You have entered a dangerous zone!</color>"},
                }},
                {"Manual Event Failed", new Dictionary<string, string>() {
                    {"en", "Event failed to start! Unable to obtain a valid position. Please try again."},
                }},
                {"Help", new Dictionary<string, string>() {
                    {"en", "/{0} <tp> - start a manual event, and teleport to the position if TP argument is specified and you are an admin."},
                }},
                {"Started", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>The event has started at <color=#FFFF00>{0}</color>! The protective fire aura has been obliterated!</color>"},
                }},
                {"StartedNpcs", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>The event has started at <color=#FFFF00>{0}</color>! The protective fire aura has been obliterated! Npcs must be killed before the treasure will become lootable.</color>"},
                }},
                {"Opened", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>An event has opened at <color=#FFFF00>{0}</color>! Event will start in <color=#FFFF00>{1}</color>. You are <color=#FFA500>{2}m</color> away. Use <color=#FFA500>/{3}</color> for help.</color>"},
                }},
                {"OpenedX", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0><color=#FFFF00>Multiple events have opened! Use <color=#FFA500>/{0}</color> for help.</color>"},
                }},
                {"Barrage", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>A barrage of <color=#FFFF00>{0}</color> rockets can be heard at the location of the event!</color>"},
                }},
                {"Info", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>Event will start in <color=#FFFF00>{0}</color> at <color=#FFFF00>{1}</color>. You are <color=#FFA500>{2}m</color> away.</color>"},
                }},
                {"Already", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>The event has already started at <color=#FFFF00>{0}</color>! You are <color=#FFA500>{1}m</color> away.</color>"},
                }},
                {"Next", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>No events are open. Next event in <color=#FFFF00>{0}</color></color>"},
                }},
                {"Thief", new Dictionary<string, string>() {
                    {"en", "<color=#C0C0C0>The treasures at <color=#FFFF00>{0}</color> have been stolen by <color=#FFFF00>{1}</color>!</color>"},
                }},
                {"Wins", new Dictionary<string, string>()
                {
                    {"en", "<color=#C0C0C0>You have stolen <color=#FFFF00>{0}</color> treasure chests! View the ladder using <color=#FFA500>/{1} ladder</color> or <color=#FFA500>/{1} lifetime</color></color>"},
                }},
                {"Ladder", new Dictionary<string, string>()
                {
                    {"en", "<color=#FFFF00>[ Top 10 Treasure Hunters (This Wipe) ]</color>:"},
                }},
                {"Ladder Total", new Dictionary<string, string>()
                {
                    {"en", "<color=#FFFF00>[ Top 10 Treasure Hunters (Lifetime) ]</color>:"},
                }},
                {"Ladder Insufficient Players", new Dictionary<string, string>()
                {
                    {"en", "<color=#FFFF00>No players are on the ladder yet!</color>"},
                }},
                {"Event At", new Dictionary<string, string>() {
                    {"en", "Event at {0}"},
                }},
                {"Next Automated Event", new Dictionary<string, string>() {
                    {"en", "Next automated event in {0} at {1}"},
                }},
                {"Not Enough Online", new Dictionary<string, string>() {
                    {"en", "Not enough players online ({0} minimum)"},
                }},
                {"Treasure Chest", new Dictionary<string, string>() {
                    {"en", "Treasure Chest <color=#FFA500>{0}m</color>"},
                }},
                {"Invalid Constant", new Dictionary<string, string>() {
                    {"en", "Invalid constant {0} - please notify the author!"},
                }},
                {"Destroyed Treasure Chest", new Dictionary<string, string>() {
                    {"en", "Destroyed a left over treasure chest at {0}"},
                }},
                {"Indestructible", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>Treasure chests are indestructible!</color>"},
                }},
                {"View Config", new Dictionary<string, string>() {
                    {"en", "Please view the config if you haven't already."},
                }},
                {"Newman Enter", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>To walk with clothes is to set one-self on fire. Tread lightly.</color>"},
                }},
                {"Newman Traitor Burn", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>Tempted by the riches you have defiled these grounds. Vanish from these lands or PERISH!</color>"},
                }},
                {"Newman Traitor", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>Tempted by the riches you have defiled these grounds. Vanish from these lands!</color>"},
                }},
                {"Newman Protected", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>This newman is temporarily protected on these grounds!</color>"},
                }},
                {"Newman Protect", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>You are protected on these grounds. Do not defile them.</color>"},
                }},
                {"Newman Protect Fade", new Dictionary<string, string>() {
                    {"en", "<color=#FF0000>Your protection has faded.</color>"},
                }},
                {"Log Stolen", new Dictionary<string, string>() {
                    {"en", "{0} ({1}) chests stolen {2}"},
                }},
                {"Log Granted", new Dictionary<string, string>() {
                    {"en", "Granted {0} ({1}) permission {2} for group {3}"},
                }},
                {"Log Saved", new Dictionary<string, string>() {
                    {"en", "Treasure Hunters have been logged to: {0}"},
                }},
                {"MessagePrefix", new Dictionary<string, string>() {
                    {"en", "[ <color=#406B35>Dangerous Treasures</color> ] "},
                }},
                {"Countdown", new Dictionary<string, string>()
                {
                    {"en", "<color=#C0C0C0>Event at <color=#FFFF00>{0}</color> will start in <color=#FFFF00>{1}</color>!</color>"},
                }},
                {"RestartDetected", new Dictionary<string, string>()
                {
                    {"en", "Restart detected. Next event in {0} minutes."},
                }},
                {"DestroyingTreasure", new Dictionary<string, string>()
                {
                    {"en", "<color=#C0C0C0>The treasure at <color=#FFFF00>{0}</color> will be destroyed by fire in <color=#FFFF00>{1}</color> if not looted! Use <color=#FFA500>/{2}</color> to find this chest.</color>"},
                }},
                {"EconomicsDeposit", new Dictionary<string, string>()
                {
                    {"en", "You have received <color=#FFFF00>${0}</color> for stealing the treasure!"},
                }},
                {"ServerRewardPoints", new Dictionary<string, string>()
                {
                    {"en", "You have received <color=#FFFF00>{0} RP</color> for stealing the treasure!"},
                }},
                {"InvalidItem", new Dictionary<string, string>()
                {
                    {"en", "Invalid item shortname: {0}. Use /{1} additem <shortname> <amount> [skin]"},
                }},
                {"AddedItem", new Dictionary<string, string>()
                {
                    {"en", "Added item: {0} amount: {1}, skin: {2}"},
                }},
                {"CustomPositionSet", new Dictionary<string, string>()
                {
                    {"en", "Custom event spawn location set to: {0}"},
                }},
                {"CustomPositionRemoved", new Dictionary<string, string>()
                {
                    {"en", "Custom event spawn location removed."},
                }},
                {"OpenedEvents", new Dictionary<string, string>()
                {
                    {"en", "Opened {0}/{1} events."},
                }},
                {"OnFirstPlayerEntered", new Dictionary<string, string>()
                {
                    {"en", "<color=#FFFF00>{0}</color> is the first to enter the dangerous treasure event at <color=#FFFF00>{1}</color>"},
                }},
                {"OnChestOpened", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>{0}</color> is the first to see the treasures at <color=#FFFF00>{1}</color>!</color>"},
                }},
                {"OnChestDespawned", new Dictionary<string, string>() {
                    {"en", "The treasures at <color=#FFFF00>{0}</color> have been lost forever! Better luck next time."},
                }},
                {"CannotBeMounted", new Dictionary<string, string>() {
                    {"en", "You cannot loot the treasure while mounted!"},
                }},
                {"CannotTeleport", new Dictionary<string, string>() {
                    {"en", "You are not allowed to teleport from this event."},
                }},
                {"TreasureHunter", new Dictionary<string, string>() {
                    {"en", "<color=#ADD8E6>{0}</color>. <color=#C0C0C0>{1}</color> (<color=#FFFF00>{2}</color>)"},
                }},
                {"Timed Event", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>You cannot loot until the fire aura expires! Tread lightly, the fire aura is very deadly!</color>)"},
                }},
                {"Timed Npc Event", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>You cannot loot until you kill all of the npcs and wait for the fire aura to expire! Tread lightly, the fire aura is very deadly!</color>)"},
                }},
                {"Npc Event", new Dictionary<string, string>() {
                    {"en", "<color=#FFFF00>You cannot loot until you kill all of the npcs surrounding the fire aura! Tread lightly, the fire aura is very deadly!</color>)"},
                }},
            };
        }

        protected override void LoadDefaultMessages()
        {
            var compiledLangs = new Dictionary<string, Dictionary<string, string>>();

            foreach (var line in GetMessages())
            {
                foreach (var translate in line.Value)
                {
                    if (!compiledLangs.ContainsKey(translate.Key))
                        compiledLangs[translate.Key] = new Dictionary<string, string>();

                    compiledLangs[translate.Key][line.Key] = translate.Value;
                }
            }

            foreach (var cLangs in compiledLangs)
                lang.RegisterMessages(cLangs.Value, this, cLangs.Key);
        }

        static int GetPercentIncreasedAmount(int amount)
        {
            if (_config.Treasure.UseDOWL && !_config.Treasure.Increased && _config.Treasure.PercentLoss > 0m)
            {
                return UnityEngine.Random.Range(Convert.ToInt32(amount - (amount * _config.Treasure.PercentLoss)), amount + 1);
            }

            decimal percentIncrease = 0m;

            switch (DateTime.Now.DayOfWeek)
            {
                case DayOfWeek.Monday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnMonday;
                        break;
                    }
                case DayOfWeek.Tuesday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnTuesday;
                        break;
                    }
                case DayOfWeek.Wednesday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnWednesday;
                        break;
                    }
                case DayOfWeek.Thursday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnThursday;
                        break;
                    }
                case DayOfWeek.Friday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnFriday;
                        break;
                    }
                case DayOfWeek.Saturday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnSaturday;
                        break;
                    }
                case DayOfWeek.Sunday:
                    {
                        percentIncrease = _config.Treasure.PercentIncreaseOnSunday;
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

                if (_config.Treasure.PercentLoss > 0m)
                {
                    amount = UnityEngine.Random.Range(Convert.ToInt32(amount - (amount * _config.Treasure.PercentLoss)), amount + 1);
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
            string message = _config.EventMessages.Prefix && id != null && id != "server_console" ? lang.GetMessage("MessagePrefix", this, null) + lang.GetMessage(key, this, id) : lang.GetMessage(key, this, id);

            return message.Contains("{0}") ? string.Format(message, args) : message;
        }

        string msg2(string key, string id, params object[] args)
        {
            string message = lang.GetMessage(key, this, id);

            return message.Contains("{0}") ? string.Format(message, args) : message;
        }

        string RemoveFormatting(string source) => source.Contains(">") ? System.Text.RegularExpressions.Regex.Replace(source, "<.*?>", string.Empty) : source;
        #endregion

        #region Configuration

        private static Configuration _config;

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

        public class EventMessageSettings
        {
            [JsonProperty(PropertyName = "Show Noob Warning Message")]
            public bool NoobWarning { get; set; }

            [JsonProperty(PropertyName = "Show Barrage Message")]
            public bool Barrage { get; set; } = true;

            [JsonProperty(PropertyName = "Show Despawn Message")]
            public bool Destruct { get; set; } = true;

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

            [JsonProperty(PropertyName = "Weapon", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> Weapon { get; set; } = new List<string> { "rifle.ak" };
        }

        public class NpcSettings
        {
            [JsonProperty(PropertyName = "Enabled")]
            public bool Enabled { get; set; } = true;

            [JsonProperty(PropertyName = "Aggression Range")]
            public float AggressionRange { get; set; } = 70f;

            [JsonProperty(PropertyName = "Give Npcs Wallhack Cheat")]
            public bool Wallhack { get; set; } = true;

            [JsonProperty(PropertyName = "Block Damage From Players Beyond X Distance (0 = disabled)")]
            public float Range { get; set; } = 0f;

            [JsonProperty(PropertyName = "Murderers Die Instantly From Headshots")]
            public bool MurdererHeadshot { get; set; } 

            [JsonProperty(PropertyName = "Scientists Die Instantly From Headshots")]
            public bool ScientistHeadshot { get; set; } 

            [JsonProperty(PropertyName = "Scientist Weapon Accuracy (0 - 100)")]
            public float Accuracy { get; set; } = 25f;

            [JsonProperty(PropertyName = "Spawn Murderers")]
            public bool SpawnMurderers { get; set; } = false;

            [JsonProperty(PropertyName = "Spawn Scientists Only")]
            public bool SpawnScientistsOnly { get; set; } = false;

            [JsonProperty(PropertyName = "Spawn Murderers And Scientists")]
            public bool SpawnBoth { get; set; } = true;

            [JsonProperty(PropertyName = "Amount To Spawn")]
            public int SpawnAmount { get; set; } = 3;

            [JsonProperty(PropertyName = "Minimum Amount To Spawn")]
            public int SpawnMinAmount { get; set; } = 1;

            [JsonProperty(PropertyName = "Despawn Inventory On Death")]
            public bool DespawnInventory { get; set; } = true;

            [JsonProperty(PropertyName = "Spawn Random Amount")]
            public bool SpawnRandomAmount { get; set; } = false;

            [JsonProperty(PropertyName = "Health For Murderers (100 min, 5000 max)")]
            public float MurdererHealth { get; set; } = 150f;

            [JsonProperty(PropertyName = "Health For Scientists (100 min, 5000 max)")]
            public float ScientistHealth { get; set; } = 150f;

            /*[JsonProperty(PropertyName = "Murderer Sprinting Speed (0 min, 2.5 max)")]
            public float MurdererSpeedSprinting { get; set; } = 2.5f;

            [JsonProperty(PropertyName = "Murderer Walking Speed (0 min, 2.5 max)")]
            public float MurdererSpeedWalking { get; set; } = 1.5f;

            [JsonProperty(PropertyName = "Scientist Sprinting Speed (0 min, 2.5 max)")]
            public float ScientistSpeedSprinting { get; set; } = 1.0f;

            [JsonProperty(PropertyName = "Scientist Walking Speed (0 min, 2.5 max)")]
            public float ScientistSpeedWalking { get; set; } = 0.0f;*/

            [JsonProperty(PropertyName = "Murderer (Items)")]
            public MurdererKitSettings MurdererItems { get; set; } = new MurdererKitSettings();

            [JsonProperty(PropertyName = "Scientist (Items)")]
            public ScientistKitSettings ScientistItems { get; set; } = new ScientistKitSettings();

            [JsonProperty(PropertyName = "Murderer Kits", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> MurdererKits { get; set; } = new List<string> { "murderer_kit_1", "murderer_kit_2" };

            [JsonProperty(PropertyName = "Scientist Kits", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> ScientistKits { get; set; } = new List<string> { "scientist_kit_1", "scientist_kit_2" };

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

            [JsonProperty(PropertyName = "Random Names", ObjectCreationHandling = ObjectCreationHandling.Replace)]
            public List<string> RandomNames { get; set; } = new List<string>();
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
            [JsonProperty(PropertyName = "Use Random Skin")]
            public bool RandomSkins { get; set; } = true;

            [JsonProperty(PropertyName = "Preset Skin")]
            public ulong PresetSkin { get; set; } = 0;

            [JsonProperty(PropertyName = "Include Workshop Skins")]
            public bool RandomWorkshopSkins { get; set; } = true;
        }

        public class LootItem
        {
            public string shortname { get; set; }
            public int amount { get; set; }
            public ulong skin { get; set; }
            public int amountMin { get; set; }
            public float probability { get; set; } = 1f;
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

        private const string notitlePermission = "dangeroustreasures.notitle";

        protected override void LoadConfig()
        {
            base.LoadConfig();
            try
            {
                _config = Config.ReadObject<Configuration>();
                if (_config == null) throw new Exception();
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
            if (_config.Rocket.Speed > 0.1f) _config.Rocket.Speed = 0.1f;
            if (_config.Treasure.PercentLoss > 0) _config.Treasure.PercentLoss /= 100m;
            if (_config.Monuments.Chance < 0) _config.Monuments.Chance = 0f;
            if (_config.Monuments.Chance > 1f) _config.Monuments.Chance /= 100f;
            if (_config.Event.Radius < 10f) _config.Event.Radius = 10f;
            if (_config.Event.Radius > 150f) _config.Event.Radius = 150f;
            if (_config.MissileLauncher.Distance < 1f) _config.MissileLauncher.Distance = 15f;
            if (_config.MissileLauncher.Distance > _config.Event.Radius * 15) _config.MissileLauncher.Distance = _config.Event.Radius * 2;
            if (_config.LustyMap.IconFile == "special") _config.LustyMap.IconFile = "http://i.imgur.com/XoEMTJj.png";

            if (!string.IsNullOrEmpty(_config.Settings.PermName) && !permission.PermissionExists(_config.Settings.PermName)) permission.RegisterPermission(_config.Settings.PermName, this);
            if (!string.IsNullOrEmpty(_config.Settings.EventChatCommand)) cmd.AddChatCommand(_config.Settings.EventChatCommand, this, cmdDangerousTreasures);
            if (!string.IsNullOrEmpty(_config.Settings.DistanceChatCommand)) cmd.AddChatCommand(_config.Settings.DistanceChatCommand, this, cmdTreasureHunter);
            if (!string.IsNullOrEmpty(_config.Settings.EventConsoleCommand)) cmd.AddConsoleCommand(_config.Settings.EventConsoleCommand, this, nameof(ccmdDangerousTreasures));
            if (string.IsNullOrEmpty(_config.RankedLadder.Permission)) _config.RankedLadder.Permission = "dangeroustreasures.th";
            if (string.IsNullOrEmpty(_config.RankedLadder.Group)) _config.RankedLadder.Group = "treasurehunter";
            if (string.IsNullOrEmpty(_config.LustyMap.IconFile) || string.IsNullOrEmpty(_config.LustyMap.IconName)) _config.LustyMap.Enabled = false;

            if (!string.IsNullOrEmpty(_config.RankedLadder.Permission))
            {
                if (!permission.PermissionExists(_config.RankedLadder.Permission))
                    permission.RegisterPermission(_config.RankedLadder.Permission, this);

                if (!string.IsNullOrEmpty(_config.RankedLadder.Group))
                {
                    permission.CreateGroup(_config.RankedLadder.Group, _config.RankedLadder.Group, 0);
                    permission.GrantGroupPermission(_config.RankedLadder.Group, _config.RankedLadder.Permission, this);
                }
            }

            permission.RegisterPermission(notitlePermission, this);

            if (_config.UnlootedAnnouncements.Interval < 1f) _config.UnlootedAnnouncements.Interval = 1f;
            if (_config.Event.AutoDrawDistance < 0f) _config.Event.AutoDrawDistance = 0f;
            if (_config.Event.AutoDrawDistance > ConVar.Server.worldsize) _config.Event.AutoDrawDistance = ConVar.Server.worldsize;
            if (_config.GUIAnnouncement.TintColor.ToLower() == "black") _config.GUIAnnouncement.TintColor = "grey";
            if (_config.NPC.SpawnAmount < 1) _config.NPC.Enabled = false;
            if (_config.NPC.SpawnAmount > 25) _config.NPC.SpawnAmount = 25;
            if (_config.NPC.SpawnMinAmount < 1 || _config.NPC.SpawnMinAmount > _config.NPC.SpawnAmount) _config.NPC.SpawnMinAmount = 1;
            if (_config.NPC.ScientistHealth < 100) _config.NPC.ScientistHealth = 100f;
            if (_config.NPC.ScientistHealth > 5000) _config.NPC.ScientistHealth = 5000f;
            if (_config.NPC.MurdererHealth < 100) _config.NPC.MurdererHealth = 100f;
            if (_config.NPC.MurdererHealth > 5000) _config.NPC.MurdererHealth = 5000f;
            /*if (_config.NPC.MurdererSpeedSprinting > 2.5f) _config.NPC.MurdererSpeedSprinting = 2.5f;
            if (_config.NPC.MurdererSpeedWalking > 2.5) _config.NPC.MurdererSpeedWalking = 2.5f;
            if (_config.NPC.MurdererSpeedWalking < 0) _config.NPC.MurdererSpeedWalking = 0f;
            if (_config.NPC.ScientistSpeedSprinting > 2.5f) _config.NPC.ScientistSpeedSprinting = 2.5f;
            if (_config.NPC.ScientistSpeedWalking < 0) _config.NPC.ScientistSpeedWalking = 0f;
            if (_config.NPC.ScientistSpeedWalking > 2.5) _config.NPC.ScientistSpeedWalking = 2.5f;*/
        }

        List<LootItem> ChestLoot
        {
            get
            {
                if (IsUnloading || !init)
                {
                    return new List<LootItem>();
                }

                if (_config.Treasure.UseDOWL)
                {
                    switch (DateTime.Now.DayOfWeek)
                    {
                        case DayOfWeek.Monday:
                            {
                                return _config.Treasure.DOWL_Monday;
                            }
                        case DayOfWeek.Tuesday:
                            {
                                return _config.Treasure.DOWL_Tuesday;
                            }
                        case DayOfWeek.Wednesday:
                            {
                                return _config.Treasure.DOWL_Wednesday;
                            }
                        case DayOfWeek.Thursday:
                            {
                                return _config.Treasure.DOWL_Thursday;
                            }
                        case DayOfWeek.Friday:
                            {
                                return _config.Treasure.DOWL_Friday;
                            }
                        case DayOfWeek.Saturday:
                            {
                                return _config.Treasure.DOWL_Saturday;
                            }
                        case DayOfWeek.Sunday:
                            {
                                return _config.Treasure.DOWL_Sunday;
                            }
                    }
                }

                return _config.Treasure.Loot;
            }
        }

        protected override void SaveConfig() => Config.WriteObject(_config);

        protected override void LoadDefaultConfig() => _config = new Configuration();

        #endregion
    }
}
