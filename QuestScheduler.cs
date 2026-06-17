using System;
using System.Collections.Generic;
using System.Linq;
using Styx.Logic.Questing;
using Styx.WoWInternals.WoWObjects;

namespace WholesomeAQ
{
    public class QuestScheduler
    {
        private readonly DataLoader _dataLoader;
        private readonly ProfileBuilder _profileBuilder;
        private readonly WholesomeAQSettings _settings;
        private int _scanThreshold;
        private HashSet<uint> _completedQuests = new HashSet<uint>();
        private HashSet<uint> _blacklistedQuests = new HashSet<uint>();
        private DateTime _lastScan = DateTime.MinValue;
        private static readonly TimeSpan ScanCooldown = TimeSpan.FromSeconds(10);

        public int ScanThreshold => _scanThreshold;
        public string CurrentProfilePath { get; private set; }
        public int LastQuestCount { get; private set; }
        public string LastStatus { get; private set; }
        public HashSet<int> ActiveQuestIds { get; private set; }
        public List<VendorEntry> CurrentVendors { get; set; }

        public QuestScheduler(DataLoader dataLoader, ProfileBuilder profileBuilder, WholesomeAQSettings settings)
        {
            _dataLoader = dataLoader;
            _profileBuilder = profileBuilder;
            _settings = settings;
            _scanThreshold = settings.ScanStartDistance;
            ApplyBlacklist();
        }

        public void SyncBlacklist()
        {
            _blacklistedQuests.Clear();
            ApplyBlacklist();
        }

        private void ApplyBlacklist()
        {
            foreach (int id in _settings.BlacklistedQuests)
                _blacklistedQuests.Add((uint)id);
        }

        public bool NeedNewScan()
        {
            return DateTime.Now - _lastScan >= ScanCooldown;
        }

        public bool ScanAndBuildProfile(LocalPlayer me)
        {
            _completedQuests = new HashSet<uint>(
                me.QuestLog.GetCompletedQuests() ?? Enumerable.Empty<uint>()
            );
            _lastScan = DateTime.Now;

            QuestDatabase db = _dataLoader.Database;
            if (db == null)
            {
                LastStatus = "No quest data loaded";
                return false;
            }

            List<QuestEntry> available = FilterAvailableQuests(db, me);
            LastQuestCount = available.Count;

            if (available.Count == 0)
            {
                _scanThreshold += _settings.ScanStep;
                LastStatus = _scanThreshold >= _settings.ScanMaxDistance
                    ? $"No quests at max range ({_scanThreshold}yd)"
                    : $"No quests within {_scanThreshold}yd, expanding...";
                return false;
            }

            _scanThreshold = _settings.ScanStartDistance;
            ActiveQuestIds = new HashSet<int>(available.Select(q => q.Id));

            string xml = _profileBuilder.BuildProfileXml(available, db, me.ZoneText, me.Name, me.Level, CurrentVendors);
            CurrentProfilePath = _profileBuilder.WriteProfile(xml);
            string ids = string.Join(",", available.Select(q => q.Id));
            LastStatus = $"Found {available.Count} quests within {_scanThreshold}yd [{ids}]";

            return true;
        }

        public bool BuildForLogQuests(LocalPlayer me)
        {
            QuestDatabase db = _dataLoader.Database;
            if (db == null)
            {
                LastStatus = "No quest data loaded";
                return false;
            }

            _lastScan = DateTime.Now;

            List<int> logQuestIds = me.QuestLog.GetAllQuests().Select(q => (int)q.Id).ToList();
            if (logQuestIds.Count == 0)
            {
                LastStatus = "No quests in log";
                return false;
            }

            List<QuestEntry> logQuests = db.Quests
                .Where(q => logQuestIds.Contains(q.Id))
                .ToList();

            if (logQuests.Count == 0)
            {
                LastStatus = "No log quests match quest data";
                return false;
            }

            List<QuestEntry> withObjectives = logQuests
                .Where(q => HasObjectivesWithSpawnsInRange(q, db, me))
                .ToList();

            if (withObjectives.Count == 0)
            {
                LastStatus = "No log quests with objectives in range";
                return false;
            }

            _scanThreshold = _settings.ScanStartDistance * 2;
            ActiveQuestIds = new HashSet<int>(withObjectives.Select(q => q.Id));

            string xml = _profileBuilder.BuildProfileXml(withObjectives, db, me.ZoneText, me.Name, me.Level, CurrentVendors);
            CurrentProfilePath = _profileBuilder.WriteProfile(xml);
            LastStatus = $"Building profile for {withObjectives.Count} log quests with objectives in range";
            LastQuestCount = withObjectives.Count;

            return true;
        }

