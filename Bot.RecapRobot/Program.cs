using Bot.RecapRobot.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Telegram.Bot;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureServices((context, services) =>
    {
        services.AddHttpClient("telegram_bot_client").RemoveAllLoggers()
            .AddTypedClient<ITelegramBotClient>((httpClient, _) =>
            {
                TelegramBotClientOptions options = 
                    new(context.Configuration["BotConfiguration:BotToken"] ?? string.Empty);
                return new TelegramBotClient(options, httpClient);
            });

        services.AddScoped<UpdateHandler>();
        services.AddScoped<ReceiverService>();
        services.AddHostedService<PollingService>();
    })
    .UseSerilog((context, configuration) =>
    {
        configuration.WriteTo.Console();
        configuration.WriteTo.Seq(context.Configuration["Urls:LogServer"] ?? string.Empty);
    })
    .Build();

await host.RunAsync();