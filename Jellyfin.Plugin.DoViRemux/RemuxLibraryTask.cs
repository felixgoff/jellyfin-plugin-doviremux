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
using System.Diagnostics;

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
        if (doviStream.BlPresentFlag != 1) return false;
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
        var source = item.GetMediaSources(true).FirstOrDefault(s => s.Container == "mkv");
        if (source == null)
        {
            _logger.LogWarning("No MKV media source found for item {ItemId}", item.Id);
            return;
        }
        var inputPath = source.Path;
        var tempDir = _paths.TempDirectory;
        var baseName = Path.GetFileNameWithoutExtension(inputPath);

        var tempHevcPath = Path.Combine(tempDir, $"{baseName}_hdr10.hevc");
        var tempOutputPath = Path.Combine(tempDir, $"{baseName}_hdr10.mkv");

        // 1. ffmpeg process: outputs HEVC stream to stdout
        using var ffmpeg = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(_mediaEncoder.EncoderPath)
            {
                Arguments = $"-nostdin -loglevel error -y -i \"{inputPath}\" -map 0:v:0 -c copy -bsf:v hevc_mp4toannexb -f hevc -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        // 2. dovi_tool process: reads from stdin, writes to output file
        using var doviTool = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo(configuration.PathToDoviTool)
            {
                Arguments = $"remove -i - -o \"{tempHevcPath}\"",
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        _logger.LogInformation("Extracting video stream and converting to HDR10: {FfmpegCmd} | {DoviToolCmd}", ffmpeg.StartInfo.Arguments, doviTool.StartInfo.Arguments);

        ffmpeg.Start();
        doviTool.Start();

        // Pipe ffmpeg stdout to dovi_tool stdin
        await ffmpeg.StandardOutput.BaseStream.CopyToAsync(doviTool.StandardInput.BaseStream, cancellationToken);
        ffmpeg.StandardOutput.Close();
        doviTool.StandardInput.Close();
        _ = WriteStreamToLog(Path.Combine(_paths.LogDirectoryPath, $"ffmpeg_hevc_{source.Id}_{Guid.NewGuid().ToString()[..8]}.log"),
                             ffmpeg.StandardError.BaseStream, ffmpeg, cancellationToken)
            .ConfigureAwait(false);

        string ffmpegStdErr = await ffmpeg.StandardError.ReadToEndAsync();
        string doviStdErr = await doviTool.StandardError.ReadToEndAsync();

        await ffmpeg.WaitForExitAsync(cancellationToken);
        await doviTool.WaitForExitAsync(cancellationToken);

        if (ffmpeg.ExitCode != 0)
        {
            _logger.LogError("ffmpeg extraction failed: {Error}", ffmpegStdErr);
            throw new Exception("ffmpeg extraction failed: " + ffmpegStdErr);
        }
        if (doviTool.ExitCode != 0)
        {
            _logger.LogError("dovi_tool remove failed: {Error}", doviStdErr);
            throw new Exception("dovi_tool remove failed: " + doviStdErr);
        }

        // 3. Use mkvmerge to combine the original MKV's non-video streams with the new HDR10 video stream
        var mkvmergeArgs = $"-o \"{tempOutputPath}\" --no-video \"{inputPath}\" \"{tempHevcPath}\"";
        var mkvmergeProcess = new System.Diagnostics.Process
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
        _logger.LogInformation("Merging HDR10 video with original streams: {Command} {Arguments}", mkvmergeProcess.StartInfo.FileName, mkvmergeProcess.StartInfo.Arguments);
        mkvmergeProcess.Start();
        string mkvmergeStdOut = await mkvmergeProcess.StandardOutput.ReadToEndAsync();
        string mkvmergeStdErr = await mkvmergeProcess.StandardError.ReadToEndAsync();
        await mkvmergeProcess.WaitForExitAsync(cancellationToken);
        if (mkvmergeProcess.ExitCode != 0)
        {
            _logger.LogError("mkvmerge failed: {Error}", mkvmergeStdErr);
            throw new Exception("mkvmerge failed: " + mkvmergeStdErr);
        }

        // Replace original file
        File.Move(tempOutputPath, inputPath, overwrite: true);

        // Cleanup
        File.Delete(tempHevcPath);
    }
    private async Task WriteStreamToLog(string logPath, Stream logStream, Process logProcess, CancellationToken token)
    {
        try
        {
            using var writer = new StreamWriter(File.Open(
                logPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.Read));
            using var reader = new StreamReader(logStream);

            while (!token.IsCancellationRequested)
            {
                // Drain remaining lines even after process exits
                if (reader.EndOfStream)
                {
                    if (logProcess.HasExited) break;
                    // wait briefly for more output
                    await Task.Delay(50, token).ConfigureAwait(false);
                    continue;
                }

                var line = await reader.ReadLineAsync().ConfigureAwait(false);
                if (line is null)
                {
                    if (logProcess.HasExited) break;
                    await Task.Delay(50, token).ConfigureAwait(false);
                    continue;
                }

                await writer.WriteLineAsync(line).ConfigureAwait(false);
                await writer.FlushAsync().ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
            // expected on cancellation - do not treat as error
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "WriteStreamToLog failed for {LogPath}", logPath);
        }
    }
}