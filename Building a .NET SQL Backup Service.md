Building a production-grade automated SQL Server backup service in C# and .NET is best achieved using a **.NET Worker Service** (`BackgroundService`) paired with `Microsoft.Data.SqlClient` and `PeriodicTimer`. Instead of using heavy legacy SMO (SQL Server Management Objects) libraries, executing a parameterized T-SQL `BACKUP DATABASE` command directly via ADO.NET gives you complete control over timeouts, compression, checksum verification, and asynchronous cancellation.

### Production Architecture Checklist

Before writing code, a production-ready backup service must account for four critical operational details:

* **Permissions & Path Resolution:** The `BACKUP DATABASE` command is executed by the **SQL Server Database Engine**, not the .NET process. The target folder path must be accessible to the SQL Server service account (e.g., `NT SERVICE\MSSQLSERVER`)—or mapped to a shared volume if your SQL Server runs inside a Docker container.
* **Extended Command Timeouts:** Default ADO.NET command timeouts (30 seconds) will cause medium-to-large database backups to fail mid-operation. The timeout must be explicitly configured.
* **Data Integrity & Storage Optimization:** Production backups should enforce `CHECKSUM` to detect page corruption and `COMPRESSION` to minimize disk I/O and storage footprint.
* **Retention Policy:** Automated schedules will quickly exhaust disk space unless paired with an automated cleanup routine that purges `.bak` files older than a defined threshold.

---

1. **Initialize the Worker Service Project & Packages:** Run in your terminal or Package Manager Console.
Create a dedicated .NET Worker Service project (or integrate the classes directly into your existing ASP.NET Core Web API project) and add the SQL client and Windows Service hosting extensions:

```bash
dotnet new worker -n DatabaseBackupService
cd DatabaseBackupService
dotnet add package Microsoft.Data.SqlClient
dotnet add package Microsoft.Extensions.Hosting.WindowsServices

```


2. **Configure Backup Settings in appsettings.json:** Ensure the target path is writable by the SQL Server service instance.
Define strongly-typed configuration settings for the database targets, schedule interval, command timeout, and retention period in `appsettings.json`:

```json
{
  "Logging": {
    "LogLevel": {
      "Default": "Information",
      "Microsoft.Hosting.Lifetime": "Information"
    }
  },
  "ConnectionStrings": {
    "MasterDatabase": "Server=localhost;Database=master;Trusted_Connection=True;TrustServerCertificate=True;"
  },
  "BackupOptions": {
    "Databases": [ "CPD_V2_DB" ],
    "BackupFolderPath": "D:\\SqlBackups",
    "IntervalHours": 24,
    "RetentionDays": 14,
    "CommandTimeoutSeconds": 3600,
    "UseCompression": true
  }
}

```


3. **Create the Strongly-Typed Configuration Model:**
Create `BackupOptions.cs` to bind and validate your configuration settings:

```csharp
namespace DatabaseBackupService;

public sealed class BackupOptions
{
    public const string SectionName = "BackupOptions";

    public List<string> Databases { get; set; } = new();
    public string BackupFolderPath { get; set; } = string.Empty;
    public int IntervalHours { get; set; } = 24;
    public int RetentionDays { get; set; } = 14;
    public int CommandTimeoutSeconds { get; set; } = 3600;
    public bool UseCompression { get; set; } = true;
}

```


4. **Implement the Background Backup Worker:** Uses PeriodicTimer for non-blocking execution and handles old file retention.
Replace `Worker.cs` with `SqlBackupWorker.cs`. This implementation executes full backups with `CHECKSUM`, verifies the backup header/media integrity using `RESTORE VERIFYONLY`, and cleans up expired `.bak` files:

