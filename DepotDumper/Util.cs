using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using SteamKit2;
namespace DepotDumper
{
    static class Util
    {
        public static string ReadPassword()
        {
            ConsoleKeyInfo keyInfo;
            var password = new StringBuilder();
            do
            {
                keyInfo = Console.ReadKey(true);
                if (keyInfo.Key == ConsoleKey.Backspace)
                {
                    if (password.Length > 0)
                    {
                        password.Remove(password.Length - 1, 1);
                        Console.Write("\b \b");
                    }
                    continue;
                }
                var c = keyInfo.KeyChar;
                if (c >= ' ' && c <= '~')
                {
                    password.Append(c);
                    Console.Write('*');
                }
            } while (keyInfo.Key != ConsoleKey.Enter);
            return password.ToString();
        }
        public static byte[] DecodeHexString(string hex)
        {
            if (hex == null)
                return null;
            var chars = hex.Length;
            var bytes = new byte[chars / 2];
            for (var i = 0; i < chars; i += 2)
                bytes[i / 2] = Convert.ToByte(hex.Substring(i, 2), 16);
            return bytes;
        }
        public static byte[] SymmetricDecryptECB(byte[] input, byte[] key)
        {
            using var aes = Aes.Create();
            aes.BlockSize = 128;
            aes.KeySize = 256;
            aes.Mode = CipherMode.ECB;
            aes.Padding = PaddingMode.PKCS7;
            using var aesTransform = aes.CreateDecryptor(key, null);
            var output = aesTransform.TransformFinalBlock(input, 0, input.Length);
            return output;
        }
        public static async Task InvokeAsync(IEnumerable<Func<Task>> taskFactories, int maxDegreeOfParallelism)
        {
            ArgumentNullException.ThrowIfNull(taskFactories);
            ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(maxDegreeOfParallelism, 0);
            var queue = taskFactories.ToArray();
            if (queue.Length == 0)
            {
                return;
            }
            var tasksInFlight = new List<Task>(maxDegreeOfParallelism);
            var index = 0;
            do
            {
                while (tasksInFlight.Count < maxDegreeOfParallelism && index < queue.Length)
                {
                    var taskFactory = queue[index++];
                    tasksInFlight.Add(taskFactory());
                }
                var completedTask = await Task.WhenAny(tasksInFlight).ConfigureAwait(false);
                await completedTask.ConfigureAwait(false);
                tasksInFlight.Remove(completedTask);
            } while (index < queue.Length || tasksInFlight.Count != 0);
        }
        // "<appId>.<branch>.<yyyy-MM-dd_HH-mm-ss>.<app name>". Branch and app names may contain dots (branches like "5.6.2" or
        // "v1.0.6.1"), so the folder name is parsed by its date stamp rather than by splitting on every dot.
        private static readonly System.Text.RegularExpressions.Regex FolderNameRx = new System.Text.RegularExpressions.Regex(
            @"^(?<app>\d+)\.(?<branch>.+?)\.(?<date>\d{4}-\d{2}-\d{2}_\d{2}-\d{2}-\d{2})(\.|$)", System.Text.RegularExpressions.RegexOptions.Compiled);

        public static bool TryParseFolderName(string folderName, out string branch, out DateTime date)
        {
            branch = null; date = default;
            var m = FolderNameRx.Match(folderName ?? "");
            if (!m.Success) return false;
            if (!DateTime.TryParseExact(m.Groups["date"].Value, "yyyy-MM-dd_HH-mm-ss", System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.None, out date)) return false;
            branch = m.Groups["branch"].Value;
            return true;
        }

