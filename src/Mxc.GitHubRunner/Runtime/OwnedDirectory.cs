using System.ComponentModel;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace Mxc.GitHubRunner.Runtime;

public static class OwnedDirectory
{
    public static void AssertNoReparsePoints(string path)
    {
        for (var current = new DirectoryInfo(Path.GetFullPath(path)); current is not null; current = current.Parent)
            if (current.LinkTarget is not null || (current.Exists && (current.Attributes & FileAttributes.ReparsePoint) != 0))
                throw new IOException($"Reparse points are not allowed in the state path: {current.FullName}");
    }

    public static void CopyTree(string source, string destination, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AssertNoReparsePoints(source);
        AssertNoReparsePoints(destination);
        if ((File.GetAttributes(source) & FileAttributes.ReparsePoint) != 0)
            throw new IOException("The runner package contains a reparse point.");
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.EnumerateFiles(source))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ((File.GetAttributes(file) & FileAttributes.ReparsePoint) != 0)
                throw new IOException("The runner package contains a reparse point.");
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: false);
        }
        foreach (var child in Directory.EnumerateDirectories(source))
            CopyTree(child, Path.Combine(destination, Path.GetFileName(child)), cancellationToken);
    }

    public static void DeleteRun(string runsDirectory, string runDirectory)
    {
        var parent = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runsDirectory));
        var target = Path.TrimEndingDirectorySeparator(Path.GetFullPath(runDirectory));
        if (!string.Equals(Path.GetDirectoryName(target), parent, StringComparison.OrdinalIgnoreCase)
            || !Guid.TryParseExact(Path.GetFileName(target), "N", out _))
            throw new IOException("Refusing to delete a directory outside this application's run directory.");
        AssertNoReparsePoints(parent);
        if (Directory.Exists(target) || new DirectoryInfo(target).LinkTarget is not null) DeleteTree(target);
    }

    private static void DeleteTree(string directory)
    {
        if ((File.GetAttributes(directory) & FileAttributes.ReparsePoint) != 0)
        {
            Directory.Delete(directory);
            return;
        }
        foreach (var file in Directory.EnumerateFiles(directory))
        {
            var attributes = File.GetAttributes(file);
            // Reparse-point attributes belong to the link. Ordinary hard links
            // share attributes with their targets, so check them before mutation.
            if ((attributes & FileAttributes.ReparsePoint) == 0 && (attributes & FileAttributes.ReadOnly) != 0)
            {
                if (OperatingSystem.IsWindows() && HasMultipleHardLinks(file))
                    throw new IOException("Refusing to change attributes on a read-only hard link during cleanup.");
                File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
            }
            File.Delete(file);
        }
        foreach (var child in Directory.EnumerateDirectories(directory)) DeleteTree(child);
        Directory.Delete(directory);
    }

    private static bool HasMultipleHardLinks(string path)
    {
        using var handle = File.OpenHandle(path, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        if (!GetFileInformationByHandle(handle, out var information))
            throw new IOException("Cannot inspect file links before cleanup.", new Win32Exception(Marshal.GetLastWin32Error()));
        return information.NumberOfLinks > 1;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out FileInformation information);

    [StructLayout(LayoutKind.Sequential)]
    private struct FileInformation
    {
        public uint Attributes;
        public System.Runtime.InteropServices.ComTypes.FILETIME CreationTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastAccessTime;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }
}
