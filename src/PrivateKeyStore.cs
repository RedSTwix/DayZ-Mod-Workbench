using System;
using System.IO;
using System.Linq;

namespace DayZModWorkbench
{
    internal static class PrivateKeyStore
    {
        internal static string DirectoryPath
        {
            get { return Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "keys"); }
        }

        internal static string EnsureLocal(string configuredPath)
        {
            Directory.CreateDirectory(DirectoryPath);
            if (!string.IsNullOrWhiteSpace(configuredPath) && File.Exists(configuredPath))
            {
                string fullPath = Path.GetFullPath(configuredPath);
                if (!HasPrivateKeyExtension(fullPath))
                    throw new InvalidOperationException("A chave configurada não possui a extensão .biprivatekey.");
                return IsInsideStore(fullPath) ? fullPath : Import(fullPath);
            }

            return Directory.GetFiles(DirectoryPath, "*.biprivatekey", SearchOption.TopDirectoryOnly)
                .OrderByDescending(File.GetLastWriteTimeUtc)
                .FirstOrDefault() ?? string.Empty;
        }

        internal static string Import(string sourcePath)
        {
            if (string.IsNullOrWhiteSpace(sourcePath) || !File.Exists(sourcePath))
                throw new FileNotFoundException("Chave privada não encontrada.", sourcePath);
            if (!HasPrivateKeyExtension(sourcePath))
                throw new InvalidOperationException("Selecione um arquivo .biprivatekey.");

            Directory.CreateDirectory(DirectoryPath);
            string source = Path.GetFullPath(sourcePath);
            if (IsInsideStore(source)) return source;

            string destination = Path.Combine(DirectoryPath, Path.GetFileName(source));
            if (File.Exists(destination))
            {
                if (FilesEqual(source, destination)) return destination;
                string name = Path.GetFileNameWithoutExtension(source);
                string extension = Path.GetExtension(source);
                int suffix = 2;
                do
                {
                    destination = Path.Combine(DirectoryPath, name + "_" + suffix + extension);
                    suffix++;
                }
                while (File.Exists(destination));
            }

            File.Copy(source, destination, false);
            return destination;
        }

        internal static bool IsStoredPrivateKey(string path)
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) &&
                HasPrivateKeyExtension(path) && IsInsideStore(Path.GetFullPath(path));
        }

        private static bool IsInsideStore(string path)
        {
            string store = Path.GetFullPath(DirectoryPath).TrimEnd(Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            string candidate = Path.GetFullPath(path);
            return candidate.StartsWith(store, StringComparison.OrdinalIgnoreCase);
        }

        private static bool HasPrivateKeyExtension(string path)
        {
            return Path.GetExtension(path).Equals(".biprivatekey", StringComparison.OrdinalIgnoreCase);
        }

        private static bool FilesEqual(string first, string second)
        {
            FileInfo a = new FileInfo(first);
            FileInfo b = new FileInfo(second);
            if (a.Length != b.Length) return false;
            const int bufferSize = 8192;
            byte[] left = new byte[bufferSize];
            byte[] right = new byte[bufferSize];
            using (FileStream firstStream = new FileStream(first, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream secondStream = new FileStream(second, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                while (true)
                {
                    int leftRead = firstStream.Read(left, 0, left.Length);
                    int rightRead = secondStream.Read(right, 0, right.Length);
                    if (leftRead != rightRead) return false;
                    if (leftRead == 0) return true;
                    for (int i = 0; i < leftRead; i++)
                        if (left[i] != right[i]) return false;
                }
            }
        }
    }
}
