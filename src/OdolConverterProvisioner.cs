using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Web.Script.Serialization;

namespace DayZModWorkbench
{
    internal static class OdolConverterProvisioner
    {
        // Mantido para compatibilidade com o fallback legado monolítico.
        internal const int RequiredAddonMajorVersion = 7;

        private const int SupportedManifestSchema = 1;
        private const int SupportedAddonApi = 1;

        private const string ManifestUrl =
            "https://raw.githubusercontent.com/RedSTwix/DayZ-Mod-Workbench-Updates/main/manifest.json";

        private const string ReleaseDownloadBaseUrl =
            "https://github.com/RedSTwix/DayZ-Mod-Workbench-Updates/releases/download/";

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

        private static string ManagedAddonDirectory
        {
            get
            {
                return Path.GetDirectoryName(ManagedAddonPath);
            }
        }

        private static string ManagedEngineDirectory
        {
            get
            {
                return Path.Combine(ManagedAddonDirectory, "deodol_engine");
            }
        }

        private static string PreviousDownloadedPath
        {
            get
            {
                return Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
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
            Directory.CreateDirectory(ManagedAddonDirectory);

            string localValidationError;
            bool modularLocalValid = TryValidateManagedInstallation(
                ManagedAddonDirectory, out localValidationError);

            byte[] legacyLocalContents = null;
            string legacyLocalVersion = string.Empty;

            if (!modularLocalValid)
            {
                legacyLocalContents = ReadValidLegacyScript(destination);

                if (legacyLocalContents == null)
                {
                    string migratedFrom = TryInstallExistingLegacyCopy(configuredPath, destination);
                    if (migratedFrom == null)
                        migratedFrom = TryInstallExistingLegacyCopy(PreviousDownloadedPath, destination);
                    if (migratedFrom == null)
                        migratedFrom = TryInstallExistingLegacyCopy(@"P:\deodol_source_windows.py", destination);

                    if (migratedFrom != null)
                    {
                        legacyLocalContents = ReadValidLegacyScript(destination);
                        if (log != null)
                            log("Addon ODOL legado migrado para a pasta do Workbench: " + destination);
                    }
                }

                if (legacyLocalContents != null)
                    ValidateLegacyScript(legacyLocalContents, out legacyLocalVersion);
            }

            bool anyLocalValid = modularLocalValid || legacyLocalContents != null;

            try
            {
                if (log != null)
                    log("Verificando atualização do addon ODOL...");

                ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;

                UpdateManifest manifest = await DownloadManifestAsync();
                ValidateManifest(manifest);

                EnsureWorkbenchCompatibility(manifest);

                string remoteVersionText = manifest.engine.version.Trim();
                Version remoteVersion = ParseVersion(remoteVersionText, "versão remota do addon");

                if (modularLocalValid)
                {
                    string installedVersionText = TryReadInstalledVersion();
                    Version installedVersion;

                    if (!string.IsNullOrWhiteSpace(installedVersionText) &&
                        Version.TryParse(installedVersionText, out installedVersion))
                    {
                        int versionComparison = installedVersion.CompareTo(remoteVersion);
                        if (versionComparison >= 0)
                        {
                            if (log != null)
                            {
                                if (versionComparison == 0)
                                    log("Addon ODOL já está atualizado (" + installedVersionText + ").");
                                else
                                    log("Addon ODOL local (" + installedVersionText +
                                        ") é mais novo que o canal stable (" + remoteVersionText +
                                        "); nenhuma alteração foi feita.");
                            }

                            return destination;
                        }

                        if (log != null)
                            log("Nova versão do addon ODOL disponível: " +
                                installedVersionText + " -> " + remoteVersionText + ".");
                    }
                    else
                    {
                        if (log != null)
                            log("Versão local do addon modular não pôde ser determinada; validando novamente pelo pacote remoto.");
                    }
                }
                else if (legacyLocalContents != null)
                {
                    int legacyMajor = GetVersionNumber(legacyLocalVersion);
                    if (log != null)
                        log("Addon ODOL legado " + legacyLocalVersion +
                            " detectado; migrando para o pacote modular " + remoteVersionText + ".");

                    if (legacyMajor > remoteVersion.Major)
                    {
                        if (log != null)
                            log("Addon ODOL legado local é mais novo que o canal stable; nenhuma alteração foi feita.");
                        return destination;
                    }
                }
                else
                {
                    if (log != null)
                        log("Addon ODOL modular não está instalado; iniciando instalação " +
                            remoteVersionText + ".");
                }

                await DownloadVerifyAndInstallPackageAsync(manifest, log);

                string postInstallError;
                if (!TryValidateManagedInstallation(ManagedAddonDirectory, out postInstallError))
                    throw new InvalidOperationException(
                        "O addon foi instalado, mas falhou na validação final. " + postInstallError);

                if (log != null)
                    log("Addon ODOL modular " + remoteVersionText +
                        " instalado/atualizado com sucesso: " + destination);

                return destination;
            }
            catch (Exception ex)
            {
                string validationError;
                bool localStillValid = TryValidateManagedInstallation(
                    ManagedAddonDirectory, out validationError);

                if (!localStillValid)
                {
                    byte[] fallbackLegacy = ReadValidLegacyScript(destination);
                    localStillValid = fallbackLegacy != null;
                }

                if (!localStillValid)
                {
                    throw new InvalidOperationException(
                        "Não foi possível obter/validar o addon ODOL e não existe uma cópia local válida.",
                        ex);
                }

                if (log != null)
                    log("Atualização online do addon ODOL ignorada; mantendo a cópia local válida. " +
                        ex.Message);

                return destination;
            }
        }

        private static async Task<UpdateManifest> DownloadManifestAsync()
        {
            string cacheBustUrl = ManifestUrl +
                "?workbench=" + Uri.EscapeDataString(GetWorkbenchVersionText()) +
                "&t=" + DateTime.UtcNow.Ticks.ToString();

            string json;

            using (WebClient client = CreateWebClient())
            {
                json = await client.DownloadStringTaskAsync(new Uri(cacheBustUrl));
            }

            if (json == null)
                throw new InvalidOperationException("O manifesto remoto do addon está vazio.");

            json = json.TrimStart('\uFEFF');

            try
            {
                JavaScriptSerializer serializer = new JavaScriptSerializer();
                UpdateManifest manifest = serializer.Deserialize<UpdateManifest>(json);

                if (manifest == null)
                    throw new InvalidOperationException("O manifesto remoto não pôde ser interpretado.");

                return manifest;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException(
                    "O manifest.json remoto do addon é inválido.", ex);
            }
        }

        private static WebClient CreateWebClient()
        {
            WebClient client = new WebClient();
            client.Headers[HttpRequestHeader.UserAgent] =
                "DayZ-Mod-Workbench/" + GetWorkbenchVersionText();
            client.Headers[HttpRequestHeader.CacheControl] = "no-cache";
            client.Headers[HttpRequestHeader.Pragma] = "no-cache";
            return client;
        }

        private static void ValidateManifest(UpdateManifest manifest)
        {
            if (manifest.schema != SupportedManifestSchema)
                throw new InvalidOperationException(
                    "Schema de atualização não suportado: " + manifest.schema +
                    ". Este Workbench suporta schema " + SupportedManifestSchema + ".");

            if (manifest.engine == null)
                throw new InvalidOperationException("O manifesto não contém a seção engine.");

            if (manifest.release == null)
                throw new InvalidOperationException("O manifesto não contém a seção release.");

            ParseVersion(manifest.engine.version, "engine.version");

            if (manifest.engine.api != SupportedAddonApi)
                throw new InvalidOperationException(
                    "API do addon não suportada: " + manifest.engine.api +
                    ". Este Workbench suporta API " + SupportedAddonApi + ".");

            if (string.IsNullOrWhiteSpace(manifest.release.tag))
                throw new InvalidOperationException("O manifesto não contém release.tag.");

            if (string.IsNullOrWhiteSpace(manifest.release.asset))
                throw new InvalidOperationException("O manifesto não contém release.asset.");

            if (string.IsNullOrWhiteSpace(manifest.release.sha256) ||
                !Regex.IsMatch(manifest.release.sha256, @"\A[0-9a-fA-F]{64}\z"))
                throw new InvalidOperationException("O SHA-256 do asset no manifesto é inválido.");

            if (!string.Equals(manifest.release.archive, "7z",
                StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(
                    "Formato de pacote não suportado: " + manifest.release.archive + ".");

            if (!manifest.release.encrypted || !manifest.release.header_encrypted)
                throw new InvalidOperationException(
                    "O pacote remoto não está marcado como 7z criptografado com headers protegidos.");
        }

        private static void EnsureWorkbenchCompatibility(UpdateManifest manifest)
        {
            if (string.IsNullOrWhiteSpace(manifest.engine.minimum_workbench))
                return;

            Version minimum = ParseVersion(
                manifest.engine.minimum_workbench, "engine.minimum_workbench");

            Version current = GetWorkbenchVersion();

            if (current.CompareTo(minimum) < 0)
            {
                throw new InvalidOperationException(
                    "O addon " + manifest.engine.version +
                    " exige DayZ Mod Workbench " + manifest.engine.minimum_workbench +
                    " ou superior. Versão atual: " + GetWorkbenchVersionText() + ".");
            }
        }

        private static async Task DownloadVerifyAndInstallPackageAsync(
            UpdateManifest manifest, Action<string> log)
        {
            string updateCacheRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "DayZ Mod Workbench", "updates");

            Directory.CreateDirectory(updateCacheRoot);

            string tempRoot = Path.Combine(
                updateCacheRoot,
                "addon-" + Guid.NewGuid().ToString("N"));

            string archivePath = Path.Combine(tempRoot, manifest.release.asset);
            string extractPath = Path.Combine(tempRoot, "extract");

            Directory.CreateDirectory(tempRoot);
            Directory.CreateDirectory(extractPath);

            try
            {
                string downloadUrl = BuildReleaseAssetUrl(
                    manifest.release.tag, manifest.release.asset);

                if (log != null)
                    log("Baixando " + manifest.release.asset + "...");

                using (WebClient client = CreateWebClient())
                {
                    await client.DownloadFileTaskAsync(
                        new Uri(downloadUrl), archivePath);
                }

                string actualHash = ComputeSha256File(archivePath);

                if (!actualHash.Equals(
                    manifest.release.sha256, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException(
                        "Falha de integridade no pacote do addon. SHA-256 esperado: " +
                        manifest.release.sha256 + "; recebido: " + actualHash + ".");
                }

                if (log != null)
                    log("SHA-256 do pacote confirmado.");

                string password = ReadArchivePassword();
                string sevenZipPath = FindSevenZipExecutable();

                if (log != null)
                    log("Extraindo pacote criptografado do addon...");

                await Task.Run(() =>
                    ExtractSevenZip(sevenZipPath, archivePath, extractPath, password));

                string payloadRoot = Path.Combine(extractPath, "addons");
                ValidateStagedPayload(payloadRoot);

                InstallManagedPayload(payloadRoot);

                if (log != null)
                    log("Pacote modular validado e aplicado.");
            }
            finally
            {
                TryDeleteDirectory(tempRoot);
                TryDeleteDirectoryIfEmpty(updateCacheRoot);
            }
        }

        private static string BuildReleaseAssetUrl(string tag, string asset)
        {
            return ReleaseDownloadBaseUrl +
                Uri.EscapeDataString(tag.Trim()) + "/" +
                Uri.EscapeDataString(asset.Trim());
        }

        private static string ReadArchivePassword()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            string[] candidates =
            {
                Path.Combine(baseDirectory, "senha.txt"),
                Path.GetFullPath(Path.Combine(baseDirectory, "..", "senha.txt")),
                Path.Combine(Environment.CurrentDirectory, "senha.txt")
            };

            foreach (string candidate in candidates)
            {
                try
                {
                    if (!File.Exists(candidate))
                        continue;

                    string password = File.ReadAllText(candidate, Encoding.UTF8);
                    password = password.TrimEnd('\r', '\n');

                    if (password.Length == 0)
                        throw new InvalidOperationException(
                            "O arquivo senha.txt está vazio: " + candidate);

                    return password;
                }
                catch (UnauthorizedAccessException ex)
                {
                    throw new InvalidOperationException(
                        "Não foi possível ler senha.txt: " + candidate, ex);
                }
            }

            throw new FileNotFoundException(
                "senha.txt não foi encontrado. Coloque-o na pasta do DayZ Mod Workbench " +
                "ou ao lado do executável.");
        }
        private static string FindSevenZipExecutable()
        {
            string baseDirectory = AppDomain.CurrentDomain.BaseDirectory;

            string programW6432 =
                Environment.GetEnvironmentVariable("ProgramW6432");

            string programFiles64SevenZip =
                string.IsNullOrWhiteSpace(programW6432)
                    ? string.Empty
                    : Path.Combine(programW6432, "7-Zip", "7z.exe");

            string[] candidates =
            {
                programFiles64SevenZip,
                Path.Combine(baseDirectory, "7z.exe"),
                Path.Combine(baseDirectory, "tools", "7zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                    "7-Zip", "7z.exe"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
                    "7-Zip", "7z.exe")
            };

            foreach (string candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) && File.Exists(candidate))
                    return candidate;
            }

            string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
            string[] pathEntries = path.Split(new[] { ';' },
                StringSplitOptions.RemoveEmptyEntries);

            foreach (string entry in pathEntries)
            {
                try
                {
                    string candidate = Path.Combine(entry.Trim().Trim('"'), "7z.exe");
                    if (File.Exists(candidate))
                        return candidate;
                }
                catch
                {
                    // Ignora uma entrada PATH inválida e continua procurando.
                }
            }

            throw new FileNotFoundException(
                "7z.exe não foi encontrado. Para este primeiro updater, instale o 7-Zip " +
                "ou coloque 7z.exe em tools\\7zip dentro do Workbench.");
        }

