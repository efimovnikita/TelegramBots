using Refit;

namespace BotSharedLibrary;

public interface IYouTubeApi
{
    [Get("/api/gateway/youtube/v1/health")]
    Task CheckHealth();
    
    [Get("/api/gateway/youtube/v1/audio/get-split-audio")]
    Task<Stream> GetSplitAudio(
        [Header("Authorization")] string authorization,
        [Query] string videoUrl,
        [Query] string startTime,
        [Query] string endTime);

    [Get("/api/gateway/youtube/v1/audio")]
    Task<Stream> GetAudio(
        [Header("Authorization")] string authorization,
        [Query] string videoUrl);
}