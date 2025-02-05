using System;
using System.IO;
using System.IO.Compression;
using System.Threading.Tasks;

namespace Celeste.Mod.Helpers {
    /// <summary>
    /// A stream giving access to a ZipArchiveEntry and putting a lock on the ZipArchive when calling any method.
    /// This makes reading from multiple entries at the same time thread-safe (kind of) (I think).
    /// It also implements Seek, Position and Length.
    /// </summary>
    public class SynchronizedZipEntryStream : Stream {
        private readonly ZipArchive archive;
        private readonly ZipArchiveEntry entry;
        private Stream wrappedStream;
        private long position;

        public SynchronizedZipEntryStream(ZipArchiveEntry entry) {
            this.entry = entry;

            archive = entry.Archive;
            Length = entry.Length;

            lock (archive) {
                // open the stream
                reset();
            }
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length { get; }

        // reads with locking, and updates Position
        public override int Read(byte[] buffer, int offset, int count) {
            lock (archive) {
                int readCount = readAsMuchAsPossible(buffer, offset, count);
                position += readCount;
                return readCount;
            }
        }

        // closes and reopens the ZipArchiveEntry in order to rewind the stream
        private void reset() {
            wrappedStream?.Close();
            wrappedStream = entry.Open();
            position = 0;
        }

        // closes the stream of the ZipArchiveEntry
        protected override void Dispose(bool disposing) {
            lock (archive) {
                wrappedStream.Dispose();
            }
        }

        public override long Position {
            get => position;
            set => Seek(value, SeekOrigin.Begin);
        }

        // DIY seeking
        public override long Seek(long offset, SeekOrigin origin) {
            lock (archive) {
                // target = position from the beginning of the file
                long target = offset;
                if (origin == SeekOrigin.Current) target += Position;
                if (origin == SeekOrigin.End) target += Length;

                if (target < Position) {
                    // seek back => go back to the beginning
                    reset();
                }

                if (target > Position) {
                    // seek forward by just skipping the required amount of bytes
                    // (this also handles seeking back, by skipping from 0 to the requested position)
                    skip((int) (target - Position));
                }

                if (target != Position) {
                    Logger.Warn("SynchronizedZipEntryStream", $"[{entry.FullName}] Couldn't seek to position {target}! Sought to {Position}/{Length}.");
                }

                return Position;
            }
        }

        // read repeatedly until we read the requested amount of bytes, or we reached the end of the stream
        // (the underlying stream returns 0).
        // we *should* be able to return fewer bytes than requested, but FNA3D *really* doesn't like that.
        private int readAsMuchAsPossible(byte[] buffer, int offset, int count) {
            int remainingCount = count;
            while (remainingCount > 0) {
                int read = wrappedStream.Read(buffer, offset, remainingCount);
                if (read == 0) break;
                offset += read;
                remainingCount -= read;
            }
            return count - remainingCount;
        }

        private const int trashbinSize = 8192;
        private static byte[] trashbin = new byte[trashbinSize];

        // "skips" bytes by reading them and blatantly ignoring them
        private void skip(long count) {
            while (count > 0) {
                int read = wrappedStream.Read(trashbin, 0, count > trashbinSize ? trashbinSize : (int) count);
                if (read == 0) return;
                position += read;
                count -= read;
            }
        }

        // those are useless for a read-only stream

        public override void Flush() {
            throw new NotSupportedException("Write not supported!");
        }
        public override void SetLength(long value) {
            throw new NotSupportedException("Write not supported!");
        }
        public override void Write(byte[] buffer, int offset, int count) {
            throw new NotSupportedException("Write not supported!");
        }

        // here lies a test that writes a zip and tries reading multiple files at the same time

#if PARALLEL_ZIP_TEST
        public static void Main() {
            const int filesToGenerateCount = 1000;
            const int maxStringCountInFile = 2000;

            // "random" strings: recursive directory listing of the working directory
            string[] poolOfStrings = Directory.GetFiles(".", "*", SearchOption.AllDirectories);

            // assign a random amount of random strings to each file
            string[][] garbagePile = new string[filesToGenerateCount][];
            Random r = new Random();
            for (int i = 0; i < filesToGenerateCount; i++) {
                string[] garbage = new string[r.Next(0, maxStringCountInFile)];
                for (int j = 0; j < garbage.Length; j++) {
                    garbage[j] = poolOfStrings[r.Next(0, poolOfStrings.Length)];
                }
                garbagePile[i] = garbage;
            }

            // build a zip containing said strings using various compression levels, with BinaryWriter:
            // [amount of strings] + {[index][string]} * amount of strings
            File.Delete("/tmp/garbage.zip");
            using (ZipArchive zip = ZipFile.Open("/tmp/garbage.zip", ZipArchiveMode.Create)) {
                for (int i = 0; i < filesToGenerateCount; i++) {
                    ZipArchiveEntry entry = zip.CreateEntry($"garbage{i:D3}.bin", (CompressionLevel) (i % 4));
                    using Stream output = entry.Open();
                    using BinaryWriter writer = new BinaryWriter(output);
                    writer.Write(garbagePile[i].Length);
                    for (int k = 0; k < garbagePile[i].Length; k++) {
                        writer.Write((long) k);
                        writer.Write(garbagePile[i][k]);
                    }
                }
            }
            
            // in order to get closest to the real deal, we'll access the zip with ZipModContent
            using (ZipModContent zip = new ZipModContent("/tmp/garbage.zip")) {
                Task[] tasks = new Task[filesToGenerateCount];
                int toGo = filesToGenerateCount;

                // spawn a Task for each file
                for (int i = 0; i < filesToGenerateCount; i++) {
                    int index = i;
                    Task t = new Task(() => {
                        try {
                            using Stream input = zip.Open($"garbage{index:D3}.bin");
                            using BinaryReader reader = new BinaryReader(input);

                            int size = reader.ReadInt32();

                            // read all from string 0, seek back to string 1, read all from string 1, etc.
                            // this will test if seeking and position works.
                            // if something doesn't match what we initially wrote, an exception is thrown
                            for (int k = 0; k < size; k++) {
                                if (reader.ReadInt64() != k) throw new Exception("Test failed!");
                                if (!reader.ReadString().Equals(garbagePile[index][k])) throw new Exception("Test failed!");

                                long pos = input.Position;
                                for (int j = k + 1; j < size; j++) {
                                    if (reader.ReadInt64() != j) throw new Exception("Test failed!");
                                    if (!reader.ReadString().Equals(garbagePile[index][j])) throw new Exception("Test failed!");
                                }
                                input.Position = pos;
                            }

                            toGo--;
                            Console.WriteLine($"[{index:D3}] success! {toGo} to go");
                        } catch (Exception e) {
                            Console.Error.WriteLine($"Error, bailing out! {e}");
                            Environment.Exit(1);
                        }
                    });
                    t.Start();
                    tasks[i] = t;
                }

                Task.WaitAll(tasks);
            }

            File.Delete("/tmp/garbage.zip");
            Console.WriteLine("Test succeeded for all tasks!");
        }
#endif
    }
}
