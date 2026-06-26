using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Styx;
using Styx.Logic.POI;
using Styx.Helpers;
using Styx.Logic.AreaManagement;
using Styx.Logic.BehaviorTree;
using Styx.Logic.Inventory.Frames.Merchant;
using Styx.Logic.Inventory.Frames.Trainer;
using Styx.Logic.Pathing;
using Styx.Logic.Profiles;
using Styx.Combat.CombatRoutine;
using Styx.WoWInternals;
using Bots.Quest.QuestOrder;
using TreeSharp;
using Action = TreeSharp.Action;

namespace WholesomeAQ
{
    public class WholesomeAutoQuest : Bots.Quest.QuestBot
    {
        private DataLoader _dataLoader;
        private VendorDataLoader _vendorLoader;
        private QuestScheduler _scheduler;
        private ProfileBuilder _profileBuilder;
        private WholesomeAQSettings _settings = new WholesomeAQSettings();
        private bool _initialized;
        private bool _dataReady;
        private bool _vendorDataReady;
        private DateTime _lastScanTime = DateTime.MinValue;
        private bool _forceStopped;
        private string _profilePath;
        private string _vendorBlacklistPath;
        private string _questBlacklistPath;
        private Timer _restartTimer;
        private double _lastX, _lastY, _lastZ;
        private double _anchorX, _anchorY, _anchorZ;
        private bool _anchorSet;
        private DateTime _lastMovedTime = DateTime.Now;
        private HashSet<int> _lastReadyQuestIds;
        private bool _pendingRescan;
        private DateTime _pickupStartTime;
        private string _lastPickupTarget;
        private bool _pickupLogged;
        private bool _wasStuck;
        private bool _stuckLogged;
        private Dictionary<int, int> _deathCountByQuest = new();
        private Dictionary<int, int> _noHotspotCount = new();
        private DateTime _lastNoHotspotCheck = DateTime.MinValue;

        public override string Name => "Wholesome Auto Quest";
        public override bool RequiresProfile => false;

        public override Composite Root => base.Root;

        public override Form ConfigurationForm
        {
            get
            {
                return new SettingsForm(_settings, Log,
                    forceStop: () => { _forceStopped = true; TreeRoot.Stop(); },
                    resume: () => { _forceStopped = false; DoScan(); },
                    saveQuestBlacklist: SaveQuestBlacklist);
            }
        }

        public override void Start()
        {
            _profilePath = FindProfilePath();
            _profileBuilder = new ProfileBuilder(_profilePath);
            _dataLoader = new DataLoader();
            _dataReady = _dataLoader.Load() != null;
            _vendorLoader = new VendorDataLoader();
            _vendorDataReady = _vendorLoader.Load();
            _vendorBlacklistPath = Path.Combine(Path.GetDirectoryName(_profilePath), "vendor_blacklist.txt");
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string charName = StyxWoW.Me?.Name ?? "Unknown";
            string realm = Lua.GetReturnVal<string>("return GetRealmName()", 0) ?? "Unknown";
            _questBlacklistPath = Path.Combine(baseDir, "Settings", "WholesomeAutoQuest", $"{charName}-{realm}", "quest_blacklist.txt");
            LoadVendorBlacklist();
            LoadQuestBlacklist();
            _scheduler = new QuestScheduler(_dataLoader, _profileBuilder, _settings);
            _initialized = true;
            _lastScanTime = DateTime.MinValue;

            if (_restartTimer == null)
            {
                _restartTimer = new Timer { Interval = 1000 };
                _restartTimer.Tick += (_, _) =>
                {
                    try
                    {
                        if (_pendingRescan)
                        {
                            _pendingRescan = false;
                            _lastScanTime = DateTime.Now;
                            TreeRoot.Stop();
                            DoScan();
                            TreeRoot.Start();
                            return;
                        }

                        if (!TreeRoot.IsRunning)
                        {
                            if (_forceStopped) return;
                            if (StyxWoW.Me.Combat)
                            {
                                Log("Stopped while in combat — resuming instantly");
                                TreeRoot.Start();
                            }
                            TreeRoot.Stop();
                            DoScan();
                            TreeRoot.Start();
                        }
                        else if (_lastScanTime != DateTime.MinValue
                              && (DateTime.Now - _lastScanTime).TotalSeconds > 30
                              && !StyxWoW.Me.Combat)
                        {
                            _lastScanTime = DateTime.Now;
                            TreeRoot.Stop();
                            DoScan();
                            TreeRoot.Start();
                        }
                    }
                    catch { }
                };
                _restartTimer.Start();
            }

            base.Start();

            BotEvents.Player.OnPlayerDied -= OnPlayerDied;
            BotEvents.Player.OnPlayerDied += OnPlayerDied;

            if (StyxWoW.Me != null)
            {
                bool ranged = StyxWoW.Me.Class == WoWClass.Hunter
                    || StyxWoW.Me.Class == WoWClass.Mage
                    || StyxWoW.Me.Class == WoWClass.Priest
                    || StyxWoW.Me.Class == WoWClass.Warlock;
                int dist = ranged ? 30 : 24;
                CharacterSettings.Instance.PullDistance = dist;
                Log($"Class {StyxWoW.Me.Class} → PullDistance set to {dist}");
            }

            DoScan();

            Log(_dataReady ? "Started with quest data loaded." : "Started. No quest data loaded.");
        }

