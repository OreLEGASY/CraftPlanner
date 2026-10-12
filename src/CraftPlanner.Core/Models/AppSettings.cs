namespace CraftPlanner.Core.Models;

/// <summary>アプリ全体の設定。開いているタブなど再起動後の復元に必要な情報を持つ。</summary>
public sealed class AppSettings
{
    public const int CurrentFormatVersion = 1;

    public int FormatVersion { get; set; } = CurrentFormatVersion;

    /// <summary>開いているタブの計画 ID（表示順）。</summary>
    public List<string> OpenPlanIds { get; set; } = new();

    public string? ActivePlanId { get; set; }

    public string? LastGameDataId { get; set; }

    /// <summary>バックアップの保持世代数（ファイルごと）。</summary>
    public int BackupGenerations { get; set; } = 10;

    public double? WindowWidth { get; set; }
    public double? WindowHeight { get; set; }
    public bool WindowMaximized { get; set; }
}
