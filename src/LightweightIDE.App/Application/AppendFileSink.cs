using System.Runtime.InteropServices;
using System.Text;
using Serilog.Core;
using Serilog.Events;
using Serilog.Formatting;

namespace LightweightIDE.App.Application;

/// <summary>
/// Daily log file (<c>ide-yyyyMMdd.log</c>) that several IDE processes can share. The file is opened with
/// <c>O_APPEND</c> and every event goes out in one <c>write(2)</c>, so the kernel appends each line atomically at the
/// current end of the file. Serilog's file sink keeps its own file position (and its <c>shared</c> mode still lost
/// and cut lines on Linux): a second window overwrote the lines of the first.
/// </summary>
sealed partial class AppendFileSink(string directory, ITextFormatter formatter, int retainedDays) : ILogEventSink, IDisposable
{
    const int O_WRONLY = 0x1, O_CREAT = 0x40, O_APPEND = 0x400, O_CLOEXEC = 0x80000;

    readonly Lock gate = new();
    int fd = -1;
    DateOnly day;

    public void Emit(LogEvent logEvent)
    {
        var text = new StringWriter();
        formatter.Format(logEvent, text);
        var bytes = Encoding.UTF8.GetBytes(text.ToString());
        lock (gate)
        {
            var today = DateOnly.FromDateTime(logEvent.Timestamp.LocalDateTime);
            if (fd < 0 || today != day) Open(today);
            if (fd < 0) return;
            WriteAll(bytes);
        }
    }

    unsafe void WriteAll(byte[] bytes)
    {
        fixed (byte* p = bytes)
        {
            var done = 0;
            while (done < bytes.Length)
            {
                var n = Write(fd, p + done, (nuint)(bytes.Length - done));
                if (n < 0)
                {
                    if (Marshal.GetLastPInvokeError() == 4) continue; // EINTR
                    return;
                }
                done += (int)n;
            }
        }
    }

    void Open(DateOnly today)
    {
        if (fd >= 0) Close(fd);
        day = today;
        Directory.CreateDirectory(directory);
        fd = OpenFile(Path.Combine(directory, $"ide-{today:yyyyMMdd}.log"), O_WRONLY | O_CREAT | O_APPEND | O_CLOEXEC, 0x1A4 /* 0644 */);
        DeleteOld(today);
    }

    void DeleteOld(DateOnly today)
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(directory, "ide-*.log"))
            {
                var name = Path.GetFileNameWithoutExtension(file);
                if (DateOnly.TryParseExact(name[4..], "yyyyMMdd", out var date) && date < today.AddDays(-retainedDays + 1))
                    File.Delete(file);
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    public void Dispose()
    {
        lock (gate)
        {
            if (fd >= 0) Close(fd);
            fd = -1;
        }
    }

    [LibraryImport("libc", EntryPoint = "open", StringMarshalling = StringMarshalling.Utf8, SetLastError = true)]
    private static partial int OpenFile(string path, int flags, int mode);

    [LibraryImport("libc", EntryPoint = "write", SetLastError = true)]
    private static unsafe partial nint Write(int fd, byte* buffer, nuint count);

    [LibraryImport("libc", EntryPoint = "close")]
    private static partial int Close(int fd);
}
