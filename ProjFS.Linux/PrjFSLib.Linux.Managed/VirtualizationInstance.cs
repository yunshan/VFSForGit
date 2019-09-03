using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using PrjFSLib.Linux.Interop;
using static PrjFSLib.Linux.Interop.Errno;

namespace PrjFSLib.Linux
{
    public class VirtualizationInstance
    {
        public const int PlaceholderIdLength = 128;

        private static readonly TimeSpan MountWaitTick = TimeSpan.FromSeconds(0.2);
        private static readonly TimeSpan MountWaitTotal = TimeSpan.FromSeconds(30);

        private ProjFS projfs;
        private int currentProcessId = Process.GetCurrentProcess().Id;
        private string virtualizationRoot;

        // We must hold a reference to the delegates to prevent garbage collection
        private ProjFS.EventHandler preventGCOnProjEventDelegate;
        private ProjFS.EventHandler preventGCOnNotifyEventDelegate;
        private ProjFS.EventHandler preventGCOnPermEventDelegate;

        // References held to these delegates via class properties
        public virtual EnumerateDirectoryCallback OnEnumerateDirectory { get; set; }
        public virtual GetFileStreamCallback OnGetFileStream { get; set; }
        public virtual LogErrorCallback OnLogError { get; set; }
        public virtual LogWarningCallback OnLogWarning { get; set; }
        public virtual LogInfoCallback OnLogInfo { get; set; }

        public virtual NotifyFileModified OnFileModified { get; set; }
        public virtual NotifyFilePreConvertToFullEvent OnFilePreConvertToFull { get; set; }
        public virtual NotifyPreDeleteEvent OnPreDelete { get; set; }
        public virtual NotifyPreRenameEvent OnPreRename { get; set; }
        public virtual NotifyNewFileCreatedEvent OnNewFileCreated { get; set; }
        public virtual NotifyFileDeletedEvent OnFileDeleted { get; set; }
        public virtual NotifyFileRenamedEvent OnFileRenamed { get; set; }
        public virtual NotifyHardLinkCreatedEvent OnHardLinkCreated { get; set; }

        public virtual Result StartVirtualizationInstance(
            string storageRootFullPath,
            string virtualizationRootFullPath,
            uint poolThreadCount,
            bool initializeStorageRoot)
        {
            if (this.projfs != null)
            {
                throw new InvalidOperationException();
            }

            int statResult = LinuxNative.Stat(virtualizationRootFullPath, out LinuxNative.StatBuffer stat);
            if (statResult != 0)
            {
                return Result.Invalid;
            }

            ulong priorDev = stat.Dev;

            ProjFS.Handlers handlers = new ProjFS.Handlers
            {
                HandleProjEvent = this.preventGCOnProjEventDelegate = new ProjFS.EventHandler(this.HandleProjEvent),
                HandleNotifyEvent = this.preventGCOnNotifyEventDelegate = new ProjFS.EventHandler(this.HandleNotifyEvent),
                HandlePermEvent = this.preventGCOnPermEventDelegate = new ProjFS.EventHandler(this.HandlePermEvent)
            };

            string[] args = initializeStorageRoot ? new string[] { "-o", "initial" } : new string[] { };

            ProjFS fs = ProjFS.New(
                storageRootFullPath,
                virtualizationRootFullPath,
                handlers,
                args);

            if (fs == null)
            {
                return Result.Invalid;
            }

            if (fs.Start() != 0)
            {
                fs.Stop();
                return Result.Invalid;
            }

            Stopwatch watch = Stopwatch.StartNew();

            while (true)
            {
                statResult = LinuxNative.Stat(virtualizationRootFullPath, out stat);
                if (priorDev != stat.Dev)
                {
                    break;
                }

                Thread.Sleep(MountWaitTick);

                if (watch.Elapsed > MountWaitTotal)
                {
                    fs.Stop();
                    return Result.Invalid;
                }
            }

            this.virtualizationRoot = virtualizationRootFullPath;
            this.projfs = fs;

            return Result.Success;
        }

        public virtual void StopVirtualizationInstance()
        {
            if (!this.ObtainProjFS(out ProjFS fs))
            {
                return;
            }

            fs.Stop();
            this.projfs = null;
        }

        public virtual IEnumerable<string> EnumerateFileSystemEntries(string path)
        {
            return Directory.EnumerateFileSystemEntries(path);
        }

