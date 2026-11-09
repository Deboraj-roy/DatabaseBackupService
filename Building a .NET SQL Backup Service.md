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




When your C# Worker Service executes the `BACKUP DATABASE` command, your .NET application does not actually create or write the `.bak` file itself. Instead, it passes the instruction to the **SQL Server Database Engine**, which runs in the background as a separate Windows Service under its own virtual Windows account (such as **NT SERVICE\MSSQLSERVER** or **NT SERVICE\MSSQL$SQLEXPRESS**).

Even if your personal Windows login is an Administrator, SQL Server's virtual service account only has permission to write to its default installation directory. If you point the backup to a custom folder like **D:\SqlBackups** without granting that service account access first, Windows blocks SQL Server from creating the file and throws **Operating System Error 5 (Access is denied)**.

---

In the Windows folder **Security** settings below, notice how the SQL Server service account (**MSSQL$SQLEXPRESS**) appears in the **Group or user names** list at the top, while the **Modify**, **Read & execute**, **List folder contents**, and **Read** checkboxes are enabled under the **Allow** column at the bottom:

---

### How to Grant Folder Permissions to SQL Server

Follow these steps in Windows to find your exact SQL Server service account name and grant it write access to **D:\SqlBackups**:

1. **Find Your Exact SQL Server Service Account:** 2 min.
Press **Win + R** on your keyboard, type **services.msc**, and press **Enter** to open the Windows Services manager. Scroll down to **SQL Server (MSSQLSERVER)** (or **SQL Server (SQLEXPRESS)** if you use Express) and look at the **Log On As** column. Double-click the service, open the **Log On** tab, and copy the exact account name shown in the **This account** box (usually **NT SERVICE\MSSQLSERVER** or **NT SERVICE\MSSQL$SQLEXPRESS**).
*Verification:* You have the exact service account string copied to your clipboard.


2. **Open the Folder Security Settings:** 1 min.
Open File Explorer and create the **D:\SqlBackups** folder if it does not exist yet. Right-click the **SqlBackups** folder, click **Properties**, select the **Security** tab at the top, click the **Edit...** button, and then click **Add...**.
*Verification:* The **Select Users or Groups** dialog window opens.


3. **Set the Search Location to Your Local PC:** 1 min.
If your computer is joined to a workplace domain, click the **Locations...** button inside the **Select Users or Groups** window, select your local computer name at the very top of the directory tree, and click **OK**. (Virtual **NT SERVICE** accounts only exist on the local machine, so searching an Active Directory domain will fail to find them.)
*Verification:* The **From this location** field displays your local computer's hostname.


4. **Add and Check the Service Account Name:** 1 min.
In the **Enter the object names to select** box at the bottom, paste the exact service account name you copied in Step 1 (including the **NT SERVICE** prefix) and click **Check Names**. Click **OK** once the name resolves.
*Verification:* Windows underlines the resolved account name (often shortening the display name to **MSSQLSERVER** or **MSSQL$SQLEXPRESS**) and adds it to the **Group or user names** list.


5. **Enable Modify and Write Permissions:** 1 min.
Click on the newly added **MSSQLSERVER** (or **MSSQL$SQLEXPRESS**) entry in the **Group or user names** list to highlight it. In the bottom **Permissions for** panel, check the **Allow** box next to **Modify** (which automatically selects **Write**, **Read & execute**, **List folder contents**, and **Read**), then click **Apply** and **OK**.
*Verification:* The **Permissions** window closes and the **Security** tab displays checkmarks next to **Modify** and **Write** under the **Allow** column when your SQL Server account is selected.


---

### Immediate Verification Checklist

