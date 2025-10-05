using Jellyfin.Data.Entities;
using Jellyfin.Data.Enums;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.Entities;
using MediaBrowser.Controller.Library;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Controller.Persistence;
using MediaBrowser.Controller.Streaming;
using MediaBrowser.Model.Entities;
using MediaBrowser.Model.Tasks;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoViRemux;

public class RemuxLibraryTask(IItemRepository _itemRepo,
                              IMediaSourceManager _sourceManager,
                              ITranscodeManager _transcodeManager,
                              IPluginManager _pluginManager,
                              ILogger<RemuxLibraryTask> _logger,
                              IApplicationPaths _paths,
                              ILibraryManager _libraryManager,
                              IUserDataManager _userDataManager,
                              IUserManager _userManager,
                              DownmuxWorkflow _downmuxWorkflow)
    : IScheduledTask
{
    public string Name => "Remux Dolby Vision MKVs";

    public string Key => nameof(RemuxLibraryTask);

    public string Description => "Remuxes MKVs containing Dolby Vision 8.1 metadata into MKV";

    public string Category => "Dolby Vision Remux Plugin";

    public IEnumerable<TaskTriggerInfo> GetDefaultTriggers() => [];

    public async Task ExecuteAsync(IProgress<double> progress, CancellationToken cancellationToken)
    {
        var configuration = (_pluginManager.GetPlugin(Plugin.OurGuid)?.Instance as Plugin)?.Configuration
            ?? throw new Exception("Can't get plugin configuration");

        var itemsToProcess = _itemRepo.GetItems(new InternalItemsQuery
        {
            MediaTypes = [MediaType.Video],
            AncestorIds = configuration.IncludeAncestors
        })
            .Items
            .Cast<Video>() // has some additional properties (that I don't remember if we use or not)
            .Where(i => !cancellationToken.IsCancellationRequested && ShouldProcessItem(i))
            .ToList();

        var i = 0.0;
        foreach (var item in itemsToProcess)
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                await ProcessOneItem(item, cancellationToken, configuration);
            }
            catch (Exception x)
            {
                _logger.LogWarning(x, "Failed to process {ItemId}", item.Id);
            }

            progress.Report(++i / itemsToProcess.Count * 100);
        }

        if (itemsToProcess.Count > 0)
        {
            _libraryManager.QueueLibraryScan();
        }
    }

    private bool ShouldProcessItem(Video item)
    {
        if (item.Container != "mkv") return false;


        var streams = _sourceManager.GetMediaStreams(item.Id);
        var doviStream = streams.FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video
                                             && s.DvProfile.HasValue);

        // There used to be constraints here about which profiles we support remuxing, but LG TVs aren't consistent about
        // which ones THEY support. Mine stutters when playing profile 7 but some people have success with it,
        // so we'll remux everything just in case.
        //
        // Jellyfin itself will likely not be happy with profile 5, since there's no HDR10 fallback, so things
        // like trickplay/thumbnail images (or even the entire video) may show up as the silly purple-and-green versions.
        if (doviStream?.DvProfile is null) return false;
        if (doviStream.DvProfile != 8) return false;
        return true;
    }

    private async Task ProcessOneItem(Video item, CancellationToken cancellationToken, PluginConfiguration configuration)
    {
        var otherSources = item.GetMediaSources(true);
        var ourSource = otherSources.First(s => s.Container == "mkv");
        var inputPath = ourSource.Path;

        // Downmux to get the new video stream (should output .hevc)
        _logger.LogInformation("Downmuxing {Id} {Name} to Profile 7.6", item.Id, item.Name);
        var downmuxedVideoPath = await _downmuxWorkflow.Downmux(ourSource, cancellationToken);

        // Prepare output path in temp directory
        var tempOutputPath = Path.Combine(_paths.TempDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_dovi76.mkv");

        // Build mkvmerge command: new video + all other streams from original
        var mkvmergeArgs = $"-o \"{tempOutputPath}\" \"{downmuxedVideoPath}\" -D \"{inputPath}\"";

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "mkvmerge",
                Arguments = mkvmergeArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        process.Start();
        string stdOut = await process.StandardOutput.ReadToEndAsync();
        string stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            _logger.LogError("mkvmerge failed: {Error}", stdErr);
            throw new Exception("mkvmerge failed: " + stdErr);
        }

        // Replace original file
        File.Move(tempOutputPath, inputPath, overwrite: true);

        // Cleanup
        File.Delete(downmuxedVideoPath);
    }
}