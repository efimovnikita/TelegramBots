using Bot.VisualMigraineDiary.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Telegram.Bot;
using Bot.VisualMigraineDiary.Models;
using Microsoft.Extensions.Configuration;

var host = Host.CreateDefaultBuilder(args)
    .ConfigureAppConfiguration((hostingContext, config) =>
    {
        config.AddEnvironmentVariables();
    })
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

        services.Configure<MigraineDatabaseSettings>(
            context.Configuration.GetSection("MigraineDatabase"));

        services.AddSingleton<MigraineEventService>();
        services.AddSingleton<IMemoryStateProvider>(sp =>
        {
            var configuration = sp.GetRequiredService<IConfiguration>();
            var section = configuration.GetSection("AllowedUserIds");
            var value = section.Get<string>() ?? "";
            var strings = value.Split(",");
            var ids = strings.Select(long.Parse).ToArray();
            return new MemoryStateProvider(ids);
        });
    })
    .UseSerilog((context, configuration) =>
    {
        configuration.WriteTo.Console();
        configuration.WriteTo.Seq(context.Configuration["Urls:LogServer"] ?? string.Empty);
    })
    .Build();

await host.RunAsync();