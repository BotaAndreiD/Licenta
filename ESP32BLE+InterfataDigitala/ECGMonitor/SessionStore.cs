using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ECGMonitor
{
    public static class SessionStore
    {
        private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = false };

        public static string SessionsFolder
        {
            get
            {
                string folder = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
                    "CardioMed",
                    "Sessions"
                );
                Directory.CreateDirectory(folder);
                return folder;
            }
        }

        public static string Save(EcgSession session)
        {
            string safeName = string.IsNullOrWhiteSpace(session.PatientName)
                ? "Necunoscut"
                : session.PatientName.Replace(" ", "_");
            string fileName = $"{safeName}_{session.RecordedAt:yyyyMMdd_HHmmss}.json";
            string path = Path.Combine(SessionsFolder, fileName);

            File.WriteAllText(path, JsonSerializer.Serialize(session, JsonOptions));
            return path;
        }

        public static List<(string Path, EcgSession Session)> LoadAll()
        {
            var result = new List<(string Path, EcgSession Session)>();
            foreach (var file in Directory.GetFiles(SessionsFolder, "*.json"))
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var session = JsonSerializer.Deserialize<EcgSession>(json);
                    if (session != null)
                        result.Add((file, session));
                }
                catch
                {
                    // fisier corupt/incompatibil - ignorat
                }
            }
            return result.OrderByDescending(r => r.Session.RecordedAt).ToList();
        }

        public static void Delete(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
