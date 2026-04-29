using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;

namespace MediaSort.Models;

/// <summary>
/// One cluster of near-duplicate <see cref="MediaItem"/>s, surfaced by the
/// Find-Duplicates flow. The group keeps one item as the "winner" (the one
/// the user wants to keep) and treats the rest as candidates for moving or
/// recycling. The keep choice is observable so the UI can re-render badges
/// when the user clicks a different thumbnail.
/// </summary>
public class DuplicateGroup : INotifyPropertyChanged
{
    private MediaItem? _keep;

    public DuplicateGroup(IEnumerable<MediaItem> members)
    {
        Members = new ObservableCollection<MediaItem>(members);
        // Default keep = largest pixel dimensions (width × height). Ties broken
        // by largest file size, then newest modified date. This matches the
        // "Largest pixel dimensions (Recommended)" default the user picked.
        _keep = Members
            .OrderByDescending(m => (long)m.PixelWidth * m.PixelHeight)
            .ThenByDescending(m => m.SizeBytes)
            .ThenByDescending(m => m.ModifiedDate)
            .FirstOrDefault();
    }

    /// <summary>All items in the group, in detection order.</summary>
    public ObservableCollection<MediaItem> Members { get; }

    /// <summary>The item the user is keeping. Setting raises PropertyChanged so badges update.</summary>
    public MediaItem? Keep
    {
        get => _keep;
        set
        {
            if (ReferenceEquals(_keep, value)) return;
            _keep = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(OthersCount));
            OnPropertyChanged(nameof(HeaderText));
        }
    }

    /// <summary>Items that will be moved/discarded if the user applies the group.</summary>
    public IEnumerable<MediaItem> Others => Members.Where(m => !ReferenceEquals(m, _keep));

    public int OthersCount => Members.Count - (_keep == null ? 0 : 1);

    public string HeaderText
    {
        get
        {
            var name = _keep?.FileName ?? "(none)";
            return $"Group of {Members.Count} · keeping {name}";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