        private List<QuestEntry> FilterAvailableQuests(QuestDatabase db, LocalPlayer me)
        {
            List<QuestEntry> result = new List<QuestEntry>();

            foreach (QuestEntry qe in db.Quests)
            {
                if (_blacklistedQuests.Contains((uint)qe.Id))
                    continue;

                if (_completedQuests.Contains((uint)qe.Id))
                    continue;

                if (me.QuestLog.ContainsQuest((uint)qe.Id))
                    continue;

                if (me.Level < qe.MinLevel)
                    continue;

                int qMin = Math.Max(1, me.Level - 7);
                int qMax = me.Level;
                if (qe.QuestLevel > 0 && (qe.QuestLevel < qMin || qe.QuestLevel > qMax))
                    continue;

                if (!CheckRaceAllowed(qe.AllowableRaces, me))
                    continue;

                if (!CheckPrerequisites(qe))
                    continue;

                if (!IsGiverWithinThreshold(qe, db, me))
                    continue;

                if (!IsSupportedType(qe))
                    continue;

                result.Add(qe);
            }

            return result.OrderBy(q => q.QuestLevel).ThenBy(q => q.Id).Take(_settings.MaxQuestsPerProfile).ToList();
        }

        private bool IsGiverWithinThreshold(QuestEntry qe, QuestDatabase db, LocalPlayer me)
        {
            int playerMap = (int)me.MapId;
            List<QuestGiverEntry> givers = db.QuestGivers
                .Where(g => g.QuestId == qe.Id)
                .ToList();

            foreach (QuestGiverEntry giver in givers)
            {
                string giverKey = giver.GiverId.ToString();
                if (db.CreatureSpawns.TryGetValue(giverKey, out List<SpawnPoint> spawns))
                {
                    foreach (SpawnPoint sp in spawns)
                    {
                        if (sp.Map != playerMap) continue;
                        double dx = sp.X - me.Location.X;
                        double dy = sp.Y - me.Location.Y;
                        if (Math.Sqrt(dx * dx + dy * dy) <= _scanThreshold)
                            return true;
                    }
                }
                else if (db.GameObjectSpawns.TryGetValue(giverKey, out List<SpawnPoint> goSpawns))
                {
                    foreach (SpawnPoint sp in goSpawns)
                    {
                        if (sp.Map != playerMap) continue;
                        double dx = sp.X - me.Location.X;
                        double dy = sp.Y - me.Location.Y;
                        if (Math.Sqrt(dx * dx + dy * dy) <= _scanThreshold)
                            return true;
                    }
                }
            }

            return false;
        }

        private bool HasObjectivesWithSpawnsInRange(QuestEntry qe, QuestDatabase db, LocalPlayer me)
        {
            int playerMap = (int)me.MapId;
            foreach (QuestObjective obj in qe.Objectives)
            {
                if (obj.Type == ObjectiveType.KillMob && obj.MobId > 0)
                {
                    string mobKey = obj.MobId.ToString();
                    if (db.CreatureSpawns.TryGetValue(mobKey, out List<SpawnPoint> spawns))
                    {
                        foreach (SpawnPoint sp in spawns)
                        {
                            if (sp.Map != playerMap) continue;
                            double dx = sp.X - me.Location.X;
                            double dy = sp.Y - me.Location.Y;
                            if (Math.Sqrt(dx * dx + dy * dy) <= _scanThreshold)
                                return true;
                        }
                    }
                }

                if (obj.Type == ObjectiveType.CollectFromGameObject && obj.GameObjectId > 0)
                {
                    string goKey = obj.GameObjectId.ToString();
                    if (db.GameObjectSpawns.TryGetValue(goKey, out List<SpawnPoint> spawns))
                    {
                        foreach (SpawnPoint sp in spawns)
                        {
                            if (sp.Map != playerMap) continue;
                            double dx = sp.X - me.Location.X;
                            double dy = sp.Y - me.Location.Y;
                            if (Math.Sqrt(dx * dx + dy * dy) <= _scanThreshold)
                                return true;
                        }
                    }
                }
            }

            return false;
        }

        private bool CheckRaceAllowed(int allowableRaces, LocalPlayer me)
        {
            if (allowableRaces == 0 || allowableRaces == -1)
                return true;

            int playerRaceBit = GetRaceBit((int)me.Race);
            return (allowableRaces & playerRaceBit) != 0;
        }

        private int GetRaceBit(int raceId)
        {
            return 1 << (raceId - 1);
        }