        public override void Stop()
        {
            Log("Use Settings > Force Stop to stop the bot.");
        }

        private void DoScan()
        {
            if (!_initialized || !_dataReady) return;
            if (!StyxWoW.IsInGame || StyxWoW.Me == null) return;

            _lastScanTime = DateTime.Now;

            try
            {
                _scheduler.SyncBlacklist();

                if (_vendorDataReady && StyxWoW.Me != null)
                {
                    var bl = _settings.BlacklistedVendors;
                    _scheduler.CurrentVendors = _vendorLoader.GetNearestVendors(StyxWoW.Me, "Repair", 3, bl)
                        .Concat(_vendorLoader.GetNearestVendors(StyxWoW.Me, "Food", 3, bl))
                        .Concat(_vendorLoader.GetNearestVendors(StyxWoW.Me, "Train", 2, bl))
                        .ToList();
                }

                if (_scheduler.ScanAndRefresh(StyxWoW.Me))
                {
                    bool hasQuests = _scheduler.ActiveQuestIds != null
                                  && _scheduler.ActiveQuestIds.Count > 0;

                    if (hasQuests)
                    {
                        if (StyxWoW.Me.Combat)
                        {
                            Log("In combat — deferring profile refresh");
                            return;
                        }

                        ProfileManager.LoadNew(_scheduler.CurrentProfilePath);
                        if (TreeRoot.IsRunning)
                        {
                            TreeRoot.Stop();
                            if (StyxWoW.Me.Combat)
                            {
                                Log("In combat after stop — resuming instantly");
                                TreeRoot.Start();
                                return;
                            }
                        }
                        TreeRoot.Start();
                        Log($"Profile refreshed - {_scheduler.LastStatus}");
                        LogFarAwayQuests();
                    }
                }
            }
            catch (Exception ex)
            {
                Log($"Scan error: {ex.Message}");
            }
        }

