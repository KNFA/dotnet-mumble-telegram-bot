using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using MurmurRPC;
using Telegram.Bot;
using Telegram.Bot.Extensions.Polling;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;
using User = MurmurRPC.User;

namespace Knfa.TGM;

public record TelegramChats(long OwnerId, long GroupId);

public abstract record TelegramEvent;

public record UserListRequested(long RequesterId, int MessageId) : TelegramEvent;

internal sealed class TelegramService : BackgroundService
{
    private readonly ITelegramBotClient _telegramBotClient;
    private readonly TelegramChats _telegramChats;
    private readonly ChannelWriter<TelegramEvent> _telegramEventsChannel;
    private readonly ChannelReader<MumbleEvent> _mumbleEventsChannel;
    private readonly ILogger<TelegramService> _logger;

    public TelegramService(
        ITelegramBotClient telegramBotClient,
        TelegramChats telegramChats,
        ChannelWriter<TelegramEvent> telegramEventsChannel,
        ChannelReader<MumbleEvent> mumbleEventsChannel,
        ILogger<TelegramService> logger)
    {
        _telegramBotClient = telegramBotClient;
        _telegramChats = telegramChats;
        _telegramEventsChannel = telegramEventsChannel;
        _mumbleEventsChannel = mumbleEventsChannel;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var updater = new QueuedUpdateReceiver(_telegramBotClient);
        updater.StartReceiving(new[] { UpdateType.Message }, OnReceiveGeneralError, cancellationToken: ct);

        var updatesIterator = updater.YieldUpdatesAsync().GetAsyncEnumerator(ct);
        var channelIterator = _mumbleEventsChannel.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        var selectiveAsyncEnumerator = new SelectiveAsyncEnumerator<Update, MumbleEvent>(updatesIterator, channelIterator);

        await foreach (var @event in selectiveAsyncEnumerator)
        {
            ct.ThrowIfCancellationRequested();

            var nextTask = @event switch
            {
                { Item1.Message: { Chat.Id: var cId, Text: { } t, MessageId: var mId } }
                    when cId == _telegramChats.OwnerId || cId == _telegramChats.GroupId
                    => ProcessMessage(cId, mId, t, ct),

                { Item2: UserListFetched e } => SendUserList(e, ct),
                { Item2: UserJoined e } => SendUserJoined(e, ct),
                { Item2: UserLeft e } => SendUserLeft(e, ct),

                _ => Task.CompletedTask
            };

            await nextTask;
        }
    }

    private async Task SendUserJoined(UserJoined userJoined, CancellationToken ct)
    {
        var message = $"`{userJoined.Username} joined`";

        await _telegramBotClient.SendTextMessageAsync(
            _telegramChats.GroupId,
            message,
            ParseMode.Markdown,
            disableNotification: true,
            cancellationToken: ct);
    }

    private async Task SendUserLeft(UserLeft userLeft, CancellationToken ct)
    {
        var message = $"`{userLeft.Username} left`";

        await _telegramBotClient.SendTextMessageAsync(
            _telegramChats.GroupId,
            message,
            ParseMode.Markdown,
            disableNotification: true,
            cancellationToken: ct);
    }

    private async Task ProcessMessage(long chatId, int msgId, string messageText, CancellationToken ct)
    {
        switch (messageText)
        {
            case "/who":
                await _telegramEventsChannel.WriteAsync(new UserListRequested(chatId, msgId), ct);
                return;
        }
    }

    private async Task SendUserList(UserListFetched userListFetched, CancellationToken ct)
    {
        string messageText;

        if (userListFetched.Usernames.Length == 0)
        {
            messageText = "`no users are connected`";
        }
        else
        {
            messageText = $"```\n{string.Join("\n", userListFetched.Usernames)}\n```";
        }


        await _telegramBotClient.SendTextMessageAsync(
            new ChatId(userListFetched.RequesterId),
            messageText,
            ParseMode.Markdown,
            replyToMessageId: userListFetched.MessageId,
            disableNotification: true,
            cancellationToken: ct);
    }

    private Task OnReceiveGeneralError(Exception e, CancellationToken ct)
    {
        _logger.LogError(e, "Receive error occured: {errorMessage}", e.Message);
        Environment.FailFast("Telegram service failed");
        return Task.CompletedTask;
    }
}

public abstract record MumbleEvent;

public record UserJoined(string Username) : MumbleEvent;

public record UserLeft(string Username) : MumbleEvent;

public record UserListFetched(string[] Usernames, long RequesterId, int MessageId) : MumbleEvent;

internal sealed class MumbleService : BackgroundService
{
    private readonly V1.V1Client _mumbleGrpcClient;
    private readonly ChannelWriter<MumbleEvent> _mumbleEventsChannel;
    private readonly ChannelReader<TelegramEvent> _telegramEventsChannel;

    public MumbleService(
        V1.V1Client mumbleGrpcClient,
        ChannelWriter<MumbleEvent> mumbleEventsChannel,
        ChannelReader<TelegramEvent> telegramEventsChannel)
    {
        _mumbleGrpcClient = mumbleGrpcClient;
        _mumbleEventsChannel = mumbleEventsChannel;
        _telegramEventsChannel = telegramEventsChannel;
    }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var serverResponse = await _mumbleGrpcClient.ServerQueryAsync(new Server.Types.Query());
        var server = serverResponse.Servers.First();

        var grpcEventStream = _mumbleGrpcClient.ServerEvents(server, cancellationToken: ct).ResponseStream;
        var telegramEventStream = _telegramEventsChannel.ReadAllAsync(ct).GetAsyncEnumerator(ct);
        var selectiveIterator =
            new SelectiveAsyncEnumerator<Server.Types.Event, TelegramEvent>(
                grpcEventStream.ToAsyncEnumerator(),
                telegramEventStream);

        await foreach (var @event in selectiveIterator)
        {
            ct.ThrowIfCancellationRequested();

            var nextTask = @event switch
            {
                { Item1: { Type: Server.Types.Event.Types.Type.UserConnected } e }
                    => ProduceUserJoinedEvent(e, ct),

                { Item1: { Type: Server.Types.Event.Types.Type.UserDisconnected } e }
                    => ProduceUserLeftEvent(e, ct),

                { Item2: UserListRequested e }
                    => FetchUsersAndProduceEvent(e, server, ct),

                _ => Task.Delay(TimeSpan.FromSeconds(1), ct)
            };

            await nextTask;
        }
    }

    private Task ProduceUserJoinedEvent(Server.Types.Event e, CancellationToken ct)
        => _mumbleEventsChannel.WriteAsync(new UserJoined(e.User.Name), ct).AsTask();

    private Task ProduceUserLeftEvent(Server.Types.Event e, CancellationToken ct)
        => _mumbleEventsChannel.WriteAsync(new UserLeft(e.User.Name), ct).AsTask();

    private async Task FetchUsersAndProduceEvent(UserListRequested userListRequested, Server server, CancellationToken ct)
    {
        var users = await _mumbleGrpcClient.UserQueryAsync(new User.Types.Query { Server = server });

        var usernames = users.Users.Select(x => x.Name).ToArray();

        await _mumbleEventsChannel.WriteAsync(
            new UserListFetched(usernames, userListRequested.RequesterId, userListRequested.MessageId), ct);
    }
}
