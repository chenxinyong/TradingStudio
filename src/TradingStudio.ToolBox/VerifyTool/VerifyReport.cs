namespace TradingStudio.ToolBox.VerifyTool;

public enum VerifyRating { Clean, Warning, Severe }
public enum DimensionStatus { Pass, Warn, Fail, Skip }

public class VerifyReport
{
    public string DbPath { get; set; } = "";
    public DateTime VerifiedAt { get; set; } = DateTime.Now;
    public long FileSizeBytes { get; set; }
    public DateTime DateMin { get; set; }
    public DateTime DateMax { get; set; }

    public TableStats Bars1Min { get; set; } = new();
    public TableStats BarsDay { get; set; } = new();

    public DimensionResult Completeness { get; set; } = new() { Label = "Completeness" };
    public DimensionResult Consistency { get; set; } = new() { Label = "Consistency" };
    public DimensionResult Continuity { get; set; } = new() { Label = "Continuity" };
    public DimensionResult Accuracy { get; set; } = new() { Label = "Accuracy" };
    public DimensionResult TradingDay { get; set; } = new() { Label = "TradingDay" };
    public DimensionResult Dedup { get; set; } = new() { Label = "Dedup" };

    public VerifyRating Rating
    {
        get
        {
            var dims = new[] { Completeness, Consistency, Continuity, Accuracy, TradingDay, Dedup };
            if (dims.Any(d => d.Status == DimensionStatus.Fail)) return VerifyRating.Severe;
            if (dims.Any(d => d.Status == DimensionStatus.Warn)) return VerifyRating.Warning;
            return VerifyRating.Clean;
        }
    }

    public DimensionResult[] AllDimensions() => new[] { Completeness, Consistency, Continuity, Accuracy, TradingDay, Dedup };
}

public class TableStats
{
    public long RowCount { get; set; }
    public int InstrumentCount { get; set; }
    public string DateMin { get; set; } = "";
    public string DateMax { get; set; } = "";
    public List<string> TopInstruments { get; set; } = new();
}

public class DimensionResult
{
    public string Label { get; set; } = "";
    public DimensionStatus Status { get; set; }
    public string Summary { get; set; } = "";
    public List<string> Details { get; set; } = new();
    public int IssueCount { get; set; }
}
