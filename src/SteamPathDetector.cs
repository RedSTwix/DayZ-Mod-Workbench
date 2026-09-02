using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace DayZModWorkbench
{
    internal sealed class DetectedPaths
    {
        public string BankRevPath;
        public string CfgConvertPath;
        public string PythonPath;
        public string OdolConverterPath;
        public string AddonBuilderPath;
        public string FileBankPath;
        public string SignerPath;
        public string DayZPath;
        public string DayZDiagPath;
        public string EditorPath;
        public string WorkshopPath;
        public string EditorDependencies;
    }

    internal static class SteamPathDetector
    {
        private const string DayZAppId = "221100";
        private const string DayZToolsAppId = "830640";
        private const string DayZEditorWorkshopId = "2250764298";
        private const string CfWorkshopId = "1559212036";
        private const string DabsWorkshopId = "2545327648";

        public static DetectedPaths Detect()
        {
            List<string> libraries = DiscoverSteamLibraries();
            string dayZRoot = FindInstalledApp(libraries, DayZAppId, "DayZ_x64.exe");
            string toolsRoot = FindInstalledApp(libraries, DayZToolsAppId, Path.Combine("Bin", "PboUtils", "BankRev.exe"));

            DetectedPaths result = new DetectedPaths();
            if (!string.IsNullOrWhiteSpace(dayZRoot))
            {
                result.DayZPath = ExistingFile(Path.Combine(dayZRoot, "DayZ_x64.exe"));
                result.DayZDiagPath = ExistingFile(Path.Combine(dayZRoot, "DayZDiag_x64.exe"));

                string aliases = Path.Combine(dayZRoot, "!Workshop");
                string libraryRoot = FindContainingLibrary(libraries, dayZRoot);
                string workshopContent = string.IsNullOrWhiteSpace(libraryRoot)
                    ? string.Empty
                    : Path.Combine(libraryRoot, "steamapps", "workshop", "content", DayZAppId);
                result.WorkshopPath = Directory.Exists(aliases)
                    ? aliases
                    : ExistingDirectory(workshopContent);

                result.EditorPath = FindWorkshopMod(aliases, workshopContent, "@DayZ-Editor", DayZEditorWorkshopId);
                List<string> dependencies = new List<string>();
                AddIfFound(dependencies, FindWorkshopMod(aliases, workshopContent, "@CF", CfWorkshopId));
                AddIfFound(dependencies, FindWorkshopMod(aliases, workshopContent, "@Dabs Framework", DabsWorkshopId));
                if (dependencies.Count > 0) result.EditorDependencies = string.Join(";", dependencies.ToArray());
            }

            if (!string.IsNullOrWhiteSpace(toolsRoot))
            {
                result.BankRevPath = ExistingFile(Path.Combine(toolsRoot, "Bin", "PboUtils", "BankRev.exe"));
                result.CfgConvertPath = ExistingFile(Path.Combine(toolsRoot, "Bin", "CfgConvert", "CfgConvert.exe"));
                result.AddonBuilderPath = ExistingFile(Path.Combine(toolsRoot, "Bin", "AddonBuilder", "AddonBuilder.exe"));
                result.FileBankPath = ExistingFile(Path.Combine(toolsRoot, "Bin", "PboUtils", "FileBank.exe"));
                result.SignerPath = ExistingFile(Path.Combine(toolsRoot, "Bin", "DsUtils", "DSSignFile.exe"));
            }
            result.PythonPath = FindPython();
            result.OdolConverterPath = OdolConverterProvisioner.ManagedAddonPath;
            return result;
        }

        private static string FindPython()
        {
            string programs = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "Python");
            if (Directory.Exists(programs))
            {
                string found = Directory.GetDirectories(programs, "Python*")
                    .OrderByDescending(path => path, StringComparer.OrdinalIgnoreCase)
                    .Select(path => ExistingFile(Path.Combine(path, "python.exe")))
                    .FirstOrDefault(path => !string.IsNullOrWhiteSpace(path));
                if (!string.IsNullOrWhiteSpace(found)) return found;
            }
            return ExistingFile(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "py.exe"));
        }

        private static List<string> DiscoverSteamLibraries()
        {
            List<string> roots = new List<string>();
            AddDirectory(roots, ReadRegistryPath(Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath"));
            AddDirectory(roots, ReadRegistryPath(Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath"));
            AddDirectory(roots, ReadRegistryPath(Registry.LocalMachine, @"SOFTWARE\Valve\Steam", "InstallPath"));
            AddDirectory(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"));
            AddDirectory(roots, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam"));

            foreach (string steamRoot in roots.ToArray())
            {
                string libraryFile = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
                if (!File.Exists(libraryFile)) continue;
                try
                {
                    string contents = File.ReadAllText(libraryFile);
                    foreach (Match match in Regex.Matches(contents, "\\\"path\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase))
                        AddDirectory(roots, match.Groups[1].Value.Replace(@"\\", @"\"));
                }
                catch
                {
                    // A instalação principal ainda pode ser usada se o VDF estiver inacessível.
                }
            }
            return roots;
        }

        private static string FindInstalledApp(IEnumerable<string> libraries, string appId, string requiredRelativePath)
        {
            foreach (string root in libraries)
            {
                string steamApps = Path.Combine(root, "steamapps");
                string manifest = Path.Combine(steamApps, "appmanifest_" + appId + ".acf");
                if (!File.Exists(manifest)) continue;
                try
                {
                    Match match = Regex.Match(File.ReadAllText(manifest), "\\\"installdir\\\"\\s+\\\"([^\\\"]+)\\\"", RegexOptions.IgnoreCase);
                    if (!match.Success) continue;
                    string installRoot = Path.Combine(steamApps, "common", match.Groups[1].Value.Replace(@"\\", @"\"));
                    if (File.Exists(Path.Combine(installRoot, requiredRelativePath))) return Path.GetFullPath(installRoot);
                }
                catch
                {
                    // Ignora manifests incompletos e continua nas demais bibliotecas.
                }
            }
            return string.Empty;
        }

        private static string FindContainingLibrary(IEnumerable<string> libraries, string installedApp)
        {
            foreach (string root in libraries)
            {
                string common = Path.GetFullPath(Path.Combine(root, "steamapps", "common") + Path.DirectorySeparatorChar);
                string app = Path.GetFullPath(installedApp + Path.DirectorySeparatorChar);
                if (app.StartsWith(common, StringComparison.OrdinalIgnoreCase)) return root;
            }
            return string.Empty;
        }

        private static string FindWorkshopMod(string aliases, string content, string aliasName, string workshopId)
        {
            string alias = Path.Combine(aliases ?? string.Empty, aliasName);
            if (Directory.Exists(alias)) return Path.GetFullPath(alias);
            string numeric = Path.Combine(content ?? string.Empty, workshopId);
            return ExistingDirectory(numeric);
        }

        private static string ReadRegistryPath(RegistryKey root, string subKey, string valueName)
        {
            try
            {
                using (RegistryKey key = root.OpenSubKey(subKey))
                {
                    object value = key == null ? null : key.GetValue(valueName);
                    return value == null ? string.Empty : Convert.ToString(value);
                }
            }
            catch
            {
                return string.Empty;
            }
        }

        private static void AddDirectory(List<string> paths, string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return;
            try
            {
                string fullPath = Path.GetFullPath(path.Replace('/', Path.DirectorySeparatorChar));
                if (Directory.Exists(fullPath) && !paths.Contains(fullPath, StringComparer.OrdinalIgnoreCase)) paths.Add(fullPath);
            }
            catch
            {
                // Caminho inválido vindo do Registro/VDF.
            }
        }

        private static void AddIfFound(List<string> paths, string path)
        {
            if (!string.IsNullOrWhiteSpace(path)) paths.Add(path);
        }

        private static string ExistingFile(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? Path.GetFullPath(path) : string.Empty;
        }

        private static string ExistingDirectory(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && Directory.Exists(path) ? Path.GetFullPath(path) : string.Empty;
        }
    }
}
