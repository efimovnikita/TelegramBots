using Bot.VisualMigraineDiary.Pipelines;

namespace Bot.VisualMigraineDiary.Services;

public class MemoryStateProvider : IMemoryStateProvider
{
    public MemoryStateProvider(long[] ids)
    {
        State = new Dictionary<long, IPipeline?>(ids.Length);
        foreach (var id in ids) State.Add(id, null);
    }

    public Dictionary<long, IPipeline?> State { get; set; }
    public Dictionary<long, IPipeline?> GetState() => State;
    public bool IsContainUserId(long userId) => State.ContainsKey(userId);
    public IPipeline? GetCurrentPipeline(long userId) => IsContainUserId(userId) ? State[userId] : null;
    public void SetCurrentPipeline(IPipeline pipeline, long userId)
    {
        if (IsContainUserId(userId) == false)
        {
            return;
        }

        State[userId] = pipeline;
    }

    public void ResetCurrentPipeline(long userId)
    {
        if (IsContainUserId(userId) == false)
        {
            return;
        }

        State[userId] = null;
    }
}