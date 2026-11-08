using System;
using System.Collections.Generic;
using System.Text;

namespace DatabaseBackupService.Model
{
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
}
