using Bot.VisualMigraineDiary.Pipelines;

namespace Bot.VisualMigraineDiary.Services;

public interface IMemoryStateProvider
{
    Dictionary<long, IPipeline?> GetState();
    bool IsContainUserId(long userId);
    IPipeline? GetCurrentPipeline(long userId);
    void SetCurrentPipeline(IPipeline pipeline, long userId);
    void ResetCurrentPipeline(long userId);
}