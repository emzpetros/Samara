using System;
using System.IO;
using System.Text;

namespace AudioTool
{
    /// <summary>
    /// Lightweight audio header reader that extracts metadata (duration, channels, sample rate)
    /// from audio file headers without loading the full audio data.
    /// Thread-safe: all methods use local file handles and no shared state.
    /// Supports WAV/RIFF, AIFF/AIFC, and OGG Vorbis formats.
    /// </summary>
    public static class AudioHeaderReader
    {
        /// <summary>
        /// Result of parsing an audio file header.
        /// </summary>
        public class AudioHeaderInfo
        {
            public float Duration { get; set; }
            public int Channels { get; set; }
            public int SampleRate { get; set; }
            public int BitsPerSample { get; set; }
        }

        /// <summary>
        /// Attempts to read audio metadata from the file header.
        /// Returns null if the format is not recognized or the header is corrupt.
        /// </summary>
        public static AudioHeaderInfo ReadHeader(string filePath)
        {
            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return null;

            try
            {
                string ext = Path.GetExtension(filePath).ToLowerInvariant();
                switch (ext)
                {
                    case ".wav":
                        return ReadWavHeader(filePath);

                    case ".aiff":
                    case ".aif":
                        return ReadAiffHeader(filePath);

                    case ".ogg":
                        return ReadOggVorbisHeader(filePath);

                    default:
                        return null;
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// Reads WAV/RIFF header to extract duration. Handles non-standard chunk ordering
        /// and extended format chunks (WAVEFORMATEXTENSIBLE).
        /// Duration = dataChunkSize / byteRate.
        /// </summary>
        private static AudioHeaderInfo ReadWavHeader(string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                if (fs.Length < 44) return null;

                // RIFF header
                string riffId = new string(reader.ReadChars(4));
                if (riffId != "RIFF") return null;

                reader.ReadInt32(); // file size - 8
                string waveId = new string(reader.ReadChars(4));
                if (waveId != "WAVE") return null;

                int channels = 0;
                int sampleRate = 0;
                int byteRate = 0;
                int bitsPerSample = 0;
                long dataSize = -1;
                bool foundFmt = false;

                // Walk chunks - fmt and data can appear in any order, and there may be
                // extra chunks (LIST, fact, bext, etc.) between them.
                while (fs.Position < fs.Length - 8)
                {
                    string chunkId;
                    try
                    {
                        chunkId = new string(reader.ReadChars(4));
                    }
                    catch
                    {
                        break;
                    }

                    int chunkSize = reader.ReadInt32();
                    if (chunkSize < 0) break; // corrupt

                    long chunkEnd = fs.Position + chunkSize;
                    // chunks are word-aligned (padded to even size)
                    long paddedEnd = chunkEnd + (chunkSize % 2);

                    if (chunkId == "fmt ")
                    {
                        if (chunkSize < 16) return null;

                        short audioFormat = reader.ReadInt16(); // 1=PCM, 3=IEEE float, 0xFFFE=extensible
                        channels = reader.ReadInt16();
                        sampleRate = reader.ReadInt32();
                        byteRate = reader.ReadInt32();
                        reader.ReadInt16(); // blockAlign
                        bitsPerSample = reader.ReadInt16();

                        // only handle uncompressed formats where duration can be computed from header
                        if (audioFormat != 1 && audioFormat != 3 && audioFormat != -2 /*0xFFFE*/)
                        {
                            return null;
                        }

                        foundFmt = true;
                    }
                    else if (chunkId == "data")
                    {
                        dataSize = chunkSize;
                    }

                    if (foundFmt && dataSize >= 0) break; // got everything we need

                    // skip to next chunk
                    if (paddedEnd > fs.Length) break;
                    fs.Position = paddedEnd;
                }

                if (!foundFmt || dataSize < 0 || byteRate <= 0 || channels <= 0 || sampleRate <= 0)
                {
                    return null;
                }

                return new AudioHeaderInfo
                {
                    Duration = (float)dataSize / byteRate,
                    Channels = channels,
                    SampleRate = sampleRate,
                    BitsPerSample = bitsPerSample
                };
            }
        }

        /// <summary>
        /// Reads AIFF/AIFC header. Duration = numSampleFrames / sampleRate.
        /// AIFF uses big-endian byte order.
        /// </summary>
        private static AudioHeaderInfo ReadAiffHeader(string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                if (fs.Length < 12) return null;

                string formId = new string(reader.ReadChars(4));
                if (formId != "FORM") return null;

                reader.ReadInt32(); // file size
                string aiffId = new string(reader.ReadChars(4));
                if (aiffId != "AIFF" && aiffId != "AIFC") return null;

                int channels = 0;
                long numSampleFrames = 0;
                int bitsPerSample = 0;
                double sampleRate = 0;
                bool foundComm = false;

                while (fs.Position < fs.Length - 8)
                {
                    string chunkId;
                    try
                    {
                        chunkId = new string(reader.ReadChars(4));
                    }
                    catch
                    {
                        break;
                    }

                    // AIFF uses big-endian chunk sizes
                    int chunkSize = ReadInt32BigEndian(reader);
                    if (chunkSize < 0) break;

                    long chunkEnd = fs.Position + chunkSize;
                    long paddedEnd = chunkEnd + (chunkSize % 2);

                    if (chunkId == "COMM")
                    {
                        if (chunkSize < 18) return null;

                        channels = ReadInt16BigEndian(reader);
                        numSampleFrames = ReadUInt32BigEndian(reader);
                        bitsPerSample = ReadInt16BigEndian(reader);
                        sampleRate = ReadIeeeExtended(reader);

                        foundComm = true;
                        break; // COMM has everything we need
                    }

                    if (paddedEnd > fs.Length) break;
                    fs.Position = paddedEnd;
                }

                if (!foundComm || sampleRate <= 0 || channels <= 0)
                {
                    return null;
                }

                return new AudioHeaderInfo
                {
                    Duration = (float)(numSampleFrames / sampleRate),
                    Channels = channels,
                    SampleRate = (int)sampleRate,
                    BitsPerSample = bitsPerSample
                };
            }
        }

        /// <summary>
        /// Reads OGG Vorbis identification header for channel/sample rate info,
        /// then seeks to the last OGG page to compute duration from the granule position.
        /// </summary>
        private static AudioHeaderInfo ReadOggVorbisHeader(string filePath)
        {
            using (FileStream fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (BinaryReader reader = new BinaryReader(fs))
            {
                if (fs.Length < 58) return null;

                // first OGG page
                string capturePattern = new string(reader.ReadChars(4));
                if (capturePattern != "OggS") return null;

                reader.ReadByte(); // version
                reader.ReadByte(); // header type
                reader.ReadInt64(); // granule position
                reader.ReadInt32(); // serial
                reader.ReadInt32(); // page sequence
                reader.ReadInt32(); // checksum
                int numSegments = reader.ReadByte();
                byte[] segmentTable = reader.ReadBytes(numSegments);

                // read first packet (Vorbis identification header)
                int packetSize = 0;
                foreach (byte s in segmentTable) packetSize += s;

                if (packetSize < 30) return null;
                byte[] packet = reader.ReadBytes(packetSize);

                // Vorbis identification header: packet type (1) + "vorbis"
                if (packet[0] != 1 || Encoding.ASCII.GetString(packet, 1, 6) != "vorbis")
                {
                    return null;
                }

                int channels = packet[11];
                int sampleRate = BitConverter.ToInt32(packet, 12);

                if (channels <= 0 || sampleRate <= 0) return null;

                // find last OGG page to get final granule position for duration
                long lastGranule = FindLastOggGranulePosition(fs);
                if (lastGranule <= 0) return null;

                return new AudioHeaderInfo
                {
                    Duration = (float)lastGranule / sampleRate,
                    Channels = channels,
                    SampleRate = sampleRate,
                    BitsPerSample = 0 // Vorbis is VBR, no fixed bits per sample
                };
            }
        }

        /// <summary>
        /// Searches backwards from the end of the file for the last OGG page header
        /// and extracts its granule position (used for total sample count / duration).
        /// </summary>
        private static long FindLastOggGranulePosition(FileStream fs)
        {
            int searchSize = (int)Math.Min(65536, fs.Length);
            byte[] buffer = new byte[searchSize];
            fs.Seek(-searchSize, SeekOrigin.End);
            fs.Read(buffer, 0, searchSize);

            // find last occurrence of "OggS"
            for (int i = searchSize - 14; i >= 0; i--)
            {
                if (buffer[i] == 'O' && buffer[i + 1] == 'g' && buffer[i + 2] == 'g' && buffer[i + 3] == 'S')
                {
                    // granule position is at offset 6 from page start (little-endian int64)
                    return BitConverter.ToInt64(buffer, i + 6);
                }
            }

            return -1;
        }

        #region Big-Endian Helpers (for AIFF)

        private static short ReadInt16BigEndian(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(2);
            return (short)((bytes[0] << 8) | bytes[1]);
        }

        private static int ReadInt32BigEndian(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(4);
            return (bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3];
        }

        private static uint ReadUInt32BigEndian(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(4);
            return (uint)((bytes[0] << 24) | (bytes[1] << 16) | (bytes[2] << 8) | bytes[3]);
        }

        /// <summary>
        /// Reads an 80-bit IEEE 754 extended precision float (used in AIFF COMM chunk for sample rate).
        /// </summary>
        private static double ReadIeeeExtended(BinaryReader reader)
        {
            byte[] bytes = reader.ReadBytes(10);

            int exponent = ((bytes[0] & 0x7F) << 8) | bytes[1];
            ulong mantissa = 0;
            for (int i = 2; i < 10; i++)
            {
                mantissa = (mantissa << 8) | bytes[i];
            }

            double result;
            if (exponent == 0 && mantissa == 0)
            {
                result = 0.0;
            }
            else if (exponent == 0x7FFF)
            {
                result = double.PositiveInfinity;
            }
            else
            {
                exponent -= 16383;
                result = Math.Pow(2.0, exponent - 63) * mantissa;
            }

            if ((bytes[0] & 0x80) != 0) result = -result;
            return result;
        }

        #endregion
    }
}
