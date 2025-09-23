using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FFMpegCore;
using FFMpegCore.Enums;

namespace YouTubeNormalizerApp
{
    public class ProcessedFile
    {
        public string OriginalPath { get; set; }
        public string NormalizedPath { get; set; }
        public double OriginalLUFS { get; set; }
    }

    public class YouTubeNormalizer
    {
        private const double TARGET_LUFS = -14.0; // YouTube recommended LUFS
        private readonly string _sourceFolder;
        private readonly string _normalizedFolder;
        private readonly string _ffmpegPath;

        public YouTubeNormalizer(string sourceFolder, string normalizedFolder, string ffmpegPath = "ffmpeg")
        {
            _sourceFolder = sourceFolder ?? throw new ArgumentNullException(nameof(sourceFolder));
            _normalizedFolder = normalizedFolder ?? throw new ArgumentNullException(nameof(normalizedFolder));
            _ffmpegPath = ffmpegPath;

            // Create normalized folder if it doesn't exist
            Directory.CreateDirectory(_normalizedFolder);
        }

        public async Task ProcessVideosAsync()
        {
            try
            {
                Console.WriteLine("Starting YouTube normalization process...");

                // Get video files from files.txt
                var videoFiles = await GetVideoFilesFromListAsync(_sourceFolder);
                if (!videoFiles.Any())
                {
                    Console.WriteLine("No video files found or files.txt is empty.");
                    return;
                }

                Console.WriteLine($"Found {videoFiles.Count} video files to process from files.txt.");

                var processedFiles = new List<ProcessedFile>();

                // Step 1: Calculate LUFS and normalize each file
                foreach (var videoFile in videoFiles)
                {
                    Console.WriteLine($"\nProcessing: {Path.GetFileName(videoFile)}");

                    // Calculate actual LUFS using FFmpeg loudnorm
                    var actualLufs = await CalculateActualLUFSAsync(videoFile);
                    Console.WriteLine($"Measured LUFS: {actualLufs:F2}");

                    var normalizedFile = await NormalizeAudioAsync(videoFile, actualLufs);
                    var duration = await GetVideoDurationAsync(normalizedFile);

                    processedFiles.Add(new ProcessedFile
                    {
                        OriginalPath = videoFile,
                        NormalizedPath = normalizedFile,
                        Duration = duration,
                        OriginalLUFS = actualLufs
                    });

                    Console.WriteLine($"Normalized and saved to: {Path.GetFileName(normalizedFile)}");
                }

                // Step 2: Create chapters file
                await CreateChaptersFileAsync(processedFiles);

                // Step 3: Concatenate all normalized files
                await ConcatenateVideosAsync(processedFiles);

                Console.WriteLine("\nProcess completed successfully!");
                Console.WriteLine($"Final output: {Path.Combine(_normalizedFolder, "final_concatenated.mp4")}");
                Console.WriteLine($"Chapters file: {Path.Combine(_normalizedFolder, "chapters.txt")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during processing: {ex.Message}");
                throw;
            }
        }

        private async Task<List<string>> GetVideoFilesFromListAsync(string folder)
        {
            var filesListPath = Path.Combine(folder, "files.txt");

            if (!File.Exists(filesListPath))
            {
                throw new FileNotFoundException($"files.txt not found in source folder: {filesListPath}");
            }

            var lines = await File.ReadAllLinesAsync(filesListPath);
            var videoFiles = new List<string>();

            foreach (var line in lines)
            {
                var trimmedLine = line.Trim();
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
                {
                    continue; // Skip empty lines and comments
                }

                var fullPath = Path.Combine(folder, trimmedLine);
                if (File.Exists(fullPath))
                {
                    videoFiles.Add(fullPath);
                    Console.WriteLine($"Added to queue: {trimmedLine}");
                }
                else
                {
                    Console.WriteLine($"Warning: File not found: {trimmedLine}");
                }
            }

            return videoFiles;
        }

