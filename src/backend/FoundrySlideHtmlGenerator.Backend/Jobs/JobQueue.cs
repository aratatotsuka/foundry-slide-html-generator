using System.Threading.Channels;

namespace FoundrySlideHtmlGenerator.Backend.Jobs;

public sealed class JobQueue
{
    private readonly Channel<JobWorkItem> _channel = Channel.CreateUnbounded<JobWorkItem>(new UnboundedChannelOptions
    {
        SingleReader = true,
        SingleWriter = false,
        AllowSynchronousContinuations = false
    });

    public void Enqueue(JobWorkItem item)
    {
        if (!_channel.Writer.TryWrite(item))
        {
            throw new InvalidOperationException("Failed to enqueue job.");
        }
    }

    public ValueTask<JobWorkItem> DequeueAsync(CancellationToken cancellationToken)
        => _channel.Reader.ReadAsync(cancellationToken);
}

