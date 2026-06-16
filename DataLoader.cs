using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace WholesomeAQ
{
    public class DataLoader
    {
        private readonly string _dataFile;
        private QuestDatabase _database;
        private static readonly JsonSerializerOptions JsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new JsonStringEnumConverter() }
        };

        public QuestDatabase Database => _database;

        public DataLoader()
        {
            _dataFile = FindDataFile();
        }

        private static string FindDataFile()
        {
            string asmDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location);
            if (!string.IsNullOrEmpty(asmDir))
            {
                string path = Path.Combine(asmDir, "quest_data", "quest_data.json");
                if (File.Exists(path))
                    return path;
            }

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string[] candidates =
            {
                Path.Combine(baseDir, "Bots", "WholesomeAutoQuest", "quest_data", "quest_data.json"),
                Path.Combine(baseDir, "Plugins", "WholesomeAutoQuester", "quest_data", "quest_data.json"),
                Path.Combine(Environment.CurrentDirectory, "quest_data", "quest_data.json")
            };

            foreach (string candidate in candidates)
            {
                if (File.Exists(candidate))
                    return candidate;
            }

            return Path.Combine(Environment.CurrentDirectory, "quest_data", "quest_data.json");
        }

        public QuestDatabase Load()
        {
            if (_database != null)
                return _database;

            if (!File.Exists(_dataFile))
                return null;

            string json = File.ReadAllText(_dataFile);
            _database = JsonSerializer.Deserialize<QuestDatabase>(json, JsonOptions);
            return _database;
        }
    }
}