        public virtual Result WriteFileContents(
            int fd,
            byte[] bytes,
            uint byteCount)
        {
            if (!NativeFileWriter.TryWrite(fd, bytes, byteCount))
            {
                return Result.EIOError;
            }

            return Result.Success;
        }

        public virtual Result DeleteFile(
            string relativePath,
            UpdateType updateFlags,
            out UpdateFailureCause failureCause)
        {
            failureCause = UpdateFailureCause.NoFailure;

            if (!this.ObtainProjFS(out ProjFS fs))
            {
                return Result.EDriverNotLoaded;
            }

            if (string.IsNullOrEmpty(relativePath))
            {
                /* Our mount point directory can not be deleted; we would
                 * receive an EBUSY error.  Therefore we just return
                 * EDirectoryNotEmpty because that error is silently handled
                 * by our caller in GitIndexProjection, and this is the
                 * expected behavior (corresponding to the Mac implementation).
                 */
                return Result.EDirectoryNotEmpty;
            }

            string fullPath = Path.Combine(this.virtualizationRoot, relativePath);
            bool isDirectory = Directory.Exists(fullPath);
            Result result = Result.Success;
            if (!isDirectory)
            {
                // TODO(Linux): try to handle races with hydration?
                ProjectionState state;
                result = fs.GetProjState(relativePath, out state);

                // also treat unknown state as full/dirty (e.g., for sockets)
                if ((result == Result.Success && state == ProjectionState.Full) ||
                    (result == Result.Invalid && state == ProjectionState.Unknown))
                {
                    failureCause = UpdateFailureCause.DirtyData;
                    return Result.EVirtualizationInvalidOperation;
                }
            }

            if (result == Result.Success)
            {
                result = RemoveFileOrDirectory(fullPath, isDirectory);
            }

            if (result == Result.EAccessDenied)
            {
                failureCause = UpdateFailureCause.ReadOnly;
            }
            else if (result == Result.EFileNotFound || result == Result.EPathNotFound)
            {
                return Result.Success;
            }

            return result;
        }

        public virtual Result WritePlaceholderDirectory(
            string relativePath)
        {
            if (!this.ObtainProjFS(out ProjFS fs))
            {
                return Result.EDriverNotLoaded;
            }

            return fs.CreateProjDir(relativePath, Convert.ToUInt32("777", 8));
        }

        public virtual Result WritePlaceholderFile(
            string relativePath,
            byte[] providerId,
            byte[] contentId,
            ulong fileSize,
            uint fileMode)
        {
            if (providerId.Length != PlaceholderIdLength ||
                contentId.Length != PlaceholderIdLength)
            {
                throw new ArgumentException();
            }

            if (!this.ObtainProjFS(out ProjFS fs))
            {
                return Result.EDriverNotLoaded;
            }

            return fs.CreateProjFile(
                relativePath,
                fileSize,
                fileMode,
                providerId,
                contentId);
        }

        public virtual Result WriteSymLink(
            string relativePath,
            string symLinkTarget)
        {
            if (!this.ObtainProjFS(out ProjFS fs))
            {
                return Result.EDriverNotLoaded;
            }

            return fs.CreateProjSymlink(
                relativePath,
                symLinkTarget);
        }

        public virtual Result UpdatePlaceholderIfNeeded(
            string relativePath,
            byte[] providerId,
            byte[] contentId,
            ulong fileSize,
            uint fileMode,
            UpdateType updateFlags,
            out UpdateFailureCause failureCause)
        {
            if (providerId.Length != PlaceholderIdLength ||
                contentId.Length != PlaceholderIdLength)
            {
                throw new ArgumentException();
            }

            Result result = this.DeleteFile(relativePath, updateFlags, out failureCause);
            if (result != Result.Success)
            {
                return result;
            }

            // TODO(Linux): try to handle races with hydration?
            failureCause = UpdateFailureCause.NoFailure;
            return this.WritePlaceholderFile(relativePath, providerId, contentId, fileSize, fileMode);
        }

        public virtual Result ReplacePlaceholderFileWithSymLink(
            string relativePath,
            string symLinkTarget,
            UpdateType updateFlags,
            out UpdateFailureCause failureCause)
        {
            Result result = this.DeleteFile(relativePath, updateFlags, out failureCause);
            if (result != Result.Success)
            {
                return result;
            }

            // TODO(Linux): try to handle races with hydration?
            failureCause = UpdateFailureCause.NoFailure;
            return this.WriteSymLink(relativePath, symLinkTarget);
        }