        private async Task<string> NormalizeAudioAsync(string inputFile, double currentLufs)
        {
            var fileName = Path.GetFileNameWithoutExtension(inputFile);
            var extension = Path.GetExtension(inputFile);
            var outputFile = Path.Combine(_normalizedFolder, $"{fileName}_normalized{extension}");

            // Calculate the adjustment needed
            var adjustment = TARGET_LUFS - currentLufs;

            Console.WriteLine($"Applying volume adjustment: {adjustment:F2} dB");
            Console.WriteLine("Normalizing audio to -14 LUFS for YouTube...");

            try
            {
                await FFMpegArguments
                    .FromFileInput(inputFile)
                    .OutputToFile(outputFile, overwrite: true, options => options
                        .WithCustomArgument("-c:v copy") // Copy video without re-encoding
                        .WithAudioCodec(AudioCodec.Aac)  // Re-encode audio to AAC
                        .WithAudioBitrate(320)           // 320kbps audio
                        .WithCustomArgument($"-af volume={adjustment:F2}dB")) // Apply volume adjustment
                    .NotifyOnProgress(progress =>
                    {
                        Console.Write($"\rProgress: {progress.ToString()}");
                    })
                    .ProcessAsynchronously();

                Console.WriteLine(); // New line after progress
                Console.WriteLine("Normalization completed!");
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Audio normalization failed for {inputFile}: {ex.Message}", ex);
            }

            return outputFile;
        }

        private async Task<TimeSpan> GetVideoDurationAsync(string inputFile)
        {
            try
            {
                Console.WriteLine("Getting video duration...");

                var mediaInfo = await FFProbe.AnalyseAsync(inputFile);
                var duration = mediaInfo.Duration;

                Console.WriteLine($"Duration: {duration:hh\\:mm\\:ss}");
                return duration;
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Could not determine duration for file: {inputFile}. Error: {ex.Message}", ex);
            }
        }



        private async Task ConcatenateVideosAsync(List<ProcessedFile> files)
        {
            var outputPath = Path.Combine(_normalizedFolder, "final_concatenated.mp4");

            try
            {
                Console.WriteLine("Concatenating all normalized videos...");

                await FFMpegArguments
                    .FromConcatInput(files.Select(f => f.NormalizedPath))
                    .OutputToFile(outputPath, overwrite: true, options => options
                        .WithCustomArgument("-c copy")) // Copy all streams - no re-encoding
                    .NotifyOnProgress(progress =>
                    {
                        Console.Write($"\rConcatenation Progress: {progress.ToString()}");
                    })
                    .ProcessAsynchronously();

                Console.WriteLine("\nConcatenation completed successfully!");

                // Show final video info
                if (File.Exists(outputPath))
                {
                    var fileInfo = new FileInfo(outputPath);
                    Console.WriteLine($"Final video size: {fileInfo.Length / (1024 * 1024):F1} MB");

                    // Get final video duration
                    try
                    {
                        var mediaInfo = await FFProbe.AnalyseAsync(outputPath);
                        Console.WriteLine($"Total duration: {mediaInfo.Duration:hh\\:mm\\:ss}");
                    }
                    catch
                    {
                        // Ignore if we can't get duration
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"\nConcatenation failed: {ex.Message}");
                throw;
            }
        }
    }

    class Program
    {
        static async Task Main(string[] args)
        {
            try
            {
                // Configure paths
                var sourceFolder = @"D:\Temp\2024";
                var normalizedFolder = @"D:\Temp\2024\Normalized";
                var ffmpegPath = "ffmpeg"; // or full path like @"C:\Tools\ffmpeg\bin\ffmpeg.exe"

                // Command line arguments override
                if (args.Length >= 2)
                {
                    sourceFolder = args[0];
                    normalizedFolder = args[1];
                }
                if (args.Length >= 3)
                {
                    ffmpegPath = args[2];
                }

                Console.WriteLine($"Source folder: {sourceFolder}");
                Console.WriteLine($"Output folder: {normalizedFolder}");
                Console.WriteLine("Note: Make sure 'files.txt' exists in your source folder.");
                Console.WriteLine();

                if (!Directory.Exists(sourceFolder))
                {
                    Console.WriteLine($"Source folder does not exist: {sourceFolder}");
                    return;
                }

                var normalizer = new YouTubeNormalizer(sourceFolder, normalizedFolder, ffmpegPath);
                await normalizer.ProcessVideosAsync();

                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Application error: {ex.Message}");
                Console.WriteLine("\nPress any key to exit...");
                Console.ReadKey();
            }
        }
    }
}