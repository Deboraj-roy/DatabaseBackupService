using DatabaseBackupService;
using DatabaseBackupService.Model;

//var builder = Host.CreateApplicationBuilder(args);
//builder.Services.AddHostedService<Worker>();

//var host = builder.Build();
//host.Run();


var builder = Host.CreateApplicationBuilder(args);

// Allows the binary to run both as a console app during debugging and a Windows Service in production
builder.Services.AddWindowsService(options =>
{
    options.ServiceName = "SqlDatabaseBackupService";
});

builder.Services.Configure<BackupOptions>(
    builder.Configuration.GetSection(BackupOptions.SectionName));

builder.Services.AddHostedService<SqlBackupWorker>();

var host = builder.Build();
host.Run();

