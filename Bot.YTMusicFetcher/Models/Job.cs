using Microsoft.Extensions.Configuration;
using YoutubeExplode;

namespace Bot.YTMusicFetcher.Models;

public class Job(string[] urls, IYtMusicApi ytMusicApi, IAuthApi authApi, IConfiguration configuration)
{
    private readonly YoutubeClient _youtube = new();

    public string JobId { get; private set; } = string.Empty;
    private string[] Urls { get; } = urls;

    public string Status { get; private set; } = Statuses.UnknownStatus;
    public string Result { get; private set; } = string.Empty;

    public async Task<string> GetFirstTrackName()
    {
        var url = Urls.FirstOrDefault();
        if (url == null)
        {
            return string.Empty;
        }
        
        var metaData = await _youtube.Videos.GetAsync(url);
        
        return metaData.Title;
    }
    
    public async Task<string> GetLastTrackName()
    {
        var url = Urls.LastOrDefault();
        if (url == null)
        {
            return string.Empty;
        }
        
        var metaData = await _youtube.Videos.GetAsync(url);
        
        return metaData.Title;
    }

    public async Task UpdateStatus()
    {
        var response = await ytMusicApi.CheckStatus(JobId);
        Status = response.Status;
        Result = response.Result;
    }

    public async Task IgniteRemoteJob()
    {
        await ytMusicApi.CheckHealth();
        
        var data = new Dictionary<string, string> {
            { IAuthApi.GrantType, IAuthApi.ClientCredentials },
            { IAuthApi.ClientId, configuration["BotConfiguration:ClientId"] ?? "" },
            { IAuthApi.ClientSecret, configuration["BotConfiguration:ClientSecret"] ?? "" }
        };

        var authData = await authApi.GetAuthData(data);
        
        var job = await ytMusicApi.StartMakingArchiveJob($"Bearer {authData.AccessToken}",
            new UrlsRequest { Urls = Urls});
        
        JobId = job.JobId;
    }
}