using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace DayZModWorkbench
{
    internal sealed class SourcePreparationResult
    {
        public int FilesFound;
        public int ConfigsConverted;
        public int DataFilesConverted;
        public int RapFilesFound;
        public int RapAlreadyEditable;
        public int BinaryFilesPreserved;
        public int TextureHeadersRemoved;
        public int OdolPreserved;
        public int MlodPreserved;
        public readonly List<string> Warnings = new List<string>();
        public readonly List<string> UnrecoverableFiles = new List<string>();
        public bool IsPreAudit;

        public int ConvertedFiles
        {
            get { return ConfigsConverted + DataFilesConverted; }
        }

        public int ChangedFiles
        {
            get { return ConvertedFiles + TextureHeadersRemoved; }
        }

        public bool IsComplete
        {
            get { return BinaryFilesPreserved == 0; }
        }

        public string Summary
        {
            get
            {
                return ConvertedFiles + " arquivo(s) desbinarizado(s): " + ConfigsConverted +
                    " config.bin e " + DataFilesConverted + " configuração(ões)/material(is). " +
                    "Auditoria RaP: " + RapFilesFound + " encontrado(s), " + ConvertedFiles +
                    " convertido(s), " + RapAlreadyEditable + " já acompanhado(s) por fonte válido e " +
                    BinaryFilesPreserved + (IsPreAudit
                        ? " aguardando tentativa de recuperação v7. "
                        : " preservado(s) ainda binário(s). ") +
                    TextureHeadersRemoved + " texheaders.bin removido(s) para regeneração no build. " +
                    OdolPreserved + " modelo(s) ODOL detectado(s) para reconstrução; " +
                    MlodPreserved + " modelo(s) MLOD preservado(s).";
            }
        }
    }

    internal static class SourcePreparer
    {
        private const int ValidationTimeoutMilliseconds = 15000;

        private static readonly HashSet<string> RapTextExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".rvmat", ".bisurf", ".sqm", ".fsm", ".bikb", ".ext", ".cpp", ".cfg"
        };

        public static async Task<SourcePreparationResult> PrepareAsync(string sourceRoot, string cfgConvertPath,
            Action<string> log, bool preAudit = false)
        {
            if (!Directory.Exists(sourceRoot)) throw new DirectoryNotFoundException("Source não encontrado: " + sourceRoot);
            if (!File.Exists(cfgConvertPath)) throw new FileNotFoundException("CfgConvert.exe não encontrado.", cfgConvertPath);

            SourcePreparationResult result = new SourcePreparationResult();
            result.IsPreAudit = preAudit;
            string[] files = Directory.GetFiles(sourceRoot, "*", SearchOption.AllDirectories);
            result.FilesFound = files.Length;

            foreach (string file in files)
            {
                string extension = Path.GetExtension(file);
                if (Path.GetFileName(file).Equals("MODEL_CFG_EQUIVALENCE_VERIFICATION.txt",
                    StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    if (log != null) log("Relatório temporário de verificação removido: " +
                        RelativePath(sourceRoot, file));
                    continue;
                }
                if (Path.GetFileName(file).Equals("texheaders.bin", StringComparison.OrdinalIgnoreCase))
                {
                    File.Delete(file);
                    result.TextureHeadersRemoved++;
                    if (log != null) log("texheaders.bin ignorado; o Addon Builder irá regenerá-lo: " + RelativePath(sourceRoot, file));
                    continue;
                }

                if (extension.Equals(".p3d", StringComparison.OrdinalIgnoreCase))
                {
                    string signature = ReadSignature(file);
                    if (signature == "ODOL") result.OdolPreserved++;
                    else if (signature == "MLOD") result.MlodPreserved++;
                    continue;
                }

                if (!IsRapified(file)) continue;
                result.RapFilesFound++;

                bool isConfig = Path.GetFileName(file).Equals("config.bin", StringComparison.OrdinalIgnoreCase);
                if (!isConfig && !RapTextExtensions.Contains(extension))
                {
                    RecordUnrecoverable(result, RelativePath(sourceRoot, file),
                        "formato RaP não suportado", log, preAudit);
                    continue;
                }

                string destination = isConfig
                    ? Path.Combine(Path.GetDirectoryName(file), "config.cpp")
                    : file;
                if (isConfig && File.Exists(destination) && IsUsableTextConfig(destination))
                {
                    result.RapAlreadyEditable++;
                    if (log != null) log("Informação: config.cpp válido já existe; config.bin de runtime foi " +
                        "preservado sem impedir a edição: " + RelativePath(sourceRoot, file));
                    continue;
                }
                if (isConfig && File.Exists(destination) && log != null)
                    log("config.cpp vazio/inválido detectado; reconstruindo a partir de " + RelativePath(sourceRoot, file));

                string temporary = Path.Combine(Path.GetDirectoryName(destination),
                    Path.GetFileNameWithoutExtension(destination) + ".dayzworkbench." +
                    Guid.NewGuid().ToString("N") + Path.GetExtension(destination));
                string validation = temporary + ".validation.bin";
                try
                {
                    string parserError;
                    if (!RapTextConverter.TryConvert(file, temporary, out parserError))
                    {
                        RecordUnrecoverable(result, RelativePath(sourceRoot, file), parserError, log, preAudit);
                        continue;
                    }

                    ProcessResult validationResult = await ProcessRunner.RunAsync(cfgConvertPath,
                        "-bin -dst " + ProcessRunner.Quote(validation) + " " + ProcessRunner.Quote(temporary),
                        sourceRoot, log, ValidationTimeoutMilliseconds);
                    if (validationResult.ExitCode != 0 || !File.Exists(validation) ||
                        new FileInfo(validation).Length == 0 || !IsRapified(validation))
                        throw new InvalidDataException("o texto reconstruído não passou na recompilação de validação");

                    if (isConfig)
                    {
                        if (File.Exists(destination))
                        {
                            File.Copy(temporary, destination, true);
                            File.Delete(temporary);
                        }
                        else
                        {
                            File.Move(temporary, destination);
                        }
                        File.Delete(file);
                        result.ConfigsConverted++;
                    }
                    else
                    {
                        File.Copy(temporary, file, true);
                        result.DataFilesConverted++;
                    }
                }
                catch (Exception ex)
                {
                    RecordUnrecoverable(result, RelativePath(sourceRoot, file), ex.Message, log, preAudit);
                }
                finally
                {
                    try
                    {
                        if (File.Exists(temporary)) File.Delete(temporary);
                        if (File.Exists(validation)) File.Delete(validation);
                    }
                    catch { }
                }
            }

            if (result.UnrecoverableFiles.Count > 0 && !preAudit)
            {
                string examples = string.Join(", ", result.UnrecoverableFiles.Take(8).ToArray());
                if (result.UnrecoverableFiles.Count > 8) examples += ", …";
                result.Warnings.Add("SOURCE NÃO TOTALMENTE EDITÁVEL — " + result.UnrecoverableFiles.Count +
                    " arquivo(s) RaP continuam binários e foram preservados sem alteração; nenhum está faltando. " +
                    "Arquivos: " + examples + ". Os motivos individuais estão no log.");
            }

            return result;
        }

        private static void RecordUnrecoverable(SourcePreparationResult result, string relativePath,
            string reason, Action<string> log, bool preAudit)
        {
            result.BinaryFilesPreserved++;
            result.UnrecoverableFiles.Add(relativePath);
            if (!preAudit && log != null)
                log("Aviso: SOURCE NÃO TOTALMENTE EDITÁVEL — original binário preservado: " +
                    relativePath + ". " + reason);
        }

        internal static bool NeedsPythonConfigRecovery(string sourceRoot, out string reason)
        {
            reason = string.Empty;
            if (string.IsNullOrWhiteSpace(sourceRoot) || !Directory.Exists(sourceRoot)) return false;

            string configBin = Path.Combine(sourceRoot, "config.bin");
            if (!File.Exists(configBin) || new FileInfo(configBin).Length == 0) return false;

            string configCpp = Path.Combine(sourceRoot, "config.cpp");
            if (!File.Exists(configCpp))
            {
                reason = "config.cpp ausente";
                return true;
            }

            if (!IsUsableTextConfig(configCpp))
            {
                reason = new FileInfo(configCpp).Length == 0
                    ? "config.cpp vazio"
                    : "config.cpp inválido/não editável";
                return true;
            }

            return false;
        }

        internal static bool HasUnrecoverableConfig(SourcePreparationResult result)
        {
            if (result == null) return false;
            return result.UnrecoverableFiles.Any(path =>
                Path.GetFileName(path).Equals("config.bin", StringComparison.OrdinalIgnoreCase));
        }

        private static bool IsUsableTextConfig(string path)
        {
            try
            {
                if (!File.Exists(path) || new FileInfo(path).Length == 0 || IsRapified(path)) return false;
                using (StreamReader reader = new StreamReader(path, true))
                {
                    char[] buffer = new char[4096];
                    int read = reader.Read(buffer, 0, buffer.Length);
                    for (int i = 0; i < read; i++)
                        if (!char.IsWhiteSpace(buffer[i])) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool IsRapified(string path)
        {
            try
            {
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Length < 4) return false;
                    return stream.ReadByte() == 0 && stream.ReadByte() == 0x72 &&
                        stream.ReadByte() == 0x61 && stream.ReadByte() == 0x50;
                }
            }
            catch
            {
                return false;
            }
        }

        private static string ReadSignature(string path)
        {
            try
            {
                byte[] bytes = new byte[4];
                using (FileStream stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    if (stream.Read(bytes, 0, bytes.Length) != bytes.Length) return string.Empty;
                }
                return Encoding.ASCII.GetString(bytes);
            }
            catch
            {
                return string.Empty;
            }
        }

        private static string RelativePath(string root, string path)
        {
            string fullRoot = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string fullPath = Path.GetFullPath(path);
            return fullPath.StartsWith(fullRoot, StringComparison.OrdinalIgnoreCase)
                ? fullPath.Substring(fullRoot.Length)
                : Path.GetFileName(path);
        }
    }
}