        private static void ExtractSevenZip(
            string sevenZipPath, string archivePath, string extractPath, string password)
        {
            if (string.IsNullOrEmpty(password))
                throw new InvalidOperationException("A senha do pacote está vazia.");

            ProcessStartInfo psi = new ProcessStartInfo
            {
                FileName = sevenZipPath,
                Arguments =
                    "x -y -aoa -sccUTF-8 " +
                    QuoteArgument("-p" + password) + " " +
                    "-o" + QuoteArgument(extractPath) + " " +
                    QuoteArgument(archivePath),

                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8
            };

            using (Process process = new Process())
            {
                process.StartInfo = psi;

                if (!process.Start())
                    throw new InvalidOperationException(
                        "Não foi possível iniciar o 7-Zip.");

                string stdout = process.StandardOutput.ReadToEnd();
                string stderr = process.StandardError.ReadToEnd();

                process.WaitForExit();

                if (process.ExitCode != 0)
                {
                    string details =
                        string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;

                    if (details != null && details.Length > 1500)
                        details = details.Substring(details.Length - 1500);

                    throw new InvalidOperationException(
                        "O 7-Zip não conseguiu extrair o pacote do addon. ExitCode=" +
                        process.ExitCode + ". " +
                        (details ?? string.Empty).Trim());
                }
            }
        }

