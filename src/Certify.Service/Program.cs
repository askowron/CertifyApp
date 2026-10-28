using Certify.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

// Tryb jednorazowy: zamowienie certyfikatu z szablonu. Wraca przed uruchomieniem hosta Workera.
if (args.Length > 0 && args[0] == "--request")
{
    var utf8 = new System.Text.UTF8Encoding(false);
    Console.OutputEncoding = utf8;
    Console.SetError(new StreamWriter(Console.OpenStandardError(), utf8) { AutoFlush = true });
    using var cts = new CancellationTokenSource();
    Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
    Environment.ExitCode = await RequestCli.RunAsync(args, Console.Out, Console.Error, cts.Token);
    return;
}

// Usluga Windows odnawiajaca certyfikaty w tle (instalowana z GUI, konto LocalSystem).
// Uruchomiona z konsoli dziala jak zwykly proces (Ctrl+C konczy) - do debugowania.
var builder = Host.CreateApplicationBuilder(new HostApplicationBuilderSettings
{
    Args = args,
    // ContentRoot = katalog exe: SCM startuje usluge w System32.
    ContentRootPath = AppContext.BaseDirectory
});
builder.Services.AddWindowsService(o => o.ServiceName = ServiceInfo.Name);
builder.Services.AddHostedService<RenewalWorker>();
builder.Build().Run();
