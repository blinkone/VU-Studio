using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using VuStudio.Core.Providers;

namespace VuStudio.Sources.Windows.HwInfo;

internal readonly record struct HwInfoOpenResult(IHwInfoMemory? Memory, LocalizedText? Failure);

internal delegate HwInfoOpenResult HwInfoOpener();

/// <summary>HWiNFO's shared memory as mapped by Windows. Opened for each poll so no handle can keep an orphan alive.</summary>
[SupportedOSPlatform("windows")]
internal sealed class HwInfoWindowsMemory : IHwInfoMemory
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private readonly Mutex? _mutex;

    private HwInfoWindowsMemory(MemoryMappedFile file, MemoryMappedViewAccessor view, Mutex? mutex)
    {
        _file = file;
        _view = view;
        _mutex = mutex;
    }

    public long Length => _view.Capacity;

    public static HwInfoOpenResult Open()
    {
        MemoryMappedFile file;
        try
        {
            file = MemoryMappedFile.OpenExisting(HwInfoLayout.MappingName, MemoryMappedFileRights.Read);
        }
        catch (Exception ex) when (ex is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return new(null, HwInfoMessages.NotRunning);
        }
        catch (UnauthorizedAccessException)
        {
            return new(null, HwInfoMessages.AccessDenied);
        }

        MemoryMappedViewAccessor view;
        try
        {
            view = file.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            file.Dispose();
            return new(null, HwInfoMessages.AccessDenied);
        }

        Mutex? mutex = null;
        try
        {
            if (Mutex.TryOpenExisting(HwInfoLayout.MutexName, out var opened))
            {
                mutex = opened;
            }
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or WaitHandleCannotBeOpenedException or IOException)
        {
            // HWiNFO runs elevated and the mutex DACL may deny us: the header re-check keeps reads safe anyway.
        }

        return new(new HwInfoWindowsMemory(file, view, mutex), null);
    }

    public void Read(long position, byte[] destination, int count) => _view.ReadArray(position, destination, 0, count);

    public IDisposable AcquireLock()
    {
        if (_mutex is null)
        {
            return NoLock.Instance;
        }

        try
        {
            return _mutex.WaitOne(1000) ? new Lease(_mutex) : NoLock.Instance;
        }
        catch (AbandonedMutexException)
        {
            return new Lease(_mutex); // the previous owner died; we own it now
        }
        catch (Exception ex) when (ex is ObjectDisposedException or UnauthorizedAccessException)
        {
            return NoLock.Instance;
        }
    }

    public void Dispose()
    {
        _mutex?.Dispose();
        _view.Dispose();
        _file.Dispose();
    }

    /// <summary>The system ANSI code page (not the UI culture), used for the pre-UTF-8 string fields.</summary>
    public static Encoding AnsiEncoding()
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding((int)GetACP());
        }
        catch (Exception)
        {
            return Encoding.Latin1;
        }
    }

    [DllImport("kernel32.dll")]
    private static extern uint GetACP();

    private sealed class NoLock : IDisposable
    {
        public static readonly NoLock Instance = new();

        public void Dispose()
        {
        }
    }

    private sealed class Lease(Mutex mutex) : IDisposable
    {
        public void Dispose()
        {
            try
            {
                mutex.ReleaseMutex();
            }
            catch (Exception ex) when (ex is ApplicationException or UnauthorizedAccessException or ObjectDisposedException)
            {
            }
        }
    }
}
