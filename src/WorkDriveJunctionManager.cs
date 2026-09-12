using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Web.Script.Serialization;

namespace DayZModWorkbench
{
    internal sealed class WorkDriveSourceEntry
    {
        public string ProjectName;
        public string SourceName;
        public string SourcePath;
    }

    internal sealed class WorkDriveJunctionRecord
    {
        public string DriveName { get; set; }
        public string Name { get; set; }
        public string ProjectName { get; set; }
        public string TargetPath { get; set; }
        public string CreatedUtc { get; set; }
    }

    internal sealed class WorkDriveJunctionManifest
    {
        public int Version { get; set; }
        public List<WorkDriveJunctionRecord> Junctions { get; set; }
    }

    internal sealed class WorkDriveJunctionViewItem
    {
        public string ProjectName;
        public string SourceName;
        public string SourcePath;
        public string JunctionPath;
        public string Status;
        public bool IsManaged;
        public bool CanToggle;
        public bool IsOrphan;
    }

    internal static class WorkDriveJunctionManager
    {
        private const uint InvalidFileAttributes = 0xFFFFFFFF;
        private const uint FileAttributeReparsePoint = 0x400;
        private const uint IoReparseTagMountPoint = 0xA0000003;
        private const uint FsctlGetReparsePoint = 0x000900A8;
        private const uint OpenExisting = 3;
        private const uint FileFlagOpenReparsePoint = 0x00200000;
        private const uint FileFlagBackupSemantics = 0x02000000;
        private const int ReparseBufferSize = 16384;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern uint GetFileAttributes(string lpFileName);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFile(string lpFileName, uint dwDesiredAccess, uint dwShareMode,
            IntPtr lpSecurityAttributes, uint dwCreationDisposition, uint dwFlagsAndAttributes,
            IntPtr hTemplateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr hDevice, uint dwIoControlCode, IntPtr lpInBuffer,
            int nInBufferSize, IntPtr lpOutBuffer, int nOutBufferSize, out int lpBytesReturned,
            IntPtr lpOverlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr hObject);

        private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

