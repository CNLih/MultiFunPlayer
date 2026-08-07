using System.Runtime.ExceptionServices;
using System.Windows;

namespace MultiFunPlayer.Tests;

/// <summary>
/// Loads the compiled WPF resources on a real STA thread. The test is intentionally
/// kept in the Windows test project because the WindowsDesktop runtime is required.
/// </summary>
public sealed class WpfXamlTests
{
    [Fact]
    public void Every_compiled_view_and_resource_dictionary_loads_on_net10()
    {
        if (!OperatingSystem.IsWindows())
            return;

        RunSta(() =>
        {
            var application = new App();
            application.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            try
            {
                var projectRoot = FindProjectRoot();
                var xamlRoot = Path.Combine(projectRoot, "Source", "MultiFunPlayer");
                var assemblyName = typeof(App).Assembly.GetName().Name;
                var failures = new List<string>();

                foreach (var file in Directory.EnumerateFiles(xamlRoot, "*.xaml", SearchOption.AllDirectories)
                             .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
                {
                    var relativePath = Path.GetRelativePath(xamlRoot, file).Replace(Path.DirectorySeparatorChar, '/');
                    if (string.Equals(relativePath, "App.xaml", StringComparison.OrdinalIgnoreCase))
                        continue;

                    var resourceUri = new Uri($"/{assemblyName};component/{relativePath}", UriKind.Relative);
                    try
                    {
                        _ = global::System.Windows.Application.LoadComponent(resourceUri);
                    }
                    catch (Exception exception)
                    {
                        failures.Add($"{relativePath}: {exception.GetType().Name}: {exception.Message}");
                    }
                }

                Assert.True(failures.Count == 0,
                    "The following compiled WPF resources failed to load:\n" + string.Join("\n", failures));
            }
            finally
            {
                application.Shutdown();
            }
        });
    }

    private static string FindProjectRoot()
    {
        for (var directory = new DirectoryInfo(Directory.GetCurrentDirectory()); directory != null; directory = directory.Parent)
        {
            if (File.Exists(Path.Combine(directory.FullName, "MultiFunPlayer.sln")))
                return directory.FullName;
        }

        throw new DirectoryNotFoundException("Unable to locate the repository root from the test working directory.");
    }

    private static void RunSta(Action action)
    {
        Exception failure = null;
        using var completed = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception exception)
            {
                failure = exception;
            }
            finally
            {
                completed.Set();
            }
        });

        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        Assert.True(completed.Wait(TimeSpan.FromMinutes(2)), "WPF resource loading did not complete.");
        thread.Join();

        if (failure != null)
            ExceptionDispatchInfo.Capture(failure).Throw();
    }
}
