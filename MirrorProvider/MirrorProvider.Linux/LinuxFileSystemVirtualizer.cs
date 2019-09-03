using MirrorProvider.POSIX;
using PrjFSLib.Linux;
using System;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace MirrorProvider.Linux
{
    public class LinuxFileSystemVirtualizer : FileSystemVirtualizer
    {
        private VirtualizationInstance virtualizationInstance = new VirtualizationInstance();

        protected override StringComparison PathComparison => StringComparison.Ordinal;

        public override bool TryConvertVirtualizationRoot(string directory, out string error)
        {
            error = null;
            return true;
        }

        public override bool TryStartVirtualizationInstance(Enlistment enlistment, out string error)
        {
            string storageRoot = Path.Combine(enlistment.DotMirrorRoot, "lower");

            Directory.CreateDirectory(storageRoot);

            this.virtualizationInstance.OnEnumerateDirectory = this.OnEnumerateDirectory;
            this.virtualizationInstance.OnGetFileStream = this.OnGetFileStream;
            this.virtualizationInstance.OnLogError = this.OnLogError;
            this.virtualizationInstance.OnLogWarning = this.OnLogWarning;
            this.virtualizationInstance.OnLogInfo = this.OnLogInfo;
            this.virtualizationInstance.OnFileModified = this.OnFileModified;
            this.virtualizationInstance.OnPreDelete = this.OnPreDelete;
            this.virtualizationInstance.OnNewFileCreated = this.OnNewFileCreated;
            this.virtualizationInstance.OnFileRenamed = this.OnFileRenamed;
            this.virtualizationInstance.OnHardLinkCreated = this.OnHardLinkCreated;
            this.virtualizationInstance.OnFilePreConvertToFull = this.OnFilePreConvertToFull;

            Result result = this.virtualizationInstance.StartVirtualizationInstance(
                storageRoot,
                enlistment.SrcRoot,
                poolThreadCount: (uint)Environment.ProcessorCount * 2,
                initializeStorageRoot: IsDirectoryEmpty(storageRoot));

            if (result == Result.Success)
            {
                return base.TryStartVirtualizationInstance(enlistment, out error);
            }
            else
            {
                error = result.ToString();
                return false;
            }
        }

        public override void Stop()
        {
            this.virtualizationInstance.StopVirtualizationInstance();
        }

        private bool IsDirectoryEmpty(string dir)
        {
            return !this.virtualizationInstance.EnumerateFileSystemEntries(dir).Any();
        }

        private Result OnEnumerateDirectory(
            ulong commandId,
            string relativePath,
            int triggeringProcessId,
            string triggeringProcessName)
        {
            Console.WriteLine($"OnEnumerateDirectory({commandId}, '{relativePath}', {triggeringProcessId}, {triggeringProcessName})");

            try
            {
                if (!this.DirectoryExists(relativePath))
                {
                    return Result.EFileNotFound;
                }

                foreach (ProjectedFileInfo child in this.GetChildItems(relativePath))
                {
                    if (child.Type == ProjectedFileInfo.FileType.Directory)
                    {
                        Result result = this.virtualizationInstance.WritePlaceholderDirectory(
                            Path.Combine(relativePath, child.Name));

                        if (result != Result.Success)
                        {
                            Console.WriteLine($"WritePlaceholderDirectory failed: {result}");
                            return result;
                        }
                    }
                    else if (child.Type == ProjectedFileInfo.FileType.SymLink)
                    {
                        string childRelativePath = Path.Combine(relativePath, child.Name);

                        string symLinkTarget;
                        if (this.TryGetSymLinkTarget(childRelativePath, out symLinkTarget))
                        {
                            Result result = this.virtualizationInstance.WriteSymLink(
                                childRelativePath,
                                symLinkTarget);

                            if (result != Result.Success)
                            {
                                Console.WriteLine($"WriteSymLink failed: {result}");
                                return result;
                            }
                        }
                        else
                        {
                            return Result.EIOError;
                        }
                    }
                    else
                    {
                        string childRelativePath = Path.Combine(relativePath, child.Name);
                        int statResult = PrjFSLib.Linux.LinuxNative.LStat(this.GetFullPathInMirror(childRelativePath), out PrjFSLib.Linux.LinuxNative.StatBuffer statBuffer);
                        if (statResult == -1)
                        {
                            Console.WriteLine($"NativeMethods.LStat failed: {Marshal.GetLastWin32Error()}");
                            return Result.EIOError;
                        }
                        uint fileMode = statBuffer.Mode & 0xFFF;  // 07777 = ALLPERMS

                        Result result = this.virtualizationInstance.WritePlaceholderFile(
                            childRelativePath,
                            providerId: ToVersionIdByteArray(1),
                            contentId: ToVersionIdByteArray(0),
                            fileSize: (ulong)child.Size,
                            fileMode: fileMode);
                        if (result != Result.Success)
                        {
                            Console.WriteLine($"WritePlaceholderFile failed: {result}");
                            return result;
                        }
                    }
                }
            }
            catch (IOException e)
            {
                Console.WriteLine($"IOException in OnEnumerateDirectory: {e.Message}");
                return Result.EIOError;
            }

            return Result.Success;
        }

        private Result OnGetFileStream(
            ulong commandId,
            string relativePath,
            byte[] providerId,
            byte[] contentId,
            int triggeringProcessId,
            string triggeringProcessName,
            int fd)
        {
            Console.WriteLine($"OnGetFileStream({commandId}, '{relativePath}', {contentId.Length}/{contentId[0]}:{contentId[1]}, {providerId.Length}/{providerId[0]}:{providerId[1]}, {triggeringProcessId}, {triggeringProcessName}, {fd})");

            if (!this.FileExists(relativePath))
            {
                return Result.EFileNotFound;
            }

            try
            {
                const int bufferSize = 4096;
                FileSystemResult hydrateFileResult = this.HydrateFile(
                    relativePath,
                    bufferSize,
                    (buffer, bytesToCopy) =>
                    {
                        Result result = this.virtualizationInstance.WriteFileContents(
                            fd,
                            buffer,
                            (uint)bytesToCopy);
                        if (result != Result.Success)
                        {
                            Console.WriteLine($"WriteFileContents failed: {result}");
                            return false;
                        }

                        return true;
                    });

                if (hydrateFileResult != FileSystemResult.Success)
                {
                    return Result.EIOError;
                }
            }
            catch (IOException e)
            {
                Console.WriteLine($"IOException in OnGetFileStream: {e.Message}");
                return Result.EIOError;
            }

            return Result.Success;
        }

        private void OnLogError(string errorMessage)
        {
            Console.WriteLine($"OnLogError: {errorMessage}");
        }

        private void OnLogWarning(string warningMessage)
        {
            Console.WriteLine($"OnLogWarning: {warningMessage}");
        }

        private void OnLogInfo(string infoMessage)
        {
            Console.WriteLine($"OnLogInfo: {infoMessage}");
        }

        private void OnFileModified(string relativePath)
        {
            Console.WriteLine($"OnFileModified: {relativePath}");
        }

        private Result OnPreDelete(string relativePath, bool isDirectory)
        {
            Console.WriteLine($"OnPreDelete (isDirectory: {isDirectory}): {relativePath}");
            return Result.Success;
        }

        private void OnNewFileCreated(string relativePath, bool isDirectory)
        {
            Console.WriteLine($"OnNewFileCreated (isDirectory: {isDirectory}): {relativePath}");
        }

        private void OnFileRenamed(string relativeDestinationPath, bool isDirectory)
        {
            Console.WriteLine($"OnFileRenamed (isDirectory: {isDirectory}) destination: {relativeDestinationPath}");
        }

        private void OnHardLinkCreated(string relativeNewLinkPath)
        {
            Console.WriteLine($"OnHardLinkCreated: {relativeNewLinkPath}");
        }

        private Result OnFilePreConvertToFull(string relativePath)
        {
            Console.WriteLine($"OnFilePreConvertToFull: {relativePath}");
            return Result.Success;
        }

        private bool TryGetSymLinkTarget(string relativePath, out string symLinkTarget)
        {
            string fullPathInMirror = this.GetFullPathInMirror(relativePath);
            symLinkTarget = LinuxNative.ReadLink(fullPathInMirror, out int error);
            if (symLinkTarget == null)
            {
                Console.WriteLine($"GetSymLinkTarget failed: {error}");
                return false;
            }

            if (symLinkTarget.StartsWith(this.Enlistment.MirrorRoot, PathComparison))
            {
                // Link target is an absolute path inside the MirrorRoot.
                // The target needs to be adjusted to point inside the src root
                symLinkTarget = Path.Combine(
                    this.Enlistment.SrcRoot.TrimEnd(Path.DirectorySeparatorChar),
                    symLinkTarget.Substring(this.Enlistment.MirrorRoot.Length).TrimStart(Path.DirectorySeparatorChar));
            }

            return true;
        }

        private static byte[] ToVersionIdByteArray(byte version)
        {
            byte[] bytes = new byte[VirtualizationInstance.PlaceholderIdLength];
            bytes[0] = version;

            return bytes;
        }
    }
}
