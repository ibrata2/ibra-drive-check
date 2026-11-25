using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Windows.Media;
using IbraDriveCheck.App.Infrastructure;
using IbraDriveCheck.Core.Localization;
using IbraDriveCheck.Core.Model;

namespace IbraDriveCheck.App.ViewModels;

/// <summary>صف في سجل الفحوصات.</summary>
public sealed class HistoryItem
{
    public HistoryItem(ScanResult r) => Result = r;
    public ScanResult Result { get; }
    public string Date => Result.StartedUtc.ToLocalTime().ToString("yyyy-MM-dd  HH:mm");
    public string DriveName => $"{Result.Drive.DisplayName}  {Result.Drive.Letters}".Trim();
    public string KindText => Strings.Kind(Loc.I.Lang, Result.Kind);
    public string VerdictText => Result.Advice != null ? Strings.Level(Loc.I.Lang, Result.Advice.Level) : Strings.Verdict(Loc.I.Lang, Result.Verdict);
    public Brush VerdictBrush => VerdictColors.For(Result.Verdict);
    public string CapacityText =>
        Result.EstimatedRealBytes is long e ? $"{Fmt.Bytes(Result.DeclaredBytes)} → {Fmt.Bytes(e)}" :
        Result.VerifiedBytes is long v ? $"{Fmt.Bytes(v)} ✓" :
        Result.Speed is { } s && s.SeqRead.Measured ? $"R {Fmt.Speed(s.SeqRead.AvgMBps)}" + (s.SeqWrite.Measured ? $" / W {Fmt.Speed(s.SeqWrite.AvgMBps)}" : "") :
        Fmt.Bytes(Result.DeclaredBytes);
}

/// <summary>قائمة السجل المعروضة: تتحدث تلقائياً مع السجل ومع تغيير اللغة.</summary>
public sealed class HistoryList
{
    public static HistoryList I { get; } = new();
    public ObservableCollection<HistoryItem> Items { get; } = new();

    private HistoryList()
    {
        MainViewModel.I.History.CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && e.NewStartingIndex == 0 && e.NewItems?.Count == 1)
                Items.Insert(0, new HistoryItem((ScanResult)e.NewItems[0]!));
            else Rebuild();
        };
        Loc.I.LanguageChanged += Rebuild;
        Rebuild();
    }

    public void Rebuild()
    {
        Items.Clear();
        foreach (var r in MainViewModel.I.History) Items.Add(new HistoryItem(r));
    }
}
