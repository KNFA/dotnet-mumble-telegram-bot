using CommandLine;
namespace Knfa.TGM;
public class HostConfiguration
{
    public const string DefaultPath = "/etc/dotnet-mtb.json";

    [Option('c', longName: "configurationFile", Required = false, HelpText = "Path to configuration file", Default = DefaultPath)]
    public string ConfigurationFilePath { get; set; } = null!;
}

public class TelegramConfiguration
{
    public string BotKey { get; set; } = null!;
    public long OwnerId { get; set; }
    public long GroupId { get; set; }
}

public class MumbleConfiguration
{
    public string GrpcAddress { get; set; } = null!;
}

public class ApplicationConfiguration
{
    public TelegramConfiguration Telegram { get; set; } = null!;
    public MumbleConfiguration Mumble { get; set; } = null!;
}
