using System.Text.Json;
using Refit;

namespace Bot.YTMusicFetcher.Models;

public interface IYtMusicApi
{
    [Get("/api/gateway/youtube/v1/health")]
    Task CheckHealth();

    [Headers("Content-Type: application/json")]
    [Post("/api/gateway/youtube/v1/audio/get-music-archive")]
    Task<YtMusicResponseData> StartMakingArchiveJob([Header("Authorization")] string authorization, 
        [Body][AliasAs("Urls")] UrlsRequest urls);
    
    [Get("/api/gateway/youtube/v1/audio/get-status")]
    Task<YtMusicStatusResponseData> CheckStatus([AliasAs("jobId")] string jobId);
}