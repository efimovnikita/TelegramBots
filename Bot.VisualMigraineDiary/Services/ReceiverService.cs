using Bot.VisualMigraineDiary.Abstract;
using Microsoft.Extensions.Logging;
using Telegram.Bot;

namespace Bot.VisualMigraineDiary.Services;

// Compose Receiver and UpdateHandler implementation
public class ReceiverService(ITelegramBotClient botClient, UpdateHandler updateHandler, ILogger<ReceiverServiceBase<UpdateHandler>> logger)
    : ReceiverServiceBase<UpdateHandler>(botClient, updateHandler, logger);
