using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace DayZModWorkbench
{
    internal static class OdolConverterProvisioner
    {
        internal const string DownloadUrl =
            "https://gist.githubusercontent.com/RedSTwix/7dabd68cd538bc2d74b447829a1c7ea7/raw/deodol_source_windows.py";

        private static readonly object Sync = new object();
        private static Task<string> _ensureTask;

        internal static string ManagedAddonPath
        {
            get
            {
                return Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
                    "addons", "deodol_source_windows.py");
            }
        }

        private static string PreviousDownloadedPath
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "DayZ Mod Workbench", "tools", "deodol_source_windows.py");
            }
        }

        internal static Task<string> EnsureAvailableAsync(string configuredPath, Action<string> log)
        {
            lock (Sync)
            {
                if (_ensureTask == null)
                    _ensureTask = EnsureManagedAddonAsync(configuredPath, log);
                return _ensureTask;
            }
        }

        private static async Task<string> EnsureManagedAddonAsync(string configuredPath, Action<string> log)
        {
            string destination = ManagedAddonPath;
            string directory = Path.GetDirectoryName(destination);
            Directory.CreateDirectory(directory);

            byte[] localContents = ReadValidScript(destination);
            if (localContents == null)
            {
                string migratedFrom = TryInstallExistingCopy(configuredPath, destination);
                if (migratedFrom == null)
                    migratedFrom = TryInstallExistingCopy(PreviousDownloadedPath, destination);
                if (migratedFrom == null)
                    migratedFrom = TryInstallExistingCopy(@"P:\deodol_source_windows.py", destination);
                if (migratedFrom != null)
                {
                    localContents = ReadValidScript(destination);
                    if (log != null) log("Addon ODOL migrado para a pasta do Workbench: " + destination);
                }
            }

            try
            {
                if (log != null) log("Verificando atualização do addon ODOL...");
                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
                byte[] remoteContents;
                using (WebClient client = new WebClient())
                {
                    client.Headers[HttpRequestHeader.UserAgent] = "DayZ-Mod-Workbench/1.0";
                    remoteContents = await client.DownloadDataTaskAsync(new Uri(DownloadUrl));
                }

                string remoteVersion;
                ValidateScript(remoteContents, out remoteVersion);
                string remoteHash = ComputeSha256(remoteContents);
                string localHash = localContents == null ? string.Empty : ComputeSha256(localContents);
                if (!remoteHash.Equals(localHash, StringComparison.OrdinalIgnoreCase))
                {
                    InstallAtomically(destination, remoteContents);
                    localContents = remoteContents;
                    if (log != null) log("Addon ODOL " + remoteVersion + " instalado/atualizado: " + destination);
                }
                else if (log != null)
                {
                    log("Addon ODOL já está atualizado (" + remoteVersion + "): " + destination);
                }
            }
            catch (Exception ex)
            {
                localContents = ReadValidScript(destination);
                if (localContents == null)
                    throw new InvalidOperationException("Não foi possível instalar o addon ODOL pela URL oficial e não existe uma cópia local válida.", ex);
                if (log != null) log("Não foi possível verificar a atualização do addon ODOL; usando a cópia local válida. " + ex.Message);
            }

            return destination;
        }

        private static string TryInstallExistingCopy(string source, string destination)
        {
            if (string.IsNullOrWhiteSpace(source)) return null;
            try
            {
                string fullSource = Path.GetFullPath(source);
                if (fullSource.Equals(Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase)) return null;
                byte[] contents = ReadValidScript(fullSource);
                if (contents == null) return null;
                InstallAtomically(destination, contents);
                return fullSource;
            }
            catch
            {
                return null;
            }
        }

        private static byte[] ReadValidScript(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                byte[] contents = File.ReadAllBytes(path);
                string version;
                ValidateScript(contents, out version);
                return contents;
            }
            catch
            {
                return null;
            }
        }

        private static void ValidateScript(byte[] contents, out string version)
        {
            if (contents == null || contents.Length < 1024 || contents.Length > 2 * 1024 * 1024)
                throw new InvalidOperationException("O addon ODOL possui tamanho inválido.");

            string script;
            try
            {
                script = new UTF8Encoding(false, true).GetString(contents);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidOperationException("O addon ODOL não está em UTF-8 válido.", ex);
            }

            Match versionMatch = Regex.Match(script,
                "(?m)^\\s*SCRIPT_VERSION\\s*=\\s*['\"]([^'\"]+)['\"]\\s*$");
            if (!versionMatch.Success ||
                script.IndexOf("def convert_file(", StringComparison.Ordinal) < 0 ||
                script.IndexOf("def convert_tree(", StringComparison.Ordinal) < 0 ||
                script.IndexOf("DEBINARIZE_REPORT.txt", StringComparison.Ordinal) < 0 ||
                script.IndexOf("verify_pbo_archive", StringComparison.Ordinal) < 0 ||
                script.IndexOf("PBO_EQUIVALENCE_VERIFICATION.txt", StringComparison.Ordinal) < 0 ||
                script.IndexOf("--cfgconvert", StringComparison.Ordinal) < 0 ||
                script.IndexOf("ODOL", StringComparison.OrdinalIgnoreCase) < 0 ||
                script.IndexOf("MLOD", StringComparison.OrdinalIgnoreCase) < 0)
                throw new InvalidOperationException("O arquivo baixado não parece ser o addon ODOL esperado.");

            version = versionMatch.Groups[1].Value.Trim();
        }

        private static string ComputeSha256(byte[] contents)
        {
            using (SHA256 sha = SHA256.Create())
                return BitConverter.ToString(sha.ComputeHash(contents)).Replace("-", string.Empty);
        }

        private static void InstallAtomically(string destination, byte[] contents)
        {
            string temporary = destination + ".download-" + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                File.WriteAllBytes(temporary, contents);
                if (File.Exists(destination))
                {
                    try
                    {
                        File.Replace(temporary, destination, null);
                    }
                    catch (PlatformNotSupportedException)
                    {
                        File.Copy(temporary, destination, true);
                        File.Delete(temporary);
                    }
                }
                else
                {
                    File.Move(temporary, destination);
                }
            }
            finally
            {
                try { if (File.Exists(temporary)) File.Delete(temporary); } catch { }
            }
        }
    }
}
