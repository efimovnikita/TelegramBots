using Bot.YTMusicFetcher.Abstract;
using Microsoft.Extensions.Logging;

namespace Bot.YTMusicFetcher.Services;

// Compose Polling and ReceiverService implementations
public class PollingService(IServiceProvider serviceProvider, ILogger<PollingService> logger)
    : PollingServiceBase<ReceiverService>(serviceProvider, logger);
