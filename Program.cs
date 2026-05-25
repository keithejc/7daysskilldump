using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Xml.Linq;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Services;
using Google.Apis.Sheets.v4;
using Google.Apis.Sheets.v4.Data;

//7days to die namespaces

namespace skilldump
{
    public class PlayerInfo
    {
        public string Name { get; set; }
        public string UserId { get; set; }
        public Dictionary<string, int> Skills { get; set; } = new Dictionary<string, int>();
    }

    internal class Program
    {
        private static async Task Main(string[] args)
        {
            string basePath = @"C:\Users\keith\AppData\Roaming\7DaysToDie\Saves\Navezgane\Moo";
            string playersXmlPath = Path.Combine(basePath, "players.xml");
            string playerSaveDir = Path.Combine(basePath, "Player");
            string outputCsvPath = "PlayerSkills.csv";

            string localizationPath = @"C:\Program Files (x86)\Steam\steamapps\common\7 Days To Die\Data\Config\Localization.txt";
            var localization = LoadLocalization(localizationPath);

            if (!File.Exists(playersXmlPath))
            {
                Console.WriteLine($"Error: Could not find {playersXmlPath}");
                return;
            }

            // 1. Parse players.xml to get Player Names and UserIDs
            var players = new List<PlayerInfo>();
            XDocument doc = XDocument.Load(playersXmlPath);

            foreach (var element in doc.Descendants("player"))
            {
                string name = element.Attribute("playername")?.Value ?? "UnknownPlayer";
                string userid = element.Attribute("platform")?.Value + "_" + element.Attribute("userid")?.Value;

                if (!string.IsNullOrEmpty(userid))
                {
                    players.Add(new PlayerInfo { Name = name, UserId = userid });
                }
            }

            // 2. Map players to their save files and extract skills
            var allSkillNames = new HashSet<string>();

            foreach (var player in players)
            {
                // 7 Days to Die save files use the .ttp extension
                string ttpFilePath = Path.Combine(playerSaveDir, $"{player.UserId}.ttp");

                if (File.Exists(ttpFilePath))
                {
                    Console.WriteLine($"Processing save for {player.Name}...");

                    // 3. Extract skills
                    player.Skills = ExtractSkillsFromSave(ttpFilePath);
                    foreach (var skill in player.Skills.Keys)
                    {
                        allSkillNames.Add(skill);
                    }
                }
                else
                {
                    Console.WriteLine($"Save file not found for {player.Name} at {ttpFilePath}");
                }
            }

            // 4. Upload data to Google Sheet
            var sortedSkills = allSkillNames.ToList();
            sortedSkills.Sort(StringComparer.OrdinalIgnoreCase);
            await UploadToGoogleSheetAsync(players, sortedSkills, localization);
        }

        private static Dictionary<string, int> ExtractSkillsFromSave(string fullPath)
        {
            var skills = new Dictionary<string, int>();

            if (!File.Exists(fullPath)) return skills;

            byte[] data = File.ReadAllBytes(fullPath);

            // The .ttp file is a complex binary format. Instantiating PlayerDataFile or EntityPlayer
            // outside of the Unity Engine causes SecurityExceptions.
            // Instead, we scan the binary stream for skill names and use ProgressionValue to parse the levels!
            using (MemoryStream ms = new MemoryStream(data))
            using (BinaryReader br = new BinaryReader(ms))
            {
                for (int i = 0; i < data.Length - 10; i++)
                {
                    // C# BinaryWriter prefixes strings with their length.
                    if (data[i] > 0 && data[i] < 50)
                    {
                        if (IsMatch(data, i + 1, "perk") || IsMatch(data, i + 1, "att") || IsMatch(data, i + 1, "crafting"))
                        {
                            int stringLength = data[i];
                            string skillName = System.Text.Encoding.ASCII.GetString(data, i + 1, stringLength);

                            ms.Position = i;
                            try
                            {
                                var pos = br.BaseStream.Position;
                                if (br.ReadString() == skillName)
                                {
                                    br.BaseStream.Seek(pos - 1, SeekOrigin.Begin);
                                    var pv = new ProgressionValue(skillName);

                                    pv.Read(br);
                                    skills[skillName] = pv.level;

                                    // Advance 'i' so we don't re-parse the middle of the data
                                    i = (int)ms.Position - 1;
                                }
                            }
                            catch
                            {
                                // Ignore stream reading errors and keep searching
                            }
                        }
                    }
                }
            }

            return skills;
        }

        private static bool IsMatch(byte[] data, int index, string pattern)
        {
            if (index + pattern.Length > data.Length) return false;
            for (int i = 0; i < pattern.Length; i++)
            {
                if (data[index + i] != pattern[i]) return false;
            }
            return true;
        }

