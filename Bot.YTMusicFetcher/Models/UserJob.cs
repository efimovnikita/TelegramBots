namespace Bot.YTMusicFetcher.Models;

public class UserJob(long userId, Job job)
{
    public long UserId { get; } = userId;
    public Job Job { get; } = job;
}