1. **Test via SQL Server Management Studio (SSMS):** Open SSMS, right-click your database in Object Explorer → **Tasks** → **Back Up...**, remove the default path, click **Add...** to set the destination to **D:\SqlBackups\test.bak**, and click **OK** to confirm the backup completes without an access error.
2. **Test Your .NET Worker Service:** Run your `DatabaseBackupService` project in Visual Studio 2022 and confirm that the timestamped `.bak` file appears inside **D:\SqlBackups**.
3. **Video Walkthrough:** If you run into permission errors while configuring the folder, watch [SQL DBA 81-Cannot Open Backup Device 'BackupLocationPath'](https://www.youtube.com/watch?v=yJIM3i6qEX0) for a visual demonstration of resolving Operating System Error 5 in Windows.


 sc.exe create SqlDatabaseBackupService binPath= "C:\Services\DatabaseBackupService\DatabaseBackupService.exe" start= auto
[SC] OpenSCManager FAILED 5:

Access is denied.


The **Error 5 (Access is denied)** when using `sc.exe` means your current Command Prompt or PowerShell window does not have elevated privileges. Windows strictly requires Administrator rights to register new system services.

You need to close your current terminal and open a new one as an Administrator.

1. **Open an Administrator Terminal:** 1 min.
Click the Windows Start button and type **cmd** (or **PowerShell**). In the search results, right-click **Command Prompt** (or Windows PowerShell) and select **Run as administrator**. Click **Yes** if the Windows User Account Control prompt asks for permission.
*Verification:* The title bar at the top of the terminal window will explicitly say **Administrator: Command Prompt** or **Administrator: Windows PowerShell**.


2. **Run the Service Creation Command:**
Paste your exact command into this new Administrator window and press Enter:

```cmd
sc.exe create SqlDatabaseBackupService binPath= "C:\Services\DatabaseBackupService\DatabaseBackupService.exe" start= auto

```

*Verification:* The terminal will output `[SC] CreateService SUCCESS`.


3. **Start the New Service:**
Once the service is created successfully, command it to start running in the background:

```cmd
sc.exe start SqlDatabaseBackupService

```

*Verification:* The terminal will output a status block containing `STATE : 2 START_PENDING` or `4 RUNNING`.


```
SELECT TOP 100 
    database_name, 
    type, -- 'D' = Full, 'I' = Differential, 'L' = Log
    backup_start_date, 
    backup_finish_date, 
    is_copy_only
FROM msdb.dbo.backupset 
ORDER BY backup_finish_date DESC;
```


```

service is running but backup not completed
cd C:\Services\DatabaseBackupService
PS C:\Services\DatabaseBackupService> sc.exe start SqlDatabaseBackupService

SERVICE_NAME: SqlDatabaseBackupService
        TYPE               : 10  WIN32_OWN_PROCESS
        STATE              : 2  START_PENDING
                                (NOT_STOPPABLE, NOT_PAUSABLE, IGNORES_SHUTDOWN)
        WIN32_EXIT_CODE    : 0  (0x0)
        SERVICE_EXIT_CODE  : 0  (0x0)
        CHECKPOINT         : 0x0
        WAIT_HINT          : 0x7d0
        PID                : 22724
        FLAGS              :
PS C:\Services\DatabaseBackupService>
```




The service is actually running perfectly. The reason no backup was created immediately is because of how the new `Cronos` loop is structured.

When the service starts, it calculates the next scheduled time (e.g., 2:00 AM, 6:00 AM, or 10:00 AM) and immediately executes `await Task.Delay(delay, stoppingToken);`. This puts the worker to sleep *before* it reaches the backup command. It is currently waiting in the background until the clock hits the exact scheduled time.

If you want the service to perform an immediate backup the moment it is started or restarted, and *then* fall into the Cron schedule, you need to add one line of code before the `while` loop begins.

1. **Add the Startup Backup Command:** 2 min.
Open `SqlBackupWorker.cs` in Visual Studio. Inside your `ExecuteAsync` method, add `await RunBackupCycleAsync(stoppingToken);` immediately before the `while` loop starts.

```csharp
protected override async Task ExecuteAsync(CancellationToken stoppingToken)
{
    _logger.LogInformation("Database Backup Service started at: {Time}", DateTimeOffset.Now);

    // ADD THIS LINE: Run a backup immediately when the service starts
    await RunBackupCycleAsync(stoppingToken);

    while (!stoppingToken.IsCancellationRequested)
    {
        var options = _optionsMonitor.CurrentValue;
        var expression = CronExpression.Parse(options.CronSchedule, CronFormat.Standard);
        
        var utcNow = DateTime.UtcNow;
        var nextUtc = expression.GetNextOccurrence(utcNow, TimeZoneInfo.Local);
        
        if (nextUtc == null) return; 

        var delay = nextUtc.Value - utcNow;
        var localNextTime = nextUtc.Value.ToLocalTime();
        
        _logger.LogInformation("Next backup is precisely scheduled for: {NextTime}", localNextTime);

        try
        {
            // Service sleeps here until the next scheduled time
            await Task.Delay(delay, stoppingToken);
        }
        catch (TaskCanceledException)
        {
            break;
        }

        // Runs the scheduled backup after waking up
        await RunBackupCycleAsync(stoppingToken);
    }
}

```

*Verification:* You have exactly two `await RunBackupCycleAsync(stoppingToken);` calls in your method—one before the loop, and one at the end of the loop.


2. **Stop, Publish, and Restart the Service:** 1 min.
Open your Administrator PowerShell window and stop the currently sleeping service, publish the new code, and start it again.

```powershell
sc.exe stop SqlDatabaseBackupService
cd C:\Services\DatabaseBackupService
dotnet publish -c Release -o C:\Services\DatabaseBackupService
sc.exe start SqlDatabaseBackupService

```

*Verification:* Check your `D:\GDrive\SqlBackups` folder. A new `.bak` file will appear immediately. After it finishes, the service will go to sleep and wait for the next Cron schedule.
```

the problem is while i run the app in vs its working and backup to the D:\GDrive\SqlBackups folder.
but when i publish it not backup to the folder whats going wrong?

```
The issue is caused by the `Trusted_Connection=True` setting in your `appsettings.json` connection string, combined with how Windows handles background service identities.

When you run the app in Visual Studio or manually in PowerShell, it executes under your personal Windows user account (`GSA`). Your account automatically has permission to log into SQL Server.

However, when you start it via `sc.exe`, Windows defaults to running the background service under a built-in computer account called **Local System** (`NT AUTHORITY\SYSTEM`). This invisible system account is being blocked by SQL Server because it does not have an authorized SQL login.

The fastest and most reliable fix is to configure the Windows Service to run using your exact Windows credentials, so it perfectly matches your Visual Studio environment.

1. **Open the Services Manager:** 1 min.
Press **Win + R** on your keyboard, type **services.msc**, and press **Enter**.
*Verification:* The Windows Services application opens, displaying a list of all background services.


2. **Open the Service Properties:** 1 min.
Scroll down and find **SqlDatabaseBackupService**. Right-click it and select **Properties**.
*Verification:* The properties dialog box opens to the General tab. If the service is currently running, click the **Stop** button.


3. **Change the Log On Identity:** 1 min.
Click the **Log On** tab at the top. Change the selection from **Local System account** to **This account**.
*Verification:* The username and password text boxes become editable.


4. **Enter Your Windows Credentials:** 1 min.
Type your exact Windows PC login name. (If your user folder is `C:\Users\GSA`, type **.\GSA** into the box. The `.\` tells Windows to look on the local machine). Enter your Windows login password in both password boxes, then click **Apply** and **OK**.
*Verification:* A pop-up message will appear confirming that your account has been granted the "Log on as a service" right.


5. **Restart the Service:**
Right-click **SqlDatabaseBackupService** again and click **Start**.
*Verification:* Because of the startup code we added earlier, you can immediately open your `D:\GDrive\SqlBackups` folder and verify the new `.bak` files are instantly generated.


https://--/062bnbtTd1wv