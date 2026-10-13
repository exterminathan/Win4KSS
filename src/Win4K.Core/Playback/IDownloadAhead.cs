namespace Win4K.Core.Playback;

/// <summary>
/// Gets upcoming clips onto disk before the playlist reaches them, so they play from the
/// cache instead of streaming. This is what makes a slow or flaky origin (archive.org) behave
/// like Apple's CDN: by the time a clip is handed to mpv it is usually already local.
///
/// The seam exists so the playlist engine never knows *how* clips get fetched. The
/// implementation is <see cref="Caching.DownloadQueue"/>, at download-ahead priority (below
/// pins, above background caching of played clips).
///
/// "Upcoming" means the clips *not yet handed to mpv*: a queued entry's URL (disk or stream)
/// is fixed when it is queued, so each upcoming clip gets roughly a clip's running time to
/// land on disk and is then queued as a local file. Archive clips (70–80 MB) comfortably
/// make it; that is what lets them play as reliably as Apple's.
/// </summary>
public interface IDownloadAhead
{
    /// <summary>
    /// Called whenever the queue moves. Idempotent: clips already cached or in flight must be
    /// ignored, because the same clips are requested repeatedly until they are played.
    /// </summary>
    /// <param name="upcoming">Next clips the engine will hand to mpv, nearest first.</param>
    /// <param name="currentIsStreaming">
    /// The clip on screen is streaming, so a download started now competes with it for
    /// bandwidth. Implementations may wait a little in that case.
    /// </param>
    void Request(IReadOnlyList<Clip> upcoming, bool currentIsStreaming);
}
