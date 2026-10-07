// SPDX-License-Identifier: MIT
using System;
using System.ComponentModel;
using System.IO;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;

namespace Astra.Bridge
{
    /// <summary>
    /// The frame ring's bytes, mapped read-only into this process. Two ways, by what the game is:
    /// <list type="bullet">
    /// <item>A WINDOWS game (also under Wine/Proton): kernel32 directly — <c>OpenFileMapping</c> for a
    /// Windows engine's <c>Local\</c> name, <c>CreateFile</c> + <c>CreateFileMapping</c> for a
    /// Linux engine's file seen through Wine's <c>Z:</c> drive. Not <c>MemoryMappedFile</c>: old
    /// Mono builds do not implement opening an existing named mapping.</item>
    /// <item>A native Linux or macOS game: the file through <see cref="MemoryMappedFile"/>.</item>
    /// </list>
    /// </summary>
    sealed unsafe class RingMapping : IDisposable
    {
        public byte* Base { get; private set; }
        public long Length { get; private set; }
        public string Source { get; private set; }

        Action release;

        public static RingMapping Open(string name)
        {
            if (string.IsNullOrEmpty(name)) throw new IOException("the engine named no frame ring");
            bool windows = Environment.OSVersion.Platform == PlatformID.Win32NT;
            if (!windows) return OpenUnixFile(name);
            // A Unix path from a Linux/macOS engine: this game runs under Wine, whose Z: is the root.
            if (name.StartsWith("/", StringComparison.Ordinal)) return OpenWindowsFile("Z:" + name.Replace('/', '\\'));
            if (name.Length > 2 && name[1] == ':') return OpenWindowsFile(name);
            return OpenWindowsNamed(name);
        }

        static RingMapping OpenUnixFile(string path)
        {
            var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            MemoryMappedFile file = null;
            MemoryMappedViewAccessor view = null;
            try
            {
                file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
                view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
                byte* p = null;
                view.SafeMemoryMappedViewHandle.AcquirePointer(ref p);
                var handle = view.SafeMemoryMappedViewHandle;
                MemoryMappedFile f = file;
                MemoryMappedViewAccessor v = view;
                return new RingMapping
                {
                    Base = p + view.PointerOffset,
                    Length = view.Capacity,
                    Source = path,
                    release = () =>
                    {
                        handle.ReleasePointer();
                        v.Dispose();
                        f.Dispose();
                    },
                };
            }
            catch
            {
                view?.Dispose();
                if (file != null) file.Dispose();
                else stream.Dispose();
                throw;
            }
        }

        static RingMapping OpenWindowsFile(string path)
        {
            IntPtr file = Win.CreateFileW(path, Win.GENERIC_READ, Win.FILE_SHARE_READ | Win.FILE_SHARE_WRITE | Win.FILE_SHARE_DELETE,
                IntPtr.Zero, Win.OPEN_EXISTING, 0, IntPtr.Zero);
            if (file == Win.INVALID_HANDLE_VALUE) throw Win.Error($"cannot open {path}");
            try
            {
                if (!Win.GetFileSizeEx(file, out long size)) throw Win.Error($"cannot size {path}");
                IntPtr mapping = Win.CreateFileMappingW(file, IntPtr.Zero, Win.PAGE_READONLY, 0, 0, null);
                if (mapping == IntPtr.Zero) throw Win.Error($"cannot map {path}");
                return View(mapping, size, path);
            }
            finally
            {
                Win.CloseHandle(file); // the mapping keeps the file alive
            }
        }

        static RingMapping OpenWindowsNamed(string name)
        {
            IntPtr mapping = Win.OpenFileMappingW(Win.FILE_MAP_READ, false, name);
            if (mapping == IntPtr.Zero) throw Win.Error($"cannot open the mapping {name}");
            return View(mapping, -1, name);
        }

        /// <summary>Map a whole view of <paramref name="mapping"/> (closed on failure); <paramref name="size"/>
        /// is the mapped object's size, or -1 to take the view's region size.</summary>
        static RingMapping View(IntPtr mapping, long size, string source)
        {
            IntPtr view = Win.MapViewOfFile(mapping, Win.FILE_MAP_READ, 0, 0, UIntPtr.Zero);
            if (view == IntPtr.Zero)
            {
                var e = Win.Error($"cannot view {source}");
                Win.CloseHandle(mapping);
                throw e;
            }
            if (size < 0)
            {
                Win.VirtualQuery(view, out var info, (UIntPtr)Marshal.SizeOf(typeof(Win.MemoryBasicInformation)));
                size = (long)info.RegionSize.ToUInt64();
            }
            return new RingMapping
            {
                Base = (byte*)view,
                Length = size,
                Source = source,
                release = () =>
                {
                    Win.UnmapViewOfFile(view);
                    Win.CloseHandle(mapping);
                },
            };
        }

        public void Dispose()
        {
            var r = release;
            release = null;
            Base = null;
            r?.Invoke();
        }

        static class Win
        {
            public const uint GENERIC_READ = 0x80000000;
            public const uint FILE_SHARE_READ = 1, FILE_SHARE_WRITE = 2, FILE_SHARE_DELETE = 4;
            public const uint OPEN_EXISTING = 3;
            public const uint PAGE_READONLY = 0x02;
            public const uint FILE_MAP_READ = 0x0004;
            public static readonly IntPtr INVALID_HANDLE_VALUE = new IntPtr(-1);

            [StructLayout(LayoutKind.Sequential)]
            public struct MemoryBasicInformation
            {
                public IntPtr BaseAddress;
                public IntPtr AllocationBase;
                public uint AllocationProtect;
                public UIntPtr RegionSize;
                public uint State;
                public uint Protect;
                public uint Type;
            }

            [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr security, uint disposition, uint flags, IntPtr template);

            [DllImport("kernel32", SetLastError = true)]
            public static extern bool GetFileSizeEx(IntPtr file, out long size);

            [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr CreateFileMappingW(IntPtr file, IntPtr security, uint protect, uint sizeHigh, uint sizeLow, string name);

            [DllImport("kernel32", SetLastError = true, CharSet = CharSet.Unicode)]
            public static extern IntPtr OpenFileMappingW(uint access, bool inherit, string name);

            [DllImport("kernel32", SetLastError = true)]
            public static extern IntPtr MapViewOfFile(IntPtr mapping, uint access, uint offsetHigh, uint offsetLow, UIntPtr bytes);

            [DllImport("kernel32", SetLastError = true)]
            public static extern bool UnmapViewOfFile(IntPtr view);

            [DllImport("kernel32", SetLastError = true)]
            public static extern UIntPtr VirtualQuery(IntPtr address, out MemoryBasicInformation info, UIntPtr length);

            [DllImport("kernel32", SetLastError = true)]
            public static extern bool CloseHandle(IntPtr handle);

            public static IOException Error(string what) =>
                new IOException($"{what}: {new Win32Exception(Marshal.GetLastWin32Error()).Message}");
        }
    }
}
