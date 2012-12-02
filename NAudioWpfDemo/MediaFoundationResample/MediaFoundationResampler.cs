﻿using System;
using System.Runtime.InteropServices;
using NAudio.MediaFoundation;
using NAudio.Utils;
using NAudio.Wave;

namespace NAudioWpfDemo.MediaFoundationResample
{
    // still TODO: 
    // 1. implement a reposition method
    // 2. configurable quality
    // 3. factor out most of this into a MediaFoundationTransform class,
    //    so we can make an IMP3FrameDecoder

    class MediaFoundationResampler : IWaveProvider, IDisposable
    {
        private readonly IWaveProvider sourceProvider;
        private readonly WaveFormat waveFormat;
        private readonly byte[] sourceBuffer;
        
        private byte[] outputBuffer;
        private int outputBufferOffset;
        private int outputBufferCount;

        private IMFTransform resamplerTransform;
        private bool disposed;

        public MediaFoundationResampler(IWaveProvider sourceProvider, int outputSampleRate)
        {
            MediaFoundationApi.Startup();
            if (sourceProvider.WaveFormat.Encoding == WaveFormatEncoding.Pcm)
            {
                waveFormat = new WaveFormat(outputSampleRate,
                    sourceProvider.WaveFormat.BitsPerSample,
                    sourceProvider.WaveFormat.Channels);
            }
            else if (sourceProvider.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat)
            {
                waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(outputSampleRate,
                    sourceProvider.WaveFormat.Channels);
            }
            else
            {
                throw new ArgumentException("Can only resample PCM or IEEE float");
            }
            this.sourceProvider = sourceProvider;
            sourceBuffer = new byte[sourceProvider.WaveFormat.AverageBytesPerSecond];
            outputBuffer = new byte[waveFormat.AverageBytesPerSecond + waveFormat.BlockAlign]; // we will grow this buffer if needed, but try to make something big enough
            // n.b. we will create the resampler COM object on demand in the Read method, 
            // to avoid threading issues but just
            // so we can check it exists on the system we'll make one so it will throw an 
            // exception if not exists
            var comObject = new ResamplerMediaComObject();
            Marshal.ReleaseComObject(comObject);
        }

