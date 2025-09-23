using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Diagnostics.Metrics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using FFMpegCore;
using FFMpegCore.Enums;

namespace YouTubeNormalizerApp
{
    public class ProcessedFile
    {
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public string OriginalPath { get; set; }
#pragma warning disable CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public string NormalizedPath { get; set; }
#pragma warning restore CS8618 // Non-nullable field must contain a non-null value when exiting constructor. Consider declaring as nullable.
        public double MeasuredI { get; set; }
        public double MeasuredTp { get; set; }
        public double MeasuredLRA { get; set; }
        public double MeasuredThresh { get; set; }
        public double Offset { get; set; }
    }

    public class YouTubeNormalizer
    {
        private const double TARGET_LUFS = -14.0; // YouTube recommended LUFS
        private readonly string _sourceFolder;
        private readonly string _normalizedFolder;
        private readonly string _ffmpegPath;
        private double DEFAULT_SOURCE_LUFS =-14;

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
                    var actualLufs = await CalculateLUFSAsync(videoFile);
                    Console.WriteLine($"Measured LUFS: {actualLufs:F2}");

                    var normalizedFile = await NormalizeAudioAsync(videoFile, actualLufs);

                    processedFiles.Add(new ProcessedFile
                    {
                        OriginalPath = videoFile,
                        NormalizedPath = normalizedFile,
                        MeasuredI = actualLufs.MeasuredI
                    });

                    Console.WriteLine($"Normalized and saved to: {Path.GetFileName(normalizedFile)}");
                }

 

                // Step 3: Concatenate all normalized files
                await ConcatenateVideosAsync(processedFiles);
                Console.WriteLine("\nProcess completed successfully!");
                Console.WriteLine($"Final output: {Path.Combine(_normalizedFolder, "final_concatenated.mp4")}");
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error during processing: {ex.Message}");
                throw;
            }
        }
        public class LoudnormMeasurements
        {
            public double MeasuredI { get; set; }
            public double MeasuredTp { get; set; }
            public double MeasuredLRA { get; set; }
            public double MeasuredThresh { get; set; }
            public double Offset { get; set; }
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


        private async Task<LoudnormMeasurements> CalculateLUFSAsync(string inputFile)
        {
            try
            {
                var stderr = new StringBuilder();
                var stdout = new StringBuilder();

                Console.WriteLine("Measuring LUFS with loudnorm filter...");

                // Since FFMpegCore doesn't expose stderr directly, we need to use Process
                // to capture the JSON output from loudnorm
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "ffmpeg",
                        Arguments = $"-i \"{inputFile}\" -af loudnorm=print_format=json -f null -",
                        UseShellExecute = false,
                        RedirectStandardError = true,
                        RedirectStandardOutput = true,
                        CreateNoWindow = true
                    }
                };

                process.OutputDataReceived += (sender, e) => {
                    if (e.Data != null) {stdout.AppendLine(e.Data); ; Console.WriteLine(e.Data);
                }
                };

                process.ErrorDataReceived += (sender, e) => {
                    if (e.Data != null) { stderr.AppendLine(e.Data); Console.WriteLine(e.Data); }

                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();


                if (!process.WaitForExit(300000)) // 5 minutes timeout
                {
                    process.Kill();
                    throw new TimeoutException("LUFS analysis timed out");
                }

                var stderrContent = stderr.ToString();

                // Parse the JSON output from stderr
                var lufs = ParseLUFSFromJson(stderrContent);
                Console.WriteLine($"Measured LUFS: {lufs.MeasuredI:F2}");
                return lufs;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LUFS measurement failed: {ex.Message}");
                Console.WriteLine($"Using default LUFS: {DEFAULT_SOURCE_LUFS:F2}");
                return null;
            }
        }



        private LoudnormMeasurements ParseLUFSFromJson(string stderr)
        {
            try
            {
                // Look for the JSON block in stderr output
                // FFmpeg outputs the JSON between specific markers
                var jsonMatch = Regex.Match(stderr, @"\{[^}]*""input_i""[^}]*\}", RegexOptions.Singleline);

                if (!jsonMatch.Success)
                {
                    Console.WriteLine("Could not find JSON output in FFmpeg stderr");
                    return null;
                }

                var jsonString = jsonMatch.Value;
                Console.WriteLine($"Found loudnorm JSON: {jsonString}");

                // Parse the JSON to extract the input_i value (integrated loudness/LUFS)
                using var document = JsonDocument.Parse(jsonString);

                return new LoudnormMeasurements
                {
                    MeasuredI = double.Parse(document.RootElement.GetProperty("input_i").GetString()),
                    MeasuredTp = double.Parse(document.RootElement.GetProperty("input_tp").GetString()),
                    MeasuredLRA = double.Parse(document.RootElement.GetProperty("input_lra").GetString()),
                    MeasuredThresh = double.Parse(document.RootElement.GetProperty("input_thresh").GetString()),
                    Offset = double.Parse(document.RootElement.GetProperty("target_offset").GetString())
                };


            }
            catch (JsonException ex)
            {
                Console.WriteLine($"JSON parsing error: {ex.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"Error parsing LUFS: {ex.Message}");
                return null;
            }
        }

        private async Task<string> NormalizeAudioAsync(string inputFile, LoudnormMeasurements currentLufs)
        {
            var fileName = Path.GetFileNameWithoutExtension(inputFile);
            var extension = Path.GetExtension(inputFile);
            var outputFile = Path.Combine(_normalizedFolder, $"{fileName}_normalized{extension}");

            // Calculate the adjustment needed
            Console.WriteLine("Normalizing audio to -14 LUFS for YouTube...");

            var stderr = new StringBuilder();
            var stdout = new StringBuilder();

            var arg = $"-y -i \"{inputFile}\" -af \"loudnorm=I=-14:TP=-1:LRA=7:measured_I={currentLufs.MeasuredI}:measured_tp={currentLufs.MeasuredTp}:measured_LRA={currentLufs.MeasuredLRA}:measured_thresh={currentLufs.MeasuredThresh}:offset={currentLufs.Offset}:linear=true:print_format=summary\" -c:v copy \"{outputFile}\"";


            // Since FFMpegCore doesn't expose stderr directly, we need to use Process
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "ffmpeg",
                    Arguments = arg,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    CreateNoWindow = true
                }
            };

            process.OutputDataReceived += (sender, e) => {
                if (e.Data != null)
                {
                    stdout.AppendLine(e.Data); Console.WriteLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) => {
                if (e.Data != null) { stderr.AppendLine(e.Data); 
                    Console.WriteLine(e.Data); }

            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();


            if (!process.WaitForExit(300000)) // 5 minutes timeout
            {
                process.Kill();
                throw new TimeoutException("LUFS conversion timed out");
            }

            var stderrContent = stderr.ToString();

            Console.WriteLine("Normalization completed!"); 

            return outputFile;
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