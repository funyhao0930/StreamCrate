using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using StreamCrate.Core.Models;

namespace StreamCrate.App.Presentation;

internal sealed class SelectableMediaItem : ObservableObject
{
    public SelectableMediaItem(MediaItem media, int playlistIndex)
    {
        Media = media;
        PlaylistIndex = playlistIndex;
        IsSelected = true;
    }

    public MediaItem Media { get; }

    public int PlaylistIndex { get; }

    private bool _isSelected;

    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }
}

internal sealed class PlaylistSelection
{
    public PlaylistSelection(PlaylistInfo playlist)
    {
        Playlist = playlist;
        Items = new ObservableCollection<SelectableMediaItem>(
            playlist.Items.Select((media, index) => new SelectableMediaItem(media, index + 1)));
        foreach (var item in Items)
        {
            item.PropertyChanged += (_, args) =>
            {
                if (args.PropertyName == nameof(SelectableMediaItem.IsSelected))
                {
                    SelectedCountChanged?.Invoke(this, EventArgs.Empty);
                }
            };
        }
    }

    public event EventHandler? SelectedCountChanged;

    public PlaylistInfo Playlist { get; }

    public ObservableCollection<SelectableMediaItem> Items { get; }

    public int SelectedCount => Items.Count(item => item.IsSelected);

    public void SetAllSelected(bool selected)
    {
        foreach (var item in Items)
        {
            item.IsSelected = selected;
        }
    }

    public IReadOnlyList<DownloadRequest> CreateRequests(
        string outputDirectory,
        DownloadFormat format,
        VideoQuality quality,
        CookieSelection cookies) =>
        PlaylistRequestBuilder.Build(
            Items.Select(item => item.Media).ToArray(),
            Items.Select(item => item.IsSelected).ToArray(),
            Playlist.Title,
            outputDirectory,
            format,
            quality,
            cookies);
}

internal static class PlaylistRequestBuilder
{
    public static IReadOnlyList<DownloadRequest> Build(
        IReadOnlyList<MediaItem> items,
        IReadOnlyList<bool> selected,
        string playlistTitle,
        string outputDirectory,
        DownloadFormat format,
        VideoQuality quality,
        CookieSelection cookies)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(selected);
        if (items.Count != selected.Count)
        {
            throw new ArgumentException("播放清單項目與選取狀態數量不一致。", nameof(selected));
        }

        return items
            .Select((media, index) => new { Media = media, IsSelected = selected[index], PlaylistIndex = index + 1 })
            .Where(item => item.IsSelected)
            .Select(item => new DownloadRequest(
                item.Media,
                outputDirectory,
                format,
                quality,
                cookies,
                playlistTitle,
                item.PlaylistIndex))
            .ToArray();
    }
}
