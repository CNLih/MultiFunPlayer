using MultiFunPlayer.Application;
using MultiFunPlayer.Common;
using MultiFunPlayer.UI;
using MultiFunPlayer.UI.Dialogs.ViewModels;
using Stylet;
using System.Windows;

namespace MultiFunPlayer.Desktop;

/// <summary>
/// Transitional desktop bridge. Portable services publish typed application
/// messages, while the retained WPF host and plugins continue to observe the
/// legacy Stylet message types.
/// </summary>
internal sealed class StyletApplicationMessageBus(IEventAggregator eventAggregator)
    : IApplicationMessageBus
{
    public IDisposable Subscribe<TMessage>(Action<TMessage> handler)
        where TMessage : IApplicationMessage
    {
        ArgumentNullException.ThrowIfNull(handler);

        if (typeof(TMessage) == typeof(MediaSeekCommand))
            return SubscribeLegacy<MediaSeekMessage>(message =>
                handler((TMessage)(IApplicationMessage)new MediaSeekCommand(message.Position)));
        if (typeof(TMessage) == typeof(MediaPlayPauseCommand))
            return SubscribeLegacy<MediaPlayPauseMessage>(message =>
                handler((TMessage)(IApplicationMessage)new MediaPlayPauseCommand(message.ShouldBePlaying)));
        if (typeof(TMessage) == typeof(MediaChangePathCommand))
            return SubscribeLegacy<MediaChangePathMessage>(message =>
                handler((TMessage)(IApplicationMessage)new MediaChangePathCommand(message.Path)));
        if (typeof(TMessage) == typeof(MediaChangeSpeedCommand))
            return SubscribeLegacy<MediaChangeSpeedMessage>(message =>
                handler((TMessage)(IApplicationMessage)new MediaChangeSpeedCommand(message.Speed)));
        if (typeof(TMessage) == typeof(SyncRequestCommand))
            return SubscribeLegacy<SyncRequestMessage>(message =>
                handler((TMessage)(IApplicationMessage)new SyncRequestCommand(message.Axes)));
        if (typeof(TMessage) == typeof(ReloadScriptsCommand))
            return SubscribeLegacy<ReloadScriptsRequestMessage>(message =>
                handler((TMessage)(IApplicationMessage)new ReloadScriptsCommand(message.Axes)));
        if (typeof(TMessage) == typeof(MediaSpeedChangedEvent))
            return SubscribeLegacy<MediaSpeedChangedMessage>(message =>
                handler((TMessage)(IApplicationMessage)new MediaSpeedChangedEvent(message.Speed)));
        if (typeof(TMessage) == typeof(MediaPositionChangedEvent))
            return SubscribeLegacy<MediaPositionChangedMessage>(message =>
                handler((TMessage)(IApplicationMessage)new MediaPositionChangedEvent(message.Position, message.ForceSeek)));
        if (typeof(TMessage) == typeof(MediaPlayingChangedEvent))
            return SubscribeLegacy<MediaPlayingChangedMessage>(message =>
                handler((TMessage)(IApplicationMessage)new MediaPlayingChangedEvent(message.IsPlaying)));
        if (typeof(TMessage) == typeof(MediaPathChangedEvent))
            return SubscribeLegacy<MediaPathChangedMessage>(message =>
                handler((TMessage)(IApplicationMessage)new MediaPathChangedEvent(message.Path, message.ReloadScripts, message.Context)));
        if (typeof(TMessage) == typeof(MediaDurationChangedEvent))
            return SubscribeLegacy<MediaDurationChangedMessage>(message =>
                handler((TMessage)(IApplicationMessage)new MediaDurationChangedEvent(message.Duration)));
        if (typeof(TMessage) == typeof(MediaResetEvent))
            return SubscribeLegacy<MediaResetMessage>(_ =>
                handler((TMessage)(IApplicationMessage)new MediaResetEvent()));
        if (typeof(TMessage) == typeof(ChangeScriptEvent))
            return SubscribeLegacy<ChangeScriptMessage>(message =>
                handler((TMessage)(IApplicationMessage)new ChangeScriptEvent(message.Scripts)));

        throw new NotSupportedException($"No Stylet bridge exists for {typeof(TMessage).FullName}.");
    }

    public void Publish<TMessage>(TMessage message)
        where TMessage : IApplicationMessage
    {
        ArgumentNullException.ThrowIfNull(message);

        switch (message)
        {
            case MediaSeekCommand command:
                eventAggregator.Publish(new MediaSeekMessage(command.Position));
                break;
            case MediaPlayPauseCommand command:
                eventAggregator.Publish(new MediaPlayPauseMessage(command.ShouldBePlaying));
                break;
            case MediaChangePathCommand command:
                eventAggregator.Publish(new MediaChangePathMessage(command.Path));
                break;
            case MediaChangeSpeedCommand command:
                eventAggregator.Publish(new MediaChangeSpeedMessage(command.Speed));
                break;
            case SyncRequestCommand command:
                eventAggregator.Publish(new SyncRequestMessage(command.Axes));
                break;
            case ReloadScriptsCommand command:
                eventAggregator.Publish(new ReloadScriptsRequestMessage(command.Axes));
                break;
            case MediaSpeedChangedEvent changed:
                eventAggregator.Publish(new MediaSpeedChangedMessage(changed.Speed));
                break;
            case MediaPositionChangedEvent changed:
                eventAggregator.Publish(new MediaPositionChangedMessage(changed.Position, changed.ForceSeek));
                break;
            case MediaPlayingChangedEvent changed:
                eventAggregator.Publish(new MediaPlayingChangedMessage(changed.IsPlaying));
                break;
            case MediaPathChangedEvent changed:
                eventAggregator.Publish(new MediaPathChangedMessage(changed.Path, changed.ReloadScripts, changed.Context));
                break;
            case MediaDurationChangedEvent changed:
                eventAggregator.Publish(new MediaDurationChangedMessage(changed.Duration));
                break;
            case MediaResetEvent:
                eventAggregator.Publish(new MediaResetMessage());
                break;
            case ChangeScriptEvent changed:
                eventAggregator.Publish(new ChangeScriptMessage(changed.Scripts));
                break;
            default:
                throw new NotSupportedException($"No Stylet bridge exists for {message.GetType().FullName}.");
        }
    }

    private IDisposable SubscribeLegacy<TMessage>(Action<TMessage> callback)
        where TMessage : class
    {
        var proxy = new LegacyHandler<TMessage>(callback);
        eventAggregator.Subscribe(proxy);
        return new DelegateDisposable(() => eventAggregator.Unsubscribe(proxy));
    }

    private sealed class LegacyHandler<TMessage>(Action<TMessage> callback) : IHandle<TMessage>
    {
        public void Handle(TMessage message) => callback(message);
    }

    private sealed class DelegateDisposable(Action dispose) : IDisposable
    {
        private int _disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref _disposed, 1) == 0)
                dispose();
        }
    }
}

/// <summary>WPF-only presentation adapter for portable operation failures.</summary>
internal sealed class DialogApplicationErrorSink : IOperationErrorSink
{
    public void Report(ApplicationError error)
    {
        ArgumentNullException.ThrowIfNull(error);
        var exception = error.Exception ?? new InvalidOperationException(error.Message);
        _ = DialogHelper.ShowErrorAsync(exception, error.Message, "RootDialog");
    }
}

/// <summary>WPF confirmation adapter for host-neutral application prompts.</summary>
internal sealed class DialogApplicationPrompt : IApplicationPrompt
{
    public async ValueTask<bool> ConfirmAsync(string code, string title, string message,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var result = await DialogHelper.ShowAsync<MessageBoxResult>(
            new MessageBoxDialog(message, MessageBoxButton.YesNo), "RootDialog");
        return result == MessageBoxResult.Yes;
    }
}