        public virtual Result CompleteCommand(
            ulong commandId,
            Result result)
        {
            throw new NotImplementedException();
        }

        public virtual Result ConvertDirectoryToPlaceholder(
            string relativeDirectoryPath)
        {
            throw new NotImplementedException();
        }

        private static string ConvertDotPath(string path)
        {
            if (path == ".")
            {
                path = string.Empty;
            }

            return path;
        }

        private static Result RemoveFileOrDirectory(
            string fullPath,
            bool isDirectory)
        {
            try
            {
                if (isDirectory)
                {
                    Directory.Delete(fullPath);
                }
                else
                {
                    File.Delete(fullPath);
                }
            }
            catch (IOException ex) when (ex is DirectoryNotFoundException)
            {
                return Result.EPathNotFound;
            }
            catch (IOException ex) when (ex is FileNotFoundException)
            {
                return Result.EFileNotFound;
            }
            catch (IOException ex) when (ex.HResult == Errno.Constants.ENOTEMPTY)
            {
                return Result.EDirectoryNotEmpty;
            }
            catch (IOException)
            {
                return Result.EIOError;
            }
            catch (UnauthorizedAccessException)
            {
                return Result.EAccessDenied;
            }
            catch
            {
                return Result.Invalid;
            }

            return Result.Success;
        }

        private static string GetProcCmdline(int pid)
        {
            try
            {
                using (var stream = File.OpenText($"/proc/{pid}/cmdline"))
                {
                    string[] parts = stream.ReadToEnd().Split('\0');
                    return parts.Length > 0 ? parts[0] : string.Empty;
                }
            }
            catch
            {
                // process with given pid may have exited; nothing to be done
                return string.Empty;
            }
        }

        // TODO(Linux): replace with netstandard2.1 Marshal.PtrToStringUTF8()
        private static string PtrToStringUTF8(IntPtr ptr)
        {
            return Marshal.PtrToStringAnsi(ptr);
        }

        private bool ObtainProjFS(out ProjFS fs)
        {
            fs = this.projfs;
            return fs != null;
        }

        private bool IsProviderEvent(ProjFS.Event ev)
        {
            return (ev.Pid == this.currentProcessId);
        }

        private int HandleProjEvent(ref ProjFS.Event ev)
        {
            if (!this.ObtainProjFS(out ProjFS fs))
            {
                return -Errno.Constants.ENODEV;
            }

            // ignore events triggered by own process to prevent deadlocks
            if (this.IsProviderEvent(ev))
            {
                return 0;
            }

            string triggeringProcessName = GetProcCmdline(ev.Pid);
            string relativePath = PtrToStringUTF8(ev.Path);

            Result result;

            if ((ev.Mask & ProjFS.Constants.PROJFS_ONDIR) != 0)
            {
                result = this.OnEnumerateDirectory(
                    commandId: 0,
                    relativePath: ConvertDotPath(relativePath),
                    triggeringProcessId: ev.Pid,
                    triggeringProcessName: triggeringProcessName);
            }
            else
            {
                byte[] providerId = new byte[PlaceholderIdLength];
                byte[] contentId = new byte[PlaceholderIdLength];

                result = fs.GetProjAttrs(
                    relativePath,
                    providerId,
                    contentId);

                if (result == Result.Success)
                {
                    result = this.OnGetFileStream(
                        commandId: 0,
                        relativePath: relativePath,
                        providerId: providerId,
                        contentId: contentId,
                        triggeringProcessId: ev.Pid,
                        triggeringProcessName: triggeringProcessName,
                        fd: ev.Fd);
                }
            }

            return -result.ToErrno();
        }

