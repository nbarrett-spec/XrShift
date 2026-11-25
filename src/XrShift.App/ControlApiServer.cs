using System.IO.Pipes;
using System.Security.AccessControl;
using System.Security.Principal;
using XrShift.Scene;

namespace XrShift.App;

/// <summary>
/// Carries Control API lines over a named pipe (<c>\\.\pipe\ArWorkspace.Api</c>). Only started with
/// <c>--api</c>. Only this Windows user may connect, and never from another machine.
/// </summary>
internal sealed class ControlApiServer : IDisposable
{
    const int MaxClients = 8, BufferBytes = 64 * 1024;

    readonly Func<string, (string? Reply, bool Subscribe)> _handle;
    readonly CancellationTokenSource _stop = new();
    readonly List<StreamWriter> _subscribers = [];

    /// <param name="handle">Answers one request line. Called from pipe threads: it must hop to the window thread itself.</param>
    readonly string _pipeName;

    /// <param name="pipeName">A trial window gets a name of its own, so it never takes requests meant for the real app.</param>
    public ControlApiServer(string pipeName, Func<string, (string? Reply, bool Subscribe)> handle)
    {
        _handle = handle;
        _pipeName = pipeName;
        _ = Task.Run(Accept);
        Log.Line($@"Control API listening on \\.\pipe\{pipeName} (this user, this PC only).");
    }

    /// <summary>Sends an event line to everyone who subscribed.</summary>
    public void Publish(string line)
    {
        // Never on the caller's thread: that is the window thread, and a subscriber that has stopped reading
        // must not freeze the app. Chained so events arrive in the order they happened.
        // shortcut: one stuck subscriber with a full buffer delays events to the others; give each its own queue if that ever shows.
        lock (_subscribers) _sending = _sending.ContinueWith(_ => Send(line), TaskScheduler.Default);
    }

    Task _sending = Task.CompletedTask;

    void Send(string line)
    {
        StreamWriter[] to;
        lock (_subscribers) to = [.. _subscribers];
        foreach (StreamWriter writer in to)
        {
            try { lock (writer) writer.WriteLine(line); }
            catch (Exception e) when (e is IOException or ObjectDisposedException) { lock (_subscribers) _subscribers.Remove(writer); }
        }
    }

    async Task Accept()
    {
        var security = new PipeSecurity();
        security.AddAccessRule(new PipeAccessRule(WindowsIdentity.GetCurrent().User!, PipeAccessRights.FullControl, AccessControlType.Allow));
        // Named pipes can be reached over the network by default; this one cannot.
        security.AddAccessRule(new PipeAccessRule(new SecurityIdentifier(WellKnownSidType.NetworkSid, null), PipeAccessRights.FullControl, AccessControlType.Deny));
        try
        {
            while (!_stop.IsCancellationRequested)
            {
                NamedPipeServerStream pipe = NamedPipeServerStreamAcl.Create(_pipeName,PipeDirection.InOut, MaxClients,
                    PipeTransmissionMode.Byte, PipeOptions.Asynchronous, BufferBytes, BufferBytes, security);
                try { await pipe.WaitForConnectionAsync(_stop.Token); }
                catch { pipe.Dispose(); throw; }
                _ = Task.Run(() => Serve(pipe));
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // Most likely all client slots are taken or another copy owns the pipe. The app carries on without the API.
            Log.Line($"Control API stopped: {e.Message}");
        }
    }

    async Task Serve(NamedPipeServerStream pipe)
    {
        var writer = new StreamWriter(pipe) { AutoFlush = true };
        try
        {
            using var reader = new StreamReader(pipe);
            while (await reader.ReadLineAsync(_stop.Token) is { } line)
            {
                if (line.Length == 0) continue;
                (string? reply, bool subscribe) = _handle(line);
                if (subscribe) lock (_subscribers) _subscribers.Add(writer);
                if (reply is not null) lock (writer) writer.WriteLine(reply);
            }
        }
        // The caller hung up, or the session's window has gone: either way this caller is finished.
        catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException or AggregateException) { }
        finally
        {
            lock (_subscribers) _subscribers.Remove(writer);
            pipe.Dispose();
        }
    }

    public void Dispose() => _stop.Cancel();
}

/// <summary>The live session, as the Control API sees it. Every member runs on the window thread.</summary>
internal sealed class ControlHost(Func<Workspace> workspace, Action<Workspace> useWorkspace, Func<Deck> deck, Action<Deck> useDeck, Action<string> focus, Func<string, string, string?> tell, Func<string, Uri, string?> openWidget,
    Func<ScreenSpec, (int Width, int Height)> pictureSize, Func<DisplayOptics> optics,
    Func<string, string?, (System.Text.Json.Nodes.JsonObject? State, string? Refused)> calibrate) : IControlHost
{
    public System.Text.Json.Nodes.JsonObject? Calibrate(string action, string? from, out string? refused)
    {
        (System.Text.Json.Nodes.JsonObject? state, refused) = calibrate(action, from);
        return state;
    }
    /// <summary>The real size of the monitor or window a screen shows, so presets size it as it will be drawn.</summary>
    public (int Width, int Height) PictureSize(ScreenSpec screen) => pictureSize(screen);
    /// <summary>The view angle in use now (the held picture's in stationary mode).</summary>
    public DisplayOptics Optics => optics();
    public string? OpenWidget(string label, Uri page) => openWidget(label, page);
    public IReadOnlyList<string> Projects => [.. XrShift.App.Projects.Load().Keys];
    public string? Spawn(string project) => XrShift.App.Projects.Spawn(project);
    public string? Tell(string id, string text) => tell(id, text);
    public Workspace Workspace => workspace();
    public void Use(Workspace changed) => useWorkspace(changed);
    public Deck Deck => deck();
    public void Use(Deck changed) => useDeck(changed);
    public void Focus(string id) => focus(id);
}
