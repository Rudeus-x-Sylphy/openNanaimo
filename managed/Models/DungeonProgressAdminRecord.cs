using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace OpenNanaimo.Adapter.Models;

public sealed class DungeonDifficultyPerformanceAdminRecord
{
    public byte Difficulty { get; init; }
    public int BestScore { get; init; }
    public int? BestElapsedMinutes { get; init; }
    public DateTime? ClearedAt { get; init; }
    public DateTime? UpdatedAt { get; init; }
}

public sealed class DungeonProgressAdminRecord : INotifyPropertyChanged
{
    private int _selectedDifficulty;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long CharacterId { get; init; }
    public byte Episode { get; init; }
    public byte Dungeon { get; init; }
    public bool IsSuperBoss { get; init; }
    public byte ClearMask { get; init; }
    public byte BestRatings { get; init; }
    public IReadOnlyList<DungeonDifficultyPerformanceAdminRecord> DifficultyPerformances { get; init; } = [];

    public int SelectedDifficulty => _selectedDifficulty;
    public int BestScore => CurrentPerformance?.BestScore ?? 0;
    public int? BestElapsedMinutes => CurrentPerformance?.BestElapsedMinutes;
    public DateTime? ClearedAt => CurrentPerformance?.ClearedAt;
    public DateTime? UpdatedAt => CurrentPerformance?.UpdatedAt;

    public int EpisodeDisplay => Episode + 1;
    public int DungeonDisplay => Dungeon + 1;
    public string DungeonArchiveDisplay => IsSuperBoss
        ? "挑战超级BOSS"
        : $"地宫 {DungeonDisplay}";
    public string Difficulty1Status => IsCleared(0) ? "已通过" : "未通过";
    public string Difficulty2Status => IsCleared(1) ? "已通过" : "未通过";
    public string Difficulty3Status => IsCleared(2) ? "已通过" : "未通过";
    public string BestElapsedText => BestElapsedMinutes is int minutes ? $"{minutes} 分钟" : "-";
    public string UpdatedAtText => UpdatedAt?.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss") ?? "-";

    public bool IsCleared(int difficulty) => (ClearMask & (1 << difficulty)) != 0;

    public void SelectDifficulty(int difficulty)
    {
        difficulty = Math.Clamp(difficulty, 0, 2);
        if (_selectedDifficulty == difficulty)
            return;
        _selectedDifficulty = difficulty;
        OnPropertyChanged(nameof(SelectedDifficulty));
        OnPropertyChanged(nameof(BestScore));
        OnPropertyChanged(nameof(BestElapsedMinutes));
        OnPropertyChanged(nameof(ClearedAt));
        OnPropertyChanged(nameof(UpdatedAt));
        OnPropertyChanged(nameof(BestElapsedText));
        OnPropertyChanged(nameof(UpdatedAtText));
    }

    private DungeonDifficultyPerformanceAdminRecord? CurrentPerformance =>
        DifficultyPerformances.FirstOrDefault(item => item.Difficulty == _selectedDifficulty);

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}
