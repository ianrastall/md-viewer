using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace MdViewer.Interop;

public sealed class NativeCoreException(string message) : Exception(message);

/// <summary>Declarations and plumbing for the C ABI in native/include/mdv_native.h.</summary>
internal static unsafe partial class Native
{
    private const string Library = "mdv_native";
    private const int ExpectedAbi = 3;
    private const int Ok = 0, Cancelled = 2;
    private static int abiChecked;

    [StructLayout(LayoutKind.Sequential)]
    internal struct Result
    {
        public int Status;
        public int Reserved;
        public byte* Data;
        public nuint Size;
    }

    [LibraryImport(Library, EntryPoint = "mdv_abi_version")] private static partial int AbiVersion();
    [LibraryImport(Library, EntryPoint = "mdv_result_free")] private static partial void Free(Result* result);
    [LibraryImport(Library, EntryPoint = "mdv_configure", StringMarshalling = StringMarshalling.Utf8)] internal static partial void Configure(string dataDirectory);
    [LibraryImport(Library, EntryPoint = "mdv_file_types")] internal static partial Result* FileTypes();
    [LibraryImport(Library, EntryPoint = "mdv_parse")] internal static partial Result* Parse(byte* utf8, nuint length);
    [LibraryImport(Library, EntryPoint = "mdv_reflow_headings")] internal static partial Result* Reflow(byte* utf8, nuint length);
    [LibraryImport(Library, EntryPoint = "mdv_open", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial Result* Open(string path, delegate* unmanaged[Cdecl]<nint, byte*, int> progress, nint context);
    [LibraryImport(Library, EntryPoint = "mdv_save", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial Result* Save(string path, byte* utf8, nuint length, string encoding);
    [LibraryImport(Library, EntryPoint = "mdv_pandoc_path")] internal static partial Result* PandocPath();
    [LibraryImport(Library, EntryPoint = "mdv_pandoc_format")]
    internal static partial Result* PandocFormat(byte* utf8, nuint length, delegate* unmanaged[Cdecl]<nint, byte*, int> progress, nint context);
    [LibraryImport(Library, EntryPoint = "mdv_pandoc_export", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial Result* PandocExport(byte* utf8, nuint length, string target, string? resourceDirectory, delegate* unmanaged[Cdecl]<nint, byte*, int> progress, nint context);
    [LibraryImport(Library, EntryPoint = "mdv_configure_ocr", StringMarshalling = StringMarshalling.Utf8)] internal static partial void ConfigureOcr(string engine, string languages);
    [LibraryImport(Library, EntryPoint = "mdv_tools_status")]
    internal static partial Result* ToolsStatus(int checkLatest, delegate* unmanaged[Cdecl]<nint, byte*, int> progress, nint context);
    [LibraryImport(Library, EntryPoint = "mdv_tool_install", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial Result* ToolInstall(string tool, delegate* unmanaged[Cdecl]<nint, byte*, int> progress, nint context);
    [LibraryImport(Library, EntryPoint = "mdv_crawl", StringMarshalling = StringMarshalling.Utf8)]
    internal static partial Result* Crawl(string startUrl, int maxPages, delegate* unmanaged[Cdecl]<nint, byte*, int> progress, nint context);

    /// <summary>Checks the ABI once and translates loader failures into a message for the user.</summary>
    internal static T Call<T>(Func<T> call)
    {
        try
        {
            if (Volatile.Read(ref abiChecked) == 0)
            {
                var abi = AbiVersion();
                if (abi != ExpectedAbi) throw new NativeCoreException($"The C++ core has ABI {abi}; this build expects {ExpectedAbi}. Rebuild the application.");
                Volatile.Write(ref abiChecked, 1);
            }
            return call();
        }
        catch (DllNotFoundException) { throw new NativeCoreException("The C++ core (mdv_native.dll or pdfium.dll) is missing. Run scripts\\build.ps1 to build it."); }
        catch (BadImageFormatException) { throw new NativeCoreException("The C++ core must match the application's x64 architecture."); }
        catch (EntryPointNotFoundException) { throw new NativeCoreException("The C++ core has an incompatible ABI. Rebuild the application."); }
    }

    /// <summary>Hands the payload to <paramref name="read"/>, frees the result, and turns errors and cancellation into exceptions.</summary>
    internal static T Take<T>(Result* result, SpanReader<T> read)
    {
        if (result == null) throw new NativeCoreException("The C++ core ran out of memory.");
        try
        {
            var payload = new ReadOnlySpan<byte>(result->Data, checked((int)result->Size));
            return result->Status switch
            {
                Ok => read(payload),
                Cancelled => throw new OperationCanceledException(),
                _ => throw new NativeCoreException(Encoding.UTF8.GetString(payload))
            };
        }
        finally { Free(result); }
    }

    internal delegate T SpanReader<T>(ReadOnlySpan<byte> payload);

    internal static string Text(Result* result) => Take(result, static payload => Encoding.UTF8.GetString(payload));

    /// <summary>Runs a native call with a progress/cancellation callback bound to this operation.</summary>
    internal static T WithProgress<T>(IProgress<string>? progress, CancellationToken cancellation, Func<nint, nint, T> call)
    {
        var handle = GCHandle.Alloc(new Operation(progress, cancellation));
        try { return call((nint)(delegate* unmanaged[Cdecl]<nint, byte*, int>)&OnProgress, GCHandle.ToIntPtr(handle)); }
        finally { handle.Free(); }
    }

    internal static delegate* unmanaged[Cdecl]<nint, byte*, int> Callback(nint pointer) => (delegate* unmanaged[Cdecl]<nint, byte*, int>)pointer;

    private sealed record Operation(IProgress<string>? Progress, CancellationToken Cancellation);

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static int OnProgress(nint context, byte* message)
    {
        try
        {
            var operation = (Operation)GCHandle.FromIntPtr(context).Target!;
            if (message != null) operation.Progress?.Report(Marshal.PtrToStringUTF8((nint)message) ?? "");
            return operation.Cancellation.IsCancellationRequested ? 1 : 0;
        }
        catch (Exception) { return 1; }  // never let an exception cross into C++
    }

    /// <summary>Pins UTF-8 text (which may contain NUL) for a native call.</summary>
    internal static T WithText<T>(string text, Func<nint, nuint, T> call)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        fixed (byte* pointer = bytes) return call((nint)pointer, (nuint)bytes.Length);
    }

    /// <summary>Runs blocking native work off the UI thread, on a thread with a generous stack for deep documents.</summary>
    internal static Task<T> RunAsync<T>(Func<T> work)
    {
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.SetResult(work()); }
            catch (Exception ex) { completion.SetException(ex); }
        }, 64 * 1024 * 1024) { IsBackground = true, Name = "md-viewer native" };
        thread.Start();
        return completion.Task;
    }
}
