# Audio Normalizer for YouTube

A C# console application that normalizes audio levels across multiple video files to YouTube standards and concatenates them into a single output file. Uses FFmpeg's two-pass loudnorm filter for high-quality loudness normalization.

## Features

- **Two-Pass Loudness Normalization**: Measures and normalizes audio to -16 LUFS (YouTube recommended)
- **Batch Processing**: Processes multiple video files from a list
- **Video Concatenation**: Combines all normalized videos into a single output file
- **Progress Tracking**: Real-time console output showing processing progress
- **Quality Preservation**: Copies video streams without re-encoding, only processes audio

## How It Works

1. **Analysis Phase**: Measures the current loudness (LUFS) of each video using FFmpeg's loudnorm filter
2. **Normalization Phase**: Applies two-pass loudness normalization to achieve consistent -16 LUFS target
3. **Concatenation Phase**: Merges all normalized videos into a single output file

The application uses FFmpeg's loudnorm filter with linear normalization for the highest quality results. Audio is re-encoded to AAC 320kbps at 48kHz, while video streams are copied without modification for faster processing.

## Prerequisites

- **.NET 6.0 or higher**
- **FFmpeg**: Must be installed and accessible in your system PATH
  - Download from: https://ffmpeg.org/download.html
  - Or install via package manager (e.g., `choco install ffmpeg` on Windows)

## Installation

1. Clone this repository:
   ```bash
   git clone https://github.com/yourusername/audio-normalizer.git
   cd audio-normalizer
   ```

2. Build the project:
   ```bash
   dotnet build
   ```

3. Verify FFmpeg is installed:
   ```bash
   ffmpeg -version
   ```

## Usage

### Step 1: Create files.txt

Create a `files.txt` file in your source folder listing the video files to process (one per line):

```
video1.mp4
video2.mp4
video3.mp4
# Comments start with #
subfolder/video4.mp4
```

### Step 2: Run the Application

**Using default paths** (hardcoded in Program.cs):
```bash
dotnet run
```

**Using command-line arguments**:
```bash
dotnet run -- "C:\Videos\Source" "C:\Videos\Output"
```

Or run the compiled executable:
```bash
AudioNormalizerApp.exe "C:\Videos\Source" "C:\Videos\Output"
```

### Output

The application creates:
- Individual normalized videos: `{filename}_normalized.mp4` in the output folder
- Final concatenated video: `final_concatenated.mp4` in the output folder

## Configuration

### Target Loudness

The default target is **-16 LUFS**. To change this, modify the constant in `AudioNormalizer.cs`:

```csharp
private const double TARGET_LUFS = -16.0; // Change this value
```

Common targets:
- `-16 LUFS`: YouTube recommended (current default)
- `-14 LUFS`: Alternative YouTube standard
- `-23 LUFS`: Broadcast standard (EBU R128)

### Audio Encoding Settings

Current settings (in `NormalizeAudioAsync`):
- **Codec**: AAC
- **Bitrate**: 320kbps
- **Sample Rate**: 48kHz

Modify the FFmpeg arguments to change these:
```csharp
-c:a aac -b:a 320k -ar 48000
```

### Timeouts

- **LUFS Analysis**: 10 minutes per video
- **Normalization**: 5 minutes per video
- **Concatenation**: 5 minutes total

Modify in the respective methods if processing longer videos.

## Example Workflow

```
Source Folder Structure:
D:\Temp\2024\
├── files.txt
├── clip1.mp4
├── clip2.mp4
└── clip3.mp4

After Processing:
D:\Temp\2024\Normalized\
├── clip1_normalized.mp4
├── clip2_normalized.mp4
├── clip3_normalized.mp4
└── final_concatenated.mp4  ← Your final output
```

## Technical Details

### Loudness Normalization (Two-Pass)

**Pass 1 - Measurement**:
```bash
ffmpeg -i input.mp4 -af loudnorm=print_format=json -f null -
```
Outputs JSON with current loudness metrics (input_i, input_tp, input_lra, etc.)

**Pass 2 - Normalization**:
```bash
ffmpeg -i input.mp4 -af "loudnorm=I=-16:TP=-1:LRA=7:measured_I=<value>:..." 
  -c:v copy -c:a aac -b:a 320k output.mp4
```
Applies precise normalization using first-pass measurements

### Video Concatenation

Uses FFmpeg's concat demuxer:
```bash
ffmpeg -f concat -safe 0 -i filelist.txt -c copy 
  -avoid_negative_ts make_zero -fflags +genpts output.mp4
```

## Error Handling

The application handles:
- Missing `files.txt`
- Missing video files (logs warning, continues processing)
- FFmpeg process timeouts
- JSON parsing errors from FFmpeg output
- Directory creation failures

## Troubleshooting

**FFmpeg not found**:
- Ensure FFmpeg is in your system PATH
- Or provide full path to ffmpeg.exe

**Process timeouts**:
- Increase timeout values for large files
- Check FFmpeg output for errors

**Audio/video sync issues**:
- The concatenation uses `-avoid_negative_ts make_zero -fflags +genpts` to fix timestamp issues
- If problems persist, try re-encoding instead of stream copying

**Memory issues with large files**:
- Process files in smaller batches
- Monitor system resources during processing

## License

Up to you!

## Contributing

Contributions are welcome! Please feel free to submit a Pull Request.

## Acknowledgments

- Uses [FFmpeg](https://ffmpeg.org/) for video/audio processing
- Implements EBU R128 loudness standards via FFmpeg's loudnorm filter