        private void InitializeTransformForStreaming()
        {
            resamplerTransform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_FLUSH, IntPtr.Zero);
            resamplerTransform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_BEGIN_STREAMING, IntPtr.Zero);
            resamplerTransform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_START_OF_STREAM, IntPtr.Zero);
        }

        private void CreateResampler()
        {
            var comObject = new ResamplerMediaComObject();
            resamplerTransform = (IMFTransform) comObject;

            var inputMediaFormat = MediaFoundationApi.CreateMediaTypeFromWaveFormat(sourceProvider.WaveFormat);
            resamplerTransform.SetInputType(0, inputMediaFormat, 0);
            Marshal.ReleaseComObject(inputMediaFormat);

            var outputMediaFormat = MediaFoundationApi.CreateMediaTypeFromWaveFormat(waveFormat);
            resamplerTransform.SetOutputType(0, outputMediaFormat, 0);
            Marshal.ReleaseComObject(outputMediaFormat);

            // setup quality
            var resamplerProps = (IWMResamplerProps) comObject;
            // 60 is the best quality, 1 is linear interpolation
            resamplerProps.SetHalfFilterLength(60);

            InitializeTransformForStreaming();
        }

        protected void Dispose(bool disposing)
        {
            Marshal.ReleaseComObject(resamplerTransform);
        }

        public void Dispose()
        {
            if (!disposed)
            {
                disposed = true;
                Dispose(true);
                GC.SuppressFinalize(this);
            }
        }

        ~MediaFoundationResampler()
        {
            Dispose(false);
        }

        public WaveFormat WaveFormat { get { return waveFormat; } }

        public int Read(byte[] buffer, int offset, int count)
        {
            if (resamplerTransform == null)
            {
                CreateResampler();
            }

            // strategy will be to always read 1 second from the source, and give it to the resampler
            int bytesWritten = 0;
            
            // read in any leftovers from last time
            if (outputBufferCount > 0)
            {
                bytesWritten += ReadFromOutputBuffer(buffer, offset, count - bytesWritten);
            }

            while (bytesWritten < count)
            {
                var sample = ReadFromSource();
                if (sample == null) // reached the end of our input
                {
                    // be good citizens and send some end messages:
                    EndStreamAndDrain();
                    // resampler might have given us a little bit more to return
                    bytesWritten += ReadFromOutputBuffer(buffer, offset + bytesWritten, count - bytesWritten);
                    break;
                }

                // give the input to the resampler
                // can get MF_E_NOTACCEPTING if we didn't drain the buffer properly
                resamplerTransform.ProcessInput(0, sample, 0);

                Marshal.ReleaseComObject(sample);

                int readFromTransform;
                do
                {
                    // keep reading from transform, even if we've got enough
                    readFromTransform = ReadFromTransform();
                    bytesWritten += ReadFromOutputBuffer(buffer, offset + bytesWritten, count - bytesWritten);
                } while (readFromTransform > 0);
            }

            return bytesWritten;
        }

        private void EndStreamAndDrain()
        {
            resamplerTransform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_OF_STREAM, IntPtr.Zero);
            resamplerTransform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_COMMAND_DRAIN, IntPtr.Zero);
            ReadFromTransform();
            resamplerTransform.ProcessMessage(MFT_MESSAGE_TYPE.MFT_MESSAGE_NOTIFY_END_STREAMING, IntPtr.Zero);
        }

        /// <summary>
        /// Attempts to read from the transform
        /// Some useful info here:
        /// http://msdn.microsoft.com/en-gb/library/windows/desktop/aa965264%28v=vs.85%29.aspx#process_data
        /// </summary>
        /// <returns></returns>
        private int ReadFromTransform()
        {
            var outputDataBuffer = new MFT_OUTPUT_DATA_BUFFER[1];

            //outputDataBuffer[0] = new MFT_OUTPUT_DATA_BUFFER();
            _MFT_PROCESS_OUTPUT_STATUS status;
            var hr = resamplerTransform.ProcessOutput(_MFT_PROCESS_OUTPUT_FLAGS.None, 
                1, outputDataBuffer, out status);
            if (hr == MediaFoundationErrors.MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                // nothing to read
                return 0;
            }
            else if (hr != 0)
            {
                Marshal.ThrowExceptionForHR(hr);
            }
            /* TODO: do we need to handle this? - hopefully passing 1 second in means there is always enough for a read
            if (hr == MF_E_TRANSFORM_NEED_MORE_INPUT)
            {
                // conversion end
                break;
            }*/

            IMFMediaBuffer outputMediaBuffer;
            outputDataBuffer[0].pSample.ConvertToContiguousBuffer(out outputMediaBuffer);
            IntPtr pOutputBuffer;
            int outputBufferLength;
            int maxSize;
            outputMediaBuffer.Lock(out pOutputBuffer, out maxSize, out outputBufferLength);
            outputBuffer = BufferHelpers.Ensure(outputBuffer, outputBufferLength);
            Marshal.Copy(pOutputBuffer, outputBuffer, 0, outputBufferLength);
            outputBufferOffset = 0;
            outputBufferCount = outputBufferLength;
            outputMediaBuffer.Unlock();
            return outputBufferLength;
        }

        private IMFSample ReadFromSource()
        {
            // we always read a full second
            int bytesRead = sourceProvider.Read(sourceBuffer, 0, sourceBuffer.Length);
            if (bytesRead == 0) return null;

            var mediaBuffer = MediaFoundationApi.CreateMemoryBuffer(bytesRead);
            IntPtr pBuffer;
            int maxLength, currentLength;
            mediaBuffer.Lock(out pBuffer, out maxLength, out currentLength);
            Marshal.Copy(pBuffer, sourceBuffer, 0, bytesRead);
            mediaBuffer.Unlock();
            mediaBuffer.SetCurrentLength(bytesRead);

            var sample = MediaFoundationApi.CreateSample();
            sample.AddBuffer(mediaBuffer);
            Marshal.ReleaseComObject(mediaBuffer);
            return sample;
        }

        private int ReadFromOutputBuffer(byte[] buffer, int offset, int needed)
        {
            int bytesFromOutputBuffer = Math.Min(needed, outputBufferCount);
            Array.Copy(outputBuffer, outputBufferOffset, buffer, offset, bytesFromOutputBuffer);
            outputBufferOffset += bytesFromOutputBuffer;
            outputBufferCount -= bytesFromOutputBuffer;
            if (outputBufferCount == 0)
            {
                outputBufferOffset = 0;
            }
            return bytesFromOutputBuffer;
        }
    }

    // NAudio internals:

    [ComImport, Guid("f447b69e-1884-4a7e-8055-346f74d6edb3")]
    class ResamplerMediaComObject
    {
    }

    [Guid("E7E9984F-F09F-4da4-903F-6E2E0EFE56B5"),
    InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IWMResamplerProps
    {
        /// <summary>
        /// Range is 1 to 60
        /// </summary>
        int SetHalfFilterLength(int outputQuality);

        int SetUserChannelMtx([In] float[] channelConversionMatrix);
    }
}
