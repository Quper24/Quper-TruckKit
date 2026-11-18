using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Threading;

namespace QuperDesk {
    internal sealed class FileSnapshot {
        public readonly string Path;
        public readonly byte[] Bytes;

        public FileSnapshot(string path) {
            Path = System.IO.Path.GetFullPath(path);
            Bytes = Read(Path);
        }

        public void Verify() {
            if (!Equal(Bytes, Read(Path)))
                throw new IOException(L.T("Файл изменился после чтения. Обновите данные и повторите действие: ") + System.IO.Path.GetFileName(Path));
        }

        public static byte[] Read(string path) {
            path = FileTransaction.NativePath(path);
            FileAttributes attributes;
            try { attributes = File.GetAttributes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
            if ((attributes & (FileAttributes.ReparsePoint | FileAttributes.Directory)) != 0)
                throw new IOException(L.T("Ожидался обычный файл: ") + System.IO.Path.GetFileName(path));
            using (var stream = File.OpenRead(path)) {
                if (stream.Length > Sii.MaxBytes) throw new IOException(L.T("Файл слишком большой: ") + System.IO.Path.GetFileName(path));
                var bytes = new byte[(int)stream.Length];
                int offset = 0;
                while (offset < bytes.Length) {
                    var count = stream.Read(bytes, offset, bytes.Length - offset);
                    if (count == 0) throw new EndOfStreamException(L.T("Файл изменился во время чтения."));
                    offset += count;
                }
                return bytes;
            }
        }

        public static bool Equal(byte[] left, byte[] right) {
            return left == right || left != null && right != null && left.SequenceEqual(right);
        }
    }

    internal sealed class FileUpdate {
        public readonly FileSnapshot Original;
        public readonly byte[] Contents;

        public FileUpdate(FileSnapshot original, byte[] contents) {
            Original = original;
            Contents = contents;
        }
    }

    internal static class FileTransaction {
        private sealed class Pending {
            public FileUpdate Update;
            public string Stage, Backup;
        }

        public static string Commit(IList<FileUpdate> updates, Action verify) {
            if (updates.Count == 0) throw new InvalidOperationException(L.T("Настройки уже совпадают."));
            var folder = Path.GetDirectoryName(updates[0].Original.Path);
            if (updates.Any(u => !string.Equals(Path.GetDirectoryName(u.Original.Path), folder, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException(L.T("Все файлы назначения должны быть в одной папке."));
            if (updates.Select(u => u.Original.Path).Distinct(StringComparer.OrdinalIgnoreCase).Count() != updates.Count)
                throw new InvalidOperationException(L.T("Один файл указан дважды."));

            string lockName;
            using (var sha = SHA256.Create())
                lockName = "Local\\QuperSimulator-" + BitConverter.ToString(sha.ComputeHash(Sii.Utf8.GetBytes(folder.ToUpperInvariant())));
            using (var mutex = new Mutex(false, lockName)) {
                try {
                    if (!mutex.WaitOne(0)) throw new IOException(L.T("В другом окне уже идёт запись в эту папку."));
                } catch (AbandonedMutexException) { }
                try { return CommitLocked(folder, updates, verify); }
                finally { mutex.ReleaseMutex(); }
            }
        }

        private static string CommitLocked(string folder, IList<FileUpdate> updates, Action verify) {
            EnsureDirectory(folder);
            verify();
            foreach (var update in updates) update.Original.Verify();
            var backupRoot = Path.Combine(folder, ".quper-backups");
            Directory.CreateDirectory(NativePath(backupRoot));
            EnsureDirectory(backupRoot);
            var stamp = DateTime.Now.ToString("yyyyMMdd-HHmmss-fff", CultureInfo.InvariantCulture) + "-" + Guid.NewGuid().ToString("N").Substring(0, 8);
            var backup = Path.Combine(backupRoot, stamp);
            Directory.CreateDirectory(NativePath(backup));
            var pending = new List<Pending>();
            var applied = new List<Pending>();
            try {
                var created = new List<string>();
                foreach (var update in updates) {
                    var name = Path.GetFileName(update.Original.Path);
                    var item = new Pending { Update = update, Stage = Path.Combine(folder, ".quper-temp-" + Guid.NewGuid().ToString("N")), Backup = Path.Combine(backup, name) };
                    pending.Add(item);
                    if (update.Original.Bytes == null) created.Add(name);
                    else WriteNew(item.Backup, update.Original.Bytes);
                    WriteNew(item.Stage, update.Contents);
                }
                if (created.Count > 0) WriteNew(Path.Combine(backup, "created-files.txt"), Sii.Utf8.GetBytes(string.Join(Environment.NewLine, created) + Environment.NewLine));
                verify();
                foreach (var item in pending) item.Update.Original.Verify();
                foreach (var item in pending) {
                    var original = item.Update.Original;
                    original.Verify();
                    if (original.Bytes == null) File.Move(NativePath(item.Stage), NativePath(original.Path));
                    else File.Replace(NativePath(item.Stage), NativePath(original.Path), null);
                    applied.Add(item);
                }
                return backup;
            } catch (Exception failure) {
                bool restoreFailed = false;
                for (var i = applied.Count - 1; i >= 0; i--) {
                    try { Restore(applied[i]); }
                    catch (IOException) { restoreFailed = true; }
                    catch (UnauthorizedAccessException) { restoreFailed = true; }
                }
                if (restoreFailed) throw new IOException(L.T("Не удалось полностью восстановить файлы. Резервная копия: ") + backup, failure);
                if (applied.Count > 0) throw new IOException(L.T("Запись прервалась. Исходные файлы восстановлены. ") + failure.Message, failure);
                throw;
            } finally {
                foreach (var item in pending) {
                    try { File.Delete(NativePath(item.Stage)); }
                    catch (IOException) { }
                    catch (UnauthorizedAccessException) { }
                }
            }
        }

        private static void Restore(Pending item) {
            var path = item.Update.Original.Path;
            if (!FileSnapshot.Equal(item.Update.Contents, FileSnapshot.Read(path)))
                throw new IOException(L.T("Файл изменился после записи."));
            if (item.Update.Original.Bytes == null) File.Delete(NativePath(path));
            else {
                File.Copy(NativePath(item.Backup), NativePath(item.Stage));
                File.Replace(NativePath(item.Stage), NativePath(path), null);
            }
        }

        public static void EnsureDirectory(string path) {
            var attributes = File.GetAttributes(NativePath(path));
            if ((attributes & FileAttributes.Directory) == 0 || (attributes & FileAttributes.ReparsePoint) != 0)
                throw new IOException(L.T("Ожидалась обычная папка: ") + path);
        }

        public static string NativePath(string path) {
            if (path.StartsWith(@"\\?\", StringComparison.Ordinal)) return path;
            if (path.StartsWith(@"\\", StringComparison.Ordinal)) return @"\\?\UNC\" + path.Substring(2);
            return @"\\?\" + path;
        }

        private static void WriteNew(string path, byte[] contents) {
            using (var file = new FileStream(NativePath(path), FileMode.CreateNew, FileAccess.Write, FileShare.None)) {
                file.Write(contents, 0, contents.Length);
                file.Flush(true);
            }
        }
    }
}
