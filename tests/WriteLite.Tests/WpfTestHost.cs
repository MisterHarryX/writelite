using System.Windows;
using System.Windows.Threading;

namespace WriteLite.Tests;

/// <summary>
/// Runs a piece of WPF work on the shared UI thread, against the real WriteLite theme.
/// </summary>
/// <remarks>
/// One STA thread for the whole test run, with a live dispatcher loop, because that is
/// what the application actually is. The previous version started a fresh thread per
/// test and shut its dispatcher down afterwards, which produced two failure modes that
/// look like product bugs and are not:
///
/// <list type="bullet">
/// <item>Freezables in <c>Application.Resources</c> — the theme is loaded once — end up
/// owned by whichever thread got there first, so a control template realised on a later
/// thread throws "a different thread owns it" the moment it animates.</item>
/// <item>Shutting a dispatcher down while a storyboard is still running kills the test
/// host with an <c>AnimationException</c>, and which test it lands on depends on
/// timing.</item>
/// </list>
///
/// A single long-lived UI thread removes both, and makes the tests exercise the same
/// threading model the product runs under.
/// </remarks>
internal static class WpfTestHost
{
    private const string ThemeUri = "pack://application:,,,/WriteLite;component/Themes/WriteLiteTheme.xaml";

    private static readonly Lock Gate = new();
    private static Dispatcher? _dispatcher;

    /// <summary>Loads the theme as a standalone dictionary, for token inspection.</summary>
    internal static ResourceDictionary LoadTheme()
    {
        ResourceDictionary? theme = null;
        Run(() => theme = new ResourceDictionary { Source = new Uri(ThemeUri, UriKind.Absolute) });
        return theme!;
    }

    /// <summary>Loads the theme into application resources, so control templates resolve.</summary>
    internal static void EnsureThemeApplied()
    {
        var app = EnsureApplication();
        if (app.Resources.MergedDictionaries.Count == 0)
        {
            app.Resources.MergedDictionaries.Add(
                new ResourceDictionary { Source = new Uri(ThemeUri, UriKind.Absolute) });
        }
    }

    internal static void Run(Action action)
    {
        var dispatcher = EnsureDispatcher();

        Exception? failure = null;
        dispatcher.Invoke(() =>
        {
            try
            {
                EnsureApplication();
                action();
            }
            catch (Exception ex)
            {
                failure = ex;
            }
        });

        if (failure is not null)
        {
            throw new AssertFailedException(
                $"{failure.GetType().Name}: {failure.Message}\nInner: {failure.InnerException?.Message}",
                failure);
        }
    }

    /// <summary>
    /// Runs asynchronous UI work and waits for it to finish on the UI thread.
    /// </summary>
    /// <remarks>
    /// Needed for anything that awaits inside a control — opening a document, running
    /// an analysis pass. <see cref="Run(Action)"/> would return as soon as the first
    /// await yielded, and the assertions would then run against a half-finished page.
    /// The dispatcher already carries a <see cref="DispatcherSynchronizationContext"/>,
    /// so every continuation resumes on this same thread.
    /// </remarks>
    internal static void RunAsync(Func<Task> action, int timeoutMilliseconds = 30_000)
    {
        var dispatcher = EnsureDispatcher();

        var completion = new TaskCompletionSource();

        dispatcher.InvokeAsync(async () =>
        {
            try
            {
                EnsureApplication();
                await action();
                completion.SetResult();
            }
            catch (Exception ex)
            {
                completion.SetException(ex);
            }
        });

        if (!completion.Task.Wait(timeoutMilliseconds))
        {
            throw new AssertFailedException($"UI work did not complete within {timeoutMilliseconds} ms.");
        }

        if (completion.Task.Exception?.InnerException is { } failure)
        {
            throw new AssertFailedException(
                $"{failure.GetType().Name}: {failure.Message}\nInner: {failure.InnerException?.Message}",
                failure);
        }
    }

    /// <summary>
    /// Lets queued dispatcher work run, for code that posts its own continuations.
    /// </summary>
    internal static async Task PumpAsync(int milliseconds = 200)
    {
        var deadline = DateTime.UtcNow.AddMilliseconds(milliseconds);

        while (DateTime.UtcNow < deadline)
        {
            await Dispatcher.Yield(DispatcherPriority.Background);
            await Task.Delay(20);
        }
    }

    /// <summary>
    /// Starts the UI thread on first use and keeps it for the rest of the run.
    /// </summary>
    /// <remarks>
    /// The thread is a background thread and its dispatcher is never shut down: the
    /// process exiting is what ends it. Shutting it down between tests is precisely
    /// what used to tear down animation clocks mid-flight.
    /// </remarks>
    private static Dispatcher EnsureDispatcher()
    {
        lock (Gate)
        {
            if (_dispatcher is not null)
            {
                return _dispatcher;
            }

            var ready = new ManualResetEventSlim();
            var thread = new Thread(() =>
            {
                // Creating a Dispatcher does not by itself make a thread a WPF UI
                // thread; without this context every `await` in the code under test
                // resumes on the thread pool and then throws on first control access.
                var dispatcher = Dispatcher.CurrentDispatcher;
                SynchronizationContext.SetSynchronizationContext(
                    new DispatcherSynchronizationContext(dispatcher));

                _dispatcher = dispatcher;
                ready.Set();
                Dispatcher.Run();
            })
            {
                IsBackground = true,
                Name = "WriteLite test UI"
            };

            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();
            ready.Wait();

            return _dispatcher!;
        }
    }

    // An Application instance is also what registers the pack:// scheme for resource URIs.
    private static System.Windows.Application EnsureApplication()
    {
        var app = System.Windows.Application.Current ?? new System.Windows.Application();

        // Tests open and close windows constantly. Under the default
        // OnLastWindowClose the first close would shut the shared Application down
        // and every later test would fail trying to create a second one.
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        return app;
    }
}