        private int HandleNonProjEvent(ref ProjFS.Event ev, bool perm)
        {
            if (this.projfs == null)
            {
                return -Errno.Constants.ENODEV;
            }

            // ignore events triggered by own process to prevent deadlocks
            if (this.IsProviderEvent(ev))
            {
                return perm ? (int)ProjFS.Constants.PROJFS_ALLOW : 0;
            }

            bool isLink = (ev.Mask & ProjFS.Constants.PROJFS_ONLINK) != 0;
            NotificationType nt;

            if ((ev.Mask & ProjFS.Constants.PROJFS_DELETE_PERM) != 0)
            {
                nt = NotificationType.PreDelete;
            }
            else if ((ev.Mask & ProjFS.Constants.PROJFS_MOVE_PERM) != 0)
            {
                nt = NotificationType.PreRename;
            }
            else if ((ev.Mask & ProjFS.Constants.PROJFS_CLOSE_WRITE) != 0)
            {
                nt = NotificationType.FileModified;
            }
            else if ((ev.Mask & ProjFS.Constants.PROJFS_CREATE) != 0 && !isLink)
            {
                nt = NotificationType.NewFileCreated;
            }
            else if ((ev.Mask & ProjFS.Constants.PROJFS_MOVE) != 0)
            {
                nt = NotificationType.FileRenamed;
            }
            else if ((ev.Mask & ProjFS.Constants.PROJFS_CREATE) != 0 && isLink)
            {
                nt = NotificationType.HardLinkCreated;
            }
            else if ((ev.Mask & ProjFS.Constants.PROJFS_DELETE) != 0)
            {
                nt = NotificationType.FileDeleted;
            }
            else if ((ev.Mask & ProjFS.Constants.PROJFS_OPEN_PERM) != 0)
            {
                nt = NotificationType.PreConvertToFull;
            }
            else
            {
                return 0;
            }

            bool isDirectory = (ev.Mask & ProjFS.Constants.PROJFS_ONDIR) != 0;
            string relativePath = PtrToStringUTF8(ev.Path);
            string relativeDestinationPath = null;

            if (nt == NotificationType.PreRename ||
                nt == NotificationType.FileRenamed ||
                nt == NotificationType.HardLinkCreated)
            {
                relativeDestinationPath = PtrToStringUTF8(ev.TargetPath);
            }

            Result result = this.OnNotifyOperation(
                relativePath: relativePath,
                relativeDestinationPath: relativeDestinationPath,
                isDirectory: isDirectory,
                notificationType: nt);

            int ret = -result.ToErrno();

            if (perm)
            {
                if (ret == 0)
                {
                    ret = (int)ProjFS.Constants.PROJFS_ALLOW;
                }
                else if (ret == -Errno.Constants.EPERM)
                {
                    ret = (int)ProjFS.Constants.PROJFS_DENY;
                }
            }

            return ret;
        }

        private int HandleNotifyEvent(ref ProjFS.Event ev)
        {
            return this.HandleNonProjEvent(ref ev, false);
        }

        private int HandlePermEvent(ref ProjFS.Event ev)
        {
            return this.HandleNonProjEvent(ref ev, true);
        }

        private Result OnNotifyOperation(
            string relativePath,
            string relativeDestinationPath,
            bool isDirectory,
            NotificationType notificationType)
        {
            switch (notificationType)
            {
                case NotificationType.PreDelete:
                    return this.OnPreDelete(relativePath, isDirectory);

                case NotificationType.PreRename:
                    return this.OnPreRename(relativePath, relativeDestinationPath, isDirectory);

                case NotificationType.FileModified:
                    this.OnFileModified(relativePath);
                    return Result.Success;

                case NotificationType.NewFileCreated:
                    this.OnNewFileCreated(relativePath, isDirectory);
                    return Result.Success;

                case NotificationType.FileDeleted:
                    this.OnFileDeleted(relativePath, isDirectory);
                    return Result.Success;

                case NotificationType.FileRenamed:
                    this.OnFileRenamed(relativePath, relativeDestinationPath, isDirectory);
                    return Result.Success;

                case NotificationType.HardLinkCreated:
                    this.OnHardLinkCreated(relativePath, relativeDestinationPath);
                    return Result.Success;

                case NotificationType.PreConvertToFull:
                    return this.OnFilePreConvertToFull(relativePath);
            }

            return Result.ENotYetImplemented;
        }

        private static unsafe class NativeFileWriter
        {
            public static bool TryWrite(int fd, byte[] bytes, uint byteCount)
            {
                 fixed (byte* bytesPtr = bytes)
                 {
                     byte* bytesIndexPtr = bytesPtr;

                     while (byteCount > 0)
                     {
                        long res = Write(fd, bytesIndexPtr, byteCount);
                        if (res == -1)
                        {
                            return false;
                        }

                        bytesIndexPtr += res;
                        byteCount -= (uint)res;
                    }
                }

                return true;
            }

            [DllImport("libc", EntryPoint = "write", SetLastError = true)]
            private static extern long Write(int fd, byte* buf, ulong count);
        }
    }
}
