using System;
using System.IO;
using System.Text;

namespace Tootega.Cockpit.Util
{
    /// <summary>
    /// Atomic writes and cross-process locking, shared by the stores under
    /// ~/.claude/tootega.
    ///
    /// Both matter for the same reason: several VS windows (and the CLI itself) touch these
    /// files at once. A torn write turns a statistics file into a corrupt one that is then
    /// silently discarded, losing a session's history; an unlocked read-modify-write makes
    /// two windows overwrite each other's samples.
    /// </summary>
    internal static class FileStore
    {
        /// <summary>A lock older than this is treated as orphaned and stolen.</summary>
        private const int DefaultStaleMs = 15000;

        /// <summary>
        /// Writes through a temp file and a replace, so a reader never observes a partial
        /// file. Returns false instead of throwing: losing a statistics sample is
        /// acceptable, taking the extension down for it is not.
        /// </summary>
        public static bool WriteAtomic(string path, string content)
        {
            try
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                var temp = path + ".tmp";
                File.WriteAllText(temp, content, new UTF8Encoding(false));

                // Delete-then-move rather than File.Replace: there is no backup target here,
                // and Replace fails when the destination does not exist yet.
                if (File.Exists(path)) File.Delete(path);
                File.Move(temp, path);
                return true;
            }
            catch (Exception ex)
            {
                Log.Debug("atomic write failed for " + path + ": " + ex.Message);
                return false;
            }
        }

        public static string ReadAllTextOrNull(string path)
        {
            try
            {
                return File.Exists(path) ? File.ReadAllText(path) : null;
            }
            catch
            {
                // Locked or unreadable: the caller treats it as absent.
                return null;
            }
        }

        /// <summary>
        /// An exclusive lock backed by a file that exists only while held.
        ///
        /// <see cref="TryAcquire"/> returns null when someone else holds it — callers retry
        /// rather than block, because these are all debounced background writes and waiting
        /// would be worse than being late. A lock left behind by a crashed process is stolen
        /// once it goes stale, or it would block every window forever.
        /// </summary>
        public sealed class Lock : IDisposable
        {
            private readonly string _path;
            private FileStream _stream;

            private Lock(string path, FileStream stream)
            {
                _path = path;
                _stream = stream;
            }

            public static Lock TryAcquire(string path, int staleMs = DefaultStaleMs)
            {
                var stream = Open(path);
                if (stream != null) return new Lock(path, stream);

                // Busy — but possibly by a process that no longer exists.
                try
                {
                    var age = DateTime.UtcNow - File.GetLastWriteTimeUtc(path);
                    if (age.TotalMilliseconds <= staleMs) return null;

                    File.Delete(path);
                    stream = Open(path);
                    return stream != null ? new Lock(path, stream) : null;
                }
                catch
                {
                    // Vanished between the check and the delete: let the caller retry.
                    return null;
                }
            }

            private static FileStream Open(string path)
            {
                try
                {
                    var directory = Path.GetDirectoryName(path);
                    if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

                    // CreateNew is the exclusive part: it fails if the file already exists.
                    // DeleteOnClose means a hard exit of this process still frees the lock.
                    return new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                                          1, FileOptions.DeleteOnClose);
                }
                catch
                {
                    return null;
                }
            }

            public void Dispose()
            {
                var stream = _stream;
                _stream = null;
                if (stream == null) return;

                try
                {
                    stream.Dispose();
                }
                catch
                {
                    // DeleteOnClose already removed the file in the normal path.
                }

                try
                {
                    if (File.Exists(_path)) File.Delete(_path);
                }
                catch
                {
                }
            }
        }
    }
}
