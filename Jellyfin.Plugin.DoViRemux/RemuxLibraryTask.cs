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
                              IMediaEncoder _mediaEncoder,
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
        _logger.LogInformation("RemuxLibraryTask started.");

        try
        {
            var configuration = (_pluginManager.GetPlugin(Plugin.OurGuid)?.Instance as Plugin)?.Configuration
                ?? throw new Exception("Can't get plugin configuration");

            var itemsToProcess = _itemRepo.GetItems(new InternalItemsQuery
            {
                MediaTypes = [MediaType.Video],
                AncestorIds = configuration.IncludeAncestors
            })
                .Items
                .Cast<Video>()
                .Where(i =>
                {
                    if (cancellationToken.IsCancellationRequested) return false;
                    var streams = _sourceManager.GetMediaStreams(i.Id);
                    var doviStream = streams.FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video && s.DvProfile.HasValue);
                    // Only include videos that are DV8 (for either remux or HDR10 fallback)
                    return doviStream?.DvProfile == 8;
                })
                .ToList();

            if (itemsToProcess.Count == 0)
            {
                _logger.LogInformation("No items found to process.");
            }

            var i = 0.0;
            foreach (var item in itemsToProcess)
            {
                cancellationToken.ThrowIfCancellationRequested();

                try
                {
                    if (ShouldProcessItem(item))
                    {
                        await ProcessOneItem(item, cancellationToken, configuration);
                        _logger.LogInformation("Processed {ItemId}", item.Id);
                    }
                    else
                    {
                        // Fallback: If DV8 but not remuxable, try HDR10 conversion
                        var streams = _sourceManager.GetMediaStreams(item.Id);
                        var doviStream = streams.FirstOrDefault(s => s.Type == MediaBrowser.Model.Entities.MediaStreamType.Video && s.DvProfile.HasValue);
                        if (doviStream?.DvProfile == 8)
                        {
                            _logger.LogInformation("Attempting DV8 to HDR10 conversion for {ItemId}", item.Id);
                            await ConvertDv8ToHdr10(item, configuration, cancellationToken);
                            _logger.LogInformation("DV8 to HDR10 conversion complete for {ItemId}", item.Id);
                        }
                        else
                        {
                            _logger.LogInformation("Skipping {ItemId}: Not eligible for remux or HDR10 conversion.", item.Id);
                        }
                    }
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

            _logger.LogInformation("RemuxLibraryTask finished.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("RemuxLibraryTask was cancelled.");
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "RemuxLibraryTask failed with an unhandled exception.");
            throw;
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
        if (doviStream.DvBlSignalCompatibilityId != 1) return false;
        if (doviStream.DvProfile != 8) return false;
        if (doviStream.BlPresentFlag != 1 )return false;
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
                FileName = configuration.PathTomkvmerge,
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

    private async Task ConvertDv8ToHdr10(Video item, PluginConfiguration configuration, CancellationToken cancellationToken)
    {
        var source = item.GetMediaSources(true).First(s => s.Container == "mkv");
        var inputPath = source.Path;
        var tempOutputPath = Path.Combine(_paths.TempDirectory, Path.GetFileNameWithoutExtension(inputPath) + "_hdr10.mp4");

        // ffmpeg command adapted from your Python example
        var ffmpegArgs = $"-nostdin -loglevel error -stats -y -i \"{inputPath}\" " +
            "-vf \"hwupload,libplacebo=peak_detect=false:colorspace=9:color_primaries=9:color_trc=16:range=tv:format=yuv420p10le,hwdownload,format=yuv420p10le\" " +
            "-c:v libx265 -map_chapters -1 -an -sn -b:v 12000k " +
            "-x265-params \"repeat-headers=1:sar=1:hrd=1:aud=1:open-gop=0:hdr10=1:sao=0:rect=0:cutree=0:deblock=-3-3:strong-intra-smoothing=0:chromaloc=2:aq-mode=1:vbv-maxrate=160000:vbv-bufsize=160000:max-luma=1023:max-cll=0,0:master-display=G(8500,39850)B(6550,23000)R(35400,15650)WP(15635,16450)L(10000000,1)WP(15635,16450)L(1000000,100%):preset=slow\" " +
            $"\"{tempOutputPath}\"";

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = _mediaEncoder.EncoderPath, // Make sure this is set in your config
                Arguments = ffmpegArgs,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        _logger.LogInformation("Converting DV8 to HDR10: {Command} {Arguments}", process.StartInfo.FileName, process.StartInfo.Arguments);

        process.Start();
        string stdOut = await process.StandardOutput.ReadToEndAsync();
        string stdErr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync(cancellationToken);

        if (process.ExitCode != 0)
        {
            _logger.LogError("ffmpeg HDR10 conversion failed: {Error}", stdErr);
            throw new Exception("ffmpeg HDR10 conversion failed: " + stdErr);
        }

        // Replace original file
        File.Move(tempOutputPath, inputPath, overwrite: true);
    }
}