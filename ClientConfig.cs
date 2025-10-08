using System;
using System.Collections.Generic;
using System.IO;
using WTelegram;

namespace botStarsSaller
{
    public static class ClientsFactory
    {
        public static List<(Client Client, string Phone)> CreateClients(string sessionsDir, string baseDir)
        {
            var path = Path.Combine(baseDir, "clients.txt");
            var result = new List<(Client, string)>();
            if (!File.Exists(path)) return result;

            foreach (var raw in File.ReadLines(path))
            {
                var line = raw == null ? null : raw.Trim();
                if (string.IsNullOrWhiteSpace(line) || line.StartsWith("#")) continue;

                var parts = line.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue; // ждём 5 полей: session;apiId;apiHash;phone;active

                var sessionName = parts[0].Trim();
                var apiId = parts[1].Trim();
                var apiHash = parts[2].Trim();
                var phone = parts[3].Trim();
                var active = parts[4].Trim();

                if (active != "1") continue; // 0 — пропускаем

                var sessionPath = Path.Combine(sessionsDir, sessionName + ".session");
                Func<string, string> Config = what =>
                {
                    switch (what)
                    {
                        case "session_pathname": return sessionPath;
                        case "api_id": return apiId;
                        case "api_hash": return apiHash;
                        case "phone_number": return phone;
                        default: return null;
                    }
                };

                result.Add((new Client(Config), phone));
            }

            return result;
        }
    }
}