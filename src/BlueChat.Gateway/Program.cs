using BlueChat.Gateway;
using Serilog;

try
{
    var app = GatewayApp.Build(args);
    await app.RunAsync();
}
catch (Exception e) when (e is not HostAbortedException)
{
    Log.Fatal(e, "Gateway stopped");
    Console.Error.WriteLine(e.Message);
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

public partial class Program;
