using Bot.TranscribeBuddy.Services;
using BotSharedLibrary;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Refit;
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
        
        services.AddRefitClient<IFileSharingApi>()
            .ConfigureHttpClient(client => client.BaseAddress = new Uri(context.Configuration["Urls:GatewayBaseAddress"] ?? ""));
        
        services.AddRefitClient<IAuthApi>()
            .ConfigureHttpClient(client => client.BaseAddress = new Uri(context.Configuration["Urls:AuthGatewayBaseAddress"] ?? ""));
        
        services.AddRefitClient<IAudioApi>()
            .ConfigureHttpClient(client => client.BaseAddress = new Uri(context.Configuration["Urls:GatewayBaseAddress"] ?? ""));
    })
    .UseSerilog((context, configuration) =>
    {
        configuration.WriteTo.Console();
        configuration.WriteTo.Seq(context.Configuration["Urls:LogServer"] ?? string.Empty);
    })
    .Build();

await host.RunAsync();