        internal static string ManifestFile
        {
            get
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "configs", "workdrive-junctions.json");
            }
        }

        internal static List<WorkDriveSourceEntry> DiscoverSources(string benchPath)
        {
            List<WorkDriveSourceEntry> result = new List<WorkDriveSourceEntry>();
            if (string.IsNullOrWhiteSpace(benchPath) || !Directory.Exists(benchPath)) return result;

            foreach (string projectPath in Directory.GetDirectories(benchPath)
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
            {
                string sourceRoot = Path.Combine(projectPath, "source");
                if (!Directory.Exists(sourceRoot)) continue;

                foreach (string sourcePath in Directory.GetDirectories(sourceRoot)
                    .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase))
                {
                    result.Add(new WorkDriveSourceEntry
                    {
                        ProjectName = Path.GetFileName(projectPath),
                        SourceName = Path.GetFileName(sourcePath),
                        SourcePath = Path.GetFullPath(sourcePath)
                    });
                }
            }
            return result;
        }

        internal static List<WorkDriveJunctionRecord> LoadRecords()
        {
            try
            {
                if (!File.Exists(ManifestFile)) return new List<WorkDriveJunctionRecord>();
                string json = File.ReadAllText(ManifestFile, Encoding.UTF8);
                if (string.IsNullOrWhiteSpace(json)) return new List<WorkDriveJunctionRecord>();
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                WorkDriveJunctionManifest manifest = serializer.Deserialize<WorkDriveJunctionManifest>(json);
                if (manifest == null || manifest.Junctions == null) return new List<WorkDriveJunctionRecord>();
                return manifest.Junctions.Where(x => x != null && !string.IsNullOrWhiteSpace(x.Name) &&
                    !string.IsNullOrWhiteSpace(x.TargetPath)).ToList();
            }
            catch
            {
                return new List<WorkDriveJunctionRecord>();
            }
        }

        private static void SaveRecords(IEnumerable<WorkDriveJunctionRecord> records)
        {
            string folder = Path.GetDirectoryName(ManifestFile);
            Directory.CreateDirectory(folder);
            WorkDriveJunctionManifest manifest = new WorkDriveJunctionManifest
            {
                Version = 1,
                Junctions = records.OrderBy(x => x.DriveName, StringComparer.OrdinalIgnoreCase)
                    .ThenBy(x => x.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
            };
            JavaScriptSerializer serializer = new JavaScriptSerializer();
            string json = serializer.Serialize(manifest);
            string temp = ManifestFile + ".tmp";
            File.WriteAllText(temp, json, new UTF8Encoding(false));
            if (File.Exists(ManifestFile)) File.Delete(ManifestFile);
            File.Move(temp, ManifestFile);
        }

        internal static List<WorkDriveJunctionViewItem> BuildView(string benchPath, WorkDriveState state)
        {
            List<WorkDriveSourceEntry> sources = DiscoverSources(benchPath);
            List<WorkDriveJunctionRecord> records = LoadRecords();
            string driveName = state == null ? string.Empty : state.DriveName;
            string root = state == null ? string.Empty : state.RootPath;
            bool driveAvailable = state != null && state.IsAvailable && Directory.Exists(root);

            List<WorkDriveJunctionViewItem> view = new List<WorkDriveJunctionViewItem>();
            Dictionary<string, int> duplicateCounts = sources.GroupBy(x => x.SourceName,
                StringComparer.OrdinalIgnoreCase).ToDictionary(x => x.Key, x => x.Count(),
                StringComparer.OrdinalIgnoreCase);

            foreach (WorkDriveSourceEntry source in sources)
            {
                WorkDriveJunctionRecord record = records.FirstOrDefault(x =>
                    SameDrive(x.DriveName, driveName) &&
                    x.Name.Equals(source.SourceName, StringComparison.OrdinalIgnoreCase));
                bool ownsThisSource = record != null && PathsEqual(record.TargetPath, source.SourcePath);
                string link = string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, source.SourceName);
                bool entryExists = driveAvailable && PathEntryExists(link);
                bool correctJunction = ownsThisSource && entryExists && IsJunctionTo(link, record.TargetPath);
                string status;
                bool canToggle;

                if (ownsThisSource)
                {
                    if (!driveAvailable)
                    {
                        status = "Gerenciado — monte " + driveName + " para verificar";
                        canToggle = false;
                    }
                    else if (correctJunction)
                    {
                        status = "Ativo";
                        canToggle = true;
                    }
                    else if (!entryExists)
                    {
                        status = "Vínculo registrado, junction ausente";
                        canToggle = true;
                    }
                    else
                    {
                        status = "Conflito no " + driveName + " — não será alterado";
                        canToggle = false;
                    }
                }
                else if (record != null)
                {
                    status = "Conflito: " + source.SourceName + " já está ativo em " + record.ProjectName;
                    canToggle = false;
                }
                else if (driveAvailable && entryExists)
                {
                    status = "Ocupado no " + driveName + " (não gerenciado)";
                    canToggle = false;
                }
                else
                {
                    status = duplicateCounts[source.SourceName] > 1
                        ? "Disponível — nome duplicado; apenas um pode ser ativado"
                        : "Disponível";
                    canToggle = driveAvailable;
                }

                view.Add(new WorkDriveJunctionViewItem
                {
                    ProjectName = source.ProjectName,
                    SourceName = source.SourceName,
                    SourcePath = source.SourcePath,
                    JunctionPath = link,
                    Status = status,
                    IsManaged = ownsThisSource,
                    CanToggle = canToggle,
                    IsOrphan = false
                });
            }

            foreach (WorkDriveJunctionRecord record in records.Where(x => SameDrive(x.DriveName, driveName)))
            {
                if (sources.Any(x => x.SourceName.Equals(record.Name, StringComparison.OrdinalIgnoreCase) &&
                    PathsEqual(x.SourcePath, record.TargetPath))) continue;
                view.Add(new WorkDriveJunctionViewItem
                {
                    ProjectName = record.ProjectName,
                    SourceName = record.Name,
                    SourcePath = record.TargetPath,
                    JunctionPath = string.IsNullOrWhiteSpace(root) ? string.Empty : Path.Combine(root, record.Name),
                    Status = driveAvailable
                        ? "Origem removida — aguardando sincronização"
                        : "Origem removida — monte " + driveName + " para limpar",
                    IsManaged = true,
                    CanToggle = driveAvailable,
                    IsOrphan = true
                });
            }

            return view.OrderBy(x => x.SourceName, StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(x => x.ProjectName, StringComparer.CurrentCultureIgnoreCase).ToList();
        }

        internal static int Synchronize(string benchPath, WorkDriveState state, Action<string> log)
        {
            if (state == null || !state.IsAvailable || !Directory.Exists(state.RootPath)) return 0;

            List<WorkDriveJunctionRecord> records = LoadRecords();
            bool changed = false;
            int actions = 0;

            foreach (WorkDriveJunctionRecord record in records.ToList())
            {
                if (!SameDrive(record.DriveName, state.DriveName)) continue;

                string link = Path.Combine(state.RootPath, record.Name);
                bool sourceExists = Directory.Exists(record.TargetPath);
                bool linkExists = PathEntryExists(link);

                if (!sourceExists)
                {
                    if (linkExists)
                    {
                        if (IsJunctionTo(link, record.TargetPath))
                        {
                            RemoveJunction(link);
                            if (log != null) log("Junction removida porque a source não existe mais: " + link);
                            actions++;
                        }
                        else if (log != null)
                        {
                            log("Junction gerenciada não corresponde mais ao destino registrado e foi preservada por segurança: " + link);
                        }
                    }
                    records.Remove(record);
                    changed = true;
                    continue;
                }

                if (!linkExists)
                {
                    records.Remove(record);
                    changed = true;
                    if (log != null) log("Vínculo removido do gerenciamento porque a junction não existe mais: " + link);
                    actions++;
                    continue;
                }

                if (!IsJunctionTo(link, record.TargetPath))
                {
                    records.Remove(record);
                    changed = true;
                    if (log != null) log("O caminho " + link + " foi alterado fora do Workbench; ele não será mais gerenciado.");
                    actions++;
                }
            }

            if (changed) SaveRecords(records);
            return actions;
        }

        internal static void Activate(WorkDriveSourceEntry source, WorkDriveState state)
        {
            if (source == null) throw new ArgumentNullException("source");
            if (state == null || !state.IsAvailable || !Directory.Exists(state.RootPath))
                throw new InvalidOperationException("Monte o WorkDrive antes de ativar uma source.");
            if (!Directory.Exists(source.SourcePath))
                throw new DirectoryNotFoundException("A source não existe mais: " + source.SourcePath);

            List<WorkDriveJunctionRecord> records = LoadRecords();
            WorkDriveJunctionRecord existingRecord = records.FirstOrDefault(x =>
                SameDrive(x.DriveName, state.DriveName) &&
                x.Name.Equals(source.SourceName, StringComparison.OrdinalIgnoreCase));
            if (existingRecord != null)
            {
                if (PathsEqual(existingRecord.TargetPath, source.SourcePath))
                    throw new InvalidOperationException(source.SourceName + " já está ativado no WorkDrive.");
                throw new InvalidOperationException(source.SourceName + " já está vinculado ao projeto " +
                    existingRecord.ProjectName + ". Desative esse vínculo antes de ativar outro source com o mesmo nome.");
            }

            string link = Path.Combine(state.RootPath, source.SourceName);
            if (PathEntryExists(link))
                throw new InvalidOperationException(link + " já existe no WorkDrive e não é gerenciado por esta ativação. Nada foi substituído.");

            CreateJunction(link, source.SourcePath);
            if (!PathEntryExists(link) || !IsJunctionTo(link, source.SourcePath))
            {
                if (PathEntryExists(link) && IsJunction(link))
                {
                    try { RemoveJunction(link); } catch { }
                }
                throw new InvalidOperationException("A junction foi criada, mas a validação do destino falhou.");
            }

            records.Add(new WorkDriveJunctionRecord
            {
                DriveName = state.DriveName,
                Name = source.SourceName,
                ProjectName = source.ProjectName,
                TargetPath = Path.GetFullPath(source.SourcePath),
                CreatedUtc = DateTime.UtcNow.ToString("o")
            });
            SaveRecords(records);
        }

        internal static void Deactivate(string sourceName, WorkDriveState state)
        {
            if (state == null || !state.IsAvailable || !Directory.Exists(state.RootPath))
                throw new InvalidOperationException("Monte o WorkDrive antes de desativar uma source.");
            if (string.IsNullOrWhiteSpace(sourceName)) throw new ArgumentException("Source inválida.");

            List<WorkDriveJunctionRecord> records = LoadRecords();
            WorkDriveJunctionRecord record = records.FirstOrDefault(x => SameDrive(x.DriveName, state.DriveName) &&
                x.Name.Equals(sourceName, StringComparison.OrdinalIgnoreCase));
            if (record == null) throw new InvalidOperationException(sourceName + " não é gerenciado pelo Workbench.");

            string link = Path.Combine(state.RootPath, record.Name);
            if (PathEntryExists(link))
            {
                if (!IsJunctionTo(link, record.TargetPath))
                    throw new InvalidOperationException("O caminho " + link +
                        " não aponta mais para a source registrada. Ele foi preservado por segurança.");
                RemoveJunction(link);
            }

            records.Remove(record);
            SaveRecords(records);
        }

        private static void CreateJunction(string link, string target)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(link));
            ProcessStartInfo start = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/d /c mklink /J " + Quote(link) + " " + Quote(target),
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            using (Process process = Process.Start(start))
            {
                string output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException("mklink /J falhou (" + process.ExitCode + "). " + output.Trim());
            }
        }

        private static void RemoveJunction(string link)
        {
            if (!PathEntryExists(link)) return;
            if (!IsJunction(link)) throw new InvalidOperationException("O caminho não é uma junction: " + link);
            Directory.Delete(link, false);
        }

        internal static bool PathEntryExists(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            return GetFileAttributes(path) != InvalidFileAttributes;
        }

        private static bool IsJunction(string path)
        {
            uint attributes = GetFileAttributes(path);
            if (attributes == InvalidFileAttributes || (attributes & FileAttributeReparsePoint) == 0) return false;
            string target;
            uint tag;
            return TryReadReparseTarget(path, out target, out tag) && tag == IoReparseTagMountPoint;
        }

        private static bool IsJunctionTo(string path, string expectedTarget)
        {
            string actual;
            uint tag;
            if (!TryReadReparseTarget(path, out actual, out tag) || tag != IoReparseTagMountPoint) return false;
            return PathsEqual(actual, expectedTarget);
        }

        private static bool TryReadReparseTarget(string path, out string target, out uint tag)
        {
            target = string.Empty;
            tag = 0;
            IntPtr handle = CreateFile(path, 0, 0x00000001 | 0x00000002 | 0x00000004, IntPtr.Zero,
                OpenExisting, FileFlagOpenReparsePoint | FileFlagBackupSemantics, IntPtr.Zero);
            if (handle == InvalidHandleValue) return false;

            IntPtr buffer = Marshal.AllocHGlobal(ReparseBufferSize);
            try
            {
                int bytesReturned;
                if (!DeviceIoControl(handle, FsctlGetReparsePoint, IntPtr.Zero, 0, buffer,
                    ReparseBufferSize, out bytesReturned, IntPtr.Zero)) return false;
                if (bytesReturned < 16) return false;

                tag = unchecked((uint)Marshal.ReadInt32(buffer, 0));
                if (tag != IoReparseTagMountPoint) return false;

                ushort substituteOffset = unchecked((ushort)Marshal.ReadInt16(buffer, 8));
                ushort substituteLength = unchecked((ushort)Marshal.ReadInt16(buffer, 10));
                int pathBufferOffset = 16;
                IntPtr substitutePointer = IntPtr.Add(buffer, pathBufferOffset + substituteOffset);
                string raw = Marshal.PtrToStringUni(substitutePointer, substituteLength / 2) ?? string.Empty;
                target = NormalizeReparseTarget(raw);
                return !string.IsNullOrWhiteSpace(target);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
                CloseHandle(handle);
            }
        }

        private static string NormalizeReparseTarget(string raw)
        {
            const string ntPrefix = @"\??\";
            if (raw.StartsWith(ntPrefix + "UNC\\", StringComparison.OrdinalIgnoreCase))
                return @"\\" + raw.Substring((ntPrefix + "UNC\\").Length);
            if (raw.StartsWith(ntPrefix, StringComparison.OrdinalIgnoreCase))
                return raw.Substring(ntPrefix.Length);
            return raw;
        }

        private static bool SameDrive(string a, string b)
        {
            return string.Equals((a ?? string.Empty).TrimEnd('\\').ToUpperInvariant(),
                (b ?? string.Empty).TrimEnd('\\').ToUpperInvariant(), StringComparison.OrdinalIgnoreCase);
        }

        private static bool PathsEqual(string a, string b)
        {
            try
            {
                return Path.GetFullPath(a ?? string.Empty).TrimEnd('\\', '/').Equals(
                    Path.GetFullPath(b ?? string.Empty).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return string.Equals((a ?? string.Empty).TrimEnd('\\', '/'),
                    (b ?? string.Empty).TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);
            }
        }

        private static string Quote(string value)
        {
            return "\"" + (value ?? string.Empty).Replace("\"", "\"\"") + "\"";
        }
    }
}