        public override void Pulse()
        {
            base.Pulse();

            if (StyxWoW.IsInGame && StyxWoW.Me != null && !StyxWoW.Me.Combat && TreeRoot.IsRunning)
            {
                var loc = StyxWoW.Me.Location;

                if (!_anchorSet)
                {
                    _anchorX = loc.X;
                    _anchorY = loc.Y;
                    _anchorZ = loc.Z;
                    _anchorSet = true;
                }

                double dx = loc.X - _anchorX;
                double dy = loc.Y - _anchorY;
                double dz = loc.Z - _anchorZ;
                if (dx * dx + dy * dy + dz * dz > 25.0)
                {
                    _anchorX = loc.X;
                    _anchorY = loc.Y;
                    _anchorZ = loc.Z;
                }

                if (Math.Abs(loc.X - _lastX) > 0.1 || Math.Abs(loc.Y - _lastY) > 0.1 || Math.Abs(loc.Z - _lastZ) > 0.1)
                {
                    _lastX = loc.X;
                    _lastY = loc.Y;
                    _lastZ = loc.Z;
                    _lastMovedTime = DateTime.Now;
                    _wasStuck = false;
                    _stuckLogged = false;
                }
                else
                {
                    double stuckSec = (DateTime.Now - _lastMovedTime).TotalSeconds;

                    if (stuckSec > 30 && !_wasStuck)
                    {
                        var poi = BotPoi.Current;
                        bool poiIsVendor = poi != null
                            && (poi.Type == PoiType.Sell
                             || poi.Type == PoiType.Repair
                             || poi.Type == PoiType.Buy
                             || poi.Type == PoiType.Train);

                        if (poiIsVendor)
                        {
                            _wasStuck = true;
                            _stuckLogged = true;
                            _settings.BlacklistedVendors.Add((int)poi.Entry);
                            SaveVendorBlacklist();
                            Log($"Blacklisted vendor {poi.Name} (Entry:{poi.Entry}) — stuck for {stuckSec:F0}s, forcing re-scan");
                            TreeRoot.Stop();
                        }
                        else if (MerchantFrame.Instance.IsVisible
                            && _scheduler?.CurrentVendors != null
                            && _scheduler.CurrentVendors.Any(v =>
                                Math.Sqrt(Math.Pow(v.X - loc.X, 2) + Math.Pow(v.Y - loc.Y, 2)) < 50))
                        {
                            _wasStuck = true;
                            _stuckLogged = true;
                            var stuckVendor = _scheduler.CurrentVendors
                                .Where(v => Math.Sqrt(Math.Pow(v.X - loc.X, 2) + Math.Pow(v.Y - loc.Y, 2)) < 50)
                                .OrderBy(v => Math.Sqrt(Math.Pow(v.X - loc.X, 2) + Math.Pow(v.Y - loc.Y, 2)))
                                .First();
                            _settings.BlacklistedVendors.Add(stuckVendor.Entry);
                            SaveVendorBlacklist();
                            Log($"Blacklisted vendor {stuckVendor.Name} (Entry:{stuckVendor.Entry}) — stuck for {stuckSec:F0}s (fallback), forcing re-scan");
                            TreeRoot.Stop();
                        }
                        else if (TrainerFrame.Instance.IsVisible
                            && _scheduler?.CurrentVendors != null
                            && _scheduler.CurrentVendors.Any(v =>
                                !string.IsNullOrEmpty(v.TrainClass)
                                && Math.Sqrt(Math.Pow(v.X - loc.X, 2) + Math.Pow(v.Y - loc.Y, 2)) < 50))
                        {
                            _wasStuck = true;
                            _stuckLogged = true;
                            var stuckVendor = _scheduler.CurrentVendors
                                .Where(v => !string.IsNullOrEmpty(v.TrainClass)
                                    && Math.Sqrt(Math.Pow(v.X - loc.X, 2) + Math.Pow(v.Y - loc.Y, 2)) < 50)
                                .OrderBy(v => Math.Sqrt(Math.Pow(v.X - loc.X, 2) + Math.Pow(v.Y - loc.Y, 2)))
                                .First();
                            _settings.BlacklistedVendors.Add(stuckVendor.Entry);
                            SaveVendorBlacklist();
                            Log($"Blacklisted trainer {stuckVendor.Name} (Entry:{stuckVendor.Entry}) — stuck for {stuckSec:F0}s (trainer frame open), forcing re-scan");
                            TreeRoot.Stop();
                        }
                        else if (stuckSec > 180)
                        {
                            _wasStuck = true;
                            _stuckLogged = true;
                            if (_scheduler?.ActiveQuestIds != null && _scheduler.ActiveQuestIds.Count > 0)
                            {
                                int stuckQuest = FindStuckQuestByNoHotspots();
                                if (stuckQuest == 0)
                                    stuckQuest = FindStuckQuestByPoi();
                                if (stuckQuest == 0)
                                {
                                    var qpoi = BotPoi.Current;
                                    if (qpoi == null || qpoi.Type == PoiType.None)
                                    {
                                        Log($"No POI set — can't determine stuck quest ({stuckSec:F0}s). No quest blacklisted.");
                                        _wasStuck = true;
                                        _stuckLogged = true;
                                        return;
                                    }
                                    stuckQuest = _scheduler.ActiveQuestIds.First();
                                }
                                _settings.BlacklistedQuests.Add(stuckQuest);
                                SaveQuestBlacklist();
                                Log($"Blacklisted quest {stuckQuest} — stuck for {stuckSec:F0}s, forcing re-scan");
                                TreeRoot.Stop();
                            }
                        }
                        else if (!_stuckLogged)
                        {
                            _stuckLogged = true;
                            Log($"Bot running but not moving for {stuckSec:F0}s");
                        }
                    }
                }
            }

            if (StyxWoW.IsInGame && StyxWoW.Me != null && TreeRoot.IsRunning)
            {
                var poi = BotPoi.Current;
                if (poi != null && poi.Type == PoiType.QuestPickUp)
                {
                    string target = $"{poi.Name}|{poi.Entry}";
                    if (target != _lastPickupTarget)
                    {
                        _lastPickupTarget = target;
                        _pickupStartTime = DateTime.Now;
                        _pickupLogged = false;
                    }
                    else if (!_pickupLogged
                          && _pickupStartTime != DateTime.MinValue
                          && (DateTime.Now - _pickupStartTime).TotalSeconds >= 60)
                    {
                        var pickup = QuestOrder.Instance?.CurrentBehavior as ForcedQuestPickUp;
                        if (pickup != null)
                        {
                            int qId = (int)pickup.QuestId;
                            _settings.BlacklistedQuests.Add(qId);
                            SaveQuestBlacklist();
                            Log($"Blacklisted quest {qId} ({pickup.QuestName}) from {poi.Name} (Entry:{poi.Entry}) — failed to pick up for 60s, triggering rescan");
                            TreeRoot.Stop();
                        }
                        else if (IsNearGiver((int)poi.Entry))
                        {
                            int[] ids = _dataLoader?.Database?.QuestGivers
                                ?.Where(g => g.GiverId == (int)poi.Entry)
                                .Select(g => g.QuestId).ToArray();
                            if (ids != null && ids.Length > 0)
                            {
                                foreach (int id in ids)
                                    _settings.BlacklistedQuests.Add(id);
                                SaveQuestBlacklist();
                                string idsStr = string.Join(",", ids);
                                Log($"Blacklisted quest(s) [{idsStr}] from {poi.Name} (Entry:{poi.Entry}) — failed to pick up for 60s, triggering rescan");
                                TreeRoot.Stop();
                            }
                            else
                            {
                                Log($"Stuck at {poi.Name} (Entry:{poi.Entry}) for 60s — can't resolve quest ID, no blacklist added");
                            }
                        }
                        else
                        {
                            Log($"Stuck at {poi.Name} (Entry:{poi.Entry}) for 60s — not near giver spawn, skipping blacklist");
                        }
                        _pickupLogged = true;
                    }
                }
                else
                {
                    _lastPickupTarget = null;
                    _pickupStartTime = DateTime.MinValue;
                    _pickupLogged = false;
                }

                var currentReady = new HashSet<int>(
                    StyxWoW.Me.QuestLog.GetAllQuests()
                        .Where(q => q.IsCompleted)
                        .Select(q => (int)q.Id));

                if (_lastReadyQuestIds != null && _lastReadyQuestIds.Count > 0)
                {
                    var turnedIn = _lastReadyQuestIds.Where(id => !currentReady.Contains(id)).ToList();
                    if (turnedIn.Count > 0)
                    {
                        Log($"Turned in {turnedIn.Count} quest(s): {string.Join(",", turnedIn)} — triggering rescan");
                        _pendingRescan = true;
                        _lastReadyQuestIds = null;
                        return;
                    }
                }

                _lastReadyQuestIds = currentReady;
            }

            if (TreeRoot.IsRunning && !StyxWoW.Me.Combat
                && (DateTime.Now - _lastNoHotspotCheck).TotalSeconds >= 2
                && (DateTime.Now - _lastMovedTime).TotalSeconds >= 10)
            {
                _lastNoHotspotCheck = DateTime.Now;
                var behavior = QuestOrder.Instance?.CurrentBehavior as ForcedQuestObjective;
                if (behavior?.Objective?.QuestArea?.HotspotsCreated == true)
                {
                    var qa = behavior.Objective.QuestArea;
                    int qId = (int)qa.Quest.Id;
                    var hs = qa.CurrentHotSpot;
                    bool noHotspot = hs == null
                        || (hs.Position.X == 0 && hs.Position.Y == 0 && hs.Position.Z == 0);

                    string questName = qa.Quest.Name;

                    if (noHotspot)
                    {
                        _noHotspotCount.TryGetValue(qId, out int count);
                        count++;
                        _noHotspotCount[qId] = count;

                        if (count == 5)
                        {
                            Log($"No suitable hotspot 5x for {questName} ({qId}) — abandoning and blacklisting");
                            StyxWoW.Me.QuestLog.AbandonQuestById((uint)qId);
                            _settings.BlacklistedQuests.Add(qId);
                            SaveQuestBlacklist();
                            TreeRoot.Stop();
                        }
                        else
                            Log($"No suitable hotspot for {questName} ({qId}) — count #{count}");
                    }
                    else
                    {
                        if (_noHotspotCount.TryGetValue(qId, out int old) && old > 0)
                            _noHotspotCount[qId] = 0;
                    }
                }
            }

            if (_settings.SellWhite || _settings.SellGreen || _settings.SellBlue)
                SellByQuality();
        }

