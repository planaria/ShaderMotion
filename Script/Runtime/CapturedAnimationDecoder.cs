using System;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ShaderMotion
{
    public struct CapturedFrame
    {
        public double time;
        public ulong index;
        public float[] data;
    }

    public class CapturedAnimationDecoder : IDisposable
    {
        private FileStream fileStream;
        private DeflateStream deflateStream;
        private BinaryReader binaryReader;

        [StructLayout(LayoutKind.Sequential)]
        private struct Header
        {
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 3)]
            public byte[] magic;
            public uint version;
            public double frameRate;
            public uint frameWidth;
            public uint frameHeight;
            public ulong timestampMs;
        }

        private Header header;

        public CapturedAnimationDecoder(string filePath)
        {
            fileStream = new FileStream(filePath, FileMode.Open, FileAccess.Read);
            deflateStream = new DeflateStream(fileStream, CompressionMode.Decompress);
            binaryReader = new BinaryReader(deflateStream);

            header = ReadHeader();

            var expectedMagic = new byte[] { 0x53, 0x4d, 0x43 };

            if (!header.magic.SequenceEqual(expectedMagic))
            {
                throw new InvalidDataException("Invalid file format: incorrect magic number.");
            }

            if (header.version != 1)
            {
                throw new InvalidDataException($"Unsupported version: {header.version}.");
            }

            Debug.Log($"Captured animation file opened. FrameRate: {header.frameRate}, FrameSize: {header.frameWidth}x{header.frameHeight} timestampMs: {header.timestampMs}");
        }

        public double FrameRate
        {
            get
            {
                return header.frameRate;
            }
        }

        private Header ReadHeader()
        {
            int headerSize = Marshal.SizeOf<Header>();
            byte[] headerBytes = binaryReader.ReadBytes(headerSize);

            GCHandle handle = GCHandle.Alloc(headerBytes, GCHandleType.Pinned);

            try
            {
                Header header = Marshal.PtrToStructure<Header>(handle.AddrOfPinnedObject());
                return header;
            }
            finally
            {
                handle.Free();
            }
        }

        public bool TryRead(out CapturedFrame frame)
        {
            try
            {
                var time = binaryReader.ReadDouble();
                var index = binaryReader.ReadUInt64();
                int dataSize = sizeof(float) * (int)(header.frameWidth * header.frameHeight * 4);
                byte[] dataBytes = binaryReader.ReadBytes(dataSize);
                float[] data = new float[dataSize / sizeof(float)];
                Buffer.BlockCopy(dataBytes, 0, data, 0, dataSize);

                frame = new CapturedFrame
                {
                    time = time,
                    index = index,
                    data = data
                };

                return true;
            }
            catch (EndOfStreamException)
            {
                frame = default;
                return false;
            }
        }

        public void Dispose()
        {
            binaryReader?.Dispose();
            deflateStream?.Dispose();
            fileStream?.Dispose();
        }
    }
}
