using MultiFunPlayer.Common;
using MultiFunPlayer.Plugin;
using Newtonsoft.Json.Linq;
using Stylet;
using StyletIoC;
using System.Reflection;
using System.Windows;

namespace MultiFunPlayer.Tests;

public sealed class PluginTests
{
    [Fact]
    public void PluginCompiler_compiles_loads_serializes_and_disposes_a_plugin()
    {
        var previousDispatcher = Execute.Dispatcher;
        Execute.Dispatcher = SynchronousDispatcher.Instance;
        try
        {
            var builder = new StyletIoCBuilder();
            builder.Bind<IViewManager>().ToInstance(new NullViewManager());
            var container = builder.BuildContainer();
            try
            {
                PluginCompiler.Initialize(container);
                using var directory = new TemporaryDirectory();
                var sourcePath = Path.Combine(directory.Path, "sample.cs");
                File.WriteAllText(sourcePath, """
                    public sealed class SamplePlugin : PluginBase
                    {
                        [JsonProperty]
                        public int Value { get; set; }
                    }
                    """);

                using var result = PluginCompiler.Compile(new FileInfo(sourcePath));
                Assert.True(result.Success, result.Exception?.ToString());
                Assert.NotNull(result.PluginInstance);

                var value = result.PluginInstance.GetType().GetProperty("Value",
                    BindingFlags.Instance | BindingFlags.Public);
                Assert.NotNull(value);

                value!.SetValue(result.PluginInstance, 41);
                var saved = new JObject();
                result.PluginInstance.HandleSettings(saved, SettingsAction.Saving);
                Assert.Equal(41, saved["Value"]?.Value<int>());

                value.SetValue(result.PluginInstance, 0);
                result.PluginInstance.HandleSettings(new JObject { ["Value"] = 73 }, SettingsAction.Loading);
                Assert.Equal(73, value.GetValue(result.PluginInstance));
            }
            finally
            {
                (container as IDisposable)?.Dispose();
            }
        }
        finally
        {
            Execute.Dispatcher = previousDispatcher;
        }
    }

    private sealed class NullViewManager : IViewManager
    {
        public void OnModelChanged(DependencyObject targetLocation, object oldValue, object newValue) { }
        public UIElement CreateViewForModel(object model) => null;
        public void BindViewToModel(UIElement view, object viewModel) { }
        public UIElement CreateAndBindViewForModelIfNecessary(object model) => null;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public string Path { get; } = System.IO.Path.Combine(
            System.IO.Path.GetTempPath(), $"mfp-plugin-{Guid.NewGuid():N}");

        public TemporaryDirectory() => Directory.CreateDirectory(Path);

        public void Dispose()
        {
            if (Directory.Exists(Path))
                Directory.Delete(Path, recursive: true);
        }
    }
}