    public static DateTime? GetDateFromFolderName(string folderPath)
        {
            try
            {
                // Get just the folder name from the path
                string folderName = Path.GetFileName(folderPath);

                if (TryParseFolderName(folderName, out _, out var stampedDate)) return stampedDate;   // handles dotted branch names
                
                // Example format: "284830.public.2014-04-23_12-01-22.Clockwork Tales_ Of Glass and Ink"
                
                // Split the folder name by dots
                string[] parts = folderName.Split('.');
                
                // Check if we have enough parts (we need at least 3: appId, branch, dateTime)
                if (parts.Length < 3)
                {
                    Logger.Warning($"Folder name format doesn't match expected pattern: {folderName}");
                    return null;
                }
                
                // The date part should be the third element (index 2)
                string datePart = parts[2];
                
                // Split the date part which might look like "2014-04-23_12-01-22"
                string[] dateTimeParts = datePart.Split('_');
                
                // Check if we have date and time
                if (dateTimeParts.Length != 2)
                {
                    // Try alternate formats or just return null
                    if (DateTime.TryParse(datePart, out DateTime dateResult))
                    {
                        return dateResult;
                    }
                    Logger.Warning($"Date time format doesn't match expected pattern: {datePart}");
                    return null;
                }
                
                // Parse the date and time parts
                string datePortion = dateTimeParts[0]; // 2014-04-23
                string timePortion = dateTimeParts[1].Replace('-', ':'); // 12-01-22 -> 12:01:22
                
                // Try to parse the combined date and time
                string dateTimeString = $"{datePortion} {timePortion}";
                if (DateTime.TryParse(dateTimeString, out DateTime result))
                {
                    return result;
                }
                
                Logger.Warning($"Could not parse date and time from: {dateTimeString}");
                return null;
            }
            catch (Exception ex)
            {
                Logger.Error($"Error parsing date from folder name: {ex.Message}");
                return null;
            }
        }

        public static Dictionary<string, DateTime> GetDatesFromFolders(string baseDirectory, uint appId)
        {
            var result = new Dictionary<string, DateTime>();
            
            try
            {
                // Path to the app's directory
                string appDirectory = Path.Combine(baseDirectory, appId.ToString());
                
                // Check if the directory exists
                if (!Directory.Exists(appDirectory))
                {
                    Logger.Debug($"App directory does not exist yet: {appDirectory}");   // normal for an app not dumped before
                    return result;
                }
                
                // Get all subdirectories that match the pattern for this app
                string searchPattern = $"{appId}.*";
                var folders = Directory.GetDirectories(appDirectory, searchPattern);
                
                foreach (var folder in folders)
                {
                    // Extract the branch name from the folder name
                    // Format: "284830.public.2014-04-23_12-01-22.Clockwork Tales_ Of Glass and Ink"
                    string folderName = Path.GetFileName(folder);

                    // Dotted branch names ("5.6.2", "v1.0.6.1") can't be found by splitting on '.', so read them by the date stamp first
                    if (TryParseFolderName(folderName, out var stampedBranch, out var stampedDate))
                    {
                        string cleanStamped = stampedBranch.Replace('/', '_').Replace('\\', '_');
                        result[cleanStamped] = stampedDate;
                        Logger.Debug($"Found date {stampedDate} for branch '{cleanStamped}' in folder {folderName}");
                        continue;
                    }

                    string[] parts = folderName.Split('.');
                    
                    if (parts.Length < 3)
                    {
                        Logger.Warning($"Skipping folder with unexpected format: {folderName}");
                        continue;
                    }
                    
                    // The branch is the second part
                    string branch = parts[1];
                    
                    // Get the date from the folder name
                    DateTime? folderDate = GetDateFromFolderName(folder);
                    
                    if (folderDate.HasValue)
                    {
                        // Store the branch and date
                        string cleanBranchName = branch.Replace('/', '_').Replace('\\', '_');
                        result[cleanBranchName] = folderDate.Value;
                        Logger.Debug($"Found date {folderDate.Value} for branch '{cleanBranchName}' in folder {folderName}");
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"Error getting dates from folders: {ex.Message}");
            }
            
            return result;
        }

        public static DateTime? GetDateFromExistingFolder(string appPath, string branchName, uint appId)
        {
            try
            {
                // Look for folders matching the pattern "appId.branchName.*" 
                var folders = Directory.GetDirectories(appPath, $"{appId}.{branchName}.*");
                
                foreach (var folder in folders)
                {
                    DateTime? folderDate = GetDateFromFolderName(folder);
                    if (folderDate.HasValue)
                    {
                        Logger.Debug($"Found date {folderDate.Value} from existing folder: {Path.GetFileName(folder)}");
                        return folderDate.Value;
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.Warning($"Error getting date from existing folders: {ex.Message}");
            }
            
            return null;
        }
    }
}