        private void OnPlayerDied()
        {
            var behavior = QuestOrder.Instance?.CurrentBehavior as ForcedQuestObjective;
            if (behavior?.Objective?.QuestArea == null)
                return;

            int qId = (int)behavior.Objective.QuestArea.Quest.Id;
            _deathCountByQuest.TryGetValue(qId, out int count);
            count++;
            _deathCountByQuest[qId] = count;

            string questName = behavior.Objective.QuestArea.Quest.Name;

            if (count == 5)
            {
                Log($"Died {count}x at {questName} ({qId}) — session-blacklisting and refreshing");
                _settings.BlacklistedQuests.Add(qId);
                TreeRoot.Stop();
            }
            else
            {
                Log($"Died near hotspot for {questName} ({qId}) — death #{count}");
            }
        }

        private bool _lastFrameVisible;

        private void SellByQuality()
        {
            if (!MerchantFrame.Instance.IsVisible)
            {
                _lastFrameVisible = false;
                return;
            }

            if (!_lastFrameVisible)
            {
                _lastFrameVisible = true;
                Log("Arrived at vendor — merchant frame opened");
            }

            ItemQuality mask = ItemQuality.None;
            if (_settings.SellWhite) mask |= ItemQuality.Common;
            if (_settings.SellGreen) mask |= ItemQuality.Uncommon;
            if (_settings.SellBlue) mask |= ItemQuality.Rare;

            if (mask == ItemQuality.None) return;

            var protectedIds = new HashSet<uint>();

            if (_dataLoader.Database != null && _scheduler?.ActiveQuestIds != null)
            {
                foreach (int qId in _scheduler.ActiveQuestIds)
                {
                    var quest = _dataLoader.Database.Quests.FirstOrDefault(q => q.Id == qId);
                    if (quest == null) continue;
                    if (quest.StartItem > 0)
                        protectedIds.Add((uint)quest.StartItem);
                    foreach (var obj in quest.Objectives)
                    {
                        if (obj.ItemId > 0)
                            protectedIds.Add((uint)obj.ItemId);
                    }
                }
            }

            MerchantFrame.Instance.SellItemQualities(mask, null, protectedIds);
        }

