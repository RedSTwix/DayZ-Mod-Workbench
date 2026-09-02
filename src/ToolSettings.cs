using System;
using System.Collections.Generic;
using System.IO;

namespace DayZModWorkbench
{
    internal sealed class ToolSettings
    {
        public string BenchPath;
        public string BankRevPath;
        public string CfgConvertPath;
        public string PythonPath;
        public string OdolConverterPath;
        public string AddonBuilderPath;
        public string ProjectDrivePath;
        public string FileBankPath;
        public string SignerPath;
        public string PrivateKeyPath;
        public string DayZPath;
        public string DayZDiagPath;
        public string EditorPath;
        public string WorkshopPath;
        public string EditorDependencies;
        public string ServerKeysPath;
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
                CfgConvertPath = Path.Combine(tools, @"CfgConvert\CfgConvert.exe"),
                PythonPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), @"Programs\Python\Python312\python.exe"),
                OdolConverterPath = OdolConverterProvisioner.ManagedAddonPath,
                AddonBuilderPath = Path.Combine(tools, @"AddonBuilder\AddonBuilder.exe"),
                ProjectDrivePath = @"P:\",
                FileBankPath = Path.Combine(tools, @"PboUtils\FileBank.exe"),
                SignerPath = Path.Combine(tools, @"DsUtils\DSSignFile.exe"),
                PrivateKeyPath = string.Empty,
                DayZPath = @"D:\SteamLibrary\steamapps\common\DayZ\DayZ_x64.exe",
                DayZDiagPath = @"D:\SteamLibrary\steamapps\common\DayZ\DayZDiag_x64.exe",
                EditorPath = Path.Combine(workshop, "@DayZ-Editor"),
                WorkshopPath = workshop,
                EditorDependencies = Path.Combine(workshop, "@CF") + ";" + Path.Combine(workshop, "@Dabs Framework"),
                ServerKeysPath = @"C:\DayZServer\keys",
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
            result.CfgConvertPath = Get(values, "CfgConvertPath", result.CfgConvertPath);
            result.PythonPath = Get(values, "PythonPath", result.PythonPath);
            // O conversor é um addon gerenciado e sempre fica junto ao Workbench.
            // Valores antigos em P:\ ou LocalAppData são migrados pelo provisionador.
            result.OdolConverterPath = OdolConverterProvisioner.ManagedAddonPath;
            result.AddonBuilderPath = Get(values, "AddonBuilderPath", result.AddonBuilderPath);
            result.ProjectDrivePath = Get(values, "ProjectDrivePath", result.ProjectDrivePath);
            result.FileBankPath = Get(values, "FileBankPath", result.FileBankPath);
            result.SignerPath = Get(values, "SignerPath", result.SignerPath);
            result.PrivateKeyPath = Get(values, "PrivateKeyPath", result.PrivateKeyPath);
            result.DayZPath = Get(values, "DayZPath", result.DayZPath);
            result.DayZDiagPath = Get(values, "DayZDiagPath", result.DayZDiagPath);
            result.EditorPath = Get(values, "EditorPath", result.EditorPath);
            result.WorkshopPath = Get(values, "WorkshopPath", result.WorkshopPath);
            result.EditorDependencies = Get(values, "EditorDependencies", result.EditorDependencies);
            result.ServerKeysPath = Get(values, "ServerKeysPath", result.ServerKeysPath);
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
                "CfgConvertPath=" + CfgConvertPath,
                "PythonPath=" + PythonPath,
                "OdolConverterPath=" + OdolConverterPath,
                "AddonBuilderPath=" + AddonBuilderPath,
                "ProjectDrivePath=" + ProjectDrivePath,
                "FileBankPath=" + FileBankPath,
                "SignerPath=" + SignerPath,
                "PrivateKeyPath=" + PrivateKeyPath,
                "DayZPath=" + DayZPath,
                "DayZDiagPath=" + DayZDiagPath,
                "EditorPath=" + EditorPath,
                "WorkshopPath=" + WorkshopPath,
                "EditorDependencies=" + EditorDependencies,
                "ServerKeysPath=" + ServerKeysPath,
                "ExtraLaunchArgs=" + ExtraLaunchArgs
            };
            File.WriteAllLines(SettingsFile, lines);
        }
    }
}
