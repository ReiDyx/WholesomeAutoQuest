using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Xml.Linq;

namespace WholesomeAQ
{
    public class ProfileBuilder
    {
        private static readonly XNamespace Ns = "";
        private readonly string _profilePath;

        public ProfileBuilder() : this(null) { }

        public ProfileBuilder(string profilePath)
        {
            _profilePath = profilePath;
        }

        public string BuildProfileXml(
            List<QuestEntry> quests,
            QuestDatabase db,
            string zoneName,
            string playerName,
            int playerLevel,
            List<VendorEntry> vendors = null,
            HashSet<int> preTurninIds = null)
        {
            XDocument doc = new XDocument(
                new XElement("HBProfile",
                    new XElement("Name", $"WholesomeAQ - {zoneName}"),
                    new XElement("MinLevel", 1),
                    new XElement("MaxLevel", 80),
                    new XElement("MinDurability", "0.2"),
                    new XElement("MinFreeBagSlots", "2"),
                    new XElement("AvoidMobs"),
                    new XElement("Blackspots"),
                    new XElement("Mailboxes"),
                    BuildVendorsElement(vendors)
                )
            );

            XElement root = doc.Root;

            foreach (QuestEntry qe in quests)
            {
                XElement questElement = new XElement("Quest",
                    new XAttribute("Id", qe.Id),
                    new XAttribute("Name", qe.Name)
                );

                foreach (QuestObjective obj in qe.Objectives)
                {
                    if (obj.Type == ObjectiveType.KillMob && obj.MobId > 0)
                    {
                        XElement objElement = new XElement("Objective",
                            new XAttribute("Type", "KillMob"),
                            new XAttribute("MobId", obj.MobId),
                            new XAttribute("KillCount", obj.KillCount)
                        );

                        XElement hotspots = new XElement("Hotspots");
                        string mobKey = obj.MobId.ToString();
                        if (db.CreatureSpawns.TryGetValue(mobKey, out List<SpawnPoint> spawns))
                        {
                            foreach (SpawnPoint sp in spawns.Take(10))
                            {
                                hotspots.Add(new XElement("Hotspot",
                                    new XAttribute("X", sp.X),
                                    new XAttribute("Y", sp.Y),
                                    new XAttribute("Z", sp.Z)
                                ));
                            }
                        }
                        objElement.Add(hotspots);
                        questElement.Add(objElement);
                    }
                    else if (obj.Type == ObjectiveType.CollectItem && obj.ItemId > 0)
                    {
                        XElement objElement = new XElement("Objective",
                            new XAttribute("Type", "CollectItem"),
                            new XAttribute("ItemId", obj.ItemId),
                            new XAttribute("CollectCount", obj.CollectCount)
                        );

                        XElement hotspots = new XElement("Hotspots");
                        if (obj.MobId > 0)
                        {
                            string mobKey = obj.MobId.ToString();
                            if (db.CreatureSpawns.TryGetValue(mobKey, out List<SpawnPoint> spawns))
                            {
                                foreach (SpawnPoint sp in spawns.Take(10))
                                {
                                    hotspots.Add(new XElement("Hotspot",
                                        new XAttribute("X", sp.X),
                                        new XAttribute("Y", sp.Y),
                                        new XAttribute("Z", sp.Z)
                                    ));
                                }
                            }
                        }
                        objElement.Add(hotspots);
                        questElement.Add(objElement);
                    }
                    else if (obj.Type == ObjectiveType.CollectFromGameObject && obj.GameObjectId > 0)
                    {
                        XElement objElement = new XElement("Objective",
                            new XAttribute("Type", "CollectItem"),
                            new XAttribute("ItemId", obj.ItemId),
                            new XAttribute("CollectCount", obj.CollectCount)
                        );

                        XElement collectFrom = new XElement("CollectFrom",
                            new XElement("GameObject",
                                new XAttribute("Name", obj.GameObjectName ?? $"GameObject_{obj.GameObjectId}"),
                                new XAttribute("Id", obj.GameObjectId)
                            )
                        );
                        objElement.Add(collectFrom);

                        XElement hotspots = new XElement("Hotspots");
                        string goKey = obj.GameObjectId.ToString();
                        if (db.GameObjectSpawns.TryGetValue(goKey, out List<SpawnPoint> spawns))
                        {
                            foreach (SpawnPoint sp in spawns.Take(10))
                            {
                                hotspots.Add(new XElement("Hotspot",
                                    new XAttribute("X", sp.X),
                                    new XAttribute("Y", sp.Y),
                                    new XAttribute("Z", sp.Z)
                                ));
                            }
                        }
                        objElement.Add(hotspots);
                        questElement.Add(objElement);
                    }
                }

                root.Add(questElement);
            }

            XElement questOrder = new XElement("QuestOrder");

            if (preTurninIds != null && preTurninIds.Count > 0)
            {
                foreach (QuestEntry qe in quests.Where(q => preTurninIds.Contains(q.Id)))
                {
                    QuestEnderEntry ender = db.QuestEnders
                        .FirstOrDefault(qe2 => qe2.QuestId == qe.Id);
                    if (ender != null)
                    {
                        questOrder.Add(new XElement("TurnIn",
                            new XAttribute("QuestName", qe.Name),
                            new XAttribute("QuestId", qe.Id),
                            new XAttribute("TurnInName", ender.EnderName),
                            new XAttribute("TurnInId", ender.EnderId)
                        ));
                    }
                }
            }

            foreach (QuestEntry qe in quests)
            {
                if (preTurninIds != null && preTurninIds.Contains(qe.Id))
                    continue;

                QuestGiverEntry giver = db.QuestGivers
                    .FirstOrDefault(qg => qg.QuestId == qe.Id);

                if (giver != null)
                {
                    questOrder.Add(new XElement("PickUp",
                        new XAttribute("QuestName", qe.Name),
                        new XAttribute("QuestId", qe.Id),
                        new XAttribute("GiverName", giver.GiverName),
                        new XAttribute("GiverId", giver.GiverId)
                    ));
                }
            }

            foreach (QuestEntry qe in quests)
            {
                if (preTurninIds != null && preTurninIds.Contains(qe.Id))
                    continue;

                foreach (QuestObjective obj in qe.Objectives)
                {
                    if (obj.Type == ObjectiveType.KillMob && obj.MobId > 0)
                    {
                        questOrder.Add(new XElement("Objective",
                            new XAttribute("QuestName", qe.Name),
                            new XAttribute("QuestId", qe.Id),
                            new XAttribute("Type", "KillMob"),
                            new XAttribute("MobId", obj.MobId),
                            new XAttribute("KillCount", obj.KillCount)
                        ));
                    }
                    else if (obj.Type == ObjectiveType.CollectItem && obj.ItemId > 0)
                    {
                        XElement xe = new XElement("Objective",
                            new XAttribute("QuestName", qe.Name),
                            new XAttribute("QuestId", qe.Id),
                            new XAttribute("Type", "CollectItem"),
                            new XAttribute("ItemId", obj.ItemId),
                            new XAttribute("CollectCount", obj.CollectCount)
                        );
                        if (obj.MobId > 0)
                            xe.Add(new XAttribute("MobId", obj.MobId));
                        questOrder.Add(xe);
                    }
                    else if (obj.Type == ObjectiveType.CollectFromGameObject && obj.GameObjectId > 0)
                    {
                        questOrder.Add(new XElement("Objective",
                            new XAttribute("QuestName", qe.Name),
                            new XAttribute("QuestId", qe.Id),
                            new XAttribute("Type", "CollectItem"),
                            new XAttribute("ItemId", obj.ItemId),
                            new XAttribute("CollectCount", obj.CollectCount)
                        ));
                    }
                }
            }

            foreach (QuestEntry qe in quests)
            {
                if (preTurninIds != null && preTurninIds.Contains(qe.Id))
                    continue;

                QuestEnderEntry ender = db.QuestEnders
                    .FirstOrDefault(qe2 => qe2.QuestId == qe.Id);

                if (ender != null)
                {
                    questOrder.Add(new XElement("TurnIn",
                        new XAttribute("QuestName", qe.Name),
                        new XAttribute("QuestId", qe.Id),
                        new XAttribute("TurnInName", ender.EnderName),
                        new XAttribute("TurnInId", ender.EnderId)
                    ));
                }
            }

            root.Add(questOrder);

            return doc.Declaration + Environment.NewLine + doc.ToString();
        }

