using Bot.EngTubeBot.Abstract;
using Microsoft.Extensions.Logging;

namespace Bot.EngTubeBot.Services;

// Compose Polling and ReceiverService implementations
public class PollingService(IServiceProvider serviceProvider, ILogger<PollingService> logger)
    : PollingServiceBase<ReceiverService>(serviceProvider, logger);
