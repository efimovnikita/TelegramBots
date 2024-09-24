using Bot.TranscribeBuddy.Abstract;
using Microsoft.Extensions.Logging;

namespace Bot.TranscribeBuddy.Services;

// Compose Polling and ReceiverService implementations
public class PollingService(IServiceProvider serviceProvider, ILogger<PollingService> logger)
    : PollingServiceBase<ReceiverService>(serviceProvider, logger);