        public string WriteProfile(string xml)
        {
            if (_profilePath == null)
                return null;
            File.WriteAllText(_profilePath, xml);
            return _profilePath;
        }

        public string BuildEmptyProfile(string zoneName, int playerLevel,
            List<VendorEntry> vendors = null)
        {
            XDocument doc = new XDocument(
                new XElement("HBProfile",
                    new XElement("Name", $"WholesomeAQ - {zoneName} (empty)"),
                    new XElement("MinLevel", 1),
                    new XElement("MaxLevel", 80),
                    new XElement("MinDurability", "0.2"),
                    new XElement("MinFreeBagSlots", "2"),
                    new XElement("AvoidMobs"),
                    new XElement("Blackspots"),
                    new XElement("Mailboxes"),
                    BuildVendorsElement(vendors),
                    new XElement("QuestOrder")
                )
            );

            string xml = doc.Declaration + Environment.NewLine + doc.ToString();
            return WriteProfile(xml);
        }

        private static XElement BuildVendorsElement(List<VendorEntry> vendors)
        {
            if (vendors == null || vendors.Count == 0)
                return new XElement("Vendors");

            XElement ve = new XElement("Vendors");
            foreach (VendorEntry v in vendors)
            {
                XElement vendor = new XElement("Vendor",
                    new XAttribute("Name", v.Name ?? ""),
                    new XAttribute("Entry", v.Entry),
                    new XAttribute("Type", v.Type ?? ""),
                    new XAttribute("X", v.X),
                    new XAttribute("Y", v.Y),
                    new XAttribute("Z", v.Z)
                );

                if (!string.IsNullOrEmpty(v.TrainClass))
                    vendor.Add(new XAttribute("TrainClass", v.TrainClass));

                ve.Add(vendor);
            }
            return ve;
        }
    }
}
