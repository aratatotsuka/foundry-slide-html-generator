using FoundrySlideHtmlGenerator.Backend.Orchestration;

namespace FoundrySlideHtmlGenerator.Backend.Jobs;

public sealed class JobWorker : BackgroundService
{
    private readonly JobQueue _queue;
    private readonly SlideGenerationOrchestrator _orchestrator;
    private readonly IJobStore _store;
    private readonly ILogger<JobWorker> _logger;

    public JobWorker(JobQueue queue, SlideGenerationOrchestrator orchestrator, IJobStore store, ILogger<JobWorker> logger)
    {
        _queue = queue;
        _orchestrator = orchestrator;
        _store = store;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Job worker started.");
        while (!stoppingToken.IsCancellationRequested)
        {
            JobWorkItem item;
            try
            {
                item = await _queue.DequeueAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }

            using var scope = _logger.BeginScope(new Dictionary<string, object>
            {
                ["jobId"] = item.JobId
            });

            try
            {
                await _orchestrator.RunAsync(item, stoppingToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Job {JobId} failed unexpectedly.", item.JobId);
                await _store.UpdateAsync(item.JobId, state =>
                {
                    state.Status = JobStatus.Failed;
                    state.Error = ex.Message;
                }, stoppingToken);
            }
        }
    }
}
