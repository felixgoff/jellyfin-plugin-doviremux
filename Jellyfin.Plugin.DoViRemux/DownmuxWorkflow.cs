using System.Diagnostics;
using MediaBrowser.Common.Configuration;
using MediaBrowser.Common.Plugins;
using MediaBrowser.Controller.MediaEncoding;
using MediaBrowser.Model.Dto;
using Microsoft.Extensions.Logging;

namespace Jellyfin.Plugin.DoViRemux;

// A super gross C# implementation of the following reddit comment:
// https://old.reddit.com/r/ffmpeg/comments/11gu4o4/comment/jn5gman
//
// We only need to go as far as the mp4box step, and then the main
// RemuxLibraryTask can handle the rest (it just copies the video
// stream from our MP4 instead of the original MKV)
public class DownmuxWorkflow(IPluginManager _pluginManager,
                             ILogger<DownmuxWorkflow> _logger,
                             IApplicationPaths _paths,
                             IMediaEncoder _mediaEncoder)
{
    public async Task<string> Downmux(MediaSourceInfo mediaSource, CancellationToken token)
    {
        var configuration = (_pluginManager.GetPlugin(Plugin.OurGuid)?.Instance as Plugin)?.Configuration
            ?? throw new Exception("Can't get plugin configuration");

        if (!Directory.Exists(_paths.TempDirectory))
        {
            Directory.CreateDirectory(_paths.TempDirectory);
        }

        string doviToolOutputPath = Path.Combine(_paths.TempDirectory, $"dovi_tool_{mediaSource.Id}_{Guid.NewGuid().ToString()[..8]}.hevc");

        // ffmpeg process: outputs HEVC stream to stdout
        using var ffmpeg = new Process()
        {
            StartInfo = new ProcessStartInfo(_mediaEncoder.EncoderPath)
            {
                Arguments = $"-i \"{mediaSource.Path}\" -c:v copy -bsf:v hevc_mp4toannexb -f hevc -",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        _logger.LogInformation("{Command} {Arguments}", ffmpeg.StartInfo.FileName, ffmpeg.StartInfo.Arguments);

        // dovi_tool process: reads from stdin, writes to output file
        using var doviTool = new Process()
        {
            StartInfo = new ProcessStartInfo(configuration.PathToDoviTool)
            {
                Arguments = $"-m 1 convert --discard - -o \"{doviToolOutputPath}\"", // <-- use $ for interpolation
                RedirectStandardInput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            }
        };

        _logger.LogInformation("{Command} {Arguments}", doviTool.StartInfo.FileName, doviTool.StartInfo.Arguments);

        ffmpeg.Start();
        doviTool.Start();

        // Pipe ffmpeg stdout to dovi_tool stdin
        var pipeTask = Task.Run(async () =>
        {
            await ffmpeg.StandardOutput.BaseStream.CopyToAsync(doviTool.StandardInput.BaseStream, token);
            doviTool.StandardInput.BaseStream.Close();
        }, token);

        // Optionally, log errors
        _ = WriteStreamToLog(Path.Combine(_paths.LogDirectoryPath, $"ffmpeg_hevc_{mediaSource.Id}_{Guid.NewGuid().ToString()[..8]}.log"),
                             ffmpeg.StandardError.BaseStream, ffmpeg, token)
            .ConfigureAwait(false);

        _ = WriteStreamToLog(Path.Combine(_paths.LogDirectoryPath, $"dovi_tool_{mediaSource.Id}_{Guid.NewGuid().ToString()[..8]}.log"),
                             doviTool.StandardError.BaseStream, doviTool, token)
            .ConfigureAwait(false);

        await Task.WhenAll(
            ffmpeg.WaitForExitAsync(token),
            doviTool.WaitForExitAsync(token),
            pipeTask
        );

        if (!File.Exists(doviToolOutputPath))
        {
            throw new Exception("HEVC extraction or DoVi conversion failed");
        }

        return doviToolOutputPath;
    }

    private async Task WriteStreamToLog(string logPath, Stream logStream, Process logProcess, CancellationToken token)
    {
            using var writer = new StreamWriter(File.Open(
                logPath,
                FileMode.CreateNew,
                FileAccess.ReadWrite,
                FileShare.Read));

            using var reader = new StreamReader(logStream);

            while (logStream.CanRead && !logProcess.HasExited)
            {
                var line = await reader.ReadLineAsync(token).ConfigureAwait(false);
                if (!writer.BaseStream.CanWrite)
                {
                    break;
                }
                await writer.WriteLineAsync(line).ConfigureAwait(false);
                if (!writer.BaseStream.CanWrite)
                {
                    break;
                }
                await writer.FlushAsync().ConfigureAwait(false);
            }

    }
}