        private bool CheckPrerequisites(QuestEntry qe)
        {
            if (qe.PrevQuestID > 0)
            {
                if (!_completedQuests.Contains((uint)qe.PrevQuestID))
                    return false;
            }

            foreach (int prevId in qe.PreviousQuestsIds)
            {
                if (prevId > 0 && !_completedQuests.Contains((uint)prevId))
                    return false;
            }

            return true;
        }

        private bool IsSupportedType(QuestEntry qe)
        {
            if (qe.Objectives.Count == 0)
                return false;

            bool hasStartItem = qe.StartItem > 0;

            foreach (QuestObjective obj in qe.Objectives)
            {
                if (obj.Type == ObjectiveType.KillMob && obj.MobId <= 0)
                    return false;
                if (obj.Type == ObjectiveType.CollectItem && obj.ItemId <= 0)
                    return false;
                if (obj.Type == ObjectiveType.CollectFromGameObject && obj.GameObjectId <= 0)
                    return false;

                if (hasStartItem && obj.Type == ObjectiveType.CollectItem && obj.MobId == 0)
                    return false;
            }

            return true;
        }

        public void BlacklistQuest(uint questId)
        {
            _blacklistedQuests.Add(questId);
        }

        public bool ScanAndRefresh(LocalPlayer me)
        {
            _completedQuests = new HashSet<uint>(
                me.QuestLog.GetCompletedQuests() ?? Enumerable.Empty<uint>()
            );
            _lastScan = DateTime.Now;

            QuestDatabase db = _dataLoader.Database;
            if (db == null)
            {
                LastStatus = "No quest data loaded";
                return false;
            }

            List<QuestEntry> logQuests = GetLogQuestsWithObjectives(me, db);
            int staleMinLevel = Math.Max(1, me.Level - 3);
            var logPlayersQuests = new HashSet<int>(
                me.QuestLog.GetAllQuests()
                    .Where(q => q.IsCompleted)
                    .Select(q => (int)q.Id));

            List<QuestEntry> staleTurnins = logQuests
                .Where(q => q.QuestLevel > 0 && q.QuestLevel < staleMinLevel && logPlayersQuests.Contains(q.Id))
                .ToList();
            List<QuestEntry> activeLogQuests = logQuests
                .Except(staleTurnins)
                .ToList();

            int remaining = _settings.MaxQuestsPerProfile - activeLogQuests.Count;
            List<QuestEntry> newQuests = new List<QuestEntry>();
            if (remaining > 0)
                newQuests = FilterAvailableQuests(db, me).Take(remaining).ToList();

            List<QuestEntry> allQuests = staleTurnins.Concat(activeLogQuests).Concat(newQuests)
                .Take(_settings.MaxQuestsPerProfile).ToList();

            if (allQuests.Count == 0)
            {
                _scanThreshold += _settings.ScanStep;
                if (_scanThreshold >= _settings.ScanMaxDistance)
                {
                    _scanThreshold = _settings.ScanStartDistance;
                    LastStatus = "No quests available";
                    CurrentProfilePath = _profileBuilder.BuildEmptyProfile(me.ZoneText, me.Level, CurrentVendors);
                    ActiveQuestIds = new HashSet<int>();
                    return true;
                }
                LastStatus = $"No quests within {_scanThreshold}yd";
                return false;
            }

            _scanThreshold = _settings.ScanStartDistance;
            ActiveQuestIds = new HashSet<int>(allQuests.Select(q => q.Id));

            var preIds = new HashSet<int>(staleTurnins.Select(q => q.Id));
            string xml = _profileBuilder.BuildProfileXml(allQuests, db, me.ZoneText, me.Name, me.Level, CurrentVendors, preIds);
            CurrentProfilePath = _profileBuilder.WriteProfile(xml);
            string ids = string.Join(",", allQuests.Select(q => q.Id));
            LastStatus = $"{allQuests.Count} quests [{ids}]";
            LastQuestCount = allQuests.Count;

            return true;
        }

        private List<QuestEntry> GetLogQuestsWithObjectives(LocalPlayer me, QuestDatabase db)
        {
            List<int> logIds = me.QuestLog.GetAllQuests().Select(q => (int)q.Id).ToList();
            if (logIds.Count == 0)
                return new List<QuestEntry>();

            List<QuestEntry> matches = db.Quests
                .Where(q => logIds.Contains(q.Id))
                .ToList();

            List<QuestEntry> withObjectives = matches
                .Where(q => q.Objectives.Count > 0 && IsSupportedType(q))
                .ToList();

            return withObjectives;
        }

        public void Reset()
        {
            _scanThreshold = _settings.ScanStartDistance;
            _completedQuests.Clear();
            ActiveQuestIds = null;
            CurrentProfilePath = null;
            _lastScan = DateTime.MinValue;
        }
    }
}
