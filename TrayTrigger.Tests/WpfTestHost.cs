using System.Windows;
using System.Windows.Threading;

namespace TrayTrigger.Tests;

/// <summary>
/// A single STA thread with a running Dispatcher and a live <see cref="Application"/>, shared by
/// the whole test assembly.
///
/// View-model code that marshals to the UI thread goes through Application.Current.Dispatcher and
/// quietly does nothing when there is no Application - correct in the app, where no Application
/// means it is shutting down, but it silently turns a test of such a method into a test of
/// nothing. Running the body here gives that code a real dispatcher to find, and a CheckAccess()
/// that says yes, so the work happens inline on this thread.
///
/// One Application per process is the limit, so this is created once and every caller shares it.
/// </summary>
internal static class WpfTestHost
{
    private static readonly object Gate = new();
    private static Dispatcher? _dispatcher;

    private static Dispatcher Dispatcher
    {
        get
        {
            lock (Gate)
            {
                if (_dispatcher != null) return _dispatcher;

                var ready = new ManualResetEventSlim();
                var thread = new Thread(() =>
                {
                    // Constructing it is the point - it sets Application.Current for the process.
                    _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
                    _dispatcher = Dispatcher.CurrentDispatcher;
                    ready.Set();
                    Dispatcher.Run();
                })
                {
                    IsBackground = true,
                    Name = "WpfTestHost",
                };
                thread.SetApartmentState(ApartmentState.STA);
                thread.Start();
                ready.Wait(TimeSpan.FromSeconds(10));

                return _dispatcher ?? throw new InvalidOperationException("WpfTestHost dispatcher never started.");
            }
        }
    }

    /// <summary>Runs the body on the shared UI thread, rethrowing anything it throws.</summary>
    internal static void Run(Action body) => Dispatcher.Invoke(body);
}
