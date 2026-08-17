namespace StreamCrate.Core.Models;

public sealed record ProbeResult(MediaItem? Media, PlaylistInfo? Playlist)
{
    public bool IsPlaylist => Playlist is not null;
}