        private static string QuoteArgument(string value)
        {
            if (value == null)
                value = string.Empty;

            StringBuilder result = new StringBuilder();
            result.Append('"');

            int backslashes = 0;

            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }

                if (c == '"')
                {
                    result.Append('\\', backslashes * 2 + 1);
                    result.Append('"');
                    backslashes = 0;
                    continue;
                }

                if (backslashes > 0)
                {
                    result.Append('\\', backslashes);
                    backslashes = 0;
                }

                result.Append(c);
            }

            if (backslashes > 0)
                result.Append('\\', backslashes * 2);

            result.Append('"');
            return result.ToString();
        }
        private static void ValidateStagedPayload(string payloadRoot)
        {
            if (string.IsNullOrWhiteSpace(payloadRoot) || !Directory.Exists(payloadRoot))
                throw new InvalidOperationException(
                    "O pacote não contém a pasta addons esperada.");

            string bootstrap = Path.Combine(payloadRoot, "deodol_source_windows.py");
            string engine = Path.Combine(payloadRoot, "deodol_engine");

            if (!File.Exists(bootstrap))
                throw new InvalidOperationException(
                    "O pacote não contém addons\\deodol_source_windows.py.");

            if (!Directory.Exists(engine))
                throw new InvalidOperationException(
                    "O pacote não contém addons\\deodol_engine.");

            string[] requiredFiles =
            {
                Path.Combine(engine, "__init__.py"),
                Path.Combine(engine, "version.py"),
                Path.Combine(engine, "runner.py")
            };

            foreach (string required in requiredFiles)
            {
                if (!File.Exists(required))
                    throw new InvalidOperationException(
                        "Arquivo obrigatório ausente no pacote: " +
                        MakeRelativeForMessage(payloadRoot, required));
            }

            FileInfo bootstrapInfo = new FileInfo(bootstrap);
            if (bootstrapInfo.Length < 256 || bootstrapInfo.Length > 2 * 1024 * 1024)
                throw new InvalidOperationException(
                    "deodol_source_windows.py possui tamanho inválido.");

            string script = ReadUtf8Strict(bootstrap);

            if (script.IndexOf("SCRIPT_VERSION", StringComparison.Ordinal) < 0 ||
                script.IndexOf("deodol_engine", StringComparison.Ordinal) < 0 ||
                script.IndexOf("--cfgconvert", StringComparison.Ordinal) < 0)
            {
                throw new InvalidOperationException(
                    "O bootstrap do addon modular não contém os marcadores esperados.");
            }

            string[] pythonFiles = Directory.GetFiles(
                engine, "*.py", SearchOption.AllDirectories);

            if (pythonFiles.Length < 3)
                throw new InvalidOperationException(
                    "O pacote deodol_engine parece incompleto.");
        }

        private static bool TryValidateManagedInstallation(
            string addonsDirectory, out string error)
        {
            error = string.Empty;

            try
            {
                ValidateStagedPayload(addonsDirectory);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        private static void InstallManagedPayload(
            string payloadRoot)
        {
            string sourceBootstrap = Path.Combine(
                payloadRoot, "deodol_source_windows.py");
            string sourceEngine = Path.Combine(
                payloadRoot, "deodol_engine");

            string destinationBootstrap = ManagedAddonPath;
            string destinationEngine = ManagedEngineDirectory;

            string backupRoot = Path.Combine(
                Path.GetTempPath(),
                "DayZModWorkbench-AddonBackup-" + Guid.NewGuid().ToString("N"));

            Directory.CreateDirectory(backupRoot);

            bool hadBootstrap = File.Exists(destinationBootstrap);
            bool hadEngine = Directory.Exists(destinationEngine);

            try
            {
                if (hadBootstrap)
                {
                    File.Copy(
                        destinationBootstrap,
                        Path.Combine(backupRoot, "deodol_source_windows.py"),
                        true);
                }

                if (hadEngine)
                {
                    CopyDirectory(
                        destinationEngine,
                        Path.Combine(backupRoot, "deodol_engine"));
                }

                string newBootstrap = destinationBootstrap +
                    ".new-" + Guid.NewGuid().ToString("N");

                string newEngine = destinationEngine +
                    ".new-" + Guid.NewGuid().ToString("N");

                try
                {
                    File.Copy(sourceBootstrap, newBootstrap, true);
                    CopyDirectory(sourceEngine, newEngine);

                    if (File.Exists(destinationBootstrap))
                        File.Delete(destinationBootstrap);

                    File.Move(newBootstrap, destinationBootstrap);

                    if (Directory.Exists(destinationEngine))
                        Directory.Delete(destinationEngine, true);

                    Directory.Move(newEngine, destinationEngine);

                    string finalValidationError;
                    if (!TryValidateManagedInstallation(
                        ManagedAddonDirectory, out finalValidationError))
                    {
                        throw new InvalidOperationException(
                            "Validação do addon após a troca falhou. " +
                            finalValidationError);
                    }
                }
                finally
                {
                    try
                    {
                        if (File.Exists(newBootstrap))
                            File.Delete(newBootstrap);
                    }
                    catch { }

                    TryDeleteDirectory(newEngine);
                }
            }
            catch
            {
                try
                {
                    if (File.Exists(destinationBootstrap))
                        File.Delete(destinationBootstrap);
                }
                catch { }

                TryDeleteDirectory(destinationEngine);

                try
                {
                    string backupBootstrap =
                        Path.Combine(backupRoot, "deodol_source_windows.py");

                    if (hadBootstrap && File.Exists(backupBootstrap))
                        File.Copy(backupBootstrap, destinationBootstrap, true);
                }
                catch { }

                try
                {
                    string backupEngine =
                        Path.Combine(backupRoot, "deodol_engine");

                    if (hadEngine && Directory.Exists(backupEngine))
                        CopyDirectory(backupEngine, destinationEngine);
                }
                catch { }

                throw;
            }
            finally
            {
                TryDeleteDirectory(backupRoot);
            }
        }
        private static string TryReadInstalledVersion()
        {
            string engineManifest =
                Path.Combine(ManagedEngineDirectory, "manifest.json");

            try
            {
                if (File.Exists(engineManifest))
                {
                    string json = File.ReadAllText(
                        engineManifest, Encoding.UTF8).TrimStart('\uFEFF');

                    Match match = Regex.Match(
                        json,
                        "\"(?:engine|version)\"\\s*:\\s*\"([0-9]+(?:\\.[0-9]+){1,3})\"",
                        RegexOptions.IgnoreCase);

                    if (match.Success)
                    {
                        Version parsed;
                        if (Version.TryParse(match.Groups[1].Value, out parsed))
                            return match.Groups[1].Value;
                    }
                }
            }
            catch
            {
                // Manifesto local ausente ou inválido.
            }

            return string.Empty;
        }

        private static string TryInstallExistingLegacyCopy(
            string source, string destination)
        {
            if (string.IsNullOrWhiteSpace(source))
                return null;

            try
            {
                string fullSource = Path.GetFullPath(source);

                if (fullSource.Equals(
                    Path.GetFullPath(destination),
                    StringComparison.OrdinalIgnoreCase))
                    return null;

                byte[] contents = ReadValidLegacyScript(fullSource);
                if (contents == null)
                    return null;

                InstallFileAtomically(destination, contents);
                return fullSource;
            }
            catch
            {
                return null;
            }
        }

        private static byte[] ReadValidLegacyScript(string path)
        {
            try
            {
                if (!File.Exists(path))
                    return null;

                byte[] contents = File.ReadAllBytes(path);
                string version;
                ValidateLegacyScript(contents, out version);
                return contents;
            }
            catch
            {
                return null;
            }
        }

        private static void ValidateLegacyScript(
            byte[] contents, out string version)
        {
            if (contents == null ||
                contents.Length < 1024 ||
                contents.Length > 2 * 1024 * 1024)
            {
                throw new InvalidOperationException(
                    "O addon ODOL legado possui tamanho inválido.");
            }

            string script;

            try
            {
                script = new UTF8Encoding(false, true).GetString(contents);
            }
            catch (DecoderFallbackException ex)
            {
                throw new InvalidOperationException(
                    "O addon ODOL legado não está em UTF-8 válido.", ex);
            }

            Match versionMatch = Regex.Match(script,
                "(?m)^\\s*SCRIPT_VERSION\\s*=\\s*['\"]([^'\"]+)['\"]\\s*$");

            if (!versionMatch.Success ||
                script.IndexOf("def convert_file(", StringComparison.Ordinal) < 0 ||
                script.IndexOf("def convert_tree(", StringComparison.Ordinal) < 0 ||
                script.IndexOf("DEBINARIZE_REPORT.txt", StringComparison.Ordinal) < 0 ||
                script.IndexOf("verify_pbo_archive", StringComparison.Ordinal) < 0 ||
                script.IndexOf("PBO_EQUIVALENCE_VERIFICATION.txt", StringComparison.Ordinal) < 0 ||
                script.IndexOf("recover_embedded_rvmats", StringComparison.Ordinal) < 0 ||
                script.IndexOf("PBO_FULL_PAYLOAD_RECOVERY", StringComparison.Ordinal) < 0 ||
                script.IndexOf("PBO_TOOLS_MARKER_RECOVERY", StringComparison.Ordinal) < 0 ||
                script.IndexOf("--cfgconvert", StringComparison.Ordinal) < 0 ||
                script.IndexOf("ODOL", StringComparison.OrdinalIgnoreCase) < 0 ||
                script.IndexOf("MLOD", StringComparison.OrdinalIgnoreCase) < 0)
            {
                throw new InvalidOperationException(
                    "O arquivo local não parece ser um addon ODOL legado compatível.");
            }

            version = versionMatch.Groups[1].Value.Trim();

            int majorVersion = GetVersionNumber(version);
            if (majorVersion < RequiredAddonMajorVersion)
            {
                throw new InvalidOperationException(
                    "O addon ODOL legado é antigo (" + version +
                    "); o Workbench exige v" +
                    RequiredAddonMajorVersion + " ou superior.");
            }
        }

        private static int GetVersionNumber(string version)
        {
            if (string.IsNullOrWhiteSpace(version))
                return 0;

            Match match = Regex.Match(
                version, @"(?:^|-)v(\d+)(?:-|$)", RegexOptions.IgnoreCase);

            int value;
            return match.Success &&
                int.TryParse(match.Groups[1].Value, out value)
                ? value
                : 0;
        }

        private static Version ParseVersion(string value, string label)
        {
            Version parsed;

            if (string.IsNullOrWhiteSpace(value) ||
                !Version.TryParse(value.Trim(), out parsed))
            {
                throw new InvalidOperationException(
                    label + " inválida: " + (value ?? "(null)") + ".");
            }

            return parsed;
        }

        private static Version GetWorkbenchVersion()
        {
            Version version = Assembly.GetExecutingAssembly().GetName().Version;
            return version ?? new Version(0, 0, 0, 0);
        }

        private static string GetWorkbenchVersionText()
        {
            Version version = GetWorkbenchVersion();

            if (version.Revision > 0)
                return version.ToString(4);

            if (version.Build >= 0)
                return version.ToString(3);

            return version.ToString();
        }

        private static string ComputeSha256File(string path)
        {
            using (SHA256 sha = SHA256.Create())
            using (FileStream stream = File.OpenRead(path))
            {
                return BitConverter.ToString(
                    sha.ComputeHash(stream)).Replace("-", string.Empty);
            }
        }

        private static string ReadUtf8Strict(string path)
        {
            byte[] bytes = File.ReadAllBytes(path);
            return new UTF8Encoding(false, true).GetString(bytes);
        }

        private static void InstallFileAtomically(
            string destination, byte[] contents)
        {
            string temporary = destination +
                ".download-" + Guid.NewGuid().ToString("N") + ".tmp";

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
                try
                {
                    if (File.Exists(temporary))
                        File.Delete(temporary);
                }
                catch { }
            }
        }

        private static void CopyDirectory(
            string sourceDirectory, string destinationDirectory)
        {
            Directory.CreateDirectory(destinationDirectory);

            foreach (string file in Directory.GetFiles(sourceDirectory))
            {
                string destination = Path.Combine(
                    destinationDirectory, Path.GetFileName(file));

                File.Copy(file, destination, true);
            }

            foreach (string directory in Directory.GetDirectories(sourceDirectory))
            {
                string destination = Path.Combine(
                    destinationDirectory, Path.GetFileName(directory));

                CopyDirectory(directory, destination);
            }
        }

        private static void TryDeleteDirectory(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // Limpeza best-effort.
            }
        }

        private static void TryDeleteDirectoryIfEmpty(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return;

            try
            {
                if (Directory.Exists(path) &&
                    Directory.GetFileSystemEntries(path).Length == 0)
                {
                    Directory.Delete(path, false);
                }
            }
            catch
            {
                // Limpeza best-effort.
            }
        }
        private static string MakeRelativeForMessage(
            string root, string fullPath)
        {
            string normalizedRoot =
                Path.GetFullPath(root).TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar) +
                Path.DirectorySeparatorChar;

            string normalizedFull = Path.GetFullPath(fullPath);

            if (normalizedFull.StartsWith(
                normalizedRoot, StringComparison.OrdinalIgnoreCase))
            {
                return normalizedFull.Substring(normalizedRoot.Length);
            }

            return fullPath;
        }

        public sealed class UpdateManifest
        {
            public int schema { get; set; }
            public string channel { get; set; }
            public EngineManifest engine { get; set; }
            public ReleaseManifest release { get; set; }
        }

        public sealed class EngineManifest
        {
            public string version { get; set; }
            public int api { get; set; }
            public string minimum_workbench { get; set; }
        }

        public sealed class ReleaseManifest
        {
            public string tag { get; set; }
            public string asset { get; set; }
            public string sha256 { get; set; }
            public string archive { get; set; }
            public bool encrypted { get; set; }
            public bool header_encrypted { get; set; }
            public string key_id { get; set; }
        }
    }
}
