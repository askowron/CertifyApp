using Certify.Service;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

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