        private static string FindProfilePath()
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string path = Path.Combine(baseDir, "Bots", "WholesomeAutoQuest", "WHOLESOME_AUTOQUESTER.xml");
            string dir = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                Directory.CreateDirectory(dir);
            return path;
        }

        private void LogFarAwayQuests()
        {
            var db = _dataLoader?.Database;
            if (db == null || _scheduler?.ActiveQuestIds == null) return;

            foreach (int qId in _scheduler.ActiveQuestIds)
            {
                var quest = db.Quests.FirstOrDefault(q => q.Id == qId);
                if (quest == null) continue;

                var givers = db.QuestGivers.Where(g => g.QuestId == qId).ToList();
                var enders = db.QuestEnders.Where(e => e.QuestId == qId).ToList();
                if (givers.Count == 0 || enders.Count == 0) continue;

                foreach (var giver in givers)
                {
                    string gKey = giver.GiverId.ToString();
                    var gSpawns = giver.GiverType == QuestObjectType.GameObject
                        ? (db.GameObjectSpawns.TryGetValue(gKey, out var gs) ? gs : null)
                        : (db.CreatureSpawns.TryGetValue(gKey, out var cs) ? cs : null);
                    if (gSpawns == null || gSpawns.Count == 0) continue;

                    foreach (var ender in enders)
                    {
                        string eKey = ender.EnderId.ToString();
                        var eSpawns = ender.EnderType == QuestObjectType.GameObject
                            ? (db.GameObjectSpawns.TryGetValue(eKey, out var es) ? es : null)
                            : (db.CreatureSpawns.TryGetValue(eKey, out var ces) ? ces : null);
                        if (eSpawns == null || eSpawns.Count == 0) continue;

                        var g0 = gSpawns[0];
                        var e0 = eSpawns[0];

                        double dx = g0.X - e0.X;
                        double dy = g0.Y - e0.Y;
                        double dz = g0.Z - e0.Z;
                        double dist = Math.Sqrt(dx * dx + dy * dy + dz * dz);
                        if (dist > 5000.0)
                        {
                            string extra = g0.Map != e0.Map
                                ? $" (different maps: {g0.Map} vs {e0.Map})" : "";
                            Log($"FAR: {quest.Name} ({qId}) — giver {giver.GiverName}({giver.GiverId}) to ender {ender.EnderName}({ender.EnderId}) = {dist:F0}yd{extra}");
                        }
                    }
                }
            }
        }

