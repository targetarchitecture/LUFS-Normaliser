using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace AudioNormalizerApp
{
    /// <summary>
    /// Represents a video file that has been processed for audio normalization.
    /// Tracks both the original source file and its normalized output.
    /// </summary>
    public class ProcessedFile
    {
        public string? OriginalPath { get; set; }
        public string? NormalizedPath { get; set; }
    }

    /// <summary>
    /// Main class for normalizing audio levels in video files using FFmpeg's loudnorm filter.
    /// Processes multiple videos to achieve consistent audio levels suitable for YouTube.
    /// </summary>
    public class AudioNormalizer
    {
        // YouTube recommended loudness target: -16 LUFS (Loudness Units relative to Full Scale)
        // Note: -14 LUFS is also commonly used for YouTube
        private const double TARGET_LUFS = -16.0;

        private readonly string _sourceFolder;      // Folder containing original video files
        private readonly string _normalizedFolder;  // Output folder for normalized videos

        /// <summary>
        /// Initializes the AudioNormalizer with source and destination folders.
        /// Creates the normalized folder if it doesn't exist.
        /// </summary>
        public AudioNormalizer(string sourceFolder, string normalizedFolder)
        {
            _sourceFolder = sourceFolder ?? throw new ArgumentNullException(nameof(sourceFolder));
            _normalizedFolder = normalizedFolder ?? throw new ArgumentNullException(nameof(normalizedFolder));

            // Ensure output directory exists
            Directory.CreateDirectory(_normalizedFolder);
        }

        /// <summary>
        /// Main processing pipeline: reads video list, normalizes audio for each video,
        /// and concatenates all normalized videos into a single output file.
        /// </summary>
        public async Task ProcessVideosAsync()
        {
            try
            {
                Console.WriteLine("Starting YouTube normalization process...");

                // Step 1: Read video file paths from files.txt
                var videoFiles = await GetVideoFilesFromListAsync(_sourceFolder);
                if (!videoFiles.Any())
                {
                    Console.WriteLine("No video files found or files.txt is empty.");
                    return;
                }

                Console.WriteLine($"Found {videoFiles.Count} video files to process from files.txt.");

                var processedFiles = new List<ProcessedFile>();

                // Step 2: Process each video - measure LUFS and normalize audio
                foreach (var videoFile in videoFiles)
                {
                    Console.WriteLine($"\nProcessing: {Path.GetFileName(videoFile)}");

                    // Measure current loudness using FFmpeg loudnorm analysis
                    var actualLufs = await CalculateLUFSAsync(videoFile);
                    Console.WriteLine($"Measured LUFS: {actualLufs:F2}");

                    // Apply normalization based on measurements
                    var normalizedFile = await NormalizeAudioAsync(videoFile, actualLufs);

                    processedFiles.Add(new ProcessedFile
                    {
                        OriginalPath = videoFile,
                        NormalizedPath = normalizedFile,
                    });

                    Console.WriteLine($"Normalized and saved to: {Path.GetFileName(normalizedFile)}");
                }

                // Step 3: Concatenate all normalized files into final output
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

        /// <summary>
        /// Container for FFmpeg loudnorm measurements.
        /// These values are used for two-pass loudness normalization.
        /// </summary>
        public class LoudnormMeasurements
        {
            public double MeasuredI { get; set; }      // Integrated loudness (LUFS)
            public double MeasuredTp { get; set; }     // True peak (dBTP)
            public double MeasuredLRA { get; set; }    // Loudness range (LU)
            public double MeasuredThresh { get; set; } // Loudness threshold
            public double Offset { get; set; }         // Target offset
        }

        /// <summary>
        /// Reads the list of video files from files.txt in the source folder.
        /// Each line should contain a relative path to a video file.
        /// Lines starting with # are treated as comments and ignored.
        /// </summary>
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

                // Skip empty lines and comments
                if (string.IsNullOrEmpty(trimmedLine) || trimmedLine.StartsWith("#"))
                {
                    continue;
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

        /// <summary>
        /// Performs the first pass of loudnorm: measures the current loudness levels.
        /// FFmpeg outputs measurement data as JSON to stderr, which is captured and parsed.
        /// </summary>
        private async Task<LoudnormMeasurements> CalculateLUFSAsync(string inputFile)
        {
            try
            {
                var stderr = new StringBuilder();
                var stdout = new StringBuilder();

                Console.WriteLine("Measuring LUFS with loudnorm filter...");

                // Run FFmpeg to analyze audio loudness
                // -af loudnorm=print_format=json outputs measurements as JSON
                // -f null - means no output file (analysis only)
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

                // Capture stdout
                process.OutputDataReceived += (sender, e) => {
                    if (e.Data != null)
                    {
                        stdout.AppendLine(e.Data);
                        Console.WriteLine(e.Data);
                    }
                };

                // Capture stderr (where FFmpeg outputs the JSON measurements)
                process.ErrorDataReceived += (sender, e) => {
                    if (e.Data != null)
                    {
                        stderr.AppendLine(e.Data);
                        Console.WriteLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for analysis to complete (10 minute timeout)
                if (!process.WaitForExit(600000))
                {
                    process.Kill();
                    throw new TimeoutException("LUFS analysis timed out");
                }

                var stderrContent = stderr.ToString();

                // Extract measurement values from JSON output
                var lufs = ParseLUFSFromJson(stderrContent);
                Console.WriteLine($"Measured LUFS: {lufs.MeasuredI:F2}");
                return lufs;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"LUFS measurement failed: {ex.Message}");
                return null;
            }
        }

        /// <summary>
        /// Parses FFmpeg's loudnorm JSON output from stderr to extract measurement values.
        /// The JSON contains input_i (integrated loudness), input_tp (true peak), and other metrics.
        /// </summary>
        private LoudnormMeasurements ParseLUFSFromJson(string stderr)
        {
            try
            {
                // FFmpeg outputs JSON in stderr - find the JSON block containing "input_i"
                var jsonMatch = Regex.Match(stderr, @"\{[^}]*""input_i""[^}]*\}", RegexOptions.Singleline);

                if (!jsonMatch.Success)
                {
                    Console.WriteLine("Could not find JSON output in FFmpeg stderr");
                    return null;
                }

                var jsonString = jsonMatch.Value;
                Console.WriteLine($"Found loudnorm JSON: {jsonString}");

                // Parse JSON and extract all measurement values
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

        /// <summary>
        /// Performs the second pass of loudnorm: applies normalization using the measurements
        /// from the first pass to achieve the target loudness (TARGET_LUFS).
        /// Uses linear=true for higher quality processing.
        /// </summary>
        private async Task<string> NormalizeAudioAsync(string inputFile, LoudnormMeasurements currentLufs)
        {
            var fileName = Path.GetFileNameWithoutExtension(inputFile);
            var extension = Path.GetExtension(inputFile);
            var outputFile = Path.Combine(_normalizedFolder, $"{fileName}_normalized{extension}");

            Console.WriteLine($"Normalizing audio to {TARGET_LUFS} LUFS for YouTube...");

            var stderr = new StringBuilder();
            var stdout = new StringBuilder();

            // Build FFmpeg command with two-pass loudnorm parameters
            // I=-16: target integrated loudness
            // TP=-1: target true peak
            // LRA=7: target loudness range
            // measured_* parameters: values from first pass
            // linear=true: use linear normalization (higher quality)
            // -c:v copy: copy video stream without re-encoding
            // -c:a aac -b:a 320k -ar 48000: re-encode audio to AAC 320kbps 48kHz
            var arg = $"-y -i \"{inputFile}\" -af \"loudnorm=I={TARGET_LUFS}:TP=-1:LRA=7:measured_I={currentLufs.MeasuredI}:" +
                $"measured_tp={currentLufs.MeasuredTp}:measured_LRA={currentLufs.MeasuredLRA}:measured_thresh={currentLufs.MeasuredThresh}:" +
                $"offset={currentLufs.Offset}:linear=true:print_format=summary\" " +
                $"-c:v copy -c:a aac -b:a 320k -ar 48000 \"{outputFile}\"";

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
                    stdout.AppendLine(e.Data);
                    Console.WriteLine(e.Data);
                }
            };

            process.ErrorDataReceived += (sender, e) => {
                if (e.Data != null)
                {
                    stderr.AppendLine(e.Data);
                    Console.WriteLine(e.Data);
                }
            };

            process.Start();
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Wait for normalization to complete (5 minute timeout)
            if (!process.WaitForExit(300000))
            {
                process.Kill();
                throw new TimeoutException("LUFS conversion timed out");
            }

            var stderrContent = stderr.ToString();

            Console.WriteLine("Normalization completed!");

            return outputFile;
        }

        /// <summary>
        /// Concatenates all normalized video files into a single output file.
        /// Uses FFmpeg's concat demuxer with stream copy (no re-encoding).
        /// Fixes potential timestamp issues with -avoid_negative_ts and -fflags +genpts.
        /// </summary>
        public async Task ConcatenateVideosAsync(List<ProcessedFile> videoFiles)
        {
            var outputFile = Path.Combine(_normalizedFolder, "final_concatenated.mp4");

            // Create temporary file list for FFmpeg concat demuxer
            // Format: file '/path/to/video.mp4' (one per line)
            var fileListContent = string.Join("\n", videoFiles.Select(f => $"file '{f.NormalizedPath.Replace("'", @"\'")}'"));
            var tempFileList = Path.GetTempFileName();

            try
            {
                await File.WriteAllTextAsync(tempFileList, fileListContent);

                Console.WriteLine("Concatenating videos...");
                var stderr = new StringBuilder();
                var stdout = new StringBuilder();

                // -f concat: use concat demuxer
                // -safe 0: allow absolute paths
                // -c copy: copy streams without re-encoding
                // -avoid_negative_ts make_zero: fix timestamp issues
                // -fflags +genpts: generate presentation timestamps
                var arg = $"-f concat -safe 0 -i \"{tempFileList}\" -c copy -avoid_negative_ts make_zero -fflags +genpts -y \"{outputFile}\"";

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
                        stdout.AppendLine(e.Data);
                        Console.WriteLine(e.Data);
                    }
                };

                process.ErrorDataReceived += (sender, e) => {
                    if (e.Data != null)
                    {
                        stderr.AppendLine(e.Data);
                        Console.WriteLine(e.Data);
                    }
                };

                process.Start();
                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                // Wait for concatenation to complete (5 minute timeout)
                if (!process.WaitForExit(300000))
                {
                    process.Kill();
                    throw new TimeoutException("Concatenation timed out");
                }

                Console.WriteLine("Concatenation completed!");
            }
            finally
            {
                // Clean up temporary file list
                if (File.Exists(tempFileList))
                    File.Delete(tempFileList);
            }
        }
    }

    class Program
    {
        /// <summary>
        /// Entry point for the Audio Normalizer application.
        /// Processes video files listed in files.txt, normalizes their audio to YouTube standards,
        /// and concatenates them into a single output file.
        /// </summary>
        static async Task Main(string[] args)
        {
            try
            {
                // Default paths - can be overridden via command line arguments
                var sourceFolder = @"D:\Temp\2024";
                var normalizedFolder = @"D:\Temp\2024\Normalized";

                // Command line arguments override defaults
                // Usage: AudioNormalizer.exe <source_folder> <output_folder>
                if (args.Length >= 2)
                {
                    sourceFolder = args[0];
                    normalizedFolder = args[1];
                }

                Console.WriteLine($"Source folder: {sourceFolder}");
                Console.WriteLine($"Output folder: {normalizedFolder}");
                Console.WriteLine("Note: Make sure 'files.txt' exists in your source folder.");
                Console.WriteLine();

                // Validate source folder exists
                if (!Directory.Exists(sourceFolder))
                {
                    Console.WriteLine($"Source folder does not exist: {sourceFolder}");
                    return;
                }

                // Create normalizer instance and process videos
                var normalizer = new AudioNormalizer(sourceFolder, normalizedFolder);
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