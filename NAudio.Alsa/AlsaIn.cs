using System;
using NAudio.Wave.Alsa;
using NAudio.Wave.SampleProviders;
using System.Threading.Tasks;

namespace NAudio.Wave
{
    public class AlsaIn : AlsaPcm, IWaveIn
    {
        private AlsaInterop.PcmCallback callback;
        public void StartRecording(){}    
        public void StopRecording(){}
        public WaveFormat WaveFormat { get; set;}
        public event EventHandler<WaveInEventArgs> DataAvailable;
        public event EventHandler<StoppedEventArgs> RecordingStopped;
        public void Dispose()
        {
            if (isDisposed)
            {
                return;
            }
            isDisposed = true;
            AlsaInterop.PcmHwParamsFree(HwParams);
            AlsaInterop.PcmSwParamsFree(SwParams);
            AlsaInterop.PcmClose(Handle);
        }
        public AlsaIn(string pcm_name)
        {
            int error;
            if ((error = AlsaInterop.PcmOpen(out Handle, pcm_name, PCMStream.SND_PCM_STREAM_CAPTURE, 0)) < 0)
            {
                throw new AlsaException("snd_pcm_only", error);
            }
            callback = Callback;
            ulong buffer_size = PERIOD_SIZE * PERIOD_QUANTITY;
            if ((error = AlsaInterop.AsyncAddPcmHandler(out IntPtr handler, Handle, callback, default)) != 0)
            {
                InitBuffers(false);
            }
            else
            {
                InitBuffers(true);
            }
            GetHardwareParams();
            GetSoftwareParams();
            SetInterleavedAccess();
        }
        public AlsaIn() : this("default")
        {
        }
        ~AlsaIn()
        {
            Dispose();
        }
        private void Callback(IntPtr cb_info)
        {

        }
    }
}