        internal void Log(string message)
        {
            Logging.Write(System.Drawing.Color.CornflowerBlue, $"[{Name}] {message}");
        }

        private int FindStuckQuestByNoHotspots()
        {
            if (_scheduler?.ActiveQuestIds == null || _scheduler.ActiveQuestIds.Count == 0)
                return 0;

            var currentBehavior = QuestOrder.Instance?.CurrentBehavior as ForcedQuestObjective;
            if (currentBehavior?.Objective?.QuestArea == null)
                return 0;

            var qa = currentBehavior.Objective.QuestArea;
            if (qa.HotspotsCreated)
                return 0;

            int qId = (int)qa.Quest.Id;
            if (_scheduler.ActiveQuestIds.Contains(qId))
            {
                Log($"No hotspots created for quest {qId} ({qa.Quest.Name}) — will blacklist");
                return qId;
            }

            return 0;
        }

        private int FindStuckQuestByPoi()
        {
            var poi = BotPoi.Current;
            if (poi == null) return 0;
            if (poi.Type != PoiType.Quest && poi.Type != PoiType.QuestPickUp && poi.Type != PoiType.QuestTurnIn
                && poi.Type != PoiType.Kill && poi.Type != PoiType.Loot)
                return 0;
            if (_dataLoader?.Database == null || _scheduler?.ActiveQuestIds == null)
                return 0;

            int entry = (int)poi.Entry;
            if (entry <= 0) return 0;

            foreach (int qId in _scheduler.ActiveQuestIds)
            {
                var quest = _dataLoader.Database.Quests.FirstOrDefault(q => q.Id == qId);
                if (quest == null) continue;

                if (quest.Objectives.Any(o => o.MobId == entry || o.GameObjectId == entry))
                    return qId;

                if (_dataLoader.Database.QuestGivers.Any(g => g.QuestId == qId && g.GiverId == entry))
                    return qId;

                if (_dataLoader.Database.QuestEnders.Any(e => e.QuestId == qId && e.EnderId == entry))
                    return qId;
            }

            return 0;
        }

