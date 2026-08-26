using System;
using System.Collections.Generic;
using System.IO;

namespace DayZModWorkbench
{
    internal sealed class ToolSettings
    {
        public string BenchPath;
        public string BankRevPath;
        public string FileBankPath;
        public string SignerPath;
        public string PrivateKeyPath;
        public string DayZPath;
        public string DayZDiagPath;
        public string EditorPath;
        public string WorkshopPath;
        public string EditorDependencies;
        public string ServerKeysPath;
        public string BackupPath;
        public string ExtraLaunchArgs;

        public static string SettingsFile
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "settings.ini"); }
        }

        public static ToolSettings CreateDefaults()
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string tools = @"C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools\Bin";
            string workshop = @"D:\SteamLibrary\steamapps\common\DayZ\!Workshop";

            return new ToolSettings
            {
                BenchPath = Path.Combine(desktop, "edit mod"),
                BankRevPath = Path.Combine(tools, @"PboUtils\BankRev.exe"),
                FileBankPath = Path.Combine(tools, @"PboUtils\FileBank.exe"),
                SignerPath = Path.Combine(tools, @"DsUtils\DSSignFile.exe"),
                PrivateKeyPath = string.Empty,
                DayZPath = @"D:\SteamLibrary\steamapps\common\DayZ\DayZ_x64.exe",
                DayZDiagPath = @"D:\SteamLibrary\steamapps\common\DayZ\DayZDiag_x64.exe",
                EditorPath = Path.Combine(workshop, "@DayZ-Editor"),
                WorkshopPath = workshop,
                EditorDependencies = Path.Combine(workshop, "@CF") + ";" + Path.Combine(workshop, "@Dabs Framework"),
                ServerKeysPath = @"C:\DayZServer\keys",
                BackupPath = Path.Combine(desktop, @"Mods backup\DayZ Mod Workbench"),
                ExtraLaunchArgs = "-noPause -noSplash -skipIntro -doLogs -world=empty"
            };
        }

        public static ToolSettings Load()
        {
            ToolSettings result = CreateDefaults();
            if (!File.Exists(SettingsFile))
                return result;

            Dictionary<string, string> values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (string rawLine in File.ReadAllLines(SettingsFile))
            {
                string line = rawLine.Trim();
                if (line.Length == 0 || line.StartsWith("#"))
                    continue;

                int separator = line.IndexOf('=');
                if (separator <= 0)
                    continue;

                values[line.Substring(0, separator).Trim()] = line.Substring(separator + 1).Trim();
            }

            result.BenchPath = Get(values, "BenchPath", result.BenchPath);
            result.BankRevPath = Get(values, "BankRevPath", result.BankRevPath);
            result.FileBankPath = Get(values, "FileBankPath", result.FileBankPath);
            result.SignerPath = Get(values, "SignerPath", result.SignerPath);
            result.PrivateKeyPath = Get(values, "PrivateKeyPath", result.PrivateKeyPath);
            result.DayZPath = Get(values, "DayZPath", result.DayZPath);
            result.DayZDiagPath = Get(values, "DayZDiagPath", result.DayZDiagPath);
            result.EditorPath = Get(values, "EditorPath", result.EditorPath);
            result.WorkshopPath = Get(values, "WorkshopPath", result.WorkshopPath);
            result.EditorDependencies = Get(values, "EditorDependencies", result.EditorDependencies);
            result.ServerKeysPath = Get(values, "ServerKeysPath", result.ServerKeysPath);
            result.BackupPath = Get(values, "BackupPath", result.BackupPath);
            result.ExtraLaunchArgs = Get(values, "ExtraLaunchArgs", result.ExtraLaunchArgs);
            return result;
        }

        private static string Get(Dictionary<string, string> values, string key, string fallback)
        {
            string value;
            return values.TryGetValue(key, out value) ? value : fallback;
        }

        public void Save()
        {
            string[] lines =
            {
                "# DayZ Mod Workbench - configurações locais",
                "BenchPath=" + BenchPath,
                "BankRevPath=" + BankRevPath,
                "FileBankPath=" + FileBankPath,
                "SignerPath=" + SignerPath,
                "PrivateKeyPath=" + PrivateKeyPath,
                "DayZPath=" + DayZPath,
                "DayZDiagPath=" + DayZDiagPath,
                "EditorPath=" + EditorPath,
                "WorkshopPath=" + WorkshopPath,
                "EditorDependencies=" + EditorDependencies,
                "ServerKeysPath=" + ServerKeysPath,
                "BackupPath=" + BackupPath,
                "ExtraLaunchArgs=" + ExtraLaunchArgs
            };
            File.WriteAllLines(SettingsFile, lines);
        }
    }
}