        private static Dictionary<string, string> LoadLocalization(string filePath)
        {
            var dict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (!File.Exists(filePath))
            {
                Console.WriteLine($"Localization file not found at {filePath}. Using internal skill names.");
                return dict;
            }

            using var reader = new StreamReader(filePath);
            string headerLine = reader.ReadLine();
            if (headerLine == null) return dict;

            string[] headers = headerLine.Split(',');
            int engIndex = Array.IndexOf(headers, "english");
            if (engIndex == -1) engIndex = 5; // Fallback to standard column index if header differs

            while (!reader.EndOfStream)
            {
                string line = reader.ReadLine();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var columns = ParseCsvLine(line);
                if (columns.Count > engIndex)
                {
                    string key = columns[0];
                    string eng = columns[engIndex];
                    if (!string.IsNullOrEmpty(key))
                    {
                        dict[key] = eng;
                    }
                }
            }
            return dict;
        }

        private static List<string> ParseCsvLine(string line)
        {
            var result = new List<string>();
            bool inQuotes = false;
            var current = new System.Text.StringBuilder();
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '\"')
                    inQuotes = !inQuotes; // Toggle quotes and strip them from the final string
                else if (c == ',' && !inQuotes)
                {
                    result.Add(current.ToString());
                    current.Clear();
                }
                else
                    current.Append(c);
            }
            result.Add(current.ToString());
            return result;
        }

        private static string GetLocalizedName(string internalName, Dictionary<string, string> localization)
        {
            // The UI names for skills almost always map to the internal name + "Name" (e.g., perkMiner69erName)
            if (localization.TryGetValue(internalName + "Name", out string localized)) return localized;
            if (localization.TryGetValue(internalName, out localized)) return localized;
            return internalName; // Fallback to the internal code-name if no translation exists
        }

        private static string GetLocalizedDescription(string internalName, Dictionary<string, string> localization)
        {
            if (localization.TryGetValue(internalName + "Desc", out string localized))
            {
                return localized.Replace("[DECEA3]", "").Replace("[-]", "").Replace("\\n", " ");
            }
            return "";
        }

        private static string EscapeCsv(string field)
        {
            if (field.Contains(",") || field.Contains("\""))
                return "\"" + field.Replace("\"", "\"\"") + "\"";
            return field;
        }

        private static async Task UploadToGoogleSheetAsync(List<PlayerInfo> players, List<string> sortedSkills, Dictionary<string, string> localization)
        {
            const string applicationName = "7DaysToDie Skill Exporter";
            const string sheetName = "Skills"; // Target a sheet named "Skills"
            const string credentialsFile = "credentials.json";
            const string spreadsheetIdFile = "spreadsheet_id.txt";

            if (!File.Exists(spreadsheetIdFile))
            {
                Console.WriteLine($"\nError: Spreadsheet ID file not found at '{Path.GetFullPath(spreadsheetIdFile)}'.");
                Console.WriteLine("Please create this file and paste your Google Sheet ID into it.");
                return;
            }
            string spreadsheetId = File.ReadAllText(spreadsheetIdFile).Trim();

            if (!File.Exists(credentialsFile))
            {
                Console.WriteLine($"\nError: Google API credentials file not found at '{Path.GetFullPath(credentialsFile)}'.");
                Console.WriteLine("Please follow the setup instructions to create and place the credentials file in the same directory as the .exe.");
                return;
            }

            GoogleCredential credential;
            using (var stream = new FileStream(credentialsFile, FileMode.Open, FileAccess.Read))
            {
                credential = GoogleCredential.FromStream(stream)
                    .CreateScoped(SheetsService.Scope.Spreadsheets);
            }

            var service = new SheetsService(new BaseClientService.Initializer()
            {
                HttpClientInitializer = credential,
                ApplicationName = applicationName,
            });

            // Prepare data for the sheet
            var values = new List<IList<object>>();

            // Header row
            var headerRow = new List<object> { "SkillId", "SkillName", "Description" };
            headerRow.AddRange(players.Select(p => $"{p.Name} SkillLevel"));
            values.Add(headerRow);

            // Data rows
            foreach (var skill in sortedSkills)
            {
                var dataRow = new List<object>
                {
                    skill,
                    GetLocalizedName(skill, localization),
                    GetLocalizedDescription(skill, localization)
                };
                dataRow.AddRange(players.Select(p => p.Skills.TryGetValue(skill, out int level) ? (object)level : "0"));
                values.Add(dataRow);
            }

            try
            {
                Console.WriteLine("\nClearing existing data from Google Sheet...");
                var clearRange = $"{sheetName}!A1:Z";
                var clearRequest = service.Spreadsheets.Values.Clear(new ClearValuesRequest(), spreadsheetId, clearRange);
                await clearRequest.ExecuteAsync();

                Console.WriteLine("Uploading new data to Google Sheet...");
                var valueRange = new ValueRange { Values = values };
                var updateRequest = service.Spreadsheets.Values.Update(valueRange, spreadsheetId, $"{sheetName}!A1");
                updateRequest.ValueInputOption = SpreadsheetsResource.ValuesResource.UpdateRequest.ValueInputOptionEnum.USERENTERED;
                await updateRequest.ExecuteAsync();

                Console.WriteLine("\nSuccessfully uploaded data to Google Sheet!");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nAn error occurred while communicating with the Google Sheets API: {ex.Message}");
                Console.WriteLine("Please ensure the service account email has 'Editor' permissions on the sheet and that the sheet is named 'Skills'.");
            }
        }
    }
}