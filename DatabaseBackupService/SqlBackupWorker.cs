using Cronos;
using DatabaseBackupService.Model;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;
using System;
using System.Collections.Generic;
using System.Text;

namespace DatabaseBackupService
{
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

        /// <summary>
        /// The main background task for the database backup service.
        /// </summary>
        /// <param name="cancellationToken"></param>
        /// <returns></returns>
        //protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        //{
        //    _logger.LogInformation("Database Backup Service started at: {Time}", DateTimeOffset.Now);

        //    // Execute an initial check/backup on startup, then wait for the periodic timer
        //    await RunBackupCycleAsync(stoppingToken);

        //    var interval = TimeSpan.FromHours(_optionsMonitor.CurrentValue.IntervalHours);
        //    using var timer = new PeriodicTimer(interval);

        //    try
        //    {
        //        while (await timer.WaitForNextTickAsync(stoppingToken))
        //        {
        //            await RunBackupCycleAsync(stoppingToken);
        //        }
        //    }
        //    catch (OperationCanceledException)
        //    {
        //        _logger.LogInformation("Database Backup Service is stopping gracefully.");
        //    }
        //}

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("Database Backup Service started at: {Time}", DateTimeOffset.Now);

            while (!stoppingToken.IsCancellationRequested)
            {
                var options = _optionsMonitor.CurrentValue;

                // Parse the standard 5-part Cron expression from appsettings
                var expression = CronExpression.Parse(options.CronSchedule, CronFormat.Standard);

                // Calculate next occurrence using your server's Local Time Zone
                var utcNow = DateTime.UtcNow;
                var nextUtc = expression.GetNextOccurrence(utcNow, TimeZoneInfo.Local);

                if (nextUtc == null)
                {
                    _logger.LogWarning("Cron expression is invalid or will never fire.");
                    return;
                }

                var delay = nextUtc.Value - utcNow;
                var localNextTime = nextUtc.Value.ToLocalTime();

                _logger.LogInformation("Next backup is precisely scheduled for: {NextTime}", localNextTime);

                try
                {
                    // Sleep silently in the background until the exact Cron time is reached
                    await Task.Delay(delay, stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    _logger.LogInformation("Database Backup Service is stopping gracefully.");
                    break;
                }

                // The exact time has been reached, execute the backup
                await RunBackupCycleAsync(stoppingToken);
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
}
