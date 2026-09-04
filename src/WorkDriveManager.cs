using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace DayZModWorkbench
{
    internal sealed class WorkDriveState
    {
        public string DriveName;
        public string RootPath;
        public string TargetPath;
        public bool IsAvailable;
        public bool IsSubst;
    }

    internal static class WorkDriveManager
    {
        private const int OperationTimeoutMilliseconds = 20000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint QueryDosDevice(string deviceName, StringBuilder targetPath, int maximumLength);

        internal static WorkDriveState GetState(string configuredProjectDrive)
        {
            string root = string.IsNullOrWhiteSpace(configuredProjectDrive)
                ? @"P:\"
                : Path.GetPathRoot(Path.GetFullPath(configuredProjectDrive));
            if (string.IsNullOrWhiteSpace(root) || root.Length < 2 || root[1] != ':')
                throw new InvalidOperationException("O work drive configurado não possui uma letra válida: " + configuredProjectDrive);

            string driveName = root.Substring(0, 2).ToUpperInvariant();
            StringBuilder target = new StringBuilder(1024);
            uint length = QueryDosDevice(driveName, target, target.Capacity);
            string targetPath = length == 0 ? string.Empty : target.ToString();
            const string substPrefix = @"\??\";
            return new WorkDriveState
            {
                DriveName = driveName,
                RootPath = driveName + Path.DirectorySeparatorChar,
                TargetPath = targetPath.StartsWith(substPrefix, StringComparison.OrdinalIgnoreCase)
                    ? targetPath.Substring(substPrefix.Length)
                    : targetPath,
                IsAvailable = length > 0 && Directory.Exists(driveName + Path.DirectorySeparatorChar),
                IsSubst = targetPath.StartsWith(substPrefix, StringComparison.OrdinalIgnoreCase)
            };
        }

        internal static string FindExecutable(params string[] configuredToolPaths)
        {
            List<string> candidates = new List<string>();
            foreach (string tool in configuredToolPaths ?? new string[0])
            {
                if (string.IsNullOrWhiteSpace(tool)) continue;
                try
                {
                    DirectoryInfo toolFolder = Directory.GetParent(Path.GetFullPath(tool));
                    DirectoryInfo binFolder = toolFolder == null ? null : toolFolder.Parent;
                    if (binFolder != null)
                        candidates.Add(Path.Combine(binFolder.FullName, "WorkDrive", "WorkDrive.exe"));
                }
                catch { }
            }
            candidates.Add(@"C:\Program Files (x86)\Steam\steamapps\common\DayZ Tools\Bin\WorkDrive\WorkDrive.exe");
            return candidates.Find(File.Exists) ?? string.Empty;
        }

        internal static async Task<ProcessResult> SetMountedAsync(string executable, bool mount, Action<string> log)
        {
            if (!File.Exists(executable)) throw new FileNotFoundException("WorkDrive.exe do DayZ Tools não encontrado.", executable);
            string argument = mount ? "/Mount" : "/Dismount";
            string successMarker = mount ? "Work drive mounted" : "Work drive unmounted";
            ProcessResult result;
            try
            {
                result = await RunUntilSuccessMarkerAsync(executable, argument, successMarker);
            }
            catch (TimeoutException)
            {
                throw new InvalidOperationException(
                    "O WorkDrive não respondeu em 20 segundos. Confirme que a Steam está aberta, com o login ativo, e tente novamente.");
            }
            if (result.ExitCode != 0 && (result.Output == null ||
                result.Output.IndexOf(successMarker, StringComparison.OrdinalIgnoreCase) < 0))
                throw new InvalidOperationException("WorkDrive terminou com código " + result.ExitCode + ". " +
                    (result.Output ?? string.Empty).Trim());
            if (log != null) log(successMarker + ".");
            return result;
        }

        private static Task<ProcessResult> RunUntilSuccessMarkerAsync(string executable, string argument,
            string successMarker)
        {
            return Task.Run(delegate
            {
                StringBuilder output = new StringBuilder();
                using (ManualResetEventSlim markerFound = new ManualResetEventSlim(false))
                using (Process process = new Process())
                {
                    process.StartInfo = new ProcessStartInfo
                    {
                        FileName = executable,
                        Arguments = argument,
                        WorkingDirectory = Path.GetDirectoryName(executable),
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    DataReceivedEventHandler capture = delegate(object sender, DataReceivedEventArgs e)
                    {
                        if (e.Data == null) return;
                        lock (output) output.AppendLine(e.Data);
                        if (e.Data.IndexOf(successMarker, StringComparison.OrdinalIgnoreCase) >= 0)
                            markerFound.Set();
                    };
                    process.OutputDataReceived += capture;
                    process.ErrorDataReceived += capture;
                    process.Start();
                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    Stopwatch elapsed = Stopwatch.StartNew();
                    while (!markerFound.IsSet && !process.HasExited &&
                        elapsed.ElapsedMilliseconds < OperationTimeoutMilliseconds)
                        markerFound.Wait(50);

                    if (process.HasExited)
                    {
                        // Flush the asynchronous output handlers before checking
                        // whether the final line contained the success marker.
                        process.WaitForExit();
                    }
                    else if (markerFound.IsSet)
                    {
                        // WorkDrive 1.2 waits for a key after completing the action.
                        // The official marker means the mount/dismount is already done.
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(2000); } catch { }
                    }
                    else
                    {
                        try { process.Kill(); } catch { }
                        try { process.WaitForExit(2000); } catch { }
                        throw new TimeoutException();
                    }

                    string text;
                    lock (output) text = output.ToString();
                    int exitCode = process.HasExited ? process.ExitCode : -1;
                    return new ProcessResult { ExitCode = exitCode, Output = text };
                }
            });
        }

        internal static bool IsManagedMapping(string executable, WorkDriveState state)
        {
            if (state == null || !state.IsAvailable || !state.IsSubst || string.IsNullOrWhiteSpace(state.TargetPath)) return false;
            string expected = ReadConfiguredWorkDirectory(executable);
            if (string.IsNullOrWhiteSpace(expected)) return false;
            try
            {
                return Path.GetFullPath(expected).TrimEnd('\\', '/').Equals(
                    Path.GetFullPath(state.TargetPath).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        private static string ReadConfiguredWorkDirectory(string executable)
        {
            try
            {
                DirectoryInfo workDriveFolder = Directory.GetParent(Path.GetFullPath(executable));
                DirectoryInfo binFolder = workDriveFolder == null ? null : workDriveFolder.Parent;
                DirectoryInfo toolsFolder = binFolder == null ? null : binFolder.Parent;
                string settings = toolsFolder == null ? string.Empty : Path.Combine(toolsFolder.FullName, "settings.ini");
                if (!File.Exists(settings)) return string.Empty;

                bool projectDriveSection = false;
                foreach (string raw in File.ReadAllLines(settings))
                {
                    string line = raw.Trim();
                    if (line.StartsWith("[") && line.EndsWith("]"))
                    {
                        projectDriveSection = line.Equals("[ProjectDrive]", StringComparison.OrdinalIgnoreCase);
                        continue;
                    }
                    if (!projectDriveSection) continue;
                    int separator = line.IndexOf('=');
                    if (separator > 0 && line.Substring(0, separator).Trim().Equals("path", StringComparison.OrdinalIgnoreCase))
                        return line.Substring(separator + 1).Trim();
                }
            }
            catch { }
            return string.Empty;
        }
    }
}