        private bool IsNearGiver(int giverEntry)
        {
            if (_dataLoader?.Database == null || StyxWoW.Me == null)
                return false;

            string key = giverEntry.ToString();
            int playerMap = (int)StyxWoW.Me.MapId;
            var loc = StyxWoW.Me.Location;

            if (_dataLoader.Database.CreatureSpawns.TryGetValue(key, out var creatureSpawns))
            {
                foreach (var sp in creatureSpawns)
                {
                    if (sp.Map != playerMap) continue;
                    double dx = sp.X - loc.X;
                    double dy = sp.Y - loc.Y;
                    double dz = sp.Z - loc.Z;
                    if (dx * dx + dy * dy + dz * dz < 225.0)
                        return true;
                }
            }

            if (_dataLoader.Database.GameObjectSpawns.TryGetValue(key, out var goSpawns))
            {
                foreach (var sp in goSpawns)
                {
                    if (sp.Map != playerMap) continue;
                    double dx = sp.X - loc.X;
                    double dy = sp.Y - loc.Y;
                    double dz = sp.Z - loc.Z;
                    if (dx * dx + dy * dy + dz * dz < 225.0)
                        return true;
                }
            }

            return false;
        }

        private void LoadVendorBlacklist()
        {
            if (!File.Exists(_vendorBlacklistPath)) return;
            try
            {
                string text = File.ReadAllText(_vendorBlacklistPath).Trim();
                _settings.VendorBlacklistText = text;
                Log($"Loaded {_settings.BlacklistedVendors.Count} blacklisted vendors");
            }
            catch (Exception ex)
            {
                Log($"Failed to load vendor blacklist: {ex.Message}");
            }
        }

        private void SaveVendorBlacklist()
        {
            try
            {
                string dir = Path.GetDirectoryName(_vendorBlacklistPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_vendorBlacklistPath, _settings.VendorBlacklistText);
            }
            catch (Exception ex)
            {
                Log($"Failed to save vendor blacklist: {ex.Message}");
            }
        }

        private void LoadQuestBlacklist()
        {
            if (!File.Exists(_questBlacklistPath)) return;
            try
            {
                string text = File.ReadAllText(_questBlacklistPath).Trim();
                _settings.BlacklistText = text;
                Log($"Loaded {_settings.BlacklistedQuests.Count} blacklisted quests");
            }
            catch (Exception ex)
            {
                Log($"Failed to load quest blacklist: {ex.Message}");
            }
        }

        private void SaveQuestBlacklist()
        {
            try
            {
                string dir = Path.GetDirectoryName(_questBlacklistPath);
                if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
                    Directory.CreateDirectory(dir);
                File.WriteAllText(_questBlacklistPath, _settings.BlacklistText);
            }
            catch (Exception ex)
            {
                Log($"Failed to save quest blacklist: {ex.Message}");
            }
        }
    }
}
