using System;
using System.IO;
using System.IO.Pipes;
using System.Security.Principal;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace TrayTrigger.Services;

/// <summary>
/// How a second <c>TrayTrigger.exe --play ...</c> reaches the one already running: a named pipe only the same Windows
/// user can open. The running copy listens (<see cref="Start"/>), the second copy sends one line (<see cref="TrySend"/>)
/// and exits. The line is a <see cref="PlayRequest"/>; the listener starts nothing itself, it only raises <see cref="Received"/>.
/// </summary>
public sealed class PlayRequestChannel : IDisposable
{
    private readonly string _pipeName;
    private readonly CancellationTokenSource _stop = new();
    private Task? _loop;

    /// <summary>Raised on a background thread for each request that arrives.</summary>
    public event Action<PlayRequest>? Received;

    /// <summary>Per Windows user, so two people signed in on one PC each reach their own TrayTrigger.</summary>
    public static string DefaultPipeName =>
        "TrayTrigger.Play." + (WindowsIdentity.GetCurrent().User?.Value ?? Environment.UserName);

    public PlayRequestChannel(string? pipeName = null) => _pipeName = pipeName ?? DefaultPipeName;

    public void Start() => _loop ??= Task.Run(() => ListenAsync(_stop.Token));

    private async Task ListenAsync(CancellationToken stop)
    {
        while (!stop.IsCancellationRequested)
        {
            try
            {
                await using var server = new NamedPipeServerStream(_pipeName, PipeDirection.In, 1, PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(stop).ConfigureAwait(false);

                // A client that connects and says nothing is let go after a moment, so it cannot hold the listener.
                using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(stop);
                readTimeout.CancelAfter(TimeSpan.FromSeconds(3));
                using var reader = new StreamReader(server, Encoding.UTF8, detectEncodingFromByteOrderMarks: false, bufferSize: 1024, leaveOpen: true);
                string? line = await ReadBoundedLineAsync(reader, PlayRequest.MaxQueryLength + 4, readTimeout.Token).ConfigureAwait(false);

                if (line != null && PlayRequest.TryParseLine(line, out var request))
                {
                    try { Received?.Invoke(request); }
                    catch (Exception ex) { LoggingService.Swallowed("PlayRequest", ex, "handling a play request"); }
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested)
            {
                break; // TrayTrigger is closing: stop listening
            }
            catch (OperationCanceledException)
            {
                // The client said nothing in time: listen again.
            }
            catch (Exception ex)
            {
                LoggingService.Swallowed("PlayRequest", ex, "listening for a play request");
                try { await Task.Delay(TimeSpan.FromMilliseconds(500), stop).ConfigureAwait(false); }
                catch (OperationCanceledException) { break; /* closing during the pause: stop listening */ }
            }
        }
    }

    /// <summary>
    /// Reads one line of at most <paramref name="maxLength"/> characters. A longer line, or none, is null, and the rest of
    /// it is never read into memory.
    /// </summary>
    internal static async Task<string?> ReadBoundedLineAsync(TextReader reader, int maxLength, CancellationToken cancel)
    {
        var text = new StringBuilder();
        var one = new char[1];
        while (await reader.ReadAsync(one.AsMemory(), cancel).ConfigureAwait(false) == 1)
        {
            if (one[0] == '\n') return text.ToString().TrimEnd('\r');
            if (text.Length >= maxLength) return null;
            text.Append(one[0]);
        }
        return text.Length > 0 ? text.ToString() : null;
    }

    /// <summary>
    /// Sends the request to the TrayTrigger that is running, waiting up to <paramref name="timeout"/> for it to be listening
    /// (a copy that has only just started has not opened the pipe yet). False when none answered.
    /// </summary>
    public static bool TrySend(PlayRequest request, TimeSpan timeout, string? pipeName = null)
    {
        try
        {
            using var client = new NamedPipeClientStream(".", pipeName ?? DefaultPipeName, PipeDirection.Out, PipeOptions.CurrentUserOnly);
            client.Connect((int)timeout.TotalMilliseconds);
            using var writer = new StreamWriter(client, new UTF8Encoding(false)) { AutoFlush = true };
            writer.WriteLine(request.ToLine());
            return true;
        }
        catch (Exception ex) when (ex is TimeoutException or IOException or UnauthorizedAccessException)
        {
            LoggingService.Verbose("PlayRequest", $"No running TrayTrigger took the play request: {ex.Message}");
            return false;
        }
    }

    public void Dispose()
    {
        try { _stop.Cancel(); } catch (ObjectDisposedException) { /* already stopped */ }
        _stop.Dispose();
    }
}
