global using System;
global using System.Collections.Generic;
global using System.Linq;
global using System.Threading;
global using System.Threading.Channels;
global using System.Threading.Tasks;
using CommandLine;
using Grpc.Core;
using Knfa.TGM;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using MurmurRPC;
using Telegram.Bot;
using Channel = System.Threading.Channels.Channel;

AppContext.SetSwitch("System.Net.Http.SocketsHttpHandler.Http2UnencryptedSupport", true);

var parseResult = Parser.Default.ParseArguments<HostConfiguration>(args);

var configurationPath = parseResult
    .MapResult(
        x => x.ConfigurationFilePath,
        _ => HostConfiguration.DefaultPath);

await Host
    .CreateDefaultBuilder()
    .UseSystemd()
    .ConfigureAppConfiguration(config => { config.AddJsonFile(configurationPath); })
    .ConfigureServices(ConfigureServices)
    .RunConsoleAsync();

static void ConfigureServices(HostBuilderContext hostBuilderContext, IServiceCollection services)
{
    var appConfiguration = hostBuilderContext.Configuration.Get<ApplicationConfiguration>();
    ThrowIfAppConfigIsInvalid(appConfiguration);

    services.AddSingleton(appConfiguration);

    var telegramEventsChannel = Channel.CreateBounded<TelegramEvent>(new BoundedChannelOptions(10)
    {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    var mumbleEventsChannel = Channel.CreateBounded<MumbleEvent>(new BoundedChannelOptions(10)
    {
        SingleReader = true,
        SingleWriter = true,
        AllowSynchronousContinuations = true,
        FullMode = BoundedChannelFullMode.DropWrite
    });

    services.AddSingleton(telegramEventsChannel.Reader);
    services.AddSingleton(telegramEventsChannel.Writer);
    services.AddSingleton(mumbleEventsChannel.Reader);
    services.AddSingleton(mumbleEventsChannel.Writer);

    services.AddGrpcClient<V1.V1Client>(options =>
    {
        options.Address = new Uri(appConfiguration.Mumble.GrpcAddress);
        options.ChannelOptionsActions.Add(channelOptions => channelOptions.Credentials = ChannelCredentials.Insecure);
    });

    var telegramBotClient = new TelegramBotClient(appConfiguration.Telegram.BotKey);
    services.AddSingleton<ITelegramBotClient>(telegramBotClient);

    var telegramChats = new TelegramChats(appConfiguration.Telegram.OwnerId, appConfiguration.Telegram.GroupId);
    services.AddSingleton(telegramChats);

    services.AddHostedService<TelegramService>();
    services.AddHostedService<MumbleService>();
}

static void ThrowIfAppConfigIsInvalid(ApplicationConfiguration appConfig)
{
    if (appConfig.Mumble == null)
        throw new ApplicationException($"{nameof(appConfig.Mumble)} is null");
    if (appConfig.Mumble.GrpcAddress == null)
        throw new ApplicationException($"{nameof(appConfig.Mumble.GrpcAddress)} is null");

    if (appConfig.Telegram == null)
        throw new ApplicationException($"{nameof(appConfig.Telegram)} is null");
    if (appConfig.Telegram.BotKey == null)
        throw new ApplicationException($"{nameof(appConfig.Telegram.BotKey)} is null");
    if (appConfig.Telegram.OwnerId == 0)
        throw new ApplicationException($"{nameof(appConfig.Telegram.OwnerId)} is null");
    if (appConfig.Telegram.GroupId == 0)
        throw new ApplicationException($"{nameof(appConfig.Telegram.GroupId)} is null");
}