```csharp
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace DatabaseBackupService;

public sealed class SqlBackupWorker : BackgroundService
{
    private readonly ILogger<SqlBackupWorker> _logger;
    private readonly IConfiguration _configuration;
    private readonly IOptionsMonitor<BackupOptions> _optionsMonitor;

    public SqlBackupWorker(
        ILogger<SqlBackupWorker> logger,
        IConfiguration configuration,
        IOptionsMonitor<BackupOptions> optionsMonitor)
    {
        _logger = logger;
        _configuration = configuration;
        _optionsMonitor = optionsMonitor;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("Database Backup Service started at: {Time}", DateTimeOffset.Now);

        // Execute an initial check/backup on startup, then wait for the periodic timer
        await RunBackupCycleAsync(stoppingToken);

        var interval = TimeSpan.FromHours(_optionsMonitor.CurrentValue.IntervalHours);
        using var timer = new PeriodicTimer(interval);

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
            {
                await RunBackupCycleAsync(stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Database Backup Service is stopping gracefully.");
        }
    }

    private async Task RunBackupCycleAsync(CancellationToken cancellationToken)
    {
        var options = _optionsMonitor.CurrentValue;
        var connectionString = _configuration.GetConnectionString("MasterDatabase");

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            _logger.LogError("Connection string 'MasterDatabase' is missing.");
            return;
        }

        // Ensure local directory exists if .NET and SQL Server share the same host filesystem
        if (!Directory.Exists(options.BackupFolderPath))
        {
            Directory.CreateDirectory(options.BackupFolderPath);
        }

        foreach (var dbName in options.Databases)
        {
            if (cancellationToken.IsCancellationRequested) break;

            await BackupSingleDatabaseAsync(connectionString, dbName, options, cancellationToken);
        }

        PurgeExpiredBackups(options);
    }

    private async Task BackupSingleDatabaseAsync(
        string connectionString,
        string dbName,
        BackupOptions options,
        CancellationToken cancellationToken)
    {
        string timestamp = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss");
        string fileName = $"{dbName}_Full_{timestamp}.bak";
        string fullBackupPath = Path.Combine(options.BackupFolderPath, fileName);

        try
        {
            _logger.LogInformation("Starting backup for database [{Database}] to {Path}", dbName, fullBackupPath);

            await using var connection = new SqlConnection(connectionString);
            await connection.OpenAsync(cancellationToken);

            // Validate and quote the database identifier safely using T-SQL QUOTENAME
            string compressionOption = options.UseCompression ? ", COMPRESSION" : string.Empty;
            string backupSql = $@"
                DECLARE @SafeDbName sysname = QUOTENAME(@DatabaseName);
                DECLARE @Sql nvarchar(max) = N'BACKUP DATABASE ' + @SafeDbName + N' 
                    TO DISK = @BackupPath 
                    WITH FORMAT, INIT, 
                    NAME = @BackupName, 
                    SKIP, NOREWIND, NOUNLOAD, CHECKSUM, STATS = 10{compressionOption};';
                EXEC sp_executesql @Sql, 
                    N'@BackupPath nvarchar(500), @BackupName nvarchar(200)', 
                    @BackupPath = @Path, 
                    @BackupName = @Name;

                RESTORE VERIFYONLY FROM DISK = @Path WITH CHECKSUM;";

            await using var command = new SqlCommand(backupSql, connection)
            {
                CommandTimeout = options.CommandTimeoutSeconds
            };

            command.Parameters.AddWithValue("@DatabaseName", dbName);
            command.Parameters.AddWithValue("@Path", fullBackupPath);
            command.Parameters.AddWithValue("@Name", $"{dbName}-Full Database Backup ({timestamp})");

            await command.ExecuteNonQueryAsync(cancellationToken);

            _logger.LogInformation("Successfully backed up and verified [{Database}] -> {FileName}", dbName, fileName);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to backup database [{Database}]", dbName);
        }
    }

    private void PurgeExpiredBackups(BackupOptions options)
    {
        try
        {
            var directoryInfo = new DirectoryInfo(options.BackupFolderPath);
            if (!directoryInfo.Exists) return;

            DateTime cutoffDate = DateTime.UtcNow.AddDays(-options.RetentionDays);
            var expiredFiles = directoryInfo.GetFiles("*.bak")
                .Where(file => file.CreationTimeUtc < cutoffDate);

            foreach (var file in expiredFiles)
            {
                file.Delete();
                _logger.LogInformation("Deleted expired backup file: {FileName}", file.Name);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Could not complete retention cleanup in {Folder}", options.BackupFolderPath);
        }
    }
}

```


5. **Register the Worker and Host Configuration in Program.cs:**
Wire up the configuration bindings, Windows Service lifetime support, and hosted service in `Program.cs`:

```csharp
using DatabaseBackupService;

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

```


6. **Grant Folder Permissions & Deploy:** Prevents Operating System Error 5 (Access is Denied) during SQL backup.
Verify filesystem permissions and publish the binary for deployment:

1. **Grant SQL Server Write Access (Windows Host):** Right-click your target backup folder (`D:\SqlBackups`) -> **Properties** -> **Security** -> **Edit** -> **Add**, and grant **Modify/Write** permissions to your SQL Server service account (typically `NT SERVICE\MSSQLSERVER` or `NT SERVICE\MSSQL$SQLEXPRESS`).
2. **Docker Volume Mapping (If SQL Server runs in Docker):** Set `BackupFolderPath` in `appsettings.json` to the internal container path (e.g., `/var/opt/mssql/backup`) that is bind-mounted to your host Windows directory, and handle retention cleanup on the host path.
3. **Publish and Register as a Windows Service:**

```powershell
dotnet publish -c Release -o C:\Services\DatabaseBackupService
sc.exe create SqlDatabaseBackupService binPath= "C:\Services\DatabaseBackupService\DatabaseBackupService.exe" start= auto
sc.exe start SqlDatabaseBackupService

```


---

### Verification Steps

1. Run the project directly in Visual Studio or via `dotnet run` to trigger the immediate startup backup cycle.
2. Check the console logs for `Successfully backed up and verified` and inspect your `BackupFolderPath` to confirm the timestamped `.bak` file was created.
3. For a visual walkthrough of integrating scheduled database jobs directly inside a Web API host, watch [.NET Core Background Service | Automate Database Tasks in Web API](https://www.youtube.com/watch?v=z4F1cSolknQ), which demonstrates setting up automated background database tasks in .NET Core.


