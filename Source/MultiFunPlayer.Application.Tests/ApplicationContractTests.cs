using MultiFunPlayer.Application;

namespace MultiFunPlayer.Application.Tests;

public sealed class ApplicationContractTests
{
    [Fact]
    public void TypedBusDispatchesAndDisposesSubscriptions()
    {
        using var bus = new InMemoryApplicationMessageBus();
        var received = new List<TimeSpan>();
        using var subscription = bus.Subscribe<MediaSeekCommand>(command => received.Add(command.Position));

        bus.Publish(new MediaSeekCommand(TimeSpan.FromSeconds(1)));
        subscription.Dispose();
        bus.Publish(new MediaSeekCommand(TimeSpan.FromSeconds(2)));

        Assert.Equal([TimeSpan.FromSeconds(1)], received);
    }

    [Fact]
    public void ErrorFactoryPreservesStructuredOperationAndExceptionMetadata()
    {
        var exception = new InvalidOperationException("device unavailable");

        var error = ApplicationError.FromException("output.connect.failed", "connect",
            "output/0", exception, isTransient: true);

        Assert.Equal("output.connect.failed", error.Code);
        Assert.Equal("connect", error.Operation);
        Assert.Equal("output/0", error.Source);
        Assert.Equal("device unavailable", error.Message);
        Assert.True(error.IsTransient);
        Assert.Equal(typeof(InvalidOperationException).FullName, error.ExceptionType);
        Assert.Same(exception, error.Exception);
    }

    [Fact]
    public void DisposedBusRejectsNewPublishes()
    {
        var bus = new InMemoryApplicationMessageBus();
        bus.Dispose();

        Assert.Throws<ObjectDisposedException>(() =>
            bus.Publish(new MediaResetEvent()));
    }

    [Fact]
    public async Task NonInteractivePromptDeniesInteractiveOperations()
    {
        var approved = await DenyApplicationPrompt.Instance.ConfirmAsync(
            "media.executable.download", "Download", "Download now?");

        Assert.False(approved);
    }
}
