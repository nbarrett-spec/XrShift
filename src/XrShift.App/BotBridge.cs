using System.ComponentModel;
using System.Diagnostics;
using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Nodes;
using XrShift.Scene;

namespace XrShift.App;

/// <summary>What the bot deck needs to know about other programs: whether one is still running, and where its window is.</summary>
internal static class BotProcess
{
    // Programs that only pass a command along; the session is whatever started them.
    static readonly string[] Shells = ["cmd", "powershell", "pwsh", "bash", "sh", "conhost", "xrshift"];

    public static bool Alive(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return false; }
        catch (Win32Exception) { return true; } // it is there; we are just not allowed to look
    }

    // shortcut: Windows reuses process numbers, so a long-dead parent's number can belong to a stranger; compare start times if that ever bites.
    public static int? ParentOf(int pid)
    {
        try
        {
            using Process process = Process.GetProcessById(pid);
            var info = new ProcessBasicInformation();
            return NtQueryInformationProcess(process.Handle, 0, ref info, Marshal.SizeOf<ProcessBasicInformation>(), out _) == 0
                ? (int)info.InheritedFromUniqueProcessId
                : null;
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or Win32Exception) { return null; }
    }

    /// <summary>The program that started this one, looking past any shells in between: for a hook, the Claude Code session.</summary>
    public static int? SessionAncestor()
    {
        int? pid = ParentOf(Environment.ProcessId);
        for (int depth = 0; depth < 6 && pid is { } at; depth++)
        {
            try
            {
                using Process process = Process.GetProcessById(at);
                if (!Shells.Contains(process.ProcessName, StringComparer.OrdinalIgnoreCase)) return at;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return null; }
            pid = ParentOf(at);
        }
        return null;
    }

    /// <summary>The window a process lives in: its own, or the nearest one above it (a terminal, say).</summary>
    public static IntPtr WindowOf(int pid)
    {
        int? at = pid;
        for (int depth = 0; depth < 8 && at is { } current; depth++)
        {
            try
            {
                using Process process = Process.GetProcessById(current);
                if (process.MainWindowHandle != IntPtr.Zero) return process.MainWindowHandle;
            }
            catch (Exception e) when (e is ArgumentException or InvalidOperationException) { return IntPtr.Zero; }
            at = ParentOf(current);
        }
        return IntPtr.Zero;
    }

    [StructLayout(LayoutKind.Sequential)]
    struct ProcessBasicInformation
    {
        public IntPtr ExitStatus, PebBaseAddress, AffinityMask, BasePriority, UniqueProcessId, InheritedFromUniqueProcessId;
    }

    [DllImport("ntdll.dll")]
    static extern int NtQueryInformationProcess(IntPtr process, int kind, ref ProcessBasicInformation info, int size, out int returned);
}

/// <summary>
/// <c>XrShift --bot-hook</c>: what a Claude Code hook runs. It reads the hook's JSON from standard input, passes
/// the event to the running app and exits. It must never slow a session down, fail it or print anything (Claude
/// Code reads what some hooks print), so every problem ends quietly, and an app that is not running is not a problem.
/// </summary>
internal static class BotHook
{
    public static int Run()
    {
        try
        {
            using Stream input = Console.OpenStandardInput();
            if (JsonNode.Parse(input) is not JsonObject hook) return 0;
            string? Text(string key) => hook[key] is JsonValue v && v.TryGetValue(out string? text) ? text : null;
            if (Text("session_id") is not { Length: > 0 } session || Text("hook_event_name") is not { Length: > 0 } hookEvent) return 0;

            // The folder a session works in is the name people know it by.
            string? folder = Text("cwd") is { Length: > 0 } cwd ? Path.GetFileName(cwd.TrimEnd('\\', '/')) : null;
            var request = new JsonObject
            {
                ["jsonrpc"] = "2.0",
                ["method"] = "bot.report",
                ["params"] = new JsonObject { ["id"] = session, ["event"] = hookEvent, ["label"] = folder, ["pid"] = BotProcess.SessionAncestor() },
            };

            using var pipe = new NamedPipeClientStream(".", ControlApi.PipeName, PipeDirection.InOut);
            pipe.Connect(200);
            using var writer = new StreamWriter(pipe);
            writer.WriteLine(request.ToJsonString());
            writer.Flush();
            pipe.WaitForPipeDrain();
        }
        catch (Exception e) when (e is IOException or TimeoutException or JsonException or UnauthorizedAccessException or InvalidOperationException) { }
        return 0;
    